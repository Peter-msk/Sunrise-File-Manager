using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>
/// One per-process authorized OAuth session. Cloud windows and the four workspace slots must share
/// the same memory-only session when auto sign-in is disabled. Passwords and client secrets are never stored.
/// </summary>
public static class CloudSessionHub
{
    public static HttpClient Http { get; } = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public static CloudOAuthService OAuth { get; } = new(Http,
        new CloudAccountVault(CloudAccountClientSettings.CredentialDirectory));

    public static event Action<CloudProvider>? ConnectionChanged;
    public static void NotifyConnectionChanged(CloudProvider provider) => ConnectionChanged?.Invoke(provider);

    public static CloudOAuthConfig Configuration(CloudProvider provider)
    {
        CloudClientEntry entry = CloudAccountClientSettings.Load(provider);
        return new CloudOAuthConfig(provider, entry.ClientId,
            provider == CloudProvider.OneDrive && entry.Tenant.Length > 0 ? entry.Tenant : null);
    }

    public static async Task<(ICloudDriveApi Api, string RootId, string? DriveId)> OpenRootAsync(
        CloudProvider provider, CancellationToken token)
    {
        CloudOAuthConfig config = Configuration(provider);
        string access = await OAuth.GetAccessTokenAsync(config, token);
        if (provider == CloudProvider.GoogleDrive)
            return (new GoogleDriveApi(Http, ct => OAuth.GetAccessTokenAsync(config, ct)), "root", null);

        // Never follow an authentication-bearing request to a non-Graph domain.
        async Task<string> ReadIdAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/drive" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 || !response.IsSuccessStatusCode)
                throw new HttpRequestException($"OneDrive 연결 오류 (HTTP {(int)response.StatusCode}).");
            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            string? id = doc.RootElement.GetProperty("id").GetString();
            return !string.IsNullOrWhiteSpace(id) ? id : throw new InvalidDataException("OneDrive 식별자가 없습니다.");
        }
        string driveId = await ReadIdAsync("");
        string rootId = await ReadIdAsync("/root");
        return (new OneDriveApi(Http, ct => OAuth.GetAccessTokenAsync(config, ct), driveId), rootId, driveId);
    }
}
