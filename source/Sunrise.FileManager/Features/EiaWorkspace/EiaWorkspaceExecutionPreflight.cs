using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// DEV25: execution-adjacent RECHECK only. The result is not an authorization,
/// and this assembly never calls a filesystem writer for a real business folder.
/// Do not confuse two matching path scans with an atomic NTFS/SMB transaction.
/// </summary>
public sealed record EiaWorkspaceExecutionPreflightResult(
    string BaseDirectory, string WorkspacePath, string SourceLabel,
    string SourceFingerprintSha256, string ObservationFingerprintSha256,
    IReadOnlyList<EiaFolderPathObservation> CurrentPaths,
    int Existing, int WouldCreate, int Blocked, DateTimeOffset CheckedAtUtc)
{
    public bool GrantsWritePermission => false;
    public bool ProductionWritesEnabled => false;
    public bool RequiresFreshApprovalAtExecution => true;

    public string BuildReadOnlyReport(int maxPaths = 5001)
    {
        if (maxPaths < 1 || maxPaths > 5001)
            throw new ArgumentOutOfRangeException(nameof(maxPaths));
        var sb = new StringBuilder();
        sb.AppendLine("Sunrise File Manager DEV25 · 실행 직전 재점검 (읽기 전용)");
        sb.AppendLine("생성·삭제·수정 0건 / 운영 쓰기 잠금 / 회사 표준 승인 트리 사용 안 함");
        sb.AppendLine("사업 경로: " + WorkspacePath);
        sb.AppendLine("검토 범위: " + SourceLabel);
        sb.AppendLine($"기존 {Existing} / 생성 예상 {WouldCreate} / 차단 {Blocked}");
        sb.AppendLine("검사 시간(UTC): " + CheckedAtUtc.ToString("O"));
        sb.AppendLine("계획 출처: " + SourceFingerprintSha256);
        sb.AppendLine("상태 지문: " + ObservationFingerprintSha256);
        sb.AppendLine("실제 실행 직전에는 재차 점검 및 별도 명시적 승인이 필요합니다.");
        foreach (var row in CurrentPaths.Take(maxPaths))
            sb.AppendLine($"[{row.Status}] [{row.ScopeLabel}] {row.RelativePath} :: {row.Detail}");
        if (CurrentPaths.Count > maxPaths)
            sb.AppendLine($"나머지 {CurrentPaths.Count - maxPaths}개 경로 생략");
        return sb.ToString();
    }
}

public static class EiaWorkspaceExecutionPreflight
{
    /// <summary>
    /// Reconstruct and validate all original relative paths, re-observe the disk,
    /// recompute the strict plan, and reject changed path states. NO writes.
    /// Previous acknowledgement cannot become a transferable or reusable write token.
    /// The caller must use a fresh UI session and no result can grant write access.
    /// </summary>
    public static EiaWorkspaceExecutionPreflightResult RecheckReadOnly(
        EiaWorkspaceExecutionReadiness reviewed,
        EiaWorkspaceProjectReviewAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default,
        Action<int, int>? onProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reviewed);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        EiaWorkspaceProjectReviewPlanner.RequireSameReview(acknowledgement, reviewed);
        if (reviewed.CanExecute || reviewed.ProductionWritesEnabled ||
            reviewed.ObservedPaths is null || reviewed.ObservedPaths.Count < 2 ||
            reviewed.ObservedPaths.Count > 5001 || reviewed.Blocked != 0 ||
            EiaWorkspaceCreationEngine.ProductionWritesEnabled)
            throw new InvalidDataException("미승인 또는 변경된 사업 계획은 재점검할 수 없습니다.");

        // Reject a forged/stale original observation BEFORE touching the disk.
        var original = new EiaFolderPathObservations(reviewed.BaseDirectory,
            reviewed.WorkspacePath, reviewed.ObservedPaths, reviewed.Existing,
            reviewed.Missing, reviewed.Blocked, false, true);
        EiaWorkspaceCreationEngine.RequireSameObservedSnapshot(original,
            reviewed.SourceFingerprintSha256, reviewed.StrictPlanFingerprintSha256);
        string[] relativePaths = reviewed.ObservedPaths.Skip(1)
            .Select(x => x.RelativePath).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var fresh = EiaWorkspaceCreationEngine.DiagnoseReadOnlyProposedPaths(
            reviewed.BaseDirectory, reviewed.WorkspaceFolderName, relativePaths,
            reviewed.SourceLabel, cancellationToken, onProgress);
        cancellationToken.ThrowIfCancellationRequested();
        if (fresh.Blocked != 0)
            throw new InvalidDataException("재점검에서 충돌·접근 불가 경로가 발견됐습니다. 신규 계획과 위치 점검이 필요합니다.");
        var strict = EiaWorkspaceCreationEngine.PrepareReadOnlyProposedPaths(
            reviewed.BaseDirectory, reviewed.WorkspaceFolderName, relativePaths,
            reviewed.SourceFingerprintSha256, cancellationToken);
        EiaWorkspaceCreationEngine.RequireSameObservedSnapshot(fresh,
            reviewed.SourceFingerprintSha256, strict.FingerprintSha256);
        if (!string.Equals(strict.FingerprintSha256, reviewed.StrictPlanFingerprintSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(strict.WorkspacePath, reviewed.WorkspacePath,
                StringComparison.Ordinal))
            throw new InvalidDataException("검토 후 폴더 상태나 저장 위치가 변경됐습니다. 계획을 다시 계산하고 위치를 재점검하세요.");
        EiaWorkspaceProjectReviewPlanner.RequireSameReview(acknowledgement, reviewed);
        cancellationToken.ThrowIfCancellationRequested();
        var immutableRows = Array.AsReadOnly(fresh.Rows.Select(x => x with { }).ToArray());
        return new EiaWorkspaceExecutionPreflightResult(
            reviewed.BaseDirectory, reviewed.WorkspacePath, reviewed.SourceLabel,
            reviewed.SourceFingerprintSha256, strict.FingerprintSha256,
            immutableRows, fresh.Existing, fresh.Missing, 0, DateTimeOffset.UtcNow);
    }
}
