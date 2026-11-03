using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// v1.7.6 DEV1: read-only workspace preview. This component deliberately contains no
/// filesystem write APIs, legal-deadline inference, installer or updater calls.
/// Template paths are proposed company conventions, NOT statutory requirements.
/// </summary>
public sealed record EiaWorkspaceRequest(
    string ProjectCode,
    string ProjectTitle,
    string ContractCode,
    string PrimaryWorkstream,
    IReadOnlyList<string> Workstreams,
    string CoreMode = "standard",
    IReadOnlyList<string>? ConfirmedOptions = null);

public sealed record EiaWorkspacePreview(
    string RootFolderName,
    string TemplateVersion,
    string CompanyPolicyVersion,
    IReadOnlyList<string> DirectoryPaths,
    string FingerprintSha256,
    string Advisory,
    bool UserApprovalRequired,
    bool SupportsFilesystemWrites);

public sealed class EiaWorkspaceDraftCatalog
{
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; set; } = "";
    [JsonPropertyName("template_version")] public string TemplateVersion { get; set; } = "";
    [JsonPropertyName("company_policy_version")] public string CompanyPolicyVersion { get; set; } = "";
    [JsonPropertyName("warning")] public string Warning { get; set; } = "";
    [JsonPropertyName("labels")] public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("core_roots")] public string[] CoreRoots { get; set; } = [];
    [JsonPropertyName("core_standard")] public string[] CoreStandard { get; set; } = [];
    [JsonPropertyName("workstream_paths")] public Dictionary<string, string[]> WorkstreamPaths { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("conditional_paths")] public Dictionary<string, string[]> ConditionalPaths { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("conditional_requires_workstreams")] public Dictionary<string, string[]> ConditionalRequiresWorkstreams { get; set; } = new(StringComparer.Ordinal);
}

public static class EiaWorkspacePreviewPlanner
{
    private const string CatalogResource = "Sunrise.FileManager.EiaWorkspace.DraftCatalog";
    private const string AssessmentRoot = "40_평가업무_법정절차";
    private static readonly char[] InvalidNameChars = "<>:\"/\\|?*".ToCharArray();
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        // Win32 recognizes superscript ¹²³ as the device-number digits too.
        // Refuse these even for a read-only preview so a Windows file create
        // cannot later reinterpret a proposed ordinary folder as a device.
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"
    };

    public static EiaWorkspaceDraftCatalog LoadDraftCatalog()
    {
        using Stream stream = typeof(EiaWorkspacePreviewPlanner).Assembly
            .GetManifestResourceStream(CatalogResource)
            ?? throw new InvalidOperationException("필수 업무 표준 초안 리소스가 없습니다.");
        var catalog = JsonSerializer.Deserialize<EiaWorkspaceDraftCatalog>(stream)
            ?? throw new InvalidDataException("업무 템플릿이 비어 있습니다.");
        if (catalog.SchemaVersion != "sunrise.eia.workspace.draft.v1"
            || !catalog.TemplateVersion.EndsWith("-DRAFT", StringComparison.Ordinal)
            || catalog.CompanyPolicyVersion != "UNAPPROVED"
            || catalog.CoreRoots.Length != 8
            || catalog.WorkstreamPaths.Count < 5)
            throw new InvalidDataException("승인 전 템플릿의 형식이 맞지 않습니다.");
        return catalog;
    }

    public static EiaWorkspacePreview BuildPreview(EiaWorkspaceRequest request)
        => BuildPreview(request, LoadDraftCatalog());

    public static EiaWorkspacePreview BuildPreview(EiaWorkspaceRequest request, EiaWorkspaceDraftCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.SchemaVersion != "sunrise.eia.workspace.draft.v1"
            || !catalog.TemplateVersion.EndsWith("-DRAFT", StringComparison.Ordinal)
            || catalog.CompanyPolicyVersion != "UNAPPROVED")
            throw new InvalidDataException("초안 검증이 완료되지 않은 템플릿입니다.");

        string code = string.IsNullOrWhiteSpace(request.ProjectCode) ? "INTERNAL" : ValidateSegment(request.ProjectCode, "사업번호", 45);
        string title = ValidateSegment(request.ProjectTitle, "사업명", 80);
        string contract = string.IsNullOrWhiteSpace(request.ContractCode) ? "INTERNAL" : ValidateSegment(request.ContractCode, "계약번호", 45);
        if (request.Workstreams is null || request.Workstreams.Count == 0)
            throw new ArgumentException("평가 업무 유형을 하나 이상 선택해야 합니다.");
        var workstreams = request.Workstreams.ToArray();
        if (workstreams.Any(x => string.IsNullOrWhiteSpace(x) || !catalog.WorkstreamPaths.ContainsKey(x)))
            throw new ArgumentException("알 수 없는 평가 업무 유형이 포함되어 있습니다.");
        if (workstreams.Length != workstreams.Distinct(StringComparer.Ordinal).Count())
            throw new ArgumentException("평가 업무 유형이 중복되었습니다.");
        if (!workstreams.Contains(request.PrimaryWorkstream, StringComparer.Ordinal))
            throw new ArgumentException("주평가 유형이 선택한 업무 유형에 포함되어야 합니다.");
        if (!catalog.Labels.TryGetValue(request.PrimaryWorkstream, out string? primaryLabel))
            throw new InvalidDataException("주평가 이름이 템플릿에서 누락되었습니다.");
        string mode = request.CoreMode;
        if (mode is not ("minimal" or "standard"))
            throw new ArgumentException("현재 개발 초안에서 지원되는 형태는 minimal·standard입니다.");

        string root = ValidateSegment(title, "폴더 이름", 220);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddPath(string path)
        {
            string normalized = ValidateRelativePath(path);
            // Conservative Windows MAX_PATH budget for a new project directory.
            // D3 must additionally re-check the real selected base directory.
            if (root.Length + 1 + normalized.Length > 240)
                throw new InvalidDataException("프로젝트 루트와 상대 폴더의 합계 경로가 너무 깁니다.");
            if (paths.Contains(normalized))
                throw new InvalidDataException($"대소문자 구별 없는 중복 폴더 경로: {normalized}");
            // The preview includes every implied parent, not only the leaf path.
            string[] parts = normalized.Split('/');
            for (int count = 1; count <= parts.Length; count++)
                paths.Add(string.Join("/", parts.Take(count)));
        }

        foreach (string path in catalog.CoreRoots) AddPath(path);
        if (mode == "standard")
            foreach (string path in catalog.CoreStandard) AddPath(path);
        foreach (string id in workstreams)
            foreach (string path in catalog.WorkstreamPaths[id])
                AddPath($"{AssessmentRoot}/{path}");

        var options = request.ConfirmedOptions?.ToArray() ?? [];
        if (options.Length != options.Distinct(StringComparer.Ordinal).Count())
            throw new ArgumentException("선택 옵션이 중복되었습니다.");
        foreach (string option in options)
        {
            if (!catalog.ConditionalPaths.TryGetValue(option, out string[]? extra)
                || !catalog.ConditionalRequiresWorkstreams.TryGetValue(option, out string[]? allowed)
                || !workstreams.Any(id => allowed.Contains(id, StringComparer.Ordinal)))
                throw new ArgumentException($"선택 유형과 맞지 않거나 존재하지 않는 옵션: {option}");
            foreach (string path in extra)
                AddPath(option == "joint_contract" ? path : $"{AssessmentRoot}/{path}");
        }
        var ordered = paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        string fingerprintInput = root + "\n" + catalog.TemplateVersion + "\n"
            + string.Join("\n", ordered);
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput))).ToLowerInvariant();
        return new(root, catalog.TemplateVersion, catalog.CompanyPolicyVersion, ordered,
            digest, catalog.Warning, true, false);
    }

    internal static string ValidateSegment(string? input, string label, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException($"{label}은(는) 필수입니다.");
        string value = input.Trim().Normalize(NormalizationForm.FormC);
        // Win32 can interpret a device stem with a trailing space before its
        // extension as a reserved device, e.g. "CON .txt" or "COM¹ .log".
        // Fail closed before publishing even a read-only plan; do not globally
        // remove internal whitespace from otherwise valid business names.
        int extensionSeparator = value.IndexOf('.');
        string deviceStem = (extensionSeparator >= 0 ? value[..extensionSeparator] : value)
            .TrimEnd(' ');
        if (value.Length > maximumLength || value is "." or ".."
            || value.EndsWith(".", StringComparison.Ordinal)
            || value.IndexOfAny(InvalidNameChars) >= 0
            || value.Any(char.IsControl)
            || ReservedNames.Contains(deviceStem))
            throw new ArgumentException($"{label}에 Windows 경로에서 사용할 수 없는 문자가 포함되었습니다.");
        return value;
    }

    internal static string ValidateRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/')
            || relative.StartsWith('\\') || relative.Contains('\\'))
            throw new InvalidDataException("템플릿 경로는 안전한 상대 경로만 허용합니다.");
        string[] parts = relative.Split('/');
        if (parts.Any(p => p is "." or ".." || p.Length == 0))
            throw new InvalidDataException("상위 경로나 빈 폴더 이름은 허용하지 않습니다.");
        string normalized = string.Join("/", parts.Select(p => ValidateSegment(p, "템플릿 폴더", 120)));
        if (normalized.Length > 220)
            throw new InvalidDataException("상대 폴더 경로가 너무 깁니다.");
        return normalized;
    }
}
