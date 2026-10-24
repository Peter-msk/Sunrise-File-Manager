using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Sunrise.FileManager.Services;

/// <summary>
/// Sunrise Control schema v1. Policy is a central operational switch, NOT a signed license or security lease.
/// No document names, user credentials or file paths are transmitted.
/// </summary>
public static class SunriseControlService
{
    public const string ControlId = "sunrise_file_manager";
    public const string Version = "1.5.5";
    public const string Url = "https://raw.githubusercontent.com/Peter-msk/Sunrise_Control/main/control.txt";
    private static readonly string CachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunrise", "FileManager", "control-v1-cache.json");
    private static readonly HttpClient Client = MakeClient();

    public sealed record Policy(string Operation, string Update, string MinimumVersion, string Message, string Source)
    {
        public bool BlocksEntry => Operation is "block" or "maintenance";
        public bool RequiresUpdate => Update == "required" && VersionIsOlder(Version, MinimumVersion);
    }

    private sealed record CacheRecord(string Content, DateTimeOffset FetchedUtc);

    private static HttpClient MakeClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    public static bool VersionIsOlder(string current, string minimum) =>
        System.Version.TryParse(current, out var c) && System.Version.TryParse(minimum, out var m) && c < m;

    public static Task<Policy> CheckAsync(CancellationToken cancellationToken = default) =>
        CheckAsync(Client, CachePath, DateTimeOffset.UtcNow, cancellationToken);

    internal static async Task<Policy> CheckAsync(HttpClient transport, string cachePath, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await transport.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 131072) throw new InvalidDataException("Control too large");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > 131072) throw new InvalidDataException("Control too large");
                buffer.Write(chunk, 0, count);
            }
            string content = new UTF8Encoding(false, true).GetString(buffer.ToArray());
            var policy = Parse(content, "online", out int ttl);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                string tmp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(tmp, JsonSerializer.Serialize(new CacheRecord(content, now)), Encoding.UTF8);
                    File.Move(tmp, cachePath, true);
                }
                finally { if (File.Exists(tmp)) File.Delete(tmp); }
            }
            catch (IOException) { /* Remote policy is still valid; cache persistence is optional. */ }
            catch (UnauthorizedAccessException) { }
            return policy;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is (HttpRequestException or OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or DecoderFallbackException))
        {
            Policy fallback = new("allow", "allow", "", "", "fallback");
            try
            {
                if (File.Exists(cachePath) && new FileInfo(cachePath).Length <= 300_000)
                {
                    var cached = JsonSerializer.Deserialize<CacheRecord>(File.ReadAllText(cachePath, Encoding.UTF8));
                    if (cached is not null)
                    {
                        var parsed = Parse(cached.Content, "cache", out int ttl);
                        var age = now - cached.FetchedUtc;
                        if (age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(ttl)) return parsed;
                        // The cache is too old for a live product policy. Only use its explicit
                        // global fallback state, as required by the Control v1 wire contract.
                        fallback = ParseFallback(cached.Content);
                    }
                }
            }
            catch (Exception cacheException) when (cacheException is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) { }
            return fallback;
        }
    }

    /// <summary>Strictly parse schema v1. Invalid remote payloads cannot become an arbitrary allow policy.</summary>
    public static Policy Parse(string body, string source, out int cacheTtlSeconds)
    {
        var values = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        foreach (string raw in body.TrimStart('\uFEFF').Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (section.Length == 0) throw new InvalidDataException("Empty control section");
                if (!values.TryAdd(section, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Duplicate control section");
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals <= 0 || section.Length == 0) throw new InvalidDataException("Invalid control entry");
            string key = line[..equals].Trim();
            if (!values[section].TryAdd(key, line[(equals + 1)..].Trim()))
                throw new InvalidDataException("Duplicate control key");
        }
        if (!values.TryGetValue("global", out var global) || Get(global, "schema_version") != "1")
            throw new InvalidDataException("Unsupported control schema");
        var product = values.TryGetValue("product." + ControlId, out var p) ? p : new Dictionary<string, string>();
        string operation = Get(product, "operation", Get(global, "default_operation", "allow")).ToLowerInvariant();
        string update = Get(product, "update", Get(global, "default_update", "allow")).ToLowerInvariant();
        if (operation is not ("allow" or "maintenance" or "block") || update is not ("allow" or "block" or "required"))
            throw new InvalidDataException("Unsupported control state");
        string minimum = Get(product, "minimum_version");
        if (minimum.Length != 0 && !System.Version.TryParse(minimum, out _)) throw new InvalidDataException("Invalid minimum version");
        cacheTtlSeconds = int.TryParse(Get(global, "cache_ttl_seconds"), NumberStyles.None, CultureInfo.InvariantCulture, out int ttl)
            ? Math.Clamp(ttl, 60, 86400) : 21600;
        return new Policy(operation, update, minimum, Get(product, "message"), source);
    }

    private static Policy ParseFallback(string content)
    {
        var global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inGlobal = false;
        foreach (string raw in content.TrimStart('\uFEFF').Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { inGlobal = line.Equals("[global]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inGlobal || line.StartsWith('#') || line.StartsWith(';')) continue;
            int eq = line.IndexOf('=');
            if (eq > 0) global[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        string operation = Get(global, "fallback_operation", "allow").ToLowerInvariant();
        string update = Get(global, "fallback_update", "allow").ToLowerInvariant();
        if (operation is not ("allow" or "maintenance" or "block") || update is not ("allow" or "block" or "required"))
            return new Policy("allow", "allow", "", "", "fallback");
        // A fallback update=required without an authoritative minimum_version is not forced.
        return new Policy(operation, update, "", "", "fallback");
    }

    private static string Get(Dictionary<string, string> values, string key, string fallback = "") =>
        values.TryGetValue(key, out string? value) ? value : fallback;
}
