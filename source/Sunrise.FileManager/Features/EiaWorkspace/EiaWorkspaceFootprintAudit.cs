using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// v1.7.7 DEV2: deterministic, read-only trace of why a proposed directory is present.
/// REQUIRED is a requirement of the UNAPPROVED draft layout only, never a claim that
/// environmental law requires a directory, that it exists, or that it may be created.
/// </summary>
public sealed record EiaDirectoryProvenance(
    string RelativePath, string Category, string OwnerScope,
    bool ImpliedParent, string Basis);

public sealed record EiaWorkspaceFootprintAuditResult(
    string RootFolderName, string ProfileFingerprintSha256,
    IReadOnlyList<EiaDirectoryProvenance> DirectoryEntries,
    IReadOnlyList<string> NotApplicableWorkstreams,
    IReadOnlyList<string> PendingSafetyGates,
    int ExplicitScenarioFolderCount, int LinkedExistingRecordCount,
    string FingerprintSha256, bool UserApprovalRequired, bool SupportsFilesystemWrites)
{
    public int DirectoryCount => DirectoryEntries.Count;
    public int LeafDirectoryCount => DirectoryEntries.Count(x => !x.ImpliedParent);
}

public static class EiaWorkspaceFootprintAudit
{
    public const string SchemaVersion = "sunrise.eia.workspace.footprint.audit.v1";
    public const string MinimalCore = "DRAFT_MINIMAL_BASE";
    public const string StandardExtra = "DRAFT_STANDARD_EXTRA";
    public const string ConfirmedRecord = "CONFIRMED_CASE_OR_RECORD";
    public const string SelectedScenario = "CONFIRMED_SCENARIO";
    public const string CompanyClientOverlay = "UNAPPROVED_COMPANY_CLIENT_OVERLAY";

    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;
    private const string WorkstreamRoot = "40_평가업무_법정절차";

    public static EiaWorkspaceFootprintAuditResult BuildDraftAudit(
        EiaCaseWorkspaceRequest request, IReadOnlyList<EiaScenarioSelection> selections)
        => BuildCore(request, selections, null);

    /// <summary>
    /// Optional, in-memory profile overlay audit. The profile is never given authority to
    /// alter source files, assert approval or bypass the original DEV4 conflict checks.
    /// </summary>
    public static EiaWorkspaceFootprintAuditResult BuildDraftAudit(
        EiaWorkspaceProfileRevision profile, EiaCaseWorkspaceRequest request,
        IReadOnlyList<EiaScenarioSelection> selections, EiaWorkspaceReviewEvidence evidence,
        string expectedCompany, string expectedRequirement)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(evidence);
        var snapshot = EiaWorkspaceReadinessPlanner.BuildPreview(profile, request, selections,
            evidence, expectedCompany, expectedRequirement);
        return BuildCore(request, selections, snapshot);
    }

    private static EiaWorkspaceFootprintAuditResult BuildCore(
        EiaCaseWorkspaceRequest request, IReadOnlyList<EiaScenarioSelection> selections,
        EiaWorkspaceReadinessSnapshot? readiness)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(request.Contracts);
        ArgumentNullException.ThrowIfNull(request.AssessmentCases);
        if (request.CoreMode is not ("standard" or "minimal"))
            throw new ArgumentException("업무 표준 초안 모드가 유효하지 않습니다.");

        // Each pass invokes the EXISTING planners; the audit must not independently
        // reproduce their paths or infer legal obligations.
        var stripped = request with
        {
            CoreMode = "minimal",
            AssessmentCases = request.AssessmentCases.Select(c => c with { ConfirmedOptions = null }).ToArray(),
            DesignIntakes = null, CorrectionRounds = null, Events = null,
        };
        EiaCaseWorkspacePreview minimal = EiaWorkspaceCasePlanner.BuildPreview(stripped);
        EiaCaseWorkspacePreview standard = EiaWorkspaceCasePlanner.BuildPreview(
            stripped with { CoreMode = request.CoreMode });
        EiaCaseWorkspacePreview withRecords = EiaWorkspaceCasePlanner.BuildPreview(request);
        EiaScenarioWorkspacePreview withScenarios = EiaWorkspaceScenarioPlanner.BuildPreview(request, selections);
        if (!string.Equals(minimal.RootFolderName, withRecords.RootFolderName, StringComparison.Ordinal)
            || !string.Equals(standard.RootFolderName, withScenarios.Baseline.RootFolderName, StringComparison.Ordinal))
            throw new InvalidDataException("미리보기의 사업 루트가 일치하지 않습니다.");

        var layers = new (string Category, IEnumerable<string> Paths)[]
        {
            (MinimalCore, minimal.DirectoryPaths),
            (StandardExtra, standard.DirectoryPaths),
            (ConfirmedRecord, withRecords.DirectoryPaths),
            (SelectedScenario, withScenarios.DirectoryPaths),
            (CompanyClientOverlay, readiness?.DirectoryPaths ?? withScenarios.DirectoryPaths),
        };
        var provenance = new Dictionary<string, (string Category, string Basis)>(IgnoreCase);
        var allowedPaths = new HashSet<string>(IgnoreCase);
        foreach (var layer in layers)
        {
            foreach (string path in layer.Paths)
            {
                string normalized = EiaWorkspacePreviewPlanner.ValidateRelativePath(path);
                if (!allowedPaths.Add(normalized)) continue;
                provenance.Add(normalized, (layer.Category, layer.Category switch
                {
                    MinimalCore => "회사 미승인 최소 업무구조 및 평가 유형별 기본 단계",
                    StandardExtra => "사용자가 선택한 회사 초안 standard 업무구조",
                    ConfirmedRecord => "명시 선택한 평가 옵션·설계수령·보완차수·업무사건",
                    SelectedScenario => "명시 선택한 S01-S48 상황 중 신규 폴더 제안 대상",
                    _ => "회사·발주처 예외 검토 결과(공식 승인 아님)",
                }));
            }
        }
        var actual = new HashSet<string>(
            readiness?.DirectoryPaths ?? withScenarios.DirectoryPaths, IgnoreCase);
        if (!allowedPaths.SetEquals(actual))
            throw new InvalidDataException("미리보기와 폴더 생성근거 감사의 경로 목록이 불일치합니다.");
        if (actual.Count > 5000) throw new InvalidDataException("감사 최대 폴더 개수를 초과했습니다.");

        var catalog = EiaWorkspacePreviewPlanner.LoadDraftCatalog();
        var selectedTypes = request.AssessmentCases.Select(x => x.Workstream)
            .ToHashSet(StringComparer.Ordinal);
        var notApplicable = catalog.WorkstreamPaths.Keys.Where(x => !selectedTypes.Contains(x))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        foreach (string type in notApplicable)
        {
            string prefix = WorkstreamRoot + "/" + catalog.WorkstreamPaths[type][0].Split('/')[0];
            if (actual.Any(p => p.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("선택하지 않은 평가 유형의 폴더가 포함되었습니다: " + type);
        }
        // A child without its parent would be an invalid preview and could hide
        // unexpected directory creation in a later writer.
        foreach (string path in actual)
        {
            int slash = path.LastIndexOf('/');
            if (slash >= 0 && !actual.Contains(path[..slash]))
                throw new InvalidDataException("상위 경로가 누락되었습니다: " + path);
        }
        string[] ordered = actual.OrderBy(x => x, IgnoreCase).ToArray();
        var entries = ordered.Select(path =>
        {
            var p = provenance[path];
            bool parent = ordered.Any(other => other.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
            return new EiaDirectoryProvenance(path, p.Category, ResolveOwner(path, request, catalog),
                parent, p.Basis);
        }).ToArray();
        int linked = withScenarios.Outcomes.Count(x => x.OutcomeKind is
            "REFERENCES_DEV2_INTAKE" or "REFERENCES_DEV2_CORRECTION");
        int eventFolderCount = entries.Count(x => x.Category == SelectedScenario);
        string[] gates = readiness?.UnverifiedRuntimeGates.ToArray()
            ?? withScenarios.Outcomes.Where(x => x.OutcomeKind == "FUTURE_SAFETY_GATE_NOT_VERIFIED")
                .Select(x => "SCENARIO_" + x.ScenarioId + "_NOT_VERIFIED")
                .Append("ACTUAL_PATH_ACL_NAS_WINDOWS_NOT_VERIFIED")
                .Append("COMPANY_STANDARD_AND_STATUTORY_APPLICABILITY_NOT_APPROVED")
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (readiness is not null && (readiness.SupportsFilesystemWrites || !readiness.UserApprovalRequired))
            throw new InvalidDataException("쓰기 가능 또는 사용자 승인 불필요 상태는 감사할 수 없습니다.");
        var seed = new StringBuilder();
        void Frame(string s) => seed.Append(s.Length).Append(':').Append(s);
        foreach (string field in new[] { SchemaVersion, withScenarios.FingerprintSha256,
            readiness?.FingerprintSha256 ?? "NO_PROFILE", withRecords.RootFolderName,
            request.CoreMode, string.Join("|", notApplicable), string.Join("|", gates) })
            Frame(field);
        foreach (var entry in entries)
        {
            Frame(entry.RelativePath);
            Frame(entry.Category);
            Frame(entry.OwnerScope);
            Frame(entry.ImpliedParent.ToString());
        }
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString())))
            .ToLowerInvariant();
        return new EiaWorkspaceFootprintAuditResult(withRecords.RootFolderName,
            readiness?.ProfileFingerprintSha256 ?? "NO_PROFILE", Array.AsReadOnly(entries),
            Array.AsReadOnly(notApplicable), Array.AsReadOnly(gates), eventFolderCount,
            linked, fingerprint, true, false);
    }

    private static string ResolveOwner(string path, EiaCaseWorkspaceRequest request,
        EiaWorkspaceDraftCatalog catalog)
    {
        foreach (var item in request.AssessmentCases)
        {
            string typeRoot = catalog.WorkstreamPaths[item.Workstream][0].Split('/')[0];
            string assessmentPrefix = $"{WorkstreamRoot}/{typeRoot}/{item.CaseCode.Trim()}";
            string designPrefix = $"20_사업_설계_기초자료/05_설계자료_수령차수/{item.CaseCode.Trim()}";
            if (path.Equals(assessmentPrefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(assessmentPrefix + "/", StringComparison.OrdinalIgnoreCase)
                || path.Equals(designPrefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(designPrefix + "/", StringComparison.OrdinalIgnoreCase))
                return "CASE:" + item.CaseCode.Trim();
        }
        foreach (var item in request.Contracts)
        {
            string prefix = "10_계약_용역관리/07_계약별/" + item.ContractCode.Trim();
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return "CONTRACT:" + item.ContractCode.Trim();
        }
        return "PROJECT_SHARED";
    }
}
