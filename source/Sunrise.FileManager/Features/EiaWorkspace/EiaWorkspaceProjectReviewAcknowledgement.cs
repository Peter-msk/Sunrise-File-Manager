using System;
using System.IO;
using System.Linq;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// DEV24: explicit project-by-project review acknowledgement only.
/// No company-wide approved folder tree, token, license, or write permission.
/// Immutable snapshot that MUST NOT be interpreted as a file-operation grant.
/// </summary>
public sealed record EiaWorkspaceProjectReviewAcknowledgement(
    string BaseDirectory, string WorkspacePath, string SourceFingerprintSha256,
    string ObservationFingerprintSha256, string ScopeLabel, DateTimeOffset ReviewedAtUtc)
{
    public bool GrantsWritePermission => false;
}

public static class EiaWorkspaceProjectReviewPlanner
{
    public static EiaWorkspaceProjectReviewAcknowledgement AcknowledgeReadOnly(
        EiaWorkspaceExecutionReadiness review, string typedWorkspaceFolderName,
        DateTimeOffset reviewedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(review);
        // Never issue a write token; only source-backed read-only observations may
        // result in an acknowledgement for one precisely identified project.
        if (review.CanExecute || review.ProductionWritesEnabled ||
            review.Blocked != 0 || review.ObservedPaths is null ||
            review.ObservedPaths.Count != review.Existing + review.Missing ||
            string.IsNullOrWhiteSpace(review.SourceFingerprintSha256) ||
            review.SourceFingerprintSha256.Length != 64 ||
            string.IsNullOrWhiteSpace(review.StrictPlanFingerprintSha256) ||
            review.StrictPlanFingerprintSha256.Length != 64 ||
            review.SourceFingerprintSha256.Any(ch => !Uri.IsHexDigit(ch)) ||
            review.StrictPlanFingerprintSha256.Any(ch => !Uri.IsHexDigit(ch)) ||
            string.IsNullOrWhiteSpace(review.SourceLabel) ||
            string.IsNullOrWhiteSpace(review.WorkspacePath) ||
            string.IsNullOrWhiteSpace(review.BaseDirectory) ||
            string.IsNullOrWhiteSpace(review.WorkspaceFolderName) ||
            string.IsNullOrWhiteSpace(typedWorkspaceFolderName) ||
            !string.Equals(typedWorkspaceFolderName, review.WorkspaceFolderName,
                StringComparison.Ordinal) ||
            reviewedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("현재 사업의 폴더명을 정확히 다시 입력해야 읽기 전용 검토를 확인할 수 있습니다.");

        return new EiaWorkspaceProjectReviewAcknowledgement(
            review.BaseDirectory, review.WorkspacePath,
            review.SourceFingerprintSha256, review.StrictPlanFingerprintSha256,
            review.SourceLabel, reviewedAtUtc);
    }

    /// <summary>Reject cross-project, cross-path or drifted-review reuse.</summary>
    public static void RequireSameReview(
        EiaWorkspaceProjectReviewAcknowledgement acknowledgement,
        EiaWorkspaceExecutionReadiness current)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        ArgumentNullException.ThrowIfNull(current);
        if (acknowledgement.GrantsWritePermission || current.CanExecute ||
            current.ProductionWritesEnabled || current.Blocked != 0 ||
            !string.Equals(acknowledgement.BaseDirectory, current.BaseDirectory,
                StringComparison.Ordinal) ||
            !string.Equals(acknowledgement.WorkspacePath, current.WorkspacePath,
                StringComparison.Ordinal) ||
            !string.Equals(acknowledgement.SourceFingerprintSha256,
                current.SourceFingerprintSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(acknowledgement.ObservationFingerprintSha256,
                current.StrictPlanFingerprintSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(acknowledgement.ScopeLabel, current.SourceLabel,
                StringComparison.Ordinal))
            throw new InvalidDataException("다른 사업·경로·계획의 검토 확인은 재사용할 수 없습니다.");
    }
}
