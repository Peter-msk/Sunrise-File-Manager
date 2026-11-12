using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// DEV26: immutable, read-only handoff after a project-specific acknowledgement and
/// an independently refreshed path inspection. This is not an authorization token.
/// Real business-folder writes are not available in the product.
/// </summary>
public sealed record EiaWorkspaceExecutionManifest(
    string BaseDirectory, string WorkspacePath, string Scope,
    string SourceFingerprintSha256, string ObservedFingerprintSha256,
    DateTimeOffset CheckedAtUtc, IReadOnlyList<EiaFolderCreationItem> Paths,
    int Existing, int WouldCreate)
{
    public bool GrantsWritePermission => false;
    public bool ProductionWritesEnabled => false;
    public string Gate => "BLOCKED_PRODUCTION_WRITE";

    public string BuildReadOnlyReport(int maxPaths = 5001)
    {
        if (maxPaths < 1 || maxPaths > 5001)
            throw new ArgumentOutOfRangeException(nameof(maxPaths));
        var sb = new StringBuilder();
        sb.AppendLine("Sunrise File Manager DEV26 · 사업별 폴더 생성 실행 계획표 (읽기 전용)");
        sb.AppendLine("이 보고서는 실행 전 읽기 전용 계획이며, 누락 폴더 생성은 별도 최종승인 작업입니다.");
        sb.AppendLine("경로: " + WorkspacePath);
        sb.AppendLine("범위: " + Scope);
        sb.AppendLine($"기존 {Existing} / 생성 예상 {WouldCreate} / 총 {Paths.Count}");
        sb.AppendLine("재점검(UTC): " + CheckedAtUtc.ToString("O"));
        sb.AppendLine("출처 지문: " + SourceFingerprintSha256);
        sb.AppendLine("상태 지문: " + ObservedFingerprintSha256);
        sb.AppendLine("실제 생성 후 결과는 CREATED / ALREADY_EXISTS / FAILED / NOT_ATTEMPTED로 구분해야 합니다.");
        sb.AppendLine("FAILED는 생성됐을 수도 있으므로 삭제·추정 재시도 없이 재조회해야 합니다.");
        foreach (var row in Paths.Take(maxPaths))
            sb.AppendLine($"[{row.BeforeState}] {row.RelativePath}");
        if (Paths.Count > maxPaths)
            sb.AppendLine($"나머지 {Paths.Count - maxPaths}개 생략");
        return sb.ToString();
    }
}

/// <summary>
/// A product-safe planner: no filesystem writes and no caller-provided disk-result
/// callback. Stale or cross-project observations are rejected before handoff.
/// </summary>
public static class EiaWorkspaceExecutionService
{
    public static EiaWorkspaceExecutionManifest PrepareReadOnlyManifest(
        EiaWorkspaceExecutionReadiness review,
        EiaWorkspaceProjectReviewAcknowledgement acknowledgement,
        EiaWorkspaceExecutionPreflightResult preflight,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        ArgumentNullException.ThrowIfNull(preflight);
        EiaWorkspaceProjectReviewPlanner.RequireSameReview(acknowledgement, review);
        if (EiaWorkspaceCreationEngine.ProductionWritesEnabled ||
            review.CanExecute || review.ProductionWritesEnabled ||
            preflight.GrantsWritePermission || preflight.ProductionWritesEnabled ||
            !preflight.RequiresFreshApprovalAtExecution ||
            review.Blocked != 0 || preflight.Blocked != 0 ||
            preflight.CurrentPaths is null || preflight.CurrentPaths.Count < 2 ||
            preflight.CurrentPaths.Count > 5001 ||
            preflight.Existing < 0 || preflight.WouldCreate < 0 ||
            preflight.Existing + preflight.WouldCreate != preflight.CurrentPaths.Count ||
            !string.Equals(review.BaseDirectory, preflight.BaseDirectory, StringComparison.Ordinal) ||
            !string.Equals(review.WorkspacePath, preflight.WorkspacePath, StringComparison.Ordinal) ||
            !string.Equals(review.SourceLabel, preflight.SourceLabel, StringComparison.Ordinal) ||
            !string.Equals(review.SourceFingerprintSha256, preflight.SourceFingerprintSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(review.StrictPlanFingerprintSha256, preflight.ObservationFingerprintSha256,
                StringComparison.OrdinalIgnoreCase) ||
            preflight.CheckedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("사업별 재점검 결과와 검토 확인이 일치하지 않습니다.");

        var observations = new EiaFolderPathObservations(preflight.BaseDirectory,
            preflight.WorkspacePath, preflight.CurrentPaths, preflight.Existing,
            preflight.WouldCreate, preflight.Blocked, false, true);
        EiaWorkspaceCreationEngine.RequireSameObservedSnapshot(observations,
            preflight.SourceFingerprintSha256, preflight.ObservationFingerprintSha256);
        cancellationToken.ThrowIfCancellationRequested();
        var rows = Array.AsReadOnly(preflight.CurrentPaths.Select(row =>
            new EiaFolderCreationItem(row.RelativePath, row.AbsolutePath, row.Status)).ToArray());
        return new EiaWorkspaceExecutionManifest(preflight.BaseDirectory,
            preflight.WorkspacePath, preflight.SourceLabel,
            preflight.SourceFingerprintSha256, preflight.ObservationFingerprintSha256,
            preflight.CheckedAtUtc, rows, preflight.Existing, preflight.WouldCreate);
    }
}

/// <summary>
/// Native test-only outcome ledger. This adapter is deliberately absent from the
/// shipping product; consumers cannot submit fabricated successes to a production UI.
/// </summary>
#if EIA_WORKSPACE_SANDBOX_TEST
public sealed record EiaWorkspaceSandboxExecutionEvidence(
    string PlanFingerprintSha256, IReadOnlyList<EiaFolderCreationOutcome> Outcomes,
    int Created, int AlreadyExists, int Failed, int NotAttempted,
    bool Completed, bool RequiresManualReinspection)
{
    public bool IsProductionEvidence => false;
    public bool IsolatedSandboxOnly => true;

    public string BuildReport(int maxRows = 5001)
    {
        if (maxRows < 1 || maxRows > 5001)
            throw new ArgumentOutOfRangeException(nameof(maxRows));
        var sb = new StringBuilder();
        sb.AppendLine("Sunrise File Manager DEV26 · 격리 네이티브 실행 결과 (운영 증거 아님)");
        sb.AppendLine($"생성 {Created} / 기존 {AlreadyExists} / 실패 {Failed} / 미시도 {NotAttempted}");
        sb.AppendLine("완료: " + Completed + " / 재조회 필요: " + RequiresManualReinspection);
        sb.AppendLine("검사 지문: " + PlanFingerprintSha256);
        foreach (var row in Outcomes.Take(maxRows))
            sb.AppendLine($"[{row.Outcome}] {row.RelativePath} :: {row.Detail}");
        if (Outcomes.Count > maxRows) sb.AppendLine($"나머지 {Outcomes.Count-maxRows}개 생략");
        return sb.ToString();
    }
}

/// <summary>
/// DEV27: an independent, POST-EXECUTION read-back in the isolated Windows test.
/// "DISK_RECHECK_PASS" is ONLY local sandbox evidence; not an authorization for
/// business directories, remote SMB/NAS, or an installer release.
/// </summary>
public sealed record EiaWorkspaceSandboxFinalVerification(
    EiaWorkspaceSandboxExecutionEvidence Execution,
    string DiskVerification, DateTimeOffset VerifiedAtUtc)
{
    public bool IsProductionEvidence => false;
    public bool IsolatedSandboxOnly => true;
    public bool AllPlannedDirectoriesConfirmed => DiskVerification == "DISK_RECHECK_PASS";
    public string BuildReport()
    {
        return "Sunrise DEV27 isolated native post-execution verification\n" +
            "Status: " + DiskVerification + "\n" +
            "Isolated sandbox only: true; production authorization: false\n" +
            "Verified UTC: " + VerifiedAtUtc.ToString("O") + "\n" +
            Execution.BuildReport();
    }
}

public static class EiaWorkspaceIsolatedExecutionService
{
    /// <summary>Result validation cannot turn an uncertain native call into success.</summary>
    public static EiaWorkspaceSandboxExecutionEvidence RequireAndSummarizeSandboxResult(
        EiaFolderCreationPlan submitted, EiaFolderCreationResult actual)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(actual);
        if (submitted.Items is null || submitted.Items.Count is < 1 or > 5001 ||
            submitted.ProductionWritesEnabled || !submitted.RequiresUserApproval ||
            actual.Outcomes is null || actual.Outcomes.Count != submitted.Items.Count ||
            !actual.RequiresManualReview ||
            !string.Equals(actual.RootFolderName, submitted.RootFolderName, StringComparison.Ordinal) ||
            !string.Equals(actual.PlanFingerprintSha256, submitted.FingerprintSha256, StringComparison.Ordinal))
            throw new InvalidDataException("격리 실행 결과가 승인된 테스트 계획과 일치하지 않습니다.");
        int created = 0, existing = 0, failed = 0, notAttempted = 0;
        bool interrupted = false;
        var snapshot = new EiaFolderCreationOutcome[actual.Outcomes.Count];
        for (int i = 0; i < actual.Outcomes.Count; i++)
        {
            var item = submitted.Items[i];
            var outcome = actual.Outcomes[i];
            if (item is null || outcome is null ||
                !string.Equals(item.RelativePath, outcome.RelativePath, StringComparison.Ordinal) ||
                item.BeforeState is not ("MISSING" or "EXISTS_DIRECTORY") ||
                string.IsNullOrWhiteSpace(outcome.Detail) || outcome.Detail.Length > 8192)
                throw new InvalidDataException("격리 실행 결과의 경로·진단 원인이 잘못됐습니다.");
            switch (outcome.Outcome)
            {
                case "CREATED" when !interrupted && item.BeforeState == "MISSING": created++; break;
                case "ALREADY_EXISTS" when !interrupted && item.BeforeState == "EXISTS_DIRECTORY": existing++; break;
                case "FAILED" when !interrupted: failed++; interrupted = true; break;
                case "NOT_ATTEMPTED": notAttempted++; interrupted = true; break;
                default: throw new InvalidDataException("실패 후 성공 또는 기존 경로의 잘못된 생성 판정을 거부합니다.");
            }
            snapshot[i] = outcome with { };
        }
        bool complete = !interrupted;
        if (actual.Created != created || actual.AlreadyExists != existing ||
            actual.Failed != failed || actual.NotAttempted != notAttempted ||
            actual.Completed != complete || failed > 1 ||
            (failed > 0 && notAttempted != submitted.Items.Count - created - existing - failed))
            throw new InvalidDataException("격리 실행 결과 집계 또는 부분실패 상태가 불일치합니다.");
        return new EiaWorkspaceSandboxExecutionEvidence(submitted.FingerprintSha256,
            Array.AsReadOnly(snapshot), created, existing, failed, notAttempted,
            complete, !complete);
    }

    /// <summary>
    /// Re-inspects the REAL local sandbox disk AFTER a fully completed native run.
    /// Fail closed on any changed path, unexpected link, or missing directory.
    /// On partial/uncertain native results, NEVER infer that a missing/successful
    /// operation occurred: a new independent plan and user confirmation are required.
    /// This code is excluded from the shipping WPF assembly by conditional compilation.
    /// </summary>
    public static EiaWorkspaceSandboxFinalVerification VerifyIsolatedCompletion(
        EiaFolderCreationPlan original, EiaFolderCreationResult result,
        EiaCaseWorkspaceRequest request, IReadOnlyList<EiaScenarioSelection> scenarios,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scenarios);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("격리 네이티브 최종 확인은 Windows에서만 수행합니다.");
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(original.BaseDirectory));
        string leaf = Path.GetFileName(parent) ?? "";
        const string prefix = "Sunrise_EIA_SANDBOX_";
        string unique = leaf.StartsWith(prefix, StringComparison.Ordinal)
            ? leaf[prefix.Length..] : "";
        if (!parent.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(unique, "N", out _))
            throw new InvalidDataException("DEV27 최종 검증은 격리 임시 폴더만 허용합니다.");
        var evidence = RequireAndSummarizeSandboxResult(original, result);
        if (!evidence.Completed || evidence.Failed != 0 || evidence.NotAttempted != 0)
            return new EiaWorkspaceSandboxFinalVerification(evidence,
                "BLOCKED_NEEDS_MANUAL_REINSPECTION", DateTimeOffset.UtcNow);

        // A newly derived source plan verifies the template and every ancestor
        // using the standard reparse-point/file safety inspector, not Directory.Exists alone.
        var current = EiaWorkspaceCreationEngine.Prepare(parent, request, scenarios,
            cancellationToken);
        if (current.Items.Count != original.Items.Count ||
            !string.Equals(current.SourceAuditFingerprint, original.SourceAuditFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(current.WorkspacePath, original.WorkspacePath,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("생성 후 사업 계획 또는 기준 폴더가 변경됐습니다.");
        for (int i = 0; i < original.Items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = original.Items[i];
            var after = current.Items[i];
            if (!string.Equals(before.RelativePath, after.RelativePath, StringComparison.Ordinal) ||
                !string.Equals(before.AbsolutePath, after.AbsolutePath,
                    StringComparison.OrdinalIgnoreCase) ||
                after.BeforeState != "EXISTS_DIRECTORY")
                throw new InvalidDataException("네이티브 생성 완료 후 누락·변경·연결 경로가 있습니다.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new EiaWorkspaceSandboxFinalVerification(evidence, "DISK_RECHECK_PASS",
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The only DEV26 execution method: compiled into the separate native sandbox
    /// test assembly, never into the WPF product. Uses the existing pinned-root
    /// native executor, with its GUID temp marker and exact typed confirmation.
    /// </summary>
    public static EiaWorkspaceSandboxExecutionEvidence ExecuteIsolatedSandbox(
        EiaFolderCreationPlan plan, string typedConfirmation, string sandboxMarker,
        EiaCaseWorkspaceRequest request, IReadOnlyList<EiaScenarioSelection> scenarios,
        CancellationToken cancellationToken = default,
        Action<int,string>? beforeAttempt = null,
        Action<int,string>? afterNativeAttempt = null)
    {
        var result = EiaWorkspaceCreationEngine.ExecuteInSandbox(plan,
            typedConfirmation, sandboxMarker, request, scenarios,
            cancellationToken, beforeAttempt, afterNativeAttempt);
        return RequireAndSummarizeSandboxResult(plan, result);
    }
}
#endif
