using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Sunrise.FileManager.Services;

/// <summary>
/// Onboard local AI integration powered by open-source Ollama from GitHub (https://github.com/ollama/ollama).
/// Runs 100% locally on the user's PC (127.0.0.1:11434) with zero cloud transmission.
/// Automatic installation and service management strictly use Windows cmd.exe (see AiOllamaCmdInstaller.cs).
/// </summary>
public sealed partial class AiOllamaService : IAiIntentAdapter, IDisposable
{
    public const string GitHubRepoUrl = "https://github.com/ollama/ollama";
    public const string GitHubLatestInstallerUrl = "https://github.com/ollama/ollama/releases/latest/download/OllamaSetup.exe";
    public const string DefaultEndpoint = "http://127.0.0.1:11434";
    public const string DefaultModel = "qwen2.5:1.5b";
    public static readonly string[] RecommendedModels = ["qwen2.5:1.5b", "llama3.2:1b", "gemma2:2b", "qwen2.5:3b"];

    public const string FeatureDescription =
        "온보드 AI (Ollama)는 깃허브(GitHub: https://github.com/ollama/ollama)에 공개된 오픈소스 로컬 AI 엔진을 " +
        "사용자 PC 자체에 직접 설치하여 구동하는 온디바이스 AI 기능입니다.\n\n" +
        "• 100% 내 PC 내부 실행 (외부 클라우드 서버·Gemini 미사용): 파일명, 폴더 구조, 문서 본문이 외부 서버로 절대 전송되지 않습니다.\n" +
        "• 자연어 파일 검색 및 스마트 정리: 일상적인 한국어 명령으로 파일 검색, 확장자·유형·날짜·크기·주제별 자동 분류 및 이동/복사/일괄 이름변경 계획을 수립합니다.\n" +
        "• 로컬 문서 요약 및 스마트 메모 연동: 선택한 문서(PDF, HWPX, DOCX, TXT, MD 등)의 핵심 내용을 내 PC의 온보드 AI가 요약하여 #memo 또는 마크다운 파일로 저장합니다.\n" +
        "• CMD 기반 원클릭 자동 설치: '설치 및 연결'을 선택하면 Windows 기본 명령 프롬프트(cmd.exe)만을 사용하여 깃허브 공식 릴리스에서 Ollama와 기본 경량 AI 모델을 자동으로 설치합니다.";

    private const int MaxResponseBytes = 256 * 1024;
    private readonly Uri _baseUri;
    private readonly string _model;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public AiOllamaService(Uri? baseUri = null, string? model = null, HttpClient? client = null)
    {
        Uri resolved = baseUri ?? new Uri(DefaultEndpoint);
        if (!IsAllowedLoopback(resolved))
            throw new ArgumentException("온보드 AI(Ollama)는 사용자 PC 내부 루프백 주소(127.0.0.1 / localhost / [::1])만 허용합니다.");
        _baseUri = resolved;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        if (client is null)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
            _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            _ownsClient = true;
        }
        else
        {
            _client = client;
            _ownsClient = false;
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    internal static ProcessStartInfo CreateCmdProcessStartInfo(string arguments, bool redirectOutput = true, bool utf8Output = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            CreateNoWindow = true
        };
        if (redirectOutput && utf8Output)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }
        return psi;
    }

    public async Task<AiNaturalLanguageService.Intent> InterpretAsync(string request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request) || request.Length is < 2 or > 4000)
            throw new ArgumentException("명령을 2~4,000자로 입력하세요.");
        AiModelIntentSafety.CheckRequest(request);

        string activeModel = _model;
        if (_ownsClient)
        {
            var status = await EnsureServerRunningViaCmdAsync(token).ConfigureAwait(false);
            activeModel = status.ActiveModel;
        }

        string systemPrompt =
            "You are the onboard AI file management intent parser for Sunrise File Manager. " +
            "Analyze the Korean user command and output ONLY a valid JSON object matching this exact schema: " +
            "{\"schema_version\":1,\"intent\":{\"action\":\"search|copy|move|rename|recycle\"," +
            "\"extension\":null or lowercase extension like \".pdf\",\".docx\",\".hwpx\",\".xlsx\",\".pptx\",\".txt\",\".csv\",\".jpg\",\".png\",\".zip\",\".mp4\",\".mp3\"," +
            "\"category\":null or \"문서\"|\"이미지\"|\"동영상\"|\"오디오\"|\"압축파일\"|\"코드_설정\"," +
            "\"name_contains\":null or string," +
            "\"modified_within_days\":null or integer (e.g. 오늘=1, 어제=2, 이번주=7, 이번달=30)," +
            "\"grouping\":\"\" or \"extension\"|\"category\"|\"date\"|\"year\"|\"size\"," +
            "\"content_contains\":null or string," +
            "\"rename_prefix\":null or string," +
            "\"rename_suffix\":null or string}}";

        var payload = new
        {
            model = activeModel,
            system = systemPrompt,
            prompt = request,
            format = "json",
            stream = false,
            options = new { temperature = 0.0 }
        };

        string rawJson = await SendGenerateRequestAsync(payload, token).ConfigureAwait(false);
        var intent = ParseIntentResponse(rawJson);
        AiModelIntentSafety.Validate(request, intent);
        return intent;
    }

    public async Task<AiPlan> CreateSmartPlanAsync(AiScope scope, string userCommand, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(userCommand) || userCommand.Length is < 2 or > 4000)
            throw new ArgumentException("명령을 2~4,000자로 입력하세요.");
        AiModelIntentSafety.CheckRequest(userCommand);

        string activeModel = _model;
        if (_ownsClient)
        {
            var status = await EnsureServerRunningViaCmdAsync(token).ConfigureAwait(false);
            activeModel = status.ActiveModel;
        }

        var entriesJson = JsonSerializer.Serialize(scope.Entries.Take(200).Select(e => new
        {
            entry_id = e.Id,
            name = e.Name,
            directory = e.Directory
        }));
        bool hasDestination = scope.Targets.Any(t => t.Id == "destination");

        string systemPrompt =
            "You are the onboard AI file organization planner for Sunrise File Manager. " +
            "Create a safe JSON file operation plan for the provided scope_id and entries. " +
            "Output ONLY valid JSON with this exact schema: " +
            "{\"schema_version\":1,\"scope_id\":\"" + scope.Id + "\",\"operations\":[" +
            "{\"operation\":\"mkdir|rename|copy|move\",\"entry_id\":null or string,\"new_name\":null or string,\"target_id\":null or \"current\"|\"destination\",\"subfolder\":null or string}" +
            "]}. " +
            "Rules: 1) For rename, set operation='rename', entry_id, and new_name (preserve file extension). " +
            "2) For grouping into folders under target_id ('destination' if available, else 'current'), emit 'mkdir' operations first for each new folder name, then 'copy' or 'move' operations with subfolder set to that folder name.";

        string prompt =
            $"scope_id: {scope.Id}\n" +
            $"available_targets: {(hasDestination ? "current, destination" : "current")}\n" +
            $"entries: {entriesJson}\n" +
            $"user_request: {userCommand}";

        var payload = new
        {
            model = activeModel,
            system = systemPrompt,
            prompt,
            format = "json",
            stream = false,
            options = new { temperature = 0.0 }
        };

        string rawJson = await SendGenerateRequestAsync(payload, token).ConfigureAwait(false);
        string cleanJson = ExtractJsonObject(rawJson);
        return AiPlanService.Parse(cleanJson);
    }

    public async Task<string> AskLocalAssistantAsync(string userQuestion, string localContextText, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(userQuestion))
            throw new ArgumentException("질문이나 요약 요청을 입력하세요.");
        string activeModel = _model;
        if (_ownsClient)
        {
            var status = await EnsureServerRunningViaCmdAsync(token).ConfigureAwait(false);
            activeModel = status.ActiveModel;
        }
        string boundedContext = localContextText.Length > 12_000 ? localContextText[..12_000] : localContextText;
        var payload = new
        {
            model = activeModel,
            system = "당신은 사용자 PC에 설치된 오픈소스 온보드 AI(Ollama) 파일·문서 도우미입니다. 외부 서버 전송 없이 제공된 로컬 파일 정보나 문서 본문만을 바탕으로 한국어로 핵심을 명확하고 간결하게 답변하세요.",
            prompt = $"[로컬 컨텍스트]\n{boundedContext}\n\n[사용자 요청]\n{userQuestion}",
            stream = false,
            options = new { temperature = 0.2 }
        };
        return await SendGenerateRequestAsync(payload, token).ConfigureAwait(false);
    }

    private async Task<string> SendGenerateRequestAsync(object payload, CancellationToken token)
    {
        string json = JsonSerializer.Serialize(payload);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "/api/generate"))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        linked.CancelAfter(TimeSpan.FromSeconds(45));

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new IOException("온보드 AI(Ollama) 로컬 서버(127.0.0.1:11434)에 연결할 수 없습니다. 상단 [AI 연결] 버튼을 눌러 Ollama를 설치하거나 시작하세요. (" + ex.Message + ")", ex);
        }

        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new IOException("온보드 AI(Ollama) 리다이렉트 응답은 허용되지 않습니다.");
            if (!response.IsSuccessStatusCode)
                throw new IOException($"온보드 AI(Ollama) 응답 오류 (HTTP {(int)response.StatusCode}). 모델({_model})이 설치되어 있는지 [AI 연결]에서 확인하세요.");

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            byte[] buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), linked.Token).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > MaxResponseBytes)
                    throw new InvalidDataException("온보드 AI 응답 크기가 허용 상한을 초과했습니다.");
                memory.Write(buffer, 0, read);
            }

            string body = Encoding.UTF8.GetString(memory.ToArray());
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("response", out JsonElement respProp) && respProp.ValueKind == JsonValueKind.String)
                return respProp.GetString() ?? "";
            return body;
        }
    }

    public static string ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "{}";
        string trimmed = text.Trim();
        int firstBrace = trimmed.IndexOf('{');
        int lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            return trimmed[firstBrace..(lastBrace + 1)];
        return trimmed;
    }

    public static AiNaturalLanguageService.Intent ParseIntentResponse(string json)
    {
        using var document = JsonDocument.Parse(ExtractJsonObject(json), new JsonDocumentOptions { MaxDepth = 6 });
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("response", out JsonElement inner) && inner.ValueKind == JsonValueKind.String)
        {
            string innerClean = ExtractJsonObject(inner.GetString() ?? "{}");
            using var nested = JsonDocument.Parse(innerClean, new JsonDocumentOptions { MaxDepth = 6 });
            return ParseIntentElement(nested.RootElement);
        }
        return ParseIntentElement(root);
    }

    private static AiNaturalLanguageService.Intent ParseIntentElement(JsonElement root)
    {
        RequireObject(root, "schema_version", "intent");
        if (root.GetProperty("schema_version").ValueKind != JsonValueKind.Number ||
            !root.GetProperty("schema_version").TryGetInt32(out int schemaVersion) || schemaVersion != 1)
            throw new InvalidDataException("지원하지 않는 온보드 AI intent 스키마 버전입니다.");
        JsonElement details = root.GetProperty("intent");
        RequireObject(details, "action", "extension", "category", "name_contains", "modified_within_days",
            "grouping", "content_contains", "rename_prefix", "rename_suffix");
        string action = StringValue(details, "action") ?? "";
        string grouping = StringValue(details, "grouping") ?? "";
        int? days = null;
        var daysElement = details.GetProperty("modified_within_days");
        if (daysElement.ValueKind == JsonValueKind.Number)
        {
            if (!daysElement.TryGetInt32(out int result)) throw new InvalidDataException("수정일 범위는 정수여야 합니다.");
            days = result;
        }
        else if (daysElement.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("수정일 조건 형식이 잘못되었습니다.");

        return new AiNaturalLanguageService.Intent(
            action,
            StringValue(details, "extension"),
            StringValue(details, "category"),
            StringValue(details, "name_contains"),
            days,
            grouping,
            "온보드 AI(Ollama) 분석 · " + action + (grouping.Length > 0 ? " · " + grouping + "별" : ""),
            StringValue(details, "content_contains"),
            StringValue(details, "rename_prefix"),
            StringValue(details, "rename_suffix"));
    }

    private static string? StringValue(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"온보드 AI {name} 형식 오류");
        string text = value.GetString() ?? "";
        if (text.Length > 120 || text.Any(char.IsControl)) throw new InvalidDataException($"온보드 AI {name} 값 오류");
        return text;
    }

    private static void RequireObject(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("온보드 AI 응답이 JSON 객체가 아닙니다.");
        var permitted = keys.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!permitted.Contains(property.Name) || !seen.Add(property.Name))
                throw new InvalidDataException("온보드 AI 응답에 미허용 또는 중복 필드가 포함되어 있습니다.");
        if (seen.Count != permitted.Count) throw new InvalidDataException("온보드 AI 응답 필수 필드가 누락되었습니다.");
    }
}
