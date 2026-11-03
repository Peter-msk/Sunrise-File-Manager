using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// DEV24: project-specific, observation-only handoff. No approved company folder
/// tree exists or is required. Optional company/client drafts remain comparison
/// inputs only. Windows native and NTFS/ACL/SMB acceptance still gate production
/// writes. This record never authorizes directory creation.
/// </summary>
public sealed record EiaWorkspaceExecutionReadiness(
    string BaseDirectory, string WorkspacePath, string SourceFingerprintSha256,
    string StrictPlanFingerprintSha256, IReadOnlyList<EiaFolderPathObservation> ObservedPaths,
    int Existing, int Missing, int Blocked, string SourceLabel,
    bool WindowsNativeVerified,
    bool NtfsAclVerified, bool NasSmbVerified, bool ProductionWritesEnabled)
{
    public bool CanExecute => false;

    /// <summary>Requested project folder name; confirmation is not write permission.</summary>
    public string WorkspaceFolderName => (Path.GetFileName(Path.TrimEndingDirectorySeparator(WorkspacePath)) ?? string.Empty);

    public string BuildReviewText(int maxPaths = 40)
    {
        if (maxPaths < 1 || maxPaths > 5001)
            throw new ArgumentOutOfRangeException(nameof(maxPaths));
        var sb = new StringBuilder();
        sb.AppendLine("Sunrise File Manager · DEV24 사업별 폴더 생성 준비 검토표 (읽기 전용)");
        sb.AppendLine("사업별 계획을 독립적으로 검토합니다. 별도 회사 표준 승인 트리는 필요하지 않으며 검토 확인은 실행 권한이 아닙니다.");
        sb.AppendLine("상태: BLOCKED_WRITE_VALIDATION · 생성 및 파일 변경 금지");
        sb.AppendLine("범위: " + SourceLabel);
        sb.AppendLine("기준 위치: " + BaseDirectory);
        sb.AppendLine("사업 폴더: " + WorkspacePath);
        sb.AppendLine($"검토 당시 기존 {Existing}개 / 생성 예상 {Missing}개 / 차단 {Blocked}개 / 총 {ObservedPaths.Count}개");
        sb.AppendLine("출처 지문: " + SourceFingerprintSha256);
        sb.AppendLine("경로 관찰 지문: " + StrictPlanFingerprintSha256);
        sb.AppendLine("필수 미완료: 운영용 생성 엔진, Windows 네이티브 시험, NTFS/ACL, SMB/NAS 실기");
        sb.AppendLine("중요: 검사 이후 파일시스템이 바뀔 수 있습니다. 향후 실제 실행 직전에 별도 재점검과 새 사용자 승인이 필요합니다.");
        sb.AppendLine("--- 검토 당시 경로 (표시 제한 있음) ---");
        foreach (var row in ObservedPaths.Take(maxPaths))
            sb.AppendLine($"[{row.Status}] [{row.ScopeLabel}] {row.RelativePath}");
        if (ObservedPaths.Count > maxPaths)
            sb.AppendLine($"... 나머지 {ObservedPaths.Count - maxPaths}개 경로는 진단 탭에서 확인");
        return sb.ToString();
    }
}

public static class EiaWorkspaceExecutionReadinessPlanner
{
    /// <summary>
    /// Construct only from a successful, matched pair of existing read-only scans.
    /// Pure data transformation after the scan; it has no filesystem write method.
    /// A blocked/mismatched observation fails closed and cannot produce a summary.
    /// </summary>
    public static EiaWorkspaceExecutionReadiness PrepareReadOnlyReview(
        EiaFolderPathObservations observed, string sourceFingerprintSha256,
        string strictPlanFingerprintSha256, string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(sourceLabel) || sourceLabel.Length > 100)
            throw new InvalidDataException("생성 준비 검토의 출처 범위가 올바르지 않습니다.");
        EiaWorkspaceCreationEngine.RequireSameObservedSnapshot(
            observed, sourceFingerprintSha256, strictPlanFingerprintSha256);
        if (EiaWorkspaceCreationEngine.ProductionWritesEnabled ||
            observed.ProductionWritesEnabled || !observed.RequiresManualReview ||
            observed.Blocked != 0 || observed.Rows.Count != observed.Existing + observed.Missing ||
            observed.Rows.Any(x => x is null || x.Status is not ("EXISTS_DIRECTORY" or "MISSING")))
            throw new InvalidDataException("실행 준비 화면에 차단되거나 미승인 상태가 있습니다.");
        // Copy the array: callers can mutate their original collection after this
        // method returns, but cannot change the displayed snapshot by aliasing.
        var frozen = Array.AsReadOnly(observed.Rows.Select(x => x with { }).ToArray());
        return new EiaWorkspaceExecutionReadiness(
            observed.BaseDirectory, observed.WorkspacePath,
            sourceFingerprintSha256, strictPlanFingerprintSha256, frozen,
            observed.Existing, observed.Missing, observed.Blocked,
            sourceLabel.Trim(), false, false, false, false);
    }
}
