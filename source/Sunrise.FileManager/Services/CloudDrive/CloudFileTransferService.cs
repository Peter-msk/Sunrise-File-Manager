using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>Reported only after the remote server acknowledges the fragment or the local stream commits.</summary>
public sealed record CloudTransferProgress(long Completed, long Total)
{
    public int Percent => Total <= 0 ? 0 : (int)Math.Clamp(Completed * 100.0 / Total, 0, 100);
}

/// <summary>Server cannot prove whether a lost last upload response committed the file.
/// The journal is retained; a new session must not be created automatically.</summary>
public sealed class CloudUploadReviewRequiredException : IOException
{
    public CloudUploadReviewRequiredException() : base(
        "클라우드 업로드 세션이 종료되었거나 이미 완료됐을 수 있습니다 (HTTP 404/410). " +
        "대상 폴더에서 파일을 직접 확인한 후 중단 업로드 기록을 정리하세요. 자동 재업로드하지 않습니다.") { }
}

/// <summary>
/// Authenticated cloud-to-local/local-to-cloud transfer. Never accepts arbitrary hosts for bearer credentials.
/// Remote URLs with embedded upload/download grants are never persisted or emitted to logs.
/// Never overwrites existing local/remote files. R28 permits only explicit user retry
/// of DPAPI-sealed, account-bound upload sessions; staging streams never persist sessions.
/// </summary>
public sealed class CloudFileTransferService
{
    private const int Chunk = 5 * 1024 * 1024; // 5 MiB: divisible by Google 256 KiB and Graph 320 KiB.
    private const int MaximumStatusReconciliations = 2; // bounded, no blind replay of uncertain writes.
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _token;
    private readonly CloudProvider _provider;
    private readonly string? _driveId;

    public CloudFileTransferService(HttpClient http, Func<CancellationToken, Task<string>> token,
        CloudProvider provider, string? driveId = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _provider = provider;
        _driveId = driveId;
        if (provider == CloudProvider.OneDrive && string.IsNullOrWhiteSpace(driveId))
            throw new ArgumentException("OneDrive 드라이브 ID가 필요합니다.", nameof(driveId));
    }

    private static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl))
            throw new ArgumentException("클라우드 항목 식별자가 올바르지 않습니다.");
        return Uri.EscapeDataString(value);
    }

    private static string CheckedName(string name) => Sunrise.FileManager.Services.FileActionsService.ValidateName(name);
    private static Uri Url(string value) => new(value, UriKind.Absolute);
    private static bool IsHost(string host, string root) => host.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase);

    internal static bool IsTransferHost(Uri uri, CloudProvider provider, bool upload)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
            uri.HostNameType != UriHostNameType.Dns) return false;
        if (provider == CloudProvider.GoogleDrive)
            return uri.Host.Equals("www.googleapis.com", StringComparison.OrdinalIgnoreCase);
        // OneDrive preauthorized URLs may include a one-time secret in the path/query;
        // accept only provider-owned transfer domains. Never forward our bearer token.
        return IsHost(uri.Host, "1drv.com") || IsHost(uri.Host, "onedrive.com") ||
               IsHost(uri.Host, "sharepoint.com") || IsHost(uri.Host, "blob.core.windows.net") ||
               IsHost(uri.Host, "storage.live.com");
    }

    private bool IsApiUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.IsDefaultPort &&
        uri.Host.Equals(_provider == CloudProvider.GoogleDrive ? "www.googleapis.com" : "graph.microsoft.com", StringComparison.OrdinalIgnoreCase);

    private async Task<HttpResponseMessage> AuthAsync(HttpMethod method, Uri url, HttpContent? body, CancellationToken ct)
    {
        if (!IsApiUri(url)) throw new InvalidOperationException("허용되지 않은 클라우드 API 주소입니다.");
        using var request = new HttpRequestMessage(method, url) { Content = body };
        string bearer = await _token(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(bearer)) throw new UnauthorizedAccessException("클라우드 계정에 로그인하세요.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> TransferAsync(HttpMethod method, Uri url, HttpContent? body, bool upload, CancellationToken ct)
    {
        if (!IsTransferHost(url, _provider, upload))
            throw new InvalidOperationException("공급자가 허용되지 않은 파일 전송 주소를 반환했습니다. 전송을 중지합니다.");
        using var request = new HttpRequestMessage(method, url) { Content = body };
        // Upload/download session URL is already authorized. Do NOT attach Authorization.
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private static void EnsureSuccess(HttpResponseMessage response, string action)
    {
        if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            throw new InvalidDataException(action + " 중 예상하지 못한 URL 이동이 발생했습니다.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{action} 실패 (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("클라우드 응답에 필수 파일 정보가 없습니다.");
        return value.GetString()!;
    }

    public async Task<string> UploadAsync(string parentId, string localFile, ICloudDriveApi api,
        IProgress<CloudTransferProgress>? progress, CancellationToken ct)
    {
        if (api is null) throw new ArgumentNullException(nameof(api));
        string path = Path.GetFullPath(localFile);
        string name = CheckedName(Path.GetFileName(path));
        if (!File.Exists(path)) throw new FileNotFoundException("업로드할 원본 파일이 없습니다.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = input.Length;
        // R41: a previous last-fragment PUT may have committed even if its response was lost.
        // Look for the account-bound recovery checkpoint BEFORE probing name conflicts.
        // Otherwise the now-visible, successfully uploaded item would prevent its own
        // session receipt from being inspected and may provoke duplicate creation.
        var checkpoint = CloudUploadResumeStore.Find(_provider, path);
        if (checkpoint is null && await RemoteNameExistsAsync(api, parentId, name, ct).ConfigureAwait(false))
            throw new IOException("대상 클라우드 폴더에 같은 이름이 있습니다. 자동 덮어쓰기를 하지 않습니다.");
        if (length == 0 && _provider == CloudProvider.GoogleDrive)
        {
            var empty = await api.CreateEmptyFileAsync(parentId, name, ct).ConfigureAwait(false);
            progress?.Report(new(0, 0));
            return empty.Id;
        }
        if (length == 0)
            throw new NotSupportedException("빈 OneDrive 파일의 생성은 충돌 방지 조건을 검증한 후 지원합니다.");
        // Hash before any network session is created. FileShare.Read prevents regular writers
        // while this handle is open; the SHA and mtime are checked again on explicit retry.
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("연결 경로의 원본 파일은 업로드 재개에 사용할 수 없습니다.");
        byte[] digest = await SHA256.HashDataAsync(input, ct).ConfigureAwait(false);
        string sourceHash = Convert.ToHexString(digest);
        CryptographicOperations.ZeroMemory(digest);
        input.Position = 0;
        long timestamp = File.GetLastWriteTimeUtc(path).Ticks;
        string identity = await AccountFingerprintAsync(ct).ConfigureAwait(false);
        if (checkpoint is not null)
        {
            CloudUploadResumeStore.Validate(checkpoint, _provider, _driveId, parentId, name,
                path, length, sourceHash, timestamp, identity);
            // Signed session URL is decrypted in memory only and host-checked again on use.
            Uri session = Url(checkpoint.SessionUrl);
            var receipt = await QueryUploadReceiptAsync(session, length, 0, length,
                google: _provider == CloudProvider.GoogleDrive, ct).ConfigureAwait(false);
            if (receipt.CompletedId is null && receipt.Next > 0 &&
                receipt.Next % (_provider == CloudProvider.GoogleDrive ? 256 * 1024 : 320 * 1024) != 0)
                throw new IOException("공급자 청크 경계와 수신 위치가 달라 안전한 이어받기를 중단했습니다.");
            if (receipt.CompletedId is string doneId)
            {
                // Only a session-bound provider receipt, never a filename or file-size match,
                // is sufficient evidence that this upload has completed.
                CloudUploadResumeStore.Complete(checkpoint);
                progress?.Report(new(length, length));
                return doneId;
            }
            // On a resumable session, a newly visible same-name item belongs to an
            // unverified concurrent writer until the provider proves otherwise.
            // Prevent a partial session from completing into an ambiguous collision.
            if (await RemoteNameExistsAsync(api, parentId, name, ct).ConfigureAwait(false))
                throw new IOException("업로드 재개 대상 폴더에 같은 이름의 항목이 발견됐습니다. 세션·원본은 유지하고 충돌을 수동 확인하세요.");
            return await UploadChunksAsync(session, input, length, progress,
                google: _provider == CloudProvider.GoogleDrive, ct, receipt.Next, checkpoint).ConfigureAwait(false);
        }
        var context = new UploadRecoveryContext(path, parentId, name, sourceHash, timestamp, identity);
        return _provider == CloudProvider.GoogleDrive
            ? await UploadGoogleAsync(parentId, name, input, length, progress, ct, context).ConfigureAwait(false)
            : await UploadOneDriveAsync(parentId, name, input, length, progress, ct, context).ConfigureAwait(false);
    }

    private static async Task<bool> RemoteNameExistsAsync(ICloudDriveApi api, string parentId,
        string name, CancellationToken ct) =>
        (await api.ListChildrenAsync(parentId, ct).ConfigureAwait(false))
            .Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private sealed record UploadRecoveryContext(string LocalPath, string ParentId, string Name,
        string Sha256, long LastWriteTicks, string AccountFingerprint);

    // R43: folder-copy recovery binds a checkpoint to the same authenticated provider,
    // drive and account; the opaque fingerprint is never displayed or logged.
    internal CloudProvider RecoveryProvider => _provider;
    internal string? RecoveryDriveId => _driveId;
    internal Task<string> RecoveryAccountFingerprintAsync(CancellationToken ct) => AccountFingerprintAsync(ct);

    private async Task<string> AccountFingerprintAsync(CancellationToken ct)
    {
        // A drive ID by itself is not an account identity. Bind Google to permissionId and
        // Microsoft to /me id, then store only a SHA-256 fingerprint of that stable identity.
        Uri endpoint = _provider == CloudProvider.GoogleDrive
            ? Url("https://www.googleapis.com/drive/v3/about?fields=user(permissionId)")
            : Url("https://graph.microsoft.com/v1.0/me?$select=id");
        using var response = await AuthAsync(HttpMethod.Get, endpoint, null, ct).ConfigureAwait(false);
        EnsureSuccess(response, "연결 계정 확인");
        using var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        string id = _provider == CloudProvider.GoogleDrive
            ? RequiredString(json.RootElement.GetProperty("user"), "permissionId")
            : RequiredString(json.RootElement, "id");
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_provider + ":" + id)));
    }

    /// <summary>
    /// R19: upload bytes directly from an authenticated, seekable encrypted-stage reader.
    /// No decrypted intermediate disk file is created, including during provider retries.
    /// </summary>
    internal async Task<string> UploadReadableAsync(string parentId, string name, Stream input,
        ICloudDriveApi api, IProgress<CloudTransferProgress>? progress, CancellationToken ct)
    {
        if (api is null) throw new ArgumentNullException(nameof(api));
        name = CheckedName(name);
        if (input is null || !input.CanRead || !input.CanSeek || input.Position != 0)
            throw new ArgumentException("업로드 원본은 처음 위치부터 읽을 수 있어야 합니다.", nameof(input));
        long length = input.Length;
        if (length < 0) throw new IOException("원본 크기가 올바르지 않습니다.");
        if ((await api.ListChildrenAsync(parentId, ct).ConfigureAwait(false))
            .Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("대상 클라우드 폴더에 같은 이름이 있습니다. 자동 덮어쓰기를 하지 않습니다.");
        if (length == 0 && _provider == CloudProvider.GoogleDrive)
        {
            var empty = await api.CreateEmptyFileAsync(parentId, name, ct).ConfigureAwait(false);
            progress?.Report(new(0, 0));
            return empty.Id;
        }
        if (length == 0) throw new NotSupportedException("빈 OneDrive 파일 전송은 아직 지원하지 않습니다.");
        return _provider == CloudProvider.GoogleDrive
            ? await UploadGoogleAsync(parentId, name, input, length, progress, ct).ConfigureAwait(false)
            : await UploadOneDriveAsync(parentId, name, input, length, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Capture remote bytes only into a per-operation AES-GCM encrypted staging file.</summary>
    internal async Task DownloadToEncryptedStageAsync(CloudItem item, EncryptedCloudStage stage,
        IProgress<CloudTransferProgress>? progress, CancellationToken ct)
    {
        if (item.IsFolder || item.Size is not long expected || expected < 0)
            throw new NotSupportedException("암호화 전송에 필요한 원본 크기를 확인할 수 없습니다.");
        using var response = await OpenDownloadResponseAsync(item, ct).ConfigureAwait(false);
        EnsureSuccess(response, "클라우드 간 암호화 다운로드");
        if (response.Content.Headers.ContentLength is long sent && sent != expected)
            throw new IOException("원격 응답 크기가 원본 정보와 다릅니다.");
        using var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await stage.CaptureAsync(network, expected, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Rehash a seekable, read-only source stream without materializing plaintext on disk.</summary>
    internal async Task VerifyRemoteMatchesReadableAsync(CloudItem uploaded, Stream source, CancellationToken ct)
    {
        if (uploaded.IsFolder || uploaded.Size is not long expected || expected < 0 ||
            source is null || !source.CanRead || !source.CanSeek || source.Length != expected)
            throw new IOException("원본 또는 대상의 파일 크기를 확인하지 못했습니다.");
        source.Position = 0;
        byte[] local = await SHA256.HashDataAsync(source, ct).ConfigureAwait(false);
        byte[] remote = await ReadRemoteSha256Async(uploaded, expected, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(local, remote))
            throw new IOException("원격 파일 내용이 원본과 일치하지 않습니다. 원본은 보존했습니다.");
        source.Position = 0;
        byte[] again = await SHA256.HashDataAsync(source, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(local, again))
            throw new IOException("검증 중 원본 데이터가 변경됐습니다. 원본은 보존했습니다.");
    }

    private async Task<string> UploadGoogleAsync(string parentId, string name, Stream input, long length,
        IProgress<CloudTransferProgress>? progress, CancellationToken ct, UploadRecoveryContext? recovery = null)
    {
        var url = Url("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id,name,size");
        using var meta = JsonContent.Create(new { name, parents = new[] { parentId }, mimeType = "application/octet-stream" });
        // HttpContentHeaders exposes the documented Google upload metadata headers via request headers instead.
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = meta };
        string bearer = await _token(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(bearer)) throw new UnauthorizedAccessException("클라우드 계정에 로그인하세요.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var start = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        EnsureSuccess(start, "Google Drive 업로드 세션 생성");
        Uri session = start.Headers.Location ?? throw new InvalidDataException("Google Drive 업로드 세션 주소가 없습니다.");
        if (!session.IsAbsoluteUri || !IsTransferHost(session, _provider, true))
            throw new InvalidDataException("Google Drive 업로드 세션 주소가 올바르지 않습니다.");
        var checkpoint = recovery is null ? null : CloudUploadResumeStore.Create(_provider, _driveId,
            parentId, name, recovery.LocalPath, length, recovery.Sha256, recovery.LastWriteTicks,
            recovery.AccountFingerprint, session, DateTimeOffset.UtcNow.AddDays(2));
        return await UploadChunksAsync(session, input, length, progress, google: true, ct,
            initialPosition: 0, checkpoint: checkpoint).ConfigureAwait(false);
    }

    private async Task<string> UploadOneDriveAsync(string parentId, string name, Stream input, long length,
        IProgress<CloudTransferProgress>? progress, CancellationToken ct, UploadRecoveryContext? recovery = null)
    {
        string uri = "https://graph.microsoft.com/v1.0/drives/" + Segment(_driveId!) + "/items/" + Segment(parentId)
            + ":/" + Segment(name) + ":/createUploadSession";
        var body = new Dictionary<string, object>
        {
            ["item"] = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "fail", ["name"] = name },
            ["fileSize"] = length
        };
        using var response = await AuthAsync(HttpMethod.Post, Url(uri), JsonContent.Create(body), ct).ConfigureAwait(false);
        EnsureSuccess(response, "OneDrive 업로드 세션 생성");
        using var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        Uri session = Url(RequiredString(json.RootElement, "uploadUrl"));
        if (!IsTransferHost(session, _provider, true))
            throw new InvalidDataException("OneDrive 업로드 세션 주소가 승인된 Microsoft 전송 도메인이 아닙니다.");
        DateTimeOffset expires = json.RootElement.TryGetProperty("expirationDateTime", out var expiry) &&
            expiry.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(expiry.GetString(), out var until)
            ? until : DateTimeOffset.UtcNow.AddMinutes(30); // fail short when provider does not supply expiry
        var checkpoint = recovery is null ? null : CloudUploadResumeStore.Create(_provider, _driveId,
            parentId, name, recovery.LocalPath, length, recovery.Sha256, recovery.LastWriteTicks,
            recovery.AccountFingerprint, session, expires);
        return await UploadChunksAsync(session, input, length, progress, google: false, ct,
            initialPosition: 0, checkpoint: checkpoint).ConfigureAwait(false);
    }

    /// <summary>
    /// R26: an upload PUT is not blindly repeated if the response is lost. Query the
    /// provider's session receipt first. Never persist the signed session URL or bearer.
    /// Sessions can be reconciled only while this process and its input stream remain alive.
    /// </summary>
    private async Task<(long Next, string? CompletedId)> QueryUploadReceiptAsync(Uri session,
        long length, long previous, long sentEnd, bool google, CancellationToken ct)
    {
        HttpResponseMessage response;
        if (google)
        {
            using var query = new ByteArrayContent(Array.Empty<byte>());
            query.Headers.TryAddWithoutValidation("Content-Range", $"bytes */{length}");
            response = await TransferAsync(HttpMethod.Put, session, query, upload: true, ct).ConfigureAwait(false);
        }
        else response = await TransferAsync(HttpMethod.Get, session, null, upload: true, ct).ConfigureAwait(false);
        using (response)
        {
            // An expired/reaped session and a successfully committed session whose final
            // response was lost can both be reported as unavailable by the provider.
            // Never send the last fragment again or silently start a fresh session.
            // Keep the DPAPI checkpoint so the user can review the remote destination.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                throw new CloudUploadReviewRequiredException();
            if (google && response.StatusCode is (HttpStatusCode.OK or HttpStatusCode.Created))
            {
                using var completed = await ReadJsonAsync(response, ct).ConfigureAwait(false);
                if (sentEnd != length)
                    throw new InvalidDataException("원격 서버가 예상보다 일찍 업로드 완료를 보고했습니다.");
                return (length, RequiredString(completed.RootElement, "id"));
            }
            long next;
            if (google)
            {
                if ((int)response.StatusCode != 308)
                {
                    EnsureSuccess(response, "Google 업로드 수신 위치 조회");
                    throw new InvalidDataException("Google 업로드 세션 수신 위치를 확인하지 못했습니다.");
                }
                next = 0;
                if (response.Headers.TryGetValues("Range", out var values))
                {
                    var range = values.FirstOrDefault() ?? "";
                    var parts = range.Split('-');
                    if (parts.Length != 2 || !parts[0].Equals("bytes=0", StringComparison.OrdinalIgnoreCase) ||
                        !long.TryParse(parts[1], out long last) || last < 0 || last >= length)
                        throw new InvalidDataException("Google 업로드 수신 범위가 유효하지 않습니다.");
                    next = last + 1;
                }
            }
            else
            {
                EnsureSuccess(response, "OneDrive 업로드 수신 위치 조회");
                using var receipt = await ReadJsonAsync(response, ct).ConfigureAwait(false);
                if (!receipt.RootElement.TryGetProperty("nextExpectedRanges", out var ranges) ||
                    ranges.ValueKind != JsonValueKind.Array || ranges.GetArrayLength() < 1 ||
                    ranges[0].ValueKind != JsonValueKind.String ||
                    !long.TryParse((ranges[0].GetString() ?? "").Split('-')[0], out next))
                    throw new InvalidDataException("OneDrive 업로드 수신 위치를 확인하지 못했습니다.");
            }
            // Do not accept server receipts outside the fragment that might have committed.
            if (next < previous || next > sentEnd || next >= length)
                throw new IOException("업로드 세션 수신 위치가 유효하지 않아 전송을 안전하게 중단했습니다.");
            return (next, null);
        }
    }

    private static bool ReconcileAllowed(HttpStatusCode? code)
        => code is null || code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
           (int)code.Value is >= 500 and <= 599;

    private async Task<string> UploadChunksAsync(Uri session, Stream input, long length,
        IProgress<CloudTransferProgress>? progress, bool google, CancellationToken ct,
        long initialPosition = 0, CloudUploadResumeStore.Checkpoint? checkpoint = null)
    {
        if (initialPosition < 0 || initialPosition >= length)
            throw new IOException("복구 시작 바이트가 원본 범위를 벗어났습니다.");
        byte[] buffer = new byte[Chunk];
        long position = initialPosition;
        int consecutiveReconciliations = 0;
        while (position < length)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, length - position);
            input.Position = position;
            await input.ReadExactlyAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            HttpResponseMessage? response = null;
            bool uncertainWrite = false;
            try
            {
                using var fragment = new ByteArrayContent(buffer, 0, count);
                fragment.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                fragment.Headers.TryAddWithoutValidation("Content-Range", $"bytes {position}-{position + count - 1}/{length}");
                response = await TransferAsync(HttpMethod.Put, session, fragment, upload: true, ct).ConfigureAwait(false);
                uncertainWrite = ReconcileAllowed(response.StatusCode) && !response.IsSuccessStatusCode;
            }
            catch (HttpRequestException ex) when (ReconcileAllowed(ex.StatusCode) && !ct.IsCancellationRequested)
            {
                uncertainWrite = true;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                uncertainWrite = true; // request timeout, not a user's cancellation
            }
            using (response)
            {
                if (uncertainWrite)
                {
                    if (++consecutiveReconciliations > MaximumStatusReconciliations)
                        throw new IOException("업로드 상태 확인 재시도 한도에 도달했습니다. 원본은 보존합니다.");
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * consecutiveReconciliations), ct).ConfigureAwait(false);
                    var receipt = await QueryUploadReceiptAsync(session, length, position, position + count, google, ct)
                        .ConfigureAwait(false);
                    if (receipt.CompletedId is string id)
                    {
                        if (checkpoint is not null) CloudUploadResumeStore.Complete(checkpoint);
                        progress?.Report(new(length, length));
                        return id;
                    }
                    position = receipt.Next;
                    progress?.Report(new(position, length));
                    continue;
                }
                if (response is null)
                    throw new IOException("업로드 서버에서 응답이 없어 작업을 중단했습니다.");
                if (google && (int)response.StatusCode == 308)
                {
                    long next = 0;
                    if (response.Headers.TryGetValues("Range", out var values))
                    {
                        string range = values.FirstOrDefault() ?? "";
                        string[] parts = range.Split('-');
                        if (parts.Length != 2 || !parts[0].Equals("bytes=0", StringComparison.OrdinalIgnoreCase) ||
                            !long.TryParse(parts[1], out long last))
                            throw new InvalidDataException("Google Drive 청크 수신 범위가 올바르지 않습니다.");
                        next = last + 1;
                    }
                    if (next <= position || next > position + count || next >= length)
                        throw new IOException("Google Drive 서버가 확인한 청크 수신 범위가 유효하지 않습니다.");
                    position = next;
                    consecutiveReconciliations = 0;
                    progress?.Report(new(position, length));
                    continue;
                }
                if (!google && response.StatusCode == HttpStatusCode.Accepted)
                {
                    using var accepted = await ReadJsonAsync(response, ct).ConfigureAwait(false);
                    if (!accepted.RootElement.TryGetProperty("nextExpectedRanges", out var ranges) ||
                        ranges.ValueKind != JsonValueKind.Array || ranges.GetArrayLength() == 0 ||
                        !long.TryParse((ranges[0].GetString() ?? "").Split('-')[0], out long next) ||
                        next != position + count || next >= length)
                        throw new IOException("OneDrive 청크 수신 위치가 불일치합니다. 원본은 보존합니다.");
                    position = next;
                    consecutiveReconciliations = 0;
                    progress?.Report(new(position, length));
                    continue;
                }
                EnsureSuccess(response, "클라우드 업로드");
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created) || position + count != length)
                    throw new InvalidDataException("클라우드 업로드 완료 응답이 올바르지 않습니다.");
                using var done = await ReadJsonAsync(response, ct).ConfigureAwait(false);
                string finalId = RequiredString(done.RootElement, "id");
                if (checkpoint is not null) CloudUploadResumeStore.Complete(checkpoint);
                progress?.Report(new(length, length));
                return finalId;
            }
        }
        throw new IOException("클라우드 업로드가 완료되지 않았습니다.");
    }

    /// <summary>
    /// Fetch an item via its provider's authorized API. Signed OneDrive download URLs
    /// MUST use TransferAsync (without a bearer) and must never appear in diagnostics.
    /// Caller owns the returned HttpResponseMessage and must dispose it.
    /// </summary>
    private sealed record DownloadDescription(long? Length, string? Revision, Uri? Preauthorized);

    /// <summary>Provider identity and version are re-read on every explicitly requested resume.</summary>
    private async Task<DownloadDescription> InspectDownloadAsync(CloudItem item, CancellationToken ct)
    {
        if (_provider == CloudProvider.GoogleDrive)
        {
            var url = Url("https://www.googleapis.com/drive/v3/files/" + Segment(item.Id) +
                "?fields=id,mimeType,size,version");
            using var response = await AuthAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            EnsureSuccess(response, "Google Drive 파일 정보 확인");
            using var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            if (!string.Equals(RequiredString(json.RootElement, "id"), item.Id, StringComparison.Ordinal))
                throw new IOException("Google Drive 원본 ID가 변경됐습니다.");
            var mime = RequiredString(json.RootElement, "mimeType");
            if (mime.StartsWith("application/vnd.google-apps.", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Google 문서·시트·슬라이드는 별도 내보내기 API가 필요합니다.");
            long? size = json.RootElement.TryGetProperty("size", out var s) &&
                long.TryParse(s.ToString(), out long parsed) ? parsed : null;
            string? version = json.RootElement.TryGetProperty("version", out var v) ? v.ToString() : null;
            return new DownloadDescription(size, version, null);
        }
        else
        {
            var url = Url("https://graph.microsoft.com/v1.0/drives/" + Segment(_driveId!) + "/items/" + Segment(item.Id)
                + "?$select=id,size,eTag,%40microsoft.graph.downloadUrl");
            using var response = await AuthAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            EnsureSuccess(response, "OneDrive 파일 정보 확인");
            using var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            if (!string.Equals(RequiredString(json.RootElement, "id"), item.Id, StringComparison.Ordinal))
                throw new IOException("OneDrive 원본 ID가 변경됐습니다.");
            long? size = json.RootElement.TryGetProperty("size", out var s) &&
                s.TryGetInt64(out long parsed) ? parsed : null;
            string? revision = json.RootElement.TryGetProperty("eTag", out var tag) ? tag.GetString() : null;
            var signed = Url(RequiredString(json.RootElement, "@microsoft.graph.downloadUrl"));
            if (!IsTransferHost(signed, _provider, upload: false))
                throw new InvalidDataException("OneDrive 다운로드 서버 주소가 허용 목록에 없습니다.");
            return new DownloadDescription(size, revision, signed);
        }
    }

    private async Task<HttpResponseMessage> FetchDownloadBytesAsync(CloudItem item,
        DownloadDescription description, long? start, CancellationToken ct)
    {
        if (_provider == CloudProvider.GoogleDrive)
        {
            Uri url = Url("https://www.googleapis.com/drive/v3/files/" + Segment(item.Id) + "?alt=media");
            if (start is null) return await AuthAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            if (!IsApiUri(url)) throw new InvalidOperationException("허용되지 않은 Google API 주소입니다.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start, null);
            string bearer = await _token(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(bearer)) throw new UnauthorizedAccessException("클라우드 계정에 로그인하세요.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        var signed = description.Preauthorized ?? throw new InvalidDataException("OneDrive 다운로드 주소가 없습니다.");
        if (start is null) return await TransferAsync(HttpMethod.Get, signed, null, upload: false, ct).ConfigureAwait(false);
        if (!IsTransferHost(signed, _provider, upload: false))
            throw new InvalidOperationException("허용되지 않은 OneDrive 전송 주소입니다.");
        using var rangedRequest = new HttpRequestMessage(HttpMethod.Get, signed);
        rangedRequest.Headers.Range = new RangeHeaderValue(start, null);
        // OneDrive preauthenticated URLs must NEVER receive the API Bearer credential.
        return await _http.SendAsync(rangedRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> OpenDownloadResponseAsync(CloudItem item, CancellationToken ct)
    {
        var description = await InspectDownloadAsync(item, ct).ConfigureAwait(false);
        return await FetchDownloadBytesAsync(item, description, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// R18: read the uploaded destination BACK from the provider and compare SHA-256
    /// against the actual local upload bytes. A matching size alone is insufficient.
    /// The remote response is hashed in memory as a stream; no extra plaintext disk copy.
    /// </summary>
    public async Task VerifyRemoteMatchesLocalAsync(CloudItem uploaded, string sourcePath, CancellationToken ct)
    {
        if (uploaded.IsFolder || uploaded.Size is not long expected || expected < 0)
            throw new IOException("검증할 원격 파일의 크기를 확인할 수 없습니다.");
        string path = Path.GetFullPath(sourcePath);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != expected)
            throw new IOException("전송 중 로컬 원본 파일의 크기가 변경됐습니다. 원본은 보존했습니다.");
        byte[] localSha = await SHA256.HashDataAsync(input, ct).ConfigureAwait(false);
        byte[] remoteSha = await ReadRemoteSha256Async(uploaded, expected, ct).ConfigureAwait(false);
        // CryptographicOperations compares the entire digest without data-dependent early exit.
        if (!CryptographicOperations.FixedTimeEquals(localSha, remoteSha))
            throw new IOException("원격 파일 내용의 SHA-256이 원본과 일치하지 않습니다. 원본은 보존했습니다. 대상 파일을 확인하세요.");
        ct.ThrowIfCancellationRequested();
        input.Position = 0;
        byte[] currentLocalSha = await SHA256.HashDataAsync(input, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(localSha, currentLocalSha))
            throw new IOException("검증 중 로컬 원본 파일이 변경됐습니다. 원본은 보존했습니다.");
    }

    /// <summary>R25: hash the provider's current file bytes without persisting plaintext.</summary>
    internal Task<byte[]> ComputeRemoteSha256Async(CloudItem item, CancellationToken ct)
    {
        if (item.IsFolder || item.Size is not long expected || expected < 0)
            throw new IOException("클라우드 원본 파일의 크기를 확인하지 못했습니다.");
        return ReadRemoteSha256Async(item, expected, ct);
    }

    private async Task<byte[]> ReadRemoteSha256Async(CloudItem uploaded, long expectedBytes, CancellationToken ct)
    {
        using var response = await OpenDownloadResponseAsync(uploaded, ct).ConfigureAwait(false);
        EnsureSuccess(response, "업로드 후 원격 내용 검증");
        if (response.Content.Headers.ContentLength is long responseLength && responseLength != expectedBytes)
            throw new IOException("원격 응답의 콘텐츠 길이가 예상과 다릅니다.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = new byte[128 * 1024];
        long readTotal = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            readTotal = checked(readTotal + count);
            if (readTotal > expectedBytes)
                throw new IOException("원격 응답의 파일 크기가 예상보다 큽니다.");
            hash.AppendData(buffer, 0, count);
        }
        if (readTotal != expectedBytes)
            throw new IOException("원격 응답의 파일 크기가 예상보다 작습니다.");
        return hash.GetHashAndReset();
    }

    public async Task DownloadAsync(CloudItem item, string localDestination,
        IProgress<CloudTransferProgress>? progress, CancellationToken ct)
    {
        if (item.IsFolder) throw new NotSupportedException("폴더 전체 내려받기는 별도 복사 작업에서 처리하세요.");
        string dest = Path.GetFullPath(localDestination);
        string directory = Path.GetDirectoryName(dest) ?? throw new ArgumentException("다운로드 폴더를 확인하세요.");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("저장 폴더가 없습니다.");
        if (File.Exists(dest) || Directory.Exists(dest)) throw new IOException("기존 이름이 있어 덮어쓰지 않습니다.");
        var description = await InspectDownloadAsync(item, ct).ConfigureAwait(false);
        if (description.Length is long remoteLength && item.Size is long itemLength && remoteLength != itemLength)
            throw new IOException("클라우드 목록과 최신 파일 크기가 일치하지 않습니다. 원본을 다시 조회하세요.");
        var pending = CloudDownloadResumeStore.Pending().Where(p =>
            p.Destination.Equals(dest, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (pending.Length > 0 && (description.Length is null || string.IsNullOrWhiteSpace(description.Revision)))
            throw new IOException("기존 다운로드 복구 기록이 있지만 서버 버전 확인이 불가능합니다. 자동 재개하지 않습니다.");
        if (description.Length is long length && length >= 0 && !string.IsNullOrWhiteSpace(description.Revision))
        {
            // Bind restart checkpoints to the authenticated provider account as well as
            // remote item/revision. Shared drive items may be accessible from two accounts.
            // Never publish mixed bytes from a journal created by the other account.
            string account = await AccountFingerprintAsync(ct).ConfigureAwait(false);
            var checkpoint = CloudDownloadResumeStore.GetOrCreate(_provider, _driveId, item.Id,
                dest, length, description.Revision, account);
            await DownloadResumableAsync(item, description, checkpoint, progress, ct).ConfigureAwait(false);
            return;
        }
        // Providers/mocks without revision identity retain the previous fail-clean behavior.
        using var response = await FetchDownloadBytesAsync(item, description, null, ct).ConfigureAwait(false);
        EnsureSuccess(response, "클라우드 파일 다운로드");
        long? expected = item.Size ?? description.Length ?? response.Content.Headers.ContentLength;
        string temporary = Path.Combine(directory, ".sfm-cloud-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            {
                byte[] buffer = new byte[128 * 1024];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
                {
                    received = checked(received + read);
                    if (expected.HasValue && received > expected.Value) throw new IOException("다운로드 응답이 예상 크기를 초과했습니다.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report(new(received, expected ?? 0));
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                if (expected.HasValue && received != expected.Value) throw new IOException("다운로드 크기가 일치하지 않습니다.");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, dest, overwrite: false);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }

    /// <summary>
    /// Resume only with the exact same remote revision and verified HTTP 206 Content-Range.
    /// A 200 on ranged GET is NEVER appended to a partial file. Any failure keeps the
    /// authorized local .part and the DPAPI checkpoint for an explicit later retry.
    /// </summary>
    private async Task DownloadResumableAsync(CloudItem item, DownloadDescription description,
        CloudDownloadResumeStore.Checkpoint checkpoint, IProgress<CloudTransferProgress>? progress, CancellationToken ct)
    {
        long expected = checkpoint.Length;
        bool resumedWithExistingBytes = false;
        if (File.Exists(checkpoint.Part) &&
            (File.GetAttributes(checkpoint.Part) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("클라우드 부분 다운로드가 연결 경로로 변경돼 중단했습니다.");
        await using (var output = new FileStream(checkpoint.Part, FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            long offset = output.Length;
            resumedWithExistingBytes = offset > 0;
            if (offset > expected) throw new IOException("중단 파일이 원본보다 커서 재개하지 않습니다.");
            output.Position = offset;
            progress?.Report(new(offset, expected));
            if (offset < expected)
            {
                using var response = await FetchDownloadBytesAsync(item, description,
                    offset > 0 ? offset : null, ct).ConfigureAwait(false);
                EnsureSuccess(response, "클라우드 다운로드 이어받기");
                if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                    throw new IOException("서버가 Range 요청을 무시했습니다. 전체 파일을 이어 붙이지 않고 원본을 보존합니다.");
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range?.From != offset || range.Length != expected || range.To is null || range.To.Value >= expected)
                        throw new IOException("원격 Content-Range와 복구 위치·파일 크기가 불일치합니다.");
                }
                else if (response.StatusCode != HttpStatusCode.OK)
                    throw new IOException("예상하지 못한 파일 전송 응답입니다.");
                using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                byte[] buffer = new byte[128 * 1024];
                long received = offset;
                int n;
                while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
                {
                    received = checked(received + n);
                    if (received > expected) throw new IOException("응답 크기 초과: 원본·부분파일 보존");
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    progress?.Report(new(received, expected));
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                if (received != expected) throw new IOException("원격 응답이 중간에 끝났습니다. 부분파일을 보존합니다.");
            }
        }
        ct.ThrowIfCancellationRequested();
        // Version checks before AND after transfer prevent publishing bytes combined from
        // different remote revisions. Never persist signed URLs from either inspection.
        var after = await InspectDownloadAsync(item, ct).ConfigureAwait(false);
        if (after.Length != expected || !string.Equals(after.Revision, checkpoint.Revision, StringComparison.Ordinal))
            throw new IOException("다운로드 중 원격 파일 버전이 바뀌었습니다. 부분파일을 보존하고 게시하지 않습니다.");
        if (new FileInfo(checkpoint.Part).Length != expected)
            throw new IOException("임시파일 크기가 달라 게시하지 않습니다.");
        // Audit v1.7.5: a resumed .part might have been changed while the application was
        // closed, without changing its length. A remote version/size match is NOT a byte
        // integrity proof. Stream both sides and compare before publishing. This costs a
        // second remote read only for resumed downloads and never exposes signed URLs.
        if (resumedWithExistingBytes)
        {
            byte[] localDigest;
            await using (var verifyInput = new FileStream(checkpoint.Part, FileMode.Open,
                FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
                localDigest = await SHA256.HashDataAsync(verifyInput, ct).ConfigureAwait(false);
            byte[] remoteDigest = await ReadRemoteSha256Async(item, expected, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(localDigest, remoteDigest))
                throw new IOException("이어받기 파일의 내용이 원격 원본과 일치하지 않아 게시하지 않습니다.");
            var finalVersion = await InspectDownloadAsync(item, ct).ConfigureAwait(false);
            if (finalVersion.Length != expected ||
                !string.Equals(finalVersion.Revision, checkpoint.Revision, StringComparison.Ordinal))
                throw new IOException("최종 무결성 확인 중 원격 버전이 변경되어 게시하지 않습니다.");
        }
        File.Move(checkpoint.Part, checkpoint.Destination, overwrite: false);
        CloudDownloadResumeStore.Complete(checkpoint);
        progress?.Report(new(expected, expected));
    }
}
