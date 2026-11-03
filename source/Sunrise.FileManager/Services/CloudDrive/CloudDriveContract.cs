using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Sunrise.FileManager.Services;
using System.Text.Json;

namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>
/// Remote identities are opaque provider item IDs, never Windows paths.
/// These APIs must only be invoked after the user explicitly authorizes the account and the action.
/// Authentication (OAuth PKCE), UI binding, credential protection and cross-provider transfer are
/// intentionally outside this R11 API-layer checkpoint.
/// </summary>
// VersionTag is a provider-issued opaque ETag. It must never be synthesized from size or a hash.
public sealed record CloudItem(string Id, string Name, bool IsFolder, long? Size, string? ParentId = null,
    string? VersionTag = null);

/// <summary>
/// Atomic provider-enforced conditional retirement for an explicit verified move.
/// Implement only when the provider documents an If-Match-style server-side guard.
/// Never fall back to unconditional MoveToTrashAsync on a missing/mismatched tag.
/// </summary>
public interface IConditionalCloudRetirement
{
    Task MoveToTrashIfVersionMatchesAsync(string itemId, string expectedVersionTag, CancellationToken token);
}

public sealed record CloudCopyResult(bool Completed, string? ItemId, Uri? MonitorUrl);

public interface ICloudDriveApi
{
    Task<IReadOnlyList<CloudItem>> ListChildrenAsync(string parentId, CancellationToken token);
    Task<CloudItem> CreateFolderAsync(string parentId, string name, CancellationToken token);
    Task<CloudItem> CreateEmptyFileAsync(string parentId, string name, CancellationToken token);
    Task<CloudItem> RenameAsync(string itemId, string name, CancellationToken token);
    Task<CloudItem> MoveAsync(string itemId, string destinationParentId, CancellationToken token);
    Task<CloudCopyResult> CopyAsync(string itemId, string destinationParentId, string name, CancellationToken token);
    Task MoveToTrashAsync(string itemId, CancellationToken token);
}

/// <summary>Per-account transport. The token factory is supplied by a future OAuth account owner.</summary>
public abstract class CloudDriveApiBase
{
    private readonly HttpClient _client;
    private readonly Func<CancellationToken, Task<string>> _tokenSource;
    // Read-only API requests are bounded per adapter. Never hold a slot for a write.
    private readonly SemaphoreSlim _readSlots = new(3, 3);
    private const int MaxGetAttempts = 3;
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(12);

    protected CloudDriveApiBase(HttpClient client, Func<CancellationToken, Task<string>> tokenSource)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
    }

    protected static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl))
            throw new ArgumentException("유효하지 않은 원격 항목 식별자입니다.");
        return Uri.EscapeDataString(value);
    }

    protected static string ItemName(string name)
    {
        // Respect the existing Sunrise naming policy. Do not allow traversal/path separators.
        return FileActionsService.ValidateName(name);
    }

    protected static JsonElement Required(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value :
            throw new InvalidDataException("클라우드 응답의 필수 항목이 없습니다: " + property);

    protected static string RequiredString(JsonElement element, string property) =>
        Required(element, property).GetString() ?? throw new InvalidDataException("클라우드 항목이 비어 있습니다: " + property);

    protected static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Only replay read-only GET requests. A POST/PATCH/DELETE may already have committed
    // when its response is lost, so automatically replaying it could corrupt user data.
    internal static bool RetryableGetStatus(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    // Respect a provider's Retry-After instead of reducing a long quota wait to our
    // maximum. An over-budget delay means fail safely and let the user retry later.
    internal static TimeSpan? GetRetryWait(HttpResponseMessage response, int retryIndex, DateTimeOffset now)
    {
        var retry = response.Headers.RetryAfter;
        TimeSpan wait;
        if (retry?.Delta is TimeSpan seconds)
            wait = seconds;
        else if (retry?.Date is DateTimeOffset date)
            wait = date - now;
        else
            wait = TimeSpan.FromMilliseconds(400 * (1 << retryIndex) + Random.Shared.Next(0, 151));
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        return wait <= MaxRetryWait ? wait : null;
    }

    protected async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri endpoint,
        object? body, CancellationToken cancellationToken, string? ifMatch = null)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps || !AllowedApiHost(endpoint.Host))
            throw new InvalidOperationException("승인되지 않은 클라우드 API 주소입니다.");
        bool readonlyGet = method == HttpMethod.Get && body is null && ifMatch is null;
        if (readonlyGet) await _readSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int attempts = readonlyGet ? MaxGetAttempts : 1;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Every attempt creates a fresh request and obtains an up-to-date token.
                using var request = new HttpRequestMessage(method, endpoint);
                string accessToken = await _tokenSource(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(accessToken))
                    throw new UnauthorizedAccessException("클라우드 계정 인증이 필요합니다.");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                if (ifMatch is not null)
                {
                    if (ifMatch == "*" || !EntityTagHeaderValue.TryParse(ifMatch, out var tag))
                        throw new InvalidDataException("클라우드 변경 버전 식별자가 올바르지 않습니다.");
                    request.Headers.IfMatch.Add(tag);
                }
                if (body is not null) request.Content = JsonContent.Create(body);
                var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                try { RejectRedirect(response); }
                catch { response.Dispose(); throw; }
                if (response.IsSuccessStatusCode) return response;

                HttpStatusCode status = response.StatusCode;
                TimeSpan? wait = readonlyGet && attempt < attempts - 1 && RetryableGetStatus(status)
                    ? GetRetryWait(response, attempt, DateTimeOffset.UtcNow) : null;
                response.Dispose();
                if (wait is null)
                    throw new HttpRequestException($"클라우드 파일 작업 실패 (HTTP {(int)status}).", null, status);
                await Task.Delay(wait.Value, cancellationToken).ConfigureAwait(false);
            }
            throw new InvalidOperationException("클라우드 읽기 재시도 한도를 초과했습니다.");
        }
        finally
        {
            if (readonlyGet) _readSlots.Release();
        }
    }

    protected async Task<JsonDocument> JsonAsync(HttpMethod method, Uri url, object? body, CancellationToken token)
    {
        using var response = await SendAsync(method, url, body, token).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
    }

    protected static Uri Url(string value) => new(value, UriKind.Absolute);

    // Callers MUST configure the per-provider HttpClient with redirects disabled; never forward
    // user content, Bearer tokens or file bytes to an unverified redirect destination.
    protected static void RejectRedirect(HttpResponseMessage response)
    {
        if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            throw new InvalidDataException("클라우드 API가 예상치 못한 외부 주소로 이동을 요청했습니다.");
    }

    protected abstract bool AllowedApiHost(string host);
}
