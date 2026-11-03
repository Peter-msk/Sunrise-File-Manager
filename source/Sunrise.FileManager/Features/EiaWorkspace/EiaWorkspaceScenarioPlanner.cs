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
/// DEV3: reviewed-by-human, read-only S01-S48 scenario overlay on DEV2 preview.
/// A safety gate is NOT a directory and is NOT marked as satisfied by this preview.
/// No statutory applicability inference and no filesystem writes, uploads, ACL changes or deletes.
/// </summary>
public sealed class EiaScenarioDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("expected")] public string Expected { get; set; } = "";
    [JsonPropertyName("risk_guard")] public string RiskGuard { get; set; } = "";
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("folder_segments")] public string[] FolderSegments { get; set; } = [];
    [JsonPropertyName("allowed_workstreams")] public string[] AllowedWorkstreams { get; set; } = [];
    [JsonPropertyName("human_legal_review_required")] public bool HumanLegalReviewRequired { get; set; }
    [JsonPropertyName("statutory_claim")] public string StatutoryClaim { get; set; } = "";
    [JsonPropertyName("classification")] public string Classification { get; set; } = "";
}
public sealed class EiaScenarioCatalog
{
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; set; } = "";
    [JsonPropertyName("catalog_version")] public string CatalogVersion { get; set; } = "";
    [JsonPropertyName("company_policy_version")] public string CompanyPolicyVersion { get; set; } = "";
    [JsonPropertyName("effective_law_reference")] public string LawReference { get; set; } = "";
    [JsonPropertyName("base_template")] public string BaseTemplate { get; set; } = "";
    [JsonPropertyName("warning")] public string Warning { get; set; } = "";
    [JsonPropertyName("scenarios")] public EiaScenarioDefinition[] Scenarios { get; set; } = [];
}

public sealed record EiaScenarioSelection(
    string ScenarioId, string ReferenceCode, string? CaseCode = null, string? ContractCode = null,
    int RoundNumber = 0, bool UserConfirmed = false, bool HumanLegalReviewConfirmed = false);

public sealed record EiaScenarioOutcome(
    string ScenarioId, string ReferenceCode, string OutcomeKind,
    string Scope, string? CaseCode, string? ContractCode, string Explanation);

public sealed record EiaScenarioWorkspacePreview(
    EiaCaseWorkspacePreview Baseline, string ScenarioCatalogVersion,
    IReadOnlyList<string> DirectoryPaths, IReadOnlyList<EiaScenarioOutcome> Outcomes,
    string FingerprintSha256, string Advisory,
    bool UserApprovalRequired, bool SupportsFilesystemWrites);

public static class EiaWorkspaceScenarioPlanner
{
    private const string Resource = "Sunrise.FileManager.EiaWorkspace.ScenarioCatalog";
    private const string ScenarioRoot = "90_발생사건/20_상황별_검토";
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;

    public static EiaScenarioCatalog LoadDraftCatalog()
    {
        using Stream stream = typeof(EiaWorkspaceScenarioPlanner).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("S01-S48 시나리오 템플릿 리소스를 찾을 수 없습니다.");
        var catalog = JsonSerializer.Deserialize<EiaScenarioCatalog>(stream)
            ?? throw new InvalidDataException("시나리오 카탈로그가 비어 있습니다.");
        ValidateCatalog(catalog);
        return catalog;
    }

    public static EiaScenarioWorkspacePreview BuildPreview(
        EiaCaseWorkspaceRequest request, IReadOnlyList<EiaScenarioSelection> selections)
        => BuildPreview(request, selections, EiaWorkspaceScenarioPlanner.LoadDraftCatalog());

    public static EiaScenarioWorkspacePreview BuildPreview(
        EiaCaseWorkspaceRequest request, IReadOnlyList<EiaScenarioSelection> selections,
        EiaScenarioCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selections);
        ValidateCatalog(catalog);
        if (selections.Count > 300) throw new ArgumentException("상황별 미리보기 입력 최대 300건을 초과했습니다.");

        // DEV2 validates case/contract/lineage and builds an immutable, no-write base preview.
        EiaCaseWorkspacePreview baseline = EiaWorkspaceCasePlanner.BuildPreview(request);
        var definitionById = catalog.Scenarios.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var contractKeys = request.Contracts.ToDictionary(
            c => Normalize(c.ContractCode, "계약번호", 45), IgnoreCase);
        var caseKeys = request.AssessmentCases.ToDictionary(
            c => Normalize(c.CaseCode, "평가업무번호", 45), IgnoreCase);
        var paths = new HashSet<string>(baseline.DirectoryPaths, IgnoreCase);
        var events = new HashSet<string>(IgnoreCase);
        var outcomes = new List<EiaScenarioOutcome>();

        foreach (var selection in selections)
        {
            ArgumentNullException.ThrowIfNull(selection);
            if (!definitionById.TryGetValue(selection.ScenarioId, out var scenario))
                throw new ArgumentException("정의되지 않은 48개 업무상황 ID입니다: " + selection.ScenarioId);
            if (!selection.UserConfirmed)
                throw new ArgumentException("상황을 명시적으로 선택·확인해야 합니다: " + scenario.Id);
            if (scenario.HumanLegalReviewRequired && !selection.HumanLegalReviewConfirmed)
                throw new ArgumentException("법정·업무 적용 여부는 담당자 확인이 필요합니다: " + scenario.Id);
            string reference = Normalize(selection.ReferenceCode, "상황 참조번호", 45);
            string? caseCode = null;
            string? contractCode = null;
            EiaAssessmentCase? assessment = null;
            if (scenario.Scope == "case")
            {
                caseCode = Normalize(selection.CaseCode, "평가업무번호", 45);
                if (!caseKeys.TryGetValue(caseCode, out assessment))
                    throw new ArgumentException("등록되지 않은 평가업무의 상황입니다: " + caseCode);
                caseCode = Normalize(assessment.CaseCode, "평가업무번호", 45);
                contractCode = assessment.ContractCode;
                if (selection.ContractCode is not null
                    && !IgnoreCase.Equals(Normalize(selection.ContractCode, "계약번호", 45), contractCode))
                    throw new ArgumentException("평가업무와 상황의 계약 참조가 일치하지 않습니다.");
                if (scenario.Id == "S17" && string.IsNullOrWhiteSpace(assessment.RelatedCaseCode))
                    throw new ArgumentException("환경보전방안은 원평가/기존 협의와의 참조관계가 필요합니다.");
                if (scenario.Id == "S14" && (assessment.ConfirmedOptions ?? []).Any(x =>
                    x is "strategy_draft" or "environmental_draft"))
                    throw new ArgumentException("초안 생략·특례 사건과 초안 생성 옵션을 동시에 선택할 수 없습니다.");
                if (scenario.AllowedWorkstreams.Length > 0 &&
                    !scenario.AllowedWorkstreams.Contains(assessment.Workstream, StringComparer.Ordinal))
                    throw new ArgumentException("선택한 평가업무 유형에는 적용할 수 없는 상황입니다: " + scenario.Id);
            }
            else if (scenario.Scope == "contract")
            {
                contractCode = Normalize(selection.ContractCode, "계약번호", 45);
                if (!contractKeys.TryGetValue(contractCode, out var contract))
                    throw new ArgumentException("등록되지 않은 계약의 상황입니다: " + contractCode);
                contractCode = contract.ContractCode.Trim().Normalize(NormalizationForm.FormC);
                if (selection.CaseCode is not null)
                    throw new ArgumentException("계약 전용 상황에는 평가업무 식별자를 지정하지 않습니다.");
            }
            else if (selection.CaseCode is not null || selection.ContractCode is not null)
                throw new ArgumentException("사업 공통 상황에는 계약·평가업무 식별자를 지정하지 않습니다.");

            int repeatableRound = scenario.Id is "S09" or "S15" or "S32" ? selection.RoundNumber : 0;
            if (!events.Add($"{scenario.Id}|{caseCode}|{contractCode}|{reference}|{repeatableRound}"))
                throw new ArgumentException("동일 범위·상황번호의 중복 등록입니다: " + scenario.Id);
            if (scenario.Mode is not ("existing_design_intake" or "existing_correction_round")
                && scenario.Id != "S32" && selection.RoundNumber != 0)
                throw new ArgumentException("이 상황은 차수 번호를 사용하지 않습니다: " + scenario.Id);

            string root = scenario.Scope switch
            {
                "case" => CaseRoot(assessment!),
                "contract" => $"10_계약_용역관리/07_계약별/{contractCode}",
                _ => "00_사업기본정보"
            };
            string eventRoot = scenario.Scope == "case" ? $"{root}/{ScenarioRoot}"
                : $"{root}/90_업무상황";
            string eventFolder = $"{eventRoot}/{scenario.Id}_{reference}";

            string kind;
            if (scenario.Mode == "safety_gate")
            {
                kind = "FUTURE_SAFETY_GATE_NOT_VERIFIED"; // No directory is created or even proposed.
            }
            else if (scenario.Mode == "existing_design_intake")
            {
                if (selection.RoundNumber is < 1 or > 99 || assessment is null ||
                    !(request.DesignIntakes ?? []).Any(x =>
                        IgnoreCase.Equals(x.CaseCode, assessment.CaseCode)
                        && IgnoreCase.Equals(x.IntakeCode, reference)
                        && x.RevisionNumber == selection.RoundNumber))
                    throw new ArgumentException("S09는 동일 평가업무의 실제 설계자료 수령차수와 연결해야 합니다.");
                kind = "REFERENCES_DEV2_INTAKE";
            }
            else if (scenario.Mode == "existing_correction_round")
            {
                if (selection.RoundNumber is < 1 or > 99 || assessment is null ||
                    !(request.CorrectionRounds ?? []).Any(x =>
                        IgnoreCase.Equals(x.CaseCode, assessment.CaseCode)
                        && IgnoreCase.Equals(x.AgencyCode, reference)
                        && x.RoundNumber == selection.RoundNumber))
                    throw new ArgumentException("S15는 동일 기관·평가업무의 실제 보완차수와 연결해야 합니다.");
                kind = "REFERENCES_DEV2_CORRECTION";
            }
            else
            {
                if (scenario.Id == "S32")
                {
                    if (reference.Length != 4 || !int.TryParse(reference, out int year) ||
                        year is < 2000 or > 2100 || selection.RoundNumber is < 1 or > 99)
                        throw new ArgumentException("S32 연차 사건은 YYYY 연도와 01~99 조사차수가 필요합니다.");
                    eventFolder += $"_조사_{selection.RoundNumber:00}";
                }
                foreach (string suffix in scenario.FolderSegments)
                    AddWithParents(paths, baseline.RootFolderName, $"{eventFolder}/{suffix}");
                kind = "PROPOSED_DIRECTORIES_ONLY";
            }
            outcomes.Add(new(scenario.Id, reference, kind, scenario.Scope, caseCode, contractCode,
                scenario.RiskGuard + " / 회사 표준 미승인·법정 적용 여부 담당자 확인 대상"));
        }

        var orderedPaths = paths.OrderBy(x => x, IgnoreCase).ToArray();
        var orderedOutcomes = outcomes.OrderBy(x => x.ScenarioId, StringComparer.Ordinal)
            .ThenBy(x => x.CaseCode, IgnoreCase).ThenBy(x => x.ContractCode, IgnoreCase)
            .ThenBy(x => x.ReferenceCode, IgnoreCase).ToArray();
        string canonicalSelection = string.Join("\n", selections.Select(x =>
                $"{x.ScenarioId}|{x.CaseCode?.Trim().ToUpperInvariant()}|{x.ContractCode?.Trim().ToUpperInvariant()}|{x.ReferenceCode.Trim().ToUpperInvariant()}|{x.RoundNumber}|{x.UserConfirmed}|{x.HumanLegalReviewConfirmed}")
            .OrderBy(s => s, StringComparer.Ordinal));
        string seed = baseline.FingerprintSha256 + "\n" + catalog.CatalogVersion + "\n" +
            canonicalSelection + "\n" + string.Join("\n", orderedPaths);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
        return new(baseline, catalog.CatalogVersion, Array.AsReadOnly(orderedPaths),
            Array.AsReadOnly(orderedOutcomes), hash, catalog.Warning, true, false);
    }

    private static string CaseRoot(EiaAssessmentCase item)
    {
        var catalog = EiaWorkspacePreviewPlanner.LoadDraftCatalog();
        return $"40_평가업무_법정절차/{catalog.WorkstreamPaths[item.Workstream][0].Split('/')[0]}/{item.CaseCode.Trim().Normalize(NormalizationForm.FormC)}";
    }

    private static void AddWithParents(HashSet<string> paths, string projectRoot, string relative)
    {
        string safe = EiaWorkspacePreviewPlanner.ValidateRelativePath(relative);
        if (projectRoot.Length + safe.Length + 1 > 240)
            throw new InvalidDataException("전체 폴더 길이 예산을 초과했습니다. 저장위치 확정 전 수정해야 합니다.");
        string[] parts = safe.Split('/');
        for (int i = 1; i <= parts.Length; i++)
            paths.Add(string.Join("/", parts.Take(i)));
    }

    private static string Normalize(string? text, string description, int max)
        => EiaWorkspacePreviewPlanner.ValidateSegment(text, description, max);

    private static void ValidateCatalog(EiaScenarioCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.SchemaVersion != "sunrise.eia.workspace.scenarios.v1"
            || catalog.CatalogVersion != "0.3.0-DRAFT"
            || catalog.CompanyPolicyVersion != "UNAPPROVED"
            || catalog.LawReference != "PER_PROJECT_CONFIRMATION_REQUIRED"
            || catalog.BaseTemplate != "sunrise.eia.workspace.draft.v1"
            || catalog.Scenarios is null || catalog.Scenarios.Length != 48)
            throw new InvalidDataException("승인 전 48개 상황 템플릿의 형식이 다릅니다.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in catalog.Scenarios)
        {
            if (item is null || !ids.Add(item.Id)
                || item.Scope is not ("project" or "contract" or "case")
                || item.Mode is not ("folders" or "existing_design_intake" or "existing_correction_round" or "safety_gate")
                || item.StatutoryClaim != "PER_PROJECT_CONFIRMATION_REQUIRED"
                || string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.RiskGuard)
                || item.FolderSegments is null || item.AllowedWorkstreams is null
                || (item.Mode == "folders" && item.FolderSegments.Length == 0)
                || (item.Mode != "folders" && item.FolderSegments.Length != 0)
                || item.AllowedWorkstreams.Any(w => w is not ("strategy" or "environmental" or "small" or "followup" or "change")))
                throw new InvalidDataException("상황 카탈로그의 ID·범위·조건·폴더 구조가 올바르지 않습니다.");
            foreach (string segment in item.FolderSegments)
                EiaWorkspacePreviewPlanner.ValidateSegment(segment, "상황 폴더명", 120);
        }
        for (int n = 1; n <= 48; n++)
            if (!ids.Contains($"S{n:00}")) throw new InvalidDataException("누락된 상황 ID가 있습니다.");
    }
}
