using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// v1.7.6 DEV2: a pure, read-only project/contract/assessment/event layout planner.
/// Folder names represent a proposed COMPANY convention, not statutory requirements.
/// No file reads (other than the pre-existing draft catalog), writes, ACL changes,
/// copying of original design data, submissions, legal decisions or network calls.
/// </summary>
public sealed record EiaServiceContract(string ContractCode, string Description);
public sealed record EiaAssessmentCase(
    string CaseCode, string ContractCode, string Workstream,
    string? RelatedCaseCode = null, IReadOnlyList<string>? ConfirmedOptions = null);
public sealed record EiaDesignIntake(string CaseCode, string IntakeCode, int RevisionNumber);
public sealed record EiaCorrectionRound(string CaseCode, string AgencyCode, int RoundNumber);
public sealed record EiaCaseEvent(string CaseCode, string EventCode, string Kind);
public sealed record EiaCaseWorkspaceRequest(
    string ProjectCode, string ProjectTitle,
    IReadOnlyList<EiaServiceContract> Contracts,
    IReadOnlyList<EiaAssessmentCase> AssessmentCases,
    IReadOnlyList<EiaDesignIntake>? DesignIntakes = null,
    IReadOnlyList<EiaCorrectionRound>? CorrectionRounds = null,
    IReadOnlyList<EiaCaseEvent>? Events = null,
    string CoreMode = "standard");
public sealed record EiaCaseRelationship(string CaseCode, string ContractCode, string Workstream,
    string? RelatedCaseCode);
public sealed record EiaCaseWorkspacePreview(
    string RootFolderName, string TemplateVersion, string CompanyPolicyVersion,
    IReadOnlyList<string> DirectoryPaths, IReadOnlyList<EiaServiceContract> Contracts,
    IReadOnlyList<EiaCaseRelationship> Relationships,
    string FingerprintSha256, string Advisory, bool UserApprovalRequired, bool SupportsFilesystemWrites);

public static class EiaWorkspaceCasePlanner
{
    private const string AssessmentRoot = "40_평가업무_법정절차";
    private const string ContractRoot = "10_계약_용역관리/07_계약별";
    private const string IntakeRoot = "20_사업_설계_기초자료/05_설계자료_수령차수";

    // These are internal business event labels; they do not assert a statutory requirement.
    private static readonly IReadOnlyDictionary<string, string> EventKinds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contract_change"] = "01_계약_변경",
            ["design_change"] = "02_설계_변경",
            ["reconsultation_review"] = "03_재협의_변경협의_검토",
            ["contract_pause"] = "04_용역_일시중지",
            ["contract_resume"] = "05_용역_재개",
            ["construction_pause"] = "06_공사_중지",
            ["construction_resume"] = "07_공사_재개",
            ["staff_handover"] = "08_담당자_인수인계",
            ["followup_year"] = "09_사후조사_연차",
            ["delivery"] = "10_납품_인수",
            ["contract_termination"] = "11_용역_해지",
        };

    // One authoritative case-to-folder mapping shared by the base and scenario planners.
    // Internal CaseCode values must never become proposed workspace directory segments.
    internal static IReadOnlyDictionary<string, string> BuildCaseRootMap(
        IEnumerable<EiaAssessmentCase> assessmentCases, EiaWorkspaceDraftCatalog catalog)
    {
        var ordered = assessmentCases.OrderBy(c => c.CaseCode.Trim().Normalize(NormalizationForm.FormC),
            StringComparer.OrdinalIgnoreCase).ToArray();
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ordered)
        {
            string typeBase = catalog.WorkstreamPaths[item.Workstream][0].Split('/')[0];
            int ordinal = counts.TryGetValue(typeBase, out int n) ? n + 1 : 1;
            counts[typeBase] = ordinal;
            bool multipleSameType = ordered.Count(c =>
                catalog.WorkstreamPaths[c.Workstream][0].Split('/')[0]
                    .Equals(typeBase, StringComparison.OrdinalIgnoreCase)) > 1;
            roots.Add(item.CaseCode.Trim().Normalize(NormalizationForm.FormC),
                $"{AssessmentRoot}/{typeBase}" + (multipleSameType ? $"/{ordinal:00}_평가업무" : ""));
        }
        return roots;
    }

    public static EiaCaseWorkspacePreview BuildPreview(EiaCaseWorkspaceRequest request)
        => BuildPreview(request, EiaWorkspacePreviewPlanner.LoadDraftCatalog());

    public static EiaCaseWorkspacePreview BuildPreview(EiaCaseWorkspaceRequest request,
        EiaWorkspaceDraftCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.SchemaVersion != "sunrise.eia.workspace.draft.v1"
            || catalog.CompanyPolicyVersion != "UNAPPROVED"
            || !catalog.TemplateVersion.EndsWith("-DRAFT", StringComparison.Ordinal)
            || catalog.CoreRoots.Length != 8)
            throw new InvalidDataException("승인 전 회사 템플릿 형식이 아닙니다.");
        if (request.CoreMode is not ("minimal" or "standard"))
            throw new ArgumentException("승인 전 단계에서는 minimal/standard만 선택할 수 있습니다.");
        string projectCode = string.IsNullOrWhiteSpace(request.ProjectCode) ? "INTERNAL" : Segment(request.ProjectCode, "사업번호", 45);
        string title = Segment(request.ProjectTitle, "사업명", 80);
        string root = Segment(title, "프로젝트 폴더", 220);
        if (request.Contracts is null || request.Contracts.Count == 0
            || request.AssessmentCases is null || request.AssessmentCases.Count == 0)
            throw new ArgumentException("최소 1개 용역계약 및 평가업무를 지정해야 합니다.");
        if (request.Contracts.Count > 100 || request.AssessmentCases.Count > 200)
            throw new ArgumentException("미리보기 처리 가능한 계약 또는 평가업무 건수를 초과했습니다.");

        var contractCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contractDetails = new List<EiaServiceContract>();
        foreach (var contract in request.Contracts)
        {
            ArgumentNullException.ThrowIfNull(contract);
            string code = Segment(contract.ContractCode, "용역계약번호", 45);
            string desc = Segment(contract.Description, "용역명", 80);
            if (!contractCodes.Add(code)) throw new ArgumentException("중복 계약번호가 있습니다: " + code);
            contractDetails.Add(new EiaServiceContract(code, desc));
        }
        var cases = new Dictionary<string, EiaAssessmentCase>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in request.AssessmentCases)
        {
            ArgumentNullException.ThrowIfNull(item);
            string caseCode = Segment(item.CaseCode, "평가업무번호", 45);
            string contractCode = Segment(item.ContractCode, "용역계약번호", 45);
            if (!contractCodes.Contains(contractCode))
                throw new ArgumentException("존재하지 않는 계약에 연결된 평가업무입니다: " + caseCode);
            if (!catalog.WorkstreamPaths.ContainsKey(item.Workstream))
                throw new ArgumentException("알 수 없는 평가 유형입니다: " + item.Workstream);
            string? relatedCode = item.RelatedCaseCode is null
                ? null : Segment(item.RelatedCaseCode, "원평가업무번호", 45);
            // Normalize all identifiers once; case/contract references remain canonical.
            var normalized = item with { CaseCode = caseCode,
                ContractCode = contractCodes.First(c => c.Equals(contractCode, StringComparison.OrdinalIgnoreCase)),
                RelatedCaseCode = relatedCode };
            if (!cases.TryAdd(caseCode, normalized))
                throw new ArgumentException("중복 평가업무번호가 있습니다: " + caseCode);
        }
        foreach (var item in cases.Values)
        {
            if (item.RelatedCaseCode is null) continue;
            string related = Segment(item.RelatedCaseCode, "연결 원평가업무번호", 45);
            if (!cases.ContainsKey(related) || related.Equals(item.CaseCode, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("원평가 참조는 동일 사업에 등록된 다른 평가업무여야 합니다.");
            // Prevent reference cycles; no legal or file lineage is inferred.
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { item.CaseCode };
            string? next = related;
            while (next is not null)
            {
                if (!visited.Add(next)) throw new ArgumentException("평가업무 참조 관계가 순환합니다.");
                next = cases[next].RelatedCaseCode;
            }
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            string validated = EiaWorkspacePreviewPlanner.ValidateRelativePath(path);
            if (root.Length + 1 + validated.Length > 240)
                throw new InvalidDataException("실제 저장위치 선정 전 미리보기 경로 길이 제한을 초과했습니다.");
            string[] parts = validated.Split('/');
            for (int i = 1; i <= parts.Length; i++)
                paths.Add(string.Join("/", parts.Take(i)));
        }
        foreach (string path in catalog.CoreRoots) Add(path);
        if (request.CoreMode == "standard")
            foreach (string path in catalog.CoreStandard) Add(path);
        foreach (string contractCode in contractCodes)
            Add($"{ContractRoot}/{(contractCode == "INTERNAL" ? "용역" : contractCode)}/01_계약원본_변경이력");

        var relations = new List<EiaCaseRelationship>();
        var caseRoots = BuildCaseRootMap(cases.Values, catalog);
        foreach (var item in cases.Values.OrderBy(c => c.CaseCode, StringComparer.OrdinalIgnoreCase))
        {
            string typeBase = catalog.WorkstreamPaths[item.Workstream][0].Split('/')[0];
            string caseRoot = caseRoots[item.CaseCode];
            Add(caseRoot + "/00_업무범위_계약연결");
            foreach (string path in catalog.WorkstreamPaths[item.Workstream])
            {
                string[] parts = path.Split('/');
                if (parts[0] != typeBase || parts.Length < 2)
                    throw new InvalidDataException("평가 유형별 템플릿 상위 폴더 불일치");
                Add(caseRoot + "/" + string.Join("/", parts.Skip(1)));
            }
            var options = item.ConfirmedOptions ?? [];
            if (options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count)
                throw new ArgumentException("동일 평가업무에 중복 조건 옵션이 있습니다.");
            foreach (string option in options)
            {
                if (!catalog.ConditionalPaths.TryGetValue(option, out string[]? extras)
                    || !catalog.ConditionalRequiresWorkstreams.TryGetValue(option, out string[]? allowed)
                    || !allowed.Contains(item.Workstream, StringComparer.Ordinal))
                    throw new ArgumentException("선택한 평가 유형에 적용할 수 없는 조건 옵션입니다: " + option);
                foreach (string path in extras)
                {
                    if (option == "joint_contract")
                        Add($"{ContractRoot}/{item.ContractCode}/06_공동도급_외주_협력기관");
                    else
                    {
                        string[] parts = path.Split('/');
                        if (parts[0] != typeBase && (option is "strategy_draft" or "environmental_draft"))
                            throw new InvalidDataException("초안 옵션의 유형 분리가 깨졌습니다.");
                        Add(caseRoot + "/" + (parts[0] == typeBase
                            ? string.Join("/", parts.Skip(1)) : path));
                    }
                }
            }
            relations.Add(new(item.CaseCode, item.ContractCode, item.Workstream, item.RelatedCaseCode));
        }

        var intakeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var intake in request.DesignIntakes ?? [])
        {
            ArgumentNullException.ThrowIfNull(intake);
            string caseCode = FindCase(intake.CaseCode, caseRoots);
            string intakeCode = Segment(intake.IntakeCode, "설계자료 인수 식별자", 50);
            int round = PositiveRound(intake.RevisionNumber);
            string key = $"{caseCode}|{intakeCode}|{round}";
            if (!intakeKeys.Add(key)) throw new ArgumentException("같은 설계 수령차수가 중복되었습니다.");
            // Keep the same readable workstream/ordinal scope as the assessment tree.
            string caseBranch = caseRoots[caseCode][(AssessmentRoot.Length + 1)..];
            string path = $"{IntakeRoot}/{caseBranch}/{intakeCode}_수령_{round:00}";
            Add(path + "/01_수령원본");
            Add(path + "/02_자료목록_출처_인수기록");
            Add(path + "/03_전차수_변경대비_작업본");
        }

        var correctionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var correction in request.CorrectionRounds ?? [])
        {
            ArgumentNullException.ThrowIfNull(correction);
            string caseCode = FindCase(correction.CaseCode, caseRoots);
            string agency = Segment(correction.AgencyCode, "검토기관 식별자", 40);
            int round = PositiveRound(correction.RoundNumber);
            string key = $"{caseCode}|{agency}|{round}";
            if (!correctionKeys.Add(key)) throw new ArgumentException("같은 기관의 보완 차수가 중복되었습니다.");
            string path = $"{caseRoots[caseCode]}/80_보완_차수/{agency}/보완_{round:00}";
            foreach (string sub in new[] {
                "01_요청서_접수원본", "02_조치대비표_작업본", "03_수정_검토본",
                "04_재제출_확정본", "05_접수_회신원본" })
                Add(path + "/" + sub);
        }

        var eventKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in request.Events ?? [])
        {
            ArgumentNullException.ThrowIfNull(item);
            string caseCode = FindCase(item.CaseCode, caseRoots);
            string code = Segment(item.EventCode, "사건 식별번호", 50);
            if (!EventKinds.TryGetValue(item.Kind, out string? kindFolder))
                throw new ArgumentException("지원하지 않는 용역 사건 유형입니다: " + item.Kind);
            if (item.Kind == "followup_year" && cases[caseCode].Workstream != "followup")
                throw new ArgumentException("연차 조사 사건은 사후환경영향조사 업무에서만 추가합니다.");
            if (!eventKeys.Add($"{caseCode}|{code}"))
                throw new ArgumentException("같은 평가업무의 사건 식별번호가 중복되었습니다.");
            Add($"{caseRoots[caseCode]}/90_발생사건/{kindFolder}/{code}/01_근거_원본");
            Add($"{caseRoots[caseCode]}/90_발생사건/{kindFolder}/{code}/02_검토_업무기록");
            Add($"{caseRoots[caseCode]}/90_발생사건/{kindFolder}/{code}/03_제출_회신");
        }
        string[] ordered = paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        EiaServiceContract[] orderedContracts = contractDetails.OrderBy(c => c.ContractCode, StringComparer.OrdinalIgnoreCase).ToArray();
        EiaCaseRelationship[] orderedRelations = relations.OrderBy(r => r.CaseCode, StringComparer.OrdinalIgnoreCase).ToArray();
        string seed = string.Join("\n", new[] { root, catalog.SchemaVersion, catalog.TemplateVersion,
            catalog.CompanyPolicyVersion, request.CoreMode }) + "\n"
            + string.Join("\n", orderedContracts.Select(c => $"{c.ContractCode}|{c.Description}"))
            + "\n" + string.Join("\n", orderedRelations.Select(r => $"{r.CaseCode}|{r.ContractCode}|{r.Workstream}|{r.RelatedCaseCode}"))
            + "\n" + string.Join("\n", ordered);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
        return new(root, catalog.TemplateVersion, catalog.CompanyPolicyVersion,
            Array.AsReadOnly(ordered), Array.AsReadOnly(orderedContracts), Array.AsReadOnly(orderedRelations), hash,
            "회사 승인 전 제안 구조입니다. 평가의 법정 의무·완료·인허가 상태를 판단하지 않으며 원자료를 복사하지 않습니다.",
            true, false);
    }

    private static string Segment(string? value, string name, int maximum)
        => EiaWorkspacePreviewPlanner.ValidateSegment(value, name, maximum);

    private static int PositiveRound(int number)
    {
        if (number < 1 || number > 99)
            throw new ArgumentException("차수는 1부터 99까지여야 합니다.");
        return number;
    }
    private static string FindCase(string code, IReadOnlyDictionary<string, string> roots)
    {
        string normalized = Segment(code, "평가업무번호", 45);
        if (!roots.ContainsKey(normalized))
            throw new ArgumentException("등록되지 않은 평가업무에 사건을 추가할 수 없습니다: " + normalized);
        return roots.Keys.First(k => string.Equals(k, normalized, StringComparison.OrdinalIgnoreCase));
    }
}
