using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// DEV1 v1.7.9: WPF-independent presentation adapter, always UNAPPROVED/read-only.
/// It neither opens filesystem paths nor invokes any creation engine or draft storage.
/// It deliberately does not infer legal requirements, completion or current ACL state.
/// </summary>
public sealed record EiaWorkspaceUiEntry(string RelativePath, string CategoryLabel,
    string OwnerLabel, string Basis, bool IsParent);
public sealed record EiaWorkspaceUiPreview(
    string RootFolderName, IReadOnlyList<EiaWorkspaceUiEntry> Entries,
    IReadOnlyList<string> UnverifiedGates, IReadOnlyList<string> ExcludedTypes,
    string FingerprintSha256, string CompanyPolicyVersion, int LeafCount,
    string Warning, bool UserApprovalRequired, bool SupportsFilesystemWrites,
    IReadOnlyList<EiaWorkspaceUiScenarioOutcome>? ScenarioOutcomes = null);

public sealed record EiaWorkspaceUiContractInput(string Code, string Description);
public sealed record EiaWorkspaceUiCaseInput(string Code, string ContractCode,
    string Workstream, string? RelatedCaseCode = null);
public sealed record EiaWorkspaceUiIntakeInput(string CaseCode, string ReferenceCode, string Round);
public sealed record EiaWorkspaceUiCorrectionInput(string CaseCode, string AgencyCode, string Round);
public sealed record EiaWorkspaceUiFollowupInput(string CaseCode, string Year, string Round);
public sealed record EiaWorkspaceUiDetailedInput(string ProjectCode, string ProjectTitle,
    IReadOnlyList<EiaWorkspaceUiContractInput> Contracts,
    IReadOnlyList<EiaWorkspaceUiCaseInput> Cases,
    IReadOnlyList<EiaWorkspaceUiIntakeInput> DesignIntakes,
    IReadOnlyList<EiaWorkspaceUiCorrectionInput> Corrections,
    IReadOnlyList<EiaWorkspaceUiFollowupInput> FollowupYears,
    string CoreMode);

/// <summary>Visible scenario outcomes are advisory; even human-reviewed rows do not authorize writes.</summary>
public sealed record EiaWorkspaceUiScenarioOutcome(string ScenarioId, string ReferenceCode,
    string OutcomeKind, string Scope, string? CaseCode, string? ContractCode, string Explanation)
{
    public string StatusLabel => OutcomeKind switch
    {
        "PROPOSED_DIRECTORIES_ONLY" => "미승인 폴더 제안",
        "REFERENCES_DEV2_INTAKE" => "기존 설계자료 수령 참조 (추가 폴더 없음)",
        "REFERENCES_DEV2_CORRECTION" => "기존 기관 보완 참조 (추가 폴더 없음)",
        "FUTURE_SAFETY_GATE_NOT_VERIFIED" => "안전검증 미완료 (폴더 없음)",
        _ => "처리 상태 확인 필요"
    };
}

public sealed record EiaWorkspaceUiScenarioInput(string ScenarioId, string ReferenceCode,
    string? CaseCode, string? ContractCode, string Round,
    bool UserConfirmed, bool HumanLegalReviewConfirmed);

public static class EiaWorkspaceUiComposer
{
    /// <summary>
    /// DEV7 review boundary: validate independently recomputed draft against what
    /// was actually shown to the user. A digest or row count alone is insufficient:
    /// row content, safety warnings and scenario decisions must also remain equal.
    /// This never grants a filesystem write permission.
    /// </summary>
    public static void RequireSameReadOnlyPreview(EiaWorkspaceUiPreview shown,
        EiaWorkspaceUiPreview recomputed)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(recomputed);
        if (shown.Entries is null || recomputed.Entries is null ||
            shown.UnverifiedGates is null || recomputed.UnverifiedGates is null ||
            shown.ExcludedTypes is null || recomputed.ExcludedTypes is null ||
            shown.SupportsFilesystemWrites || recomputed.SupportsFilesystemWrites ||
            !shown.UserApprovalRequired || !recomputed.UserApprovalRequired ||
            shown.CompanyPolicyVersion != EiaWorkspaceProfilePlanner.ApprovalUnapproved ||
            recomputed.CompanyPolicyVersion != EiaWorkspaceProfilePlanner.ApprovalUnapproved ||
            shown.RootFolderName != recomputed.RootFolderName ||
            shown.FingerprintSha256 != recomputed.FingerprintSha256 ||
            shown.LeafCount != recomputed.LeafCount ||
            shown.Warning != recomputed.Warning ||
            !shown.Entries.SequenceEqual(recomputed.Entries) ||
            !shown.UnverifiedGates.SequenceEqual(recomputed.UnverifiedGates, StringComparer.Ordinal) ||
            !shown.ExcludedTypes.SequenceEqual(recomputed.ExcludedTypes, StringComparer.Ordinal) ||
            !(shown.ScenarioOutcomes ?? Array.Empty<EiaWorkspaceUiScenarioOutcome>())
                .SequenceEqual(recomputed.ScenarioOutcomes ?? Array.Empty<EiaWorkspaceUiScenarioOutcome>()))
            throw new InvalidOperationException(
                "표시된 폴더 계획과 최신 계산 결과가 다릅니다. 다시 계산한 후 검토하세요.");
    }
    private static readonly IReadOnlyDictionary<string, string> Categories =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EiaWorkspaceFootprintAudit.MinimalCore] = "초안 기본",
            [EiaWorkspaceFootprintAudit.StandardExtra] = "초안 상세형",
            [EiaWorkspaceFootprintAudit.ConfirmedRecord] = "선택 업무기록",
            [EiaWorkspaceFootprintAudit.SelectedScenario] = "선택 상황",
            [EiaWorkspaceFootprintAudit.CompanyClientOverlay] = "회사·발주처 예외(미승인)"
        };

    /// <summary>
    /// One contract and one case per selected type is a DEV1 UI input restriction,
    /// not a limitation of the EIA planning engine. No inferred related-case links.
    /// </summary>
    public static EiaWorkspaceUiPreview BuildSingleContractDraft(
        string projectCode, string title, string contractCode, string contractDescription,
        IReadOnlyList<string> workstreams, string coreMode)
    {
        ArgumentNullException.ThrowIfNull(workstreams);
        if (workstreams.Count == 0 || workstreams.Count > 5 ||
            workstreams.Distinct(StringComparer.Ordinal).Count() != workstreams.Count)
            throw new ArgumentException("평가 유형을 1~5개 중 중복 없이 선택해야 합니다.");
        var catalog = EiaWorkspacePreviewPlanner.LoadDraftCatalog();
        if (workstreams.Any(x => !catalog.WorkstreamPaths.ContainsKey(x)))
            throw new ArgumentException("유효하지 않은 평가 업무 유형입니다.");
        // Stable ordering makes the output fingerprint independent of checkbox order.
        var request = CompileSingleContractRequest(projectCode, title, contractCode,
            contractDescription, workstreams, coreMode);
        var preview = EiaWorkspaceFootprintAudit.BuildDraftAudit(
            request, Array.Empty<EiaScenarioSelection>());
        if (preview.SupportsFilesystemWrites || !preview.UserApprovalRequired ||
            preview.ProfileFingerprintSha256 != "NO_PROFILE")
            throw new InvalidOperationException("미승인 읽기 전용 계획에 위배되는 상태입니다.");
        var rows = preview.DirectoryEntries.Select(e => new EiaWorkspaceUiEntry(
            e.RelativePath,
            Categories.TryGetValue(e.Category, out string? name) ? name : "확인 필요",
            e.OwnerScope.StartsWith("CASE:", StringComparison.Ordinal) ? "평가업무: " + e.OwnerScope[5..] :
            e.OwnerScope.StartsWith("CONTRACT:", StringComparison.Ordinal) ? "용역계약: " + e.OwnerScope[9..] : "사업 공통",
            e.Basis, e.ImpliedParent)).ToArray();
        if (rows.Length == 0 || rows.Length > 5000 ||
            rows.Select(x => x.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Length)
            throw new InvalidOperationException("폴더 구조 감사 결과가 유효하지 않습니다.");
        return new(preview.RootFolderName, Array.AsReadOnly(rows),
            Array.AsReadOnly(preview.PendingSafetyGates.ToArray()),
            Array.AsReadOnly(preview.NotApplicableWorkstreams.ToArray()),
            preview.FingerprintSha256, "UNAPPROVED", preview.LeafDirectoryCount,
            "회사 표준 미승인 초안입니다. 법정 대상·절차 및 실제 저장 위치·접근권한을 판단하지 않습니다. " +
            "용역별 별도계약·연계 원평가·사건 차수·발주처 예외는 추후 상세 화면에서 검토해야 합니다.",
            true, false);
    }

    /// <summary>
    /// Single source of truth for the basic UI input model, shared with the
    /// DEV11 read-only destination preflight. No filesystem access or writes.
    /// </summary>
    public static EiaCaseWorkspaceRequest CompileSingleContractRequest(
        string projectCode, string title, string contractCode, string contractDescription,
        IReadOnlyList<string> workstreams, string coreMode)
    {
        ArgumentNullException.ThrowIfNull(workstreams);
        if (workstreams.Count is < 1 or > 5 ||
            workstreams.Distinct(StringComparer.Ordinal).Count() != workstreams.Count)
            throw new ArgumentException("평가 유형을 1~5개 중 중복 없이 선택해야 합니다.");
        var catalog = EiaWorkspacePreviewPlanner.LoadDraftCatalog();
        if (workstreams.Any(x => !catalog.WorkstreamPaths.ContainsKey(x)))
            throw new ArgumentException("유효하지 않은 평가 업무 유형입니다.");
        string[] orderedTypes = workstreams.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var cases = orderedTypes.Select((id, index) =>
            new EiaAssessmentCase($"CASE-{index+1:00}", contractCode, id)).ToArray();
        return new EiaCaseWorkspaceRequest(projectCode, title,
            [new EiaServiceContract(contractCode, contractDescription)], cases,
            CoreMode: coreMode);
    }

    /// <summary>DEV2: explicit user-entered multi-contract/case and dated records. All read-only.</summary>
    public static EiaWorkspaceUiPreview BuildDetailedDraft(EiaWorkspaceUiDetailedInput input)
        => BuildDetailedDraft(input, Array.Empty<EiaWorkspaceUiScenarioInput>());

    /// <summary>
    /// DEV3: User-selected S01-S48 entries. Catalog scope, review, case/contract,
    /// record linking and no-directory safety gates are enforced by the existing planner.
    /// No scenario checkbox is treated as company approval or legal certification.
    /// </summary>
    public static EiaWorkspaceUiPreview BuildDetailedDraft(EiaWorkspaceUiDetailedInput input,
        IReadOnlyList<EiaWorkspaceUiScenarioInput> scenarioRows)
    {
        var (request, selections) = CompileDetailedInputs(input, scenarioRows);
        var footprint = EiaWorkspaceFootprintAudit.BuildDraftAudit(request, selections);
        var scenarioPreview = EiaWorkspaceScenarioPlanner.BuildPreview(request, selections);
        if (scenarioPreview.SupportsFilesystemWrites || !scenarioPreview.UserApprovalRequired)
            throw new InvalidOperationException("상황 선택으로 쓰기 권한을 부여할 수 없습니다.");
        var outcomes = scenarioPreview.Outcomes.Select(x => new EiaWorkspaceUiScenarioOutcome(
            x.ScenarioId, x.ReferenceCode, x.OutcomeKind, x.Scope,
            x.CaseCode, x.ContractCode, x.Explanation)).ToArray();
        if (footprint.SupportsFilesystemWrites || !footprint.UserApprovalRequired ||
            footprint.ProfileFingerprintSha256 != "NO_PROFILE")
            throw new InvalidOperationException("상세 계획이 회사 미승인 읽기 전용 경계를 위반했습니다.");
        var rows = footprint.DirectoryEntries.Select(e => new EiaWorkspaceUiEntry(
            e.RelativePath,
            Categories.TryGetValue(e.Category, out string? name) ? name : "확인 필요",
            e.OwnerScope.StartsWith("CASE:", StringComparison.Ordinal) ? "평가업무: " + e.OwnerScope[5..] :
            e.OwnerScope.StartsWith("CONTRACT:", StringComparison.Ordinal) ? "용역계약: " + e.OwnerScope[9..] : "사업 공통",
            e.Basis, e.ImpliedParent)).ToArray();
        if (rows.Length is < 1 or > 5000 ||
            rows.Select(x => x.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Length)
            throw new InvalidOperationException("상세 폴더 계획이 비정상적입니다.");
        return new EiaWorkspaceUiPreview(footprint.RootFolderName,
            Array.AsReadOnly(rows), Array.AsReadOnly(footprint.PendingSafetyGates.ToArray()),
            Array.AsReadOnly(footprint.NotApplicableWorkstreams.ToArray()),
            footprint.FingerprintSha256, "UNAPPROVED", footprint.LeafDirectoryCount,
            $"회사 미승인 초안: 계약 {request.Contracts.Count}건·평가업무 {request.AssessmentCases.Count}건·설계수령 {(request.DesignIntakes?.Count ?? 0)}건·" +
            $"기관 보완 {(request.CorrectionRounds?.Count ?? 0)}건·사후조사 연차 {(request.Events?.Count ?? 0)}건. " +
            "원평가 참조는 사업 내부 연결일 뿐 법정 적용 판단이 아닙니다. " +
            "사후조사 기간·법정 절차·회사 정책과 실제 저장 경로는 별도 승인 대상입니다. " +
            $"업무상황 {selections.Length}건(기존 기록 참조·안전검증 항목 포함), 공식 승인 아님.",
            true, false, Array.AsReadOnly(outcomes));
    }

    /// <summary>Shared UI input compiler for draft preview and company comparison. Never writes.</summary>
    public static (EiaCaseWorkspaceRequest Request, EiaScenarioSelection[] Scenarios)
        CompileDetailedInputs(EiaWorkspaceUiDetailedInput input,
            IReadOnlyList<EiaWorkspaceUiScenarioInput> scenarioRows)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(scenarioRows);
        if (scenarioRows.Count > 300 || scenarioRows.Any(x => x is null))
            throw new ArgumentException("업무상황은 최대 300건이며 빈 행을 허용하지 않습니다.");
        EiaScenarioCatalog scenarioCatalog = EiaWorkspaceScenarioPlanner.LoadDraftCatalog();
        var scenarioDefinitions = scenarioCatalog.Scenarios.ToDictionary(x => x.Id, StringComparer.Ordinal);
        // A legal review checkbox records a user's declaration, NEVER official legal approval.
        var selections = scenarioRows.Select(x =>
        {
            if (!scenarioDefinitions.TryGetValue(x.ScenarioId, out var definition))
                throw new ArgumentException("카탈로그에 없는 업무상황 ID입니다: " + x.ScenarioId);
            if (!x.UserConfirmed)
                throw new ArgumentException("업무상황을 명시적으로 선택·확인해야 합니다: " + x.ScenarioId);
            if (definition.HumanLegalReviewRequired && !x.HumanLegalReviewConfirmed)
                throw new ArgumentException("담당자의 적용성 확인이 필요한 상황입니다: " + x.ScenarioId);
            int round = string.IsNullOrWhiteSpace(x.Round) ? 0 : ParseRound(x.Round, "업무상황 차수");
            if (definition.Id is not ("S09" or "S15" or "S32") && round != 0)
                throw new ArgumentException("이 업무상황에는 차수 입력이 허용되지 않습니다: " + definition.Id);
            // Scope-specific references must be truly absent, not merely empty strings.
            string? caseCode = string.IsNullOrWhiteSpace(x.CaseCode) ? null : x.CaseCode;
            string? contractCode = string.IsNullOrWhiteSpace(x.ContractCode) ? null : x.ContractCode;
            if (definition.Scope == "project" && (caseCode is not null || contractCode is not null))
                throw new ArgumentException("사업 공통 업무상황은 계약·평가업무를 지정하지 않습니다.");
            if (definition.Scope == "contract" && caseCode is not null)
                throw new ArgumentException("계약 업무상황에는 평가업무를 지정하지 않습니다.");
            if (definition.Scope == "case" && contractCode is not null)
                throw new ArgumentException("평가업무 상황에는 소속 계약을 중복 입력하지 않습니다.");
            return new EiaScenarioSelection(x.ScenarioId, x.ReferenceCode,
                caseCode, contractCode, round, x.UserConfirmed, x.HumanLegalReviewConfirmed);
        }).ToArray();
        ArgumentNullException.ThrowIfNull(input.Contracts);
        ArgumentNullException.ThrowIfNull(input.Cases);
        ArgumentNullException.ThrowIfNull(input.DesignIntakes);
        ArgumentNullException.ThrowIfNull(input.Corrections);
        ArgumentNullException.ThrowIfNull(input.FollowupYears);
        if (input.Contracts.Count is < 1 or > 30 || input.Cases.Count is < 1 or > 100 ||
            input.DesignIntakes.Count > 200 || input.Corrections.Count > 200 ||
            input.FollowupYears.Count > 100)
            throw new ArgumentException("상세 계획 계약·평가업무·차수 입력 개수 제한을 초과했습니다.");
        if (input.Contracts.Any(x => x is null) || input.Cases.Any(x => x is null)
            || input.DesignIntakes.Any(x => x is null) || input.Corrections.Any(x => x is null)
            || input.FollowupYears.Any(x => x is null))
            throw new ArgumentException("상세 계획에 빈 행이 있습니다.");
        var catalog = EiaWorkspacePreviewPlanner.LoadDraftCatalog();
        var contracts = input.Contracts.Select(x => new EiaServiceContract(x.Code, x.Description))
            .OrderBy(x => x.ContractCode, StringComparer.OrdinalIgnoreCase).ToArray();
        var cases = input.Cases.Select(x =>
        {
            if (!catalog.WorkstreamPaths.ContainsKey(x.Workstream))
                throw new ArgumentException("유효하지 않은 평가업무 유형입니다: " + x.Workstream);
            return new EiaAssessmentCase(x.Code, x.ContractCode, x.Workstream,
                string.IsNullOrWhiteSpace(x.RelatedCaseCode) ? null : x.RelatedCaseCode);
        }).OrderBy(x => x.CaseCode, StringComparer.OrdinalIgnoreCase).ToArray();
        var intakes = input.DesignIntakes.Select(x =>
            new EiaDesignIntake(x.CaseCode, x.ReferenceCode, ParseRound(x.Round, "설계자료 수령차수")))
            .OrderBy(x => x.CaseCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.IntakeCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.RevisionNumber).ToArray();
        var corrections = input.Corrections.Select(x =>
            new EiaCorrectionRound(x.CaseCode, x.AgencyCode, ParseRound(x.Round, "기관 보완차수")))
            .OrderBy(x => x.CaseCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.AgencyCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.RoundNumber).ToArray();
        var followups = input.FollowupYears.Select(x =>
        {
            int year = ParseYear(x.Year);
            int round = ParseRound(x.Round, "사후조사 연차의 조사차수");
            // DEV2 uses only the existing generic business-event model.
            // S32 requires a separate explicit legal review and is NOT silently approved.
            return new EiaCaseEvent(x.CaseCode, $"{year}_조사_{round:00}", "followup_year");
        }).OrderBy(x => x.CaseCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.EventCode, StringComparer.Ordinal).ToArray();
        var request = new EiaCaseWorkspaceRequest(input.ProjectCode, input.ProjectTitle,
            contracts, cases, intakes, corrections, followups, input.CoreMode);
        return (request, selections);
    }

    private static int ParseRound(string? value, string label)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2 ||
            value.Any(ch => ch < '0' || ch > '9') ||
            !int.TryParse(value, out int number) || number is < 1 or > 99)
            throw new ArgumentException($"{label}: 1~99의 숫자를 입력하세요.");
        return number;
    }

    private static int ParseYear(string? value)
    {
        if (value is null || value.Length != 4 || value.Any(ch => ch < '0' || ch > '9')
            || !int.TryParse(value, out int year) || year is < 2000 or > 2100)
            throw new ArgumentException("사후조사 연도는 2000~2100의 YYYY 형식이어야 합니다.");
        return year;
    }

    public static string CreateCopyableReport(EiaWorkspaceUiPreview result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.SupportsFilesystemWrites || !result.UserApprovalRequired ||
            result.CompanyPolicyVersion != "UNAPPROVED")
            throw new InvalidOperationException("승인된 실행 계획인 것처럼 내보낼 수 없습니다.");
        var text = new StringBuilder();
        text.AppendLine("[Sunrise EIA 업무 폴더 계획 / 읽기 전용 미리보기 / 실제 생성은 재점검과 최종 확인 후]");
        text.AppendLine("사업 폴더: " + result.RootFolderName);
        text.AppendLine("상대경로 수: " + result.Entries.Count);
        text.AppendLine("미리보기 지문: " + result.FingerprintSha256);
        text.AppendLine("주의: " + result.Warning);
        text.AppendLine("검증대기: " + string.Join(", ", result.UnverifiedGates));
        foreach (var outcome in result.ScenarioOutcomes ?? Array.Empty<EiaWorkspaceUiScenarioOutcome>())
            text.AppendLine($"상황 {outcome.ScenarioId} / {outcome.ReferenceCode} / {outcome.OutcomeKind} / " +
                $"범위 {outcome.Scope} / {outcome.Explanation}");
        foreach (var entry in result.Entries)
            text.AppendLine($"{entry.RelativePath} | {entry.CategoryLabel} | {entry.OwnerLabel} | {entry.Basis}");
        return text.ToString();
    }
}
