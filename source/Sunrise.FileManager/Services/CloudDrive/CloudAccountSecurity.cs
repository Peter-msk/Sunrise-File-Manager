using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunrise.FileManager.Services.CloudDrive;

public enum CloudProvider { GoogleDrive, OneDrive }

public sealed record CloudOAuthConfig(CloudProvider Provider, string ClientId, string? MicrosoftTenant = null)
{
    public string Authority => Provider == CloudProvider.GoogleDrive
        ? "https://accounts.google.com/o/oauth2/v2/auth"
        : $"https://login.microsoftonline.com/{(string.IsNullOrWhiteSpace(MicrosoftTenant) ? "common" : MicrosoftTenant)}/oauth2/v2.0/authorize";
    public string TokenEndpoint => Provider == CloudProvider.GoogleDrive
        ? "https://oauth2.googleapis.com/token"
        : $"https://login.microsoftonline.com/{(string.IsNullOrWhiteSpace(MicrosoftTenant) ? "common" : MicrosoftTenant)}/oauth2/v2.0/token";
    public string Scope => Provider == CloudProvider.GoogleDrive
        ? "https://www.googleapis.com/auth/drive"
        : "offline_access User.Read Files.ReadWrite";
}

public sealed record CloudOAuthToken(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// User opt-in auto sign-in vault. Provider passwords are NEVER collected or stored.
/// Each saved refresh token uses a fresh 256-bit AES-GCM key plus random 96-bit nonce;
/// the key is protected independently with Windows CurrentUser DPAPI.
/// .sfcvault is a versioned format, not a claim that a file extension is encryption.
/// No embedded master key, homemade cipher table or recoverable hardcoded password.
/// </summary>
public sealed class CloudAccountVault
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SFMVLT01");
    private readonly string _directory;
    public CloudAccountVault(string directory) => _directory = Path.GetFullPath(directory);
    private string PathFor(CloudProvider provider) => Path.Combine(_directory, provider + ".sfcvault");
    private static byte[] Entropy(CloudProvider provider) => SHA256.HashData(Encoding.UTF8.GetBytes("Sunrise.FileManager.CloudVault.v2:" + provider));
    public bool HasSavedAccount(CloudProvider provider) => File.Exists(PathFor(provider));

    public void Save(CloudProvider provider, string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) throw new ArgumentException("갱신 토큰이 없습니다.");
        Directory.CreateDirectory(_directory);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = Encoding.UTF8.GetBytes(refreshToken);
        byte[] ciphertext = new byte[plain.Length];
        byte[] tag = new byte[16];
        byte[] wrapped = Array.Empty<byte>();
        try
        {
            if (plain.Length > 8192) throw new InvalidDataException("인증 정보 크기가 허용 범위를 초과했습니다.");
            using (var aes = new AesGcm(key, 16))
                aes.Encrypt(nonce, plain, ciphertext, tag, Entropy(provider));
            wrapped = ProtectedData.Protect(key, Entropy(provider), DataProtectionScope.CurrentUser);
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write((byte)provider);
                writer.Write(wrapped.Length);
                writer.Write(ciphertext.Length);
                writer.Write(nonce);
                writer.Write(tag);
                writer.Write(wrapped);
                writer.Write(ciphertext);
            }
            string path = PathFor(provider), temp = path + ".tmp";
            try { File.WriteAllBytes(temp, ms.ToArray()); File.Move(temp, path, overwrite: true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(wrapped);
        }
    }
    public string? Load(CloudProvider provider)
    {
        string path = PathFor(provider);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("인증정보 파일 형식이 올바르지 않습니다.");
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic) || reader.ReadByte() != (byte)provider)
            throw new InvalidDataException("인증정보 파일 식별자가 유효하지 않습니다.");
        int wrappedLength = reader.ReadInt32(), cipherLength = reader.ReadInt32();
        if (wrappedLength is < 1 or > 2048 || cipherLength is < 1 or > 8192)
            throw new InvalidDataException("인증정보 파일 길이가 유효하지 않습니다.");
        byte[] nonce = reader.ReadBytes(12), tag = reader.ReadBytes(16);
        byte[] wrapped = reader.ReadBytes(wrappedLength), encrypted = reader.ReadBytes(cipherLength);
        if (nonce.Length != 12 || tag.Length != 16 || wrapped.Length != wrappedLength ||
            encrypted.Length != cipherLength || stream.Position != stream.Length)
            throw new InvalidDataException("인증정보 파일이 손상되었거나 변조되었습니다.");
        byte[] key = ProtectedData.Unprotect(wrapped, Entropy(provider), DataProtectionScope.CurrentUser);
        byte[] plain = new byte[cipherLength];
        try
        {
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(nonce, encrypted, tag, plain, Entropy(provider));
            return Encoding.UTF8.GetString(plain);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    public void Disconnect(CloudProvider provider)
    {
        string path = PathFor(provider);
        if (File.Exists(path)) File.Delete(path);
        // R12 source preview used .bin DPAPI storage. Never silently reuse its credentials.
        string legacy = Path.Combine(_directory, provider + ".bin");
        if (File.Exists(legacy)) File.Delete(legacy);
    }
}

/// <summary>Installed-app authorization-code flow: system browser, loopback redirect, state and PKCE verification.</summary>
public sealed class CloudOAuthService
{
    private readonly HttpClient _http;
    private readonly CloudAccountVault _vault;
    private readonly Dictionary<CloudProvider, CloudOAuthToken> _session = [];
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    public CloudOAuthService(HttpClient http, CloudAccountVault vault) { _http = http; _vault = vault; }
    public bool HasSavedAccount(CloudProvider provider) => _vault.HasSavedAccount(provider);
    public bool HasSession(CloudProvider provider) => _session.ContainsKey(provider);
    public void SetRememberSignIn(CloudProvider provider, bool remember)
    {
        if (!remember) { _vault.Disconnect(provider); return; }
        if (!_session.TryGetValue(provider, out var session) || string.IsNullOrWhiteSpace(session.RefreshToken))
            throw new InvalidOperationException("먼저 공식 로그인으로 계정을 연결해야 자동 로그인을 사용할 수 있습니다.");
        _vault.Save(provider, session.RefreshToken);
    }

    public static string PkceChallenge(string verifier) => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string RandomUrlSafe(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string FormUrl(IEnumerable<KeyValuePair<string,string>> values) => string.Join("&", values.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
    private static string ValidateClientId(CloudOAuthConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ClientId) || config.ClientId.Length > 300 || config.ClientId.Any(char.IsControl))
            throw new ArgumentException("공급자 앱 등록 Client ID를 설정해야 합니다.");
        if (config.MicrosoftTenant is { Length: > 0 } tenant && (tenant.Length > 100 || !tenant.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '.')))
            throw new ArgumentException("Microsoft 테넌트 ID가 올바르지 않습니다.");
        return config.ClientId;
    }
    private static void EnsureTokenHost(Uri uri, CloudProvider provider)
    {
        string expected = provider == CloudProvider.GoogleDrive ? "oauth2.googleapis.com" : "login.microsoftonline.com";
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("인증 서버 주소가 신뢰할 수 없는 도메인입니다.");
    }
    public async Task<CloudOAuthToken> ConnectAsync(CloudOAuthConfig config, Action<Uri> openBrowser, bool rememberSignIn, CancellationToken cancellationToken)
    {
        string clientId = ValidateClientId(config);
        string state = RandomUrlSafe(24), verifier = RandomUrlSafe(48);
        using var listener = new HttpListener();
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        // Microsoft desktop app registration: http://localhost (dynamic port).
        string redirect = config.Provider == CloudProvider.OneDrive
            ? $"http://localhost:{port}/"
            : $"http://127.0.0.1:{port}/oauth/callback/";
        listener.Prefixes.Add(redirect);
        listener.Start();
        var authorize = new List<KeyValuePair<string,string>> {
            new("client_id", clientId), new("redirect_uri", redirect), new("response_type", "code"),
            new("scope", config.Scope), new("state", state), new("code_challenge", PkceChallenge(verifier)),
            new("code_challenge_method", "S256") };
        if (config.Provider == CloudProvider.GoogleDrive)
        {
            authorize.Add(new("access_type", "offline"));
            if (rememberSignIn) authorize.Add(new("prompt", "consent"));
        }
        Uri browserUri = new(config.Authority + "?" + FormUrl(authorize));
        openBrowser(browserUri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
        string? receivedState = context.Request.QueryString["state"];
        string? code = context.Request.QueryString["code"];
        bool valid = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(receivedState ?? ""), Encoding.UTF8.GetBytes(state));
        byte[] response = Encoding.UTF8.GetBytes(valid && !string.IsNullOrEmpty(code)
            ? "Account authorization received. Return to Sunrise File Manager." : "Account connection failed.");
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = response.Length;
        await context.Response.OutputStream.WriteAsync(response, timeout.Token).ConfigureAwait(false);
        context.Response.Close();
        if (!valid || string.IsNullOrEmpty(code)) throw new UnauthorizedAccessException("OAuth 응답 상태가 유효하지 않습니다.");
        var result = await ExchangeAsync(config, new Dictionary<string,string> {
            ["client_id"] = clientId, ["grant_type"] = "authorization_code", ["code"] = code,
            ["code_verifier"] = verifier, ["redirect_uri"] = redirect
        }, timeout.Token).ConfigureAwait(false);
        if (rememberSignIn && string.IsNullOrWhiteSpace(result.RefreshToken))
            throw new UnauthorizedAccessException("자동 로그인에 필요한 갱신 토큰을 받지 못했습니다. 계정 동의를 다시 진행하세요.");
        if (rememberSignIn) _vault.Save(config.Provider, result.RefreshToken);
        else _vault.Disconnect(config.Provider);
        _session[config.Provider] = result;
        return result with { RefreshToken = "" };
    }

    /// <summary>Use memory session first. Saved refresh credentials are loaded only after explicit opt-in.</summary>
    public async Task<string> GetAccessTokenAsync(CloudOAuthConfig config, CancellationToken token)
    {
        await _tokenGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_session.TryGetValue(config.Provider, out var current) && current.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(45))
                return current.AccessToken;
            string refresh = current?.RefreshToken ?? "";
            if (string.IsNullOrWhiteSpace(refresh)) refresh = _vault.Load(config.Provider) ?? "";
            if (string.IsNullOrWhiteSpace(refresh)) throw new UnauthorizedAccessException("먼저 클라우드 계정에 로그인하세요.");
            var result = await ExchangeAsync(config, new Dictionary<string,string> {
                ["client_id"] = ValidateClientId(config), ["grant_type"] = "refresh_token", ["refresh_token"] = refresh
            }, token).ConfigureAwait(false);
            string rotated = string.IsNullOrWhiteSpace(result.RefreshToken) ? refresh : result.RefreshToken;
            if (_vault.HasSavedAccount(config.Provider)) _vault.Save(config.Provider, rotated);
            _session[config.Provider] = result with { RefreshToken = rotated };
            return result.AccessToken;
        }
        finally { _tokenGate.Release(); }
    }
    public async Task<CloudOAuthToken> RefreshAsync(CloudOAuthConfig config, CancellationToken token)
    {
        // Preserves the R12 service entrypoint without forcing credentials onto disk.
        if (_session.TryGetValue(config.Provider, out var current))
            _session[config.Provider] = current with { ExpiresAt = DateTimeOffset.MinValue };
        string access = await GetAccessTokenAsync(config, token).ConfigureAwait(false);
        return _session[config.Provider] with { AccessToken = access, RefreshToken = "" };
    }
    public void DisableAutoSignIn(CloudProvider provider) => _vault.Disconnect(provider);
    public void Disconnect(CloudProvider provider)
    {
        _session.Remove(provider);
        _vault.Disconnect(provider);
        // Account access is revoked locally. Do not retain old signed upload capabilities.
        try { CloudUploadResumeStore.DiscardAllForProvider(provider); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The OAuth credentials are already disconnected. Never reveal signed URLs in logs.
            System.Diagnostics.Trace.TraceWarning("Encrypted cloud upload checkpoint cleanup requires manual review.");
        }
    }
    private async Task<CloudOAuthToken> ExchangeAsync(CloudOAuthConfig config, Dictionary<string,string> form, CancellationToken token)
    {
        Uri uri = new(config.TokenEndpoint);
        EnsureTokenHost(uri, config.Provider);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400) throw new InvalidDataException("예상치 못한 인증 리다이렉트입니다.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"클라우드 계정 인증 실패 (HTTP {(int)response.StatusCode}).");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        var root = json.RootElement;
        string access = root.TryGetProperty("access_token", out var a) ? a.GetString() ?? "" : "";
        string refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? "" : "";
        int seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var parsed) ? parsed : 3600;
        if (string.IsNullOrWhiteSpace(access)) throw new InvalidDataException("인증 응답에 액세스 토큰이 없습니다.");
        return new CloudOAuthToken(access, refresh, DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, seconds - 120)));
    }
}
