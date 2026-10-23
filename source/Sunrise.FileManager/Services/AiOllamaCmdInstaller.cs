using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sunrise.FileManager.Services;

/// <summary>
/// Partial module for AiOllamaService: local loopback verification, configuration persistence,
/// executable discovery, status probing, and pure Windows cmd.exe (never PowerShell) auto-installation.
/// </summary>
public sealed partial class AiOllamaService
{
    public static bool IsAllowedLoopback(Uri uri) =>
        uri is not null && uri.IsAbsoluteUri &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        (uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase)) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public static string ConfigFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunrise", "FileManager", "ollama_config.json");

    public static OllamaUserConfig LoadConfig() => LoadConfig(ConfigFilePath);

    internal static OllamaUserConfig LoadConfig(string path)
    {
        if (!File.Exists(path)) return new OllamaUserConfig();
        try
        {
            if (new FileInfo(path).Length > 64 * 1024) return new OllamaUserConfig();
            var cfg = JsonSerializer.Deserialize<OllamaUserConfig>(File.ReadAllText(path));
            if (cfg is null) return new OllamaUserConfig();
            if (!Uri.TryCreate(cfg.Endpoint, UriKind.Absolute, out Uri? uri) || !IsAllowedLoopback(uri))
                cfg.Endpoint = DefaultEndpoint;
            if (string.IsNullOrWhiteSpace(cfg.Model)) cfg.Model = DefaultModel;
            if (cfg.PreferredSetupModel is null || cfg.PreferredSetupModel.Length > 200) cfg.PreferredSetupModel = "";
            return cfg;
        }
        catch
        {
            return new OllamaUserConfig();
        }
    }

    public static void SaveConfig(OllamaUserConfig config) => SaveConfig(config, ConfigFilePath);

    internal static void SaveConfig(OllamaUserConfig config, string path)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastUpdatedUtc = DateTimeOffset.UtcNow;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static string? FindOllamaExecutable()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Programs", "Ollama", "ollama.exe")
        ];
        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var proc = Process.Start(CreateCmdProcessStartInfo("/d /s /c \"where ollama.exe 2>nul\""));
                if (proc is not null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);
                    string? first = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                    if (!string.IsNullOrEmpty(first) && File.Exists(first)) return first;
                }
            }
            catch { }
        }
        return null;
    }

    public async Task<OllamaStatusReport> GetStatusAsync(CancellationToken token = default)
    {
        string? exe = FindOllamaExecutable();
        bool installed = exe is not null;
        var models = new List<string>();
        bool running = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            linked.CancelAfter(TimeSpan.FromSeconds(3));
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "/api/tags"));
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                running = true;
                installed = true;
                string json = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("models", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in arr.EnumerateArray())
                    {
                        if (item.TryGetProperty("name", out JsonElement nameEl) && nameEl.GetString() is { Length: > 0 } m)
                            models.Add(m);
                    }
                }
            }
        }
        catch { }

        string effectiveModel = ResolveBestModelName(_model, models);
        string msg = running
            ? (models.Count > 0 ? $"온보드 AI(Ollama) 연결됨 · 활성 모델: {effectiveModel} (총 {models.Count}개)" : "Ollama 실행 중 · 기본 AI 모델 다운로드 필요")
            : installed
                ? "Ollama가 PC에 설치되어 있습니다. 요청 시 cmd.exe로 자동 시작됩니다."
                : "온보드 AI(Ollama) 미설치 · [AI 연결]에서 cmd 자동 설치 가능";
        return new OllamaStatusReport(installed, running, exe, _baseUri.ToString().TrimEnd('/'), effectiveModel, models, msg);
    }

    public static string ResolveBestModelName(string requestedModel, IReadOnlyList<string> installedModels)
    {
        if (installedModels is null || installedModels.Count == 0) return requestedModel;
        if (installedModels.Contains(requestedModel, StringComparer.OrdinalIgnoreCase)) return requestedModel;
        string prefix = requestedModel.Split(':')[0];
        string? prefixMatch = installedModels.FirstOrDefault(m => m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return prefixMatch ?? installedModels[0];
    }

    public async Task<OllamaStatusReport> EnsureServerRunningViaCmdAsync(CancellationToken token = default)
    {
        OllamaStatusReport status = await GetStatusAsync(token).ConfigureAwait(false);
        if (status.IsRunning || !status.IsInstalled || !OperatingSystem.IsWindows())
            return status;

        string exe = status.ExecutablePath ?? "ollama.exe";
        try
        {
            using var proc = Process.Start(CreateCmdProcessStartInfo(
                $"/d /s /c \"start \"OllamaLocalServer\" /B \"{exe}\" serve\"",
                redirectOutput: false));
            for (int attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(650, token).ConfigureAwait(false);
                status = await GetStatusAsync(token).ConfigureAwait(false);
                if (status.IsRunning) return status;
            }
        }
        catch { }
        return status;
    }

    public static IReadOnlyList<(string StepTitle, string CmdArguments)> BuildCmdInstallSteps(string model = DefaultModel)
    {
        string safeModel = Regex.Replace(string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim(), @"[^A-Za-z0-9._:-]", "");
        if (string.IsNullOrEmpty(safeModel)) safeModel = DefaultModel;

        string downloadAndInstall =
            "/d /s /c \"if exist \"%LOCALAPPDATA%\\Programs\\Ollama\\ollama.exe\" (echo [OK] Ollama가 이미 설치되어 있습니다.) else (" +
            "echo [1/3] GitHub 오픈소스 저장소(https://github.com/ollama/ollama)에서 OllamaSetup.exe 다운로드 중... && " +
            "curl.exe -fL \"" + GitHubLatestInstallerUrl + "\" -o \"%TEMP%\\OllamaSetup.exe\" && " +
            "echo [2/3] 사용자 PC에 온보드 AI (Ollama) 자동 설치 실행 중... && " +
            "\"%TEMP%\\OllamaSetup.exe\" /SILENT /NORESTART || " +
            "(echo [대체 경로] winget으로 Ollama 패키지 설치 시도 중... && winget.exe install --id Ollama.Ollama -e --accept-package-agreements --accept-source-agreements)" +
            ")\"";

        string startServer =
            "/d /s /c \"if exist \"%LOCALAPPDATA%\\Programs\\Ollama\\ollama.exe\" (" +
            "start \"OllamaLocalServer\" /B \"%LOCALAPPDATA%\\Programs\\Ollama\\ollama.exe\" serve" +
            ") else (" +
            "start \"OllamaLocalServer\" /B ollama.exe serve" +
            ")\"";

        string pullModel =
            "/d /s /c \"if exist \"%LOCALAPPDATA%\\Programs\\Ollama\\ollama.exe\" (" +
            "\"%LOCALAPPDATA%\\Programs\\Ollama\\ollama.exe\" pull " + safeModel +
            ") else (" +
            "ollama.exe pull " + safeModel +
            ")\"";

        return
        [
            ("1단계: GitHub에서 온보드 AI (Ollama) 다운로드 및 설치 (cmd.exe)", downloadAndInstall),
            ("2단계: 로컬 온보드 AI 배경 서비스 시작 (cmd.exe)", startServer),
            ($"3단계: 온보드 경량 AI 모델({safeModel}) 준비 (cmd.exe)", pullModel)
        ];
    }

    public async Task<bool> InstallAndConnectViaCmdAsync(string model, IProgress<string>? progress, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("온보드 AI(Ollama) cmd 자동 설치는 Windows 환경에서 실행됩니다.");

        var steps = BuildCmdInstallSteps(model);
        foreach (var (title, cmdArgs) in steps)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"▶ {title}");
            int exitCode = await RunCmdProcessAsync(cmdArgs, progress, token).ConfigureAwait(false);
            if (title.StartsWith("2단계", StringComparison.Ordinal))
            {
                await Task.Delay(1800, token).ConfigureAwait(false);
            }
            else if (exitCode != 0 && title.StartsWith("1단계", StringComparison.Ordinal) && FindOllamaExecutable() is null)
            {
                progress?.Report($"⚠ 설치 명령 종료 코드: {exitCode}. 네트워크 연결 또는 설치 권한을 확인하세요.");
                return false;
            }
        }

        var status = await EnsureServerRunningViaCmdAsync(token).ConfigureAwait(false);
        if (status.IsRunning)
        {
            progress?.Report($"✔ 온보드 AI (Ollama) 연결 완료! ({status.Endpoint}, 활성 모델: {status.ActiveModel})");
            return true;
        }
        progress?.Report("⚠ Ollama 설치는 진행되었으나 로컬 서버 응답을 대기 중입니다. 잠시 후 [상태 다시 확인]을 눌러주세요.");
        return status.IsInstalled;
    }

    private static async Task<int> RunCmdProcessAsync(string cmdArguments, IProgress<string>? progress, CancellationToken token)
    {
        var psi = CreateCmdProcessStartInfo(cmdArguments, redirectOutput: true, utf8Output: true);
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Report(e.Data.Trim()); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Report(e.Data.Trim()); };

        if (!process.Start()) throw new IOException("cmd.exe 프로세스를 시작하지 못했습니다.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var reg = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        await process.WaitForExitAsync(token).ConfigureAwait(false);
        return process.ExitCode;
    }
}
