using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// DEV12: inspect the FULL company/client proposal as filesystem metadata ONLY.
/// Never interpret existing folders as company approval or permission to create.
/// The caller must independently check the latest persisted profile revision.
/// </summary>
public sealed record EiaProfileDestinationInspection(
    string BaseDirectory, string WorkspacePath, string ProfileId, int ProfileRevision,
    string AppliedFingerprintSha256, string PathInspectionFingerprintSha256,
    int BaselineDirectories, int AddedProposedDirectories, int ExistingBaselineDirectories,
    int MissingBaselineDirectories, int ExistingAddedDirectories, int MissingAddedDirectories,
    int TotalDirectories, bool ProductionWritesEnabled, bool RequiresManualReview);

public static class EiaWorkspaceProfileDestinationInspector
{
    public static EiaProfileDestinationInspection Inspect(
        string existingBaseDirectory, EiaWorkspaceProfileRevision revision,
        EiaCaseWorkspaceRequest request, IReadOnlyList<EiaScenarioSelection> selections,
        string expectedCompanyCode, string expectedRequirementCode,
        EiaWorkspaceUiPreview shownBaseline,
        EiaWorkspaceProfileProjectComparison shownComparison,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(shownBaseline);
        ArgumentNullException.ThrowIfNull(shownComparison);

        // A tampered or obsolete baseline cannot be inspected via a valid overlay.
        // The DEV11 inspector owns complete baseline-path validation.
        var baseline = EiaWorkspaceDestinationInspector.Inspect(
            existingBaseDirectory, request, selections, shownBaseline, cancellationToken);
        var current = EiaWorkspaceProfilePlanComparator.Compare(revision,
            request, selections, expectedCompanyCode, expectedRequirementCode);
        RequireUnchangedComparison(shownComparison, current);

        var applied = EiaWorkspaceProfilePlanner.BuildPreview(revision, request,
            selections, expectedCompanyCode, expectedRequirementCode);
        if (applied.SupportsFilesystemWrites || !applied.UserApprovalRequired ||
            applied.Profile.CompanyApprovalStatus != EiaWorkspaceProfilePlanner.ApprovalUnapproved ||
            applied.AppliedFingerprintSha256 != current.AppliedFingerprintSha256 ||
            applied.Overlay.SupportsFilesystemWrites || !applied.Overlay.UserApprovalRequired ||
            applied.Overlay.DirectoryPaths.Count != current.AppliedPathCount ||
            !new HashSet<string>(applied.Overlay.DirectoryPaths, StringComparer.OrdinalIgnoreCase)
                .SetEquals(current.PathDifferences.Select(x => x.RelativePath)) ||
            current.ProjectRoot != shownBaseline.RootFolderName ||
            current.BaselinePathCount != shownBaseline.Entries.Count ||
            current.AddedPathCount != current.AppliedPathCount - current.BaselinePathCount ||
            current.RemovedPathCount != 0)
            throw new InvalidDataException("회사 초안 적용 목록이 기본 계획 또는 현재 비교와 다릅니다.");

        // Uses the exact same path containment/reparse/file checks as the original
        // baseline inspection. Profile-specific paths are NEVER used to create folders.
        cancellationToken.ThrowIfCancellationRequested();
        var plan = EiaWorkspaceCreationEngine.PrepareReadOnlyProposedPaths(
            existingBaseDirectory, current.ProjectRoot, applied.Overlay.DirectoryPaths,
            applied.AppliedFingerprintSha256, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.ProductionWritesEnabled || !plan.RequiresUserApproval ||
            plan.SourceAuditFingerprint != current.AppliedFingerprintSha256 ||
            plan.BaseDirectory != baseline.BaseDirectory ||
            plan.WorkspacePath != baseline.WorkspacePath ||
            plan.Items.Count != current.AppliedPathCount + 1)
            throw new InvalidDataException("회사 초안 경로 점검이 미승인 읽기 전용 계약과 다릅니다.");

        var original = new HashSet<string>(shownBaseline.Entries.Select(x => x.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        int existingBase = 0, missingBase = 0, existingAdded = 0, missingAdded = 0;
        foreach (var item in plan.Items)
        {
            bool baseItem = item.RelativePath == current.ProjectRoot || original.Contains(item.RelativePath);
            if (baseItem)
            {
                if (item.BeforeState == "EXISTS_DIRECTORY") existingBase++;
                else missingBase++;
            }
            else
            {
                if (item.BeforeState == "EXISTS_DIRECTORY") existingAdded++;
                else missingAdded++;
            }
        }
        if (existingBase + missingBase != current.BaselinePathCount + 1 ||
            existingAdded + missingAdded != current.AddedPathCount ||
            existingBase + missingBase + existingAdded + missingAdded != plan.Items.Count)
            throw new InvalidDataException("회사 초안 기존/추가 폴더 집계가 일치하지 않습니다.");

        return new(plan.BaseDirectory, plan.WorkspacePath, current.ProfileId,
            current.RevisionNumber, current.AppliedFingerprintSha256, plan.FingerprintSha256,
            current.BaselinePathCount + 1, current.AddedPathCount,
            existingBase, missingBase, existingAdded, missingAdded, plan.Items.Count,
            false, true);
    }

    /// <summary>DEV21: keep the applied strict status and displayed rows in sync.</summary>
    internal static void RequireConsistentObservation(
        EiaFolderPathObservations observed, string appliedFingerprint,
        string strictPlanFingerprint)
        => EiaWorkspaceCreationEngine.RequireSameObservedSnapshot(
            observed, appliedFingerprint, strictPlanFingerprint);

    /// <summary>
    /// DEV13 multi-path read-only diagnostics. The full displayed draft and
    /// comparison are checked BEFORE observing any profile-specific path.
    /// No blocked row is evidence of approval or a permission to write.
    /// </summary>
    public static EiaFolderPathObservations Diagnose(
        string existingBaseDirectory, EiaWorkspaceProfileRevision revision,
        EiaCaseWorkspaceRequest request, IReadOnlyList<EiaScenarioSelection> selections,
        string expectedCompanyCode, string expectedRequirementCode,
        EiaWorkspaceUiPreview shownBaseline,
        EiaWorkspaceProfileProjectComparison shownComparison,
        CancellationToken cancellationToken = default, Action<int, int>? onProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(shownComparison);
        var baselineAudit = EiaWorkspaceDestinationInspector.RequireFreshShownDraft(
            request, selections, shownBaseline);
        var current = EiaWorkspaceProfilePlanComparator.Compare(revision,
            request, selections, expectedCompanyCode, expectedRequirementCode);
        RequireUnchangedComparison(shownComparison, current);
        var applied = EiaWorkspaceProfilePlanner.BuildPreview(revision, request,
            selections, expectedCompanyCode, expectedRequirementCode);
        if (applied.SupportsFilesystemWrites || !applied.UserApprovalRequired ||
            applied.Profile.CompanyApprovalStatus != EiaWorkspaceProfilePlanner.ApprovalUnapproved ||
            applied.AppliedFingerprintSha256 != current.AppliedFingerprintSha256 ||
            applied.Overlay.SupportsFilesystemWrites || !applied.Overlay.UserApprovalRequired ||
            applied.Overlay.DirectoryPaths.Count != current.AppliedPathCount ||
            !new HashSet<string>(applied.Overlay.DirectoryPaths, StringComparer.OrdinalIgnoreCase)
                .SetEquals(current.PathDifferences.Select(x => x.RelativePath)) ||
            current.ProjectRoot != baselineAudit.RootFolderName ||
            current.BaselinePathCount != baselineAudit.DirectoryEntries.Count ||
            current.AddedPathCount != current.AppliedPathCount - current.BaselinePathCount ||
            current.RemovedPathCount != 0)
            throw new InvalidDataException("회사 초안 경로 진단의 미승인 계약이 일치하지 않습니다.");
        var fullScan = EiaWorkspaceCreationEngine.DiagnoseReadOnlyProposedPaths(
            existingBaseDirectory, current.ProjectRoot, applied.Overlay.DirectoryPaths, "회사 추가",
            cancellationToken, onProgress);
        var baselinePaths = new HashSet<string>(
            baselineAudit.DirectoryEntries.Select(x => x.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        cancellationToken.ThrowIfCancellationRequested();
        var scopedRows = fullScan.Rows.Select(row => row with {
            ScopeLabel = row.ScopeLabel == "사업 루트" || baselinePaths.Contains(row.RelativePath)
                ? "기본안" : "회사 추가"
        }).ToArray();
        if (scopedRows.Length != current.AppliedPathCount + 1 ||
            scopedRows.Count(x => x.ScopeLabel == "기본안") != current.BaselinePathCount + 1 ||
            scopedRows.Count(x => x.ScopeLabel == "회사 추가") != current.AddedPathCount)
            throw new InvalidDataException("진단 결과의 기본/추가 폴더 범위가 일치하지 않습니다.");
        return fullScan with { Rows = Array.AsReadOnly(scopedRows) };
    }

    /// <summary>Verify the entire displayed comparison, not only an opaque digest.</summary>
    public static void RequireUnchangedComparison(
        EiaWorkspaceProfileProjectComparison shown, EiaWorkspaceProfileProjectComparison current)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(current);
        if (shown.SupportsFilesystemWrites || current.SupportsFilesystemWrites ||
            !shown.UserApprovalRequired || !current.UserApprovalRequired ||
            shown.ProjectRoot != current.ProjectRoot || shown.ProfileId != current.ProfileId ||
            shown.RevisionNumber != current.RevisionNumber ||
            shown.BaselineFingerprintSha256 != current.BaselineFingerprintSha256 ||
            shown.ProfileFingerprintSha256 != current.ProfileFingerprintSha256 ||
            shown.AppliedFingerprintSha256 != current.AppliedFingerprintSha256 ||
            shown.BaselinePathCount != current.BaselinePathCount ||
            shown.AppliedPathCount != current.AppliedPathCount ||
            shown.AddedPathCount != current.AddedPathCount ||
            shown.RemovedPathCount != current.RemovedPathCount ||
            shown.PathDifferences is null || current.PathDifferences is null ||
            shown.RuleDecisions is null || current.RuleDecisions is null ||
            shown.PendingSafetyGates is null || current.PendingSafetyGates is null ||
            !shown.PathDifferences.SequenceEqual(current.PathDifferences) ||
            !shown.RuleDecisions.SequenceEqual(current.RuleDecisions) ||
            !shown.PendingSafetyGates.SequenceEqual(current.PendingSafetyGates,
                StringComparer.Ordinal))
            throw new InvalidDataException("표시된 회사 초안 비교가 최신 내용과 다릅니다. 다시 비교하세요.");
    }
}
