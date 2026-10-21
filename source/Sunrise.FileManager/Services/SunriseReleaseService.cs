using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunrise.FileManager.Services;

/// <summary>Product-owned installer wire adapter, bounded transport and plain-text notices.</summary>
public static class SunriseReleaseService
{
    public const string FeedUrl = "https://raw.githubusercontent.com/Peter-msk/Sunrise-File-Manager/main/latest.json";
    public const string NoticeUrl = "https://raw.githubusercontent.com/Peter-msk/Sunrise-File-Manager/main/notice.txt";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);
    internal static string UpdatesRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunrise", "FileManager", "Updates");
    private static string NoticeState => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunrise", "FileManager", "notice-sha256.txt");

    public sealed record Release(string Version, string DownloadUrl, string FileName, string Sha256,
        long FileSize, string Notes, string ReleaseUrl, string NoticeUrl, bool UpdateConfigured)
    {
        public bool IsNewer => SunriseControlService.VersionIsOlder(SunriseControlService.Version, Version);
        public bool CanDownload => IsNewer && UpdateConfigured && IsApprovedDownload(this);
    }

    private static async Task<byte[]> GetSmallAsync(string url, int max, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long len && len > max) throw new InvalidDataException("Metadata exceeds size limit");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] b = new byte[8192];
        int n;
        while ((n = await input.ReadAsync(b, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > max) throw new InvalidDataException("Metadata exceeds size limit");
            ms.Write(b, 0, n);
        }
        return ms.ToArray();
    }

    public static async Task<Release> CheckAsync(CancellationToken cancellationToken = default) =>
        Parse(await GetSmallAsync(FeedUrl, 64 * 1024, cancellationToken).ConfigureAwait(false));

    public static Release Parse(ReadOnlyMemory<byte> data)
    {
        using var json = JsonDocument.Parse(data);
        JsonElement root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid release document");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate release field");
        if (Read(root, "schema_version") != "1.1" || Read(root, "app_id") != SunriseControlService.ControlId ||
            Read(root, "product_id") != "SUNRISE-FILE-MANAGER-WIN-DESKTOP" ||
            Read(root, "channel") != "stable" || Read(root, "package_kind") != "installer" ||
            Read(root, "platform") != "windows-x64" ||
            !root.TryGetProperty("official_release", out JsonElement official) || official.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("File Manager release identity mismatch");
        string version = Read(root, "latest_version", Read(root, "version"));
        if (!System.Version.TryParse(version, out _) || Read(root, "version", version) != version)
            throw new InvalidDataException("Invalid or conflicting release version");
        bool enabled = root.TryGetProperty("update_endpoint_configured", out var configured) && configured.ValueKind == JsonValueKind.True;
        return new Release(version, Read(root, "download_url"), Read(root, "download_file_name"), Read(root, "sha256"),
            root.TryGetProperty("file_size_bytes", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out long len) ? len : 0,
            Read(root, "release_notes"), Read(root, "release_url"), Read(root, "notice_url", NoticeUrl), enabled);
    }

    public static async Task<string> GetNoticeAsync(CancellationToken cancellationToken = default) =>
        new UTF8Encoding(false, true).GetString(await GetSmallAsync(NoticeUrl, 64 * 1024, cancellationToken).ConfigureAwait(false)).TrimStart('\uFEFF');

    internal static string NoticeHash(string notice) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(notice)));
    public static bool IsUnreadNotice(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice)) return false;
        try
        {
            return !File.Exists(NoticeState) || new FileInfo(NoticeState).Length > 256 ||
                !string.Equals(File.ReadAllText(NoticeState).Trim(), NoticeHash(notice), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    public static void MarkNoticeRead(string notice)
    {
        if (string.IsNullOrWhiteSpace(notice)) return;
        string temp = NoticeState + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NoticeState)!);
            File.WriteAllText(temp, NoticeHash(notice), new UTF8Encoding(false));
            File.Move(temp, NoticeState, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("Notice state could not be saved: {0}", ex.GetType().Name); }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("Notice staging cleanup: {0}", ex.GetType().Name); }
        }
    }

    public static bool IsApprovedDownload(Release release)
    {
        if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !url.AbsolutePath.StartsWith("/Peter-msk/Sunrise-File-Manager/releases/download/", StringComparison.OrdinalIgnoreCase)) return false;
        if (release.FileSize is < 1024 or > 1024L * 1024 * 800) return false;
        if (!string.Equals(release.ReleaseUrl, $"https://github.com/Peter-msk/Sunrise-File-Manager/releases/tag/v{release.Version}", StringComparison.Ordinal)) return false;
        if (!string.Equals(release.DownloadUrl, $"https://github.com/Peter-msk/Sunrise-File-Manager/releases/download/v{release.Version}/{release.FileName}", StringComparison.Ordinal)) return false;
        if (release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit)) return false;
        return string.Equals(release.FileName, $"Sunrise_File_Manager_{release.Version}_Setup.exe", StringComparison.Ordinal);
    }

    internal static bool IsApprovedRedirect(Uri next) => next.Scheme == Uri.UriSchemeHttps && next.IsDefaultPort &&
        next.UserInfo.Length == 0 && (next.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        next.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        next.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Downloads into an operation-owned directory. Caller alone decides whether to request shutdown.</summary>
    public static Task<string> DownloadAsync(Release release, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) => DownloadAsync(release, progress, cancellationToken, Client);

    internal static async Task<string> DownloadAsync(Release release, IProgress<long>? progress,
        CancellationToken cancellationToken, HttpClient transport)
    {
        if (!release.CanDownload) throw new InvalidDataException("Release metadata is incomplete or unavailable");
        if (!await DownloadGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new IOException("다른 업데이트 다운로드가 진행 중입니다.");
        string folder = Path.Combine(UpdatesRoot, "job-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(folder, release.FileName);
        string temp = target + ".download";
        bool completed = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            Directory.CreateDirectory(folder);
            Uri next = new(release.DownloadUrl);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, next);
                using var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    Uri? redirect = response.Headers.Location;
                    if (redirect is null) throw new InvalidDataException("Missing asset redirect");
                    next = redirect.IsAbsoluteUri ? redirect : new Uri(next, redirect);
                    if (!IsApprovedRedirect(next)) throw new InvalidDataException("Unexpected download host");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long header && header != release.FileSize)
                    throw new InvalidDataException("Download size mismatch");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0;
                byte[] buffer = new byte[81920];
                // R23: dispose the exclusive writer BEFORE verifying and renaming on Windows.
                await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    int n;
                    while ((n = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (total > release.FileSize) throw new InvalidDataException("Download exceeds declared size");
                        await file.WriteAsync(buffer.AsMemory(0, n), timeout.Token).ConfigureAwait(false);
                        digest.AppendData(buffer, 0, n);
                        progress?.Report(total);
                    }
                    await file.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
                if (total != release.FileSize ||
                    !string.Equals(Convert.ToHexString(digest.GetHashAndReset()), release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded release SHA256 mismatch");
                File.Move(temp, target, true);
                completed = true;
                return target;
            }
            throw new InvalidDataException("Too many download redirects");
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
                if (!completed && Directory.Exists(folder)) Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("Update staging retained: {0}", ex.GetType().Name); }
            DownloadGate.Release();
        }
    }

    private static string Read(JsonElement root, string key, string fallback = "") =>
        root.TryGetProperty(key, out JsonElement x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? fallback : fallback;
}
