using System.IO;
using System.Text.Json;

namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>Non-secret public OAuth client identifiers only; no username, password or token.</summary>
public sealed record CloudClientEntry(string ClientId = "", string Tenant = "");
public sealed class CloudAccountClientSettings
{
    public static string BaseDirectory => Sunrise.FileManager.Services.SunriseGroupPaths.Local("Cloud");
    public static string CredentialDirectory => Path.Combine(BaseDirectory, "Credentials");
    private static string ConfigPath => Path.Combine(BaseDirectory, "clients.json");
    public static CloudClientEntry Load(CloudProvider provider)
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new();
            var values = JsonSerializer.Deserialize<Dictionary<string, CloudClientEntry>>(File.ReadAllText(ConfigPath));
            return values?.GetValueOrDefault(provider.ToString()) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public static void Save(CloudProvider provider, CloudClientEntry entry)
    {
        if (entry.ClientId.Length > 300 || entry.ClientId.Any(char.IsControl) ||
            entry.Tenant.Length > 100 || entry.Tenant.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '.')))
            throw new ArgumentException("OAuth 앱 설정값이 유효하지 않습니다.");
        Directory.CreateDirectory(BaseDirectory);
        var values = new Dictionary<string, CloudClientEntry>();
        try
        {
            if (File.Exists(ConfigPath))
                values = JsonSerializer.Deserialize<Dictionary<string, CloudClientEntry>>(File.ReadAllText(ConfigPath)) ?? values;
        }
        catch (JsonException) { /* An invalid non-secret config cannot block fresh setup. */ }
        values[provider.ToString()] = entry;
        string temp = ConfigPath + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(values)); File.Move(temp, ConfigPath, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
