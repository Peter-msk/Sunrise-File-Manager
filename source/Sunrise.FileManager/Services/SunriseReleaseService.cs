using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunrise.FileManager.Services;

/// <summary>Product-owned latest.json / notice.txt. No automatic installer execution.</summary>
public static class SunriseReleaseService
{
    // Product-owned metadata. Sunrise Control is ONLY the central operation/update policy.
    // IMPORTANT: This repository is currently private. These anonymous raw URLs return 404
    // until product metadata is published through an explicitly authorized read-only endpoint.
    // Do not embed a GitHub token or silently fall back to the Control repository.
    public const string FeedUrl = "https://raw.githubusercontent.com/Peter-msk/Sunrise-File-Manager/main/latest.json";
    public const string NoticeUrl = "https://raw.githubusercontent.com/Peter-msk/Sunrise-File-Manager/main/notice.txt";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly HttpClient Downloader = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(30) };

    public sealed record Release(string Version, string DownloadUrl, string FileName, string Sha256,
        long FileSize, string Notes, string ReleaseUrl, string NoticeUrl)
    {
        public bool IsNewer => SunriseControlService.VersionIsOlder(SunriseControlService.Version, Version);
        public bool CanDownload => IsNewer && IsApprovedDownload(this);
    }

    private static async Task<byte[]> GetSmallAsync(string url, int max)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long len && len > max) throw new InvalidDataException("Metadata exceeds size limit");
        await using var input = await response.Content.ReadAsStreamAsync();
        using var ms = new MemoryStream();
        byte[] b = new byte[8192];
        int n;
        while ((n = await input.ReadAsync(b)) > 0)
        {
            if (ms.Length + n > max) throw new InvalidDataException("Metadata exceeds size limit");
            ms.Write(b, 0, n);
        }
        return ms.ToArray();
    }

    public static async Task<Release> CheckAsync()
    {
        byte[] data = await GetSmallAsync(FeedUrl, 64 * 1024);
        using var json = JsonDocument.Parse(data);
        JsonElement root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Read(root, "product_id") != "SUNRISE-FILE-MANAGER-WIN-DESKTOP" ||
            Read(root, "channel") != "stable" || Read(root, "package_kind") != "installer" ||
            Read(root, "platform") != "windows-x64" ||
            !root.TryGetProperty("official_release", out JsonElement official) || official.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("File Manager release identity mismatch");
        string version = Read(root, "latest_version", Read(root, "version"));
        if (!System.Version.TryParse(version, out _)) throw new InvalidDataException("Invalid release version");
        return new Release(version, Read(root, "download_url"), Read(root, "download_file_name"), Read(root, "sha256"),
            root.TryGetProperty("file_size_bytes", out var size) && size.TryGetInt64(out long len) ? len : 0,
            Read(root, "release_notes"), Read(root, "release_url"), Read(root, "notice_url", NoticeUrl));
    }

    public static async Task<string> GetNoticeAsync()
    {
        byte[] bytes = await GetSmallAsync(NoticeUrl, 64 * 1024);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    public static bool IsApprovedDownload(Release release)
    {
        if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !url.AbsolutePath.StartsWith("/Peter-msk/Sunrise-File-Manager/releases/download/", StringComparison.OrdinalIgnoreCase)) return false;
        if (release.FileSize is < 1024 or > 1024 * 1024 * 800) return false;
        if (release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit)) return false;
        return string.Equals(release.FileName, $"Sunrise_File_Manager_{release.Version}_Setup.exe", StringComparison.Ordinal);
    }

    /// <summary>Download to a private local path and verify hash/size. Never run Setup automatically.</summary>
    public static async Task<string> DownloadAsync(Release release, IProgress<long>? progress = null)
    {
        if (!release.CanDownload) throw new InvalidDataException("Release metadata is incomplete or unavailable");
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sunrise", "FileManager", "Updates");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, release.FileName);
        string temp = target + ".download";
        try
        {
            // Keep redirects explicit. GitHub release assets redirect to GitHub-owned CDN hosts.
            Uri next = new(release.DownloadUrl);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, next);
                using var response = await Downloader.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    Uri? redirect = response.Headers.Location;
                    if (redirect is null) throw new InvalidDataException("Missing asset redirect");
                    next = redirect.IsAbsoluteUri ? redirect : new Uri(next, redirect);
                    if (next.Scheme != Uri.UriSchemeHttps ||
                        !(next.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                          next.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                          next.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Unexpected download host");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long header && header != release.FileSize)
                    throw new InvalidDataException("Download size mismatch");
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0;
                byte[] buffer = new byte[81920];
                int n;
                while ((n = await stream.ReadAsync(buffer)) > 0)
                {
                    total += n;
                    if (total > release.FileSize) throw new InvalidDataException("Download exceeds declared size");
                    await file.WriteAsync(buffer.AsMemory(0, n));
                    digest.AppendData(buffer, 0, n);
                    progress?.Report(total);
                }
                await file.FlushAsync();
                if (total != release.FileSize ||
                    !string.Equals(Convert.ToHexString(digest.GetHashAndReset()), release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded release SHA256 mismatch");
                File.Move(temp, target, true);
                return target;
            }
            throw new InvalidDataException("Too many download redirects");
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
    }

    private static string Read(JsonElement root, string key, string fallback = "") =>
        root.TryGetProperty(key, out JsonElement x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? fallback : fallback;
}
