using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// DEV11: actual destination status for an already displayed, UNAPPROVED proposal.
/// Reads existing filesystem metadata only. Cannot create, delete, or approve folders.
/// A successful inspection is not a promise that another process cannot change them.
/// </summary>
public sealed record EiaDestinationInspection(
    string BaseDirectory, string WorkspacePath, int ExistingDirectories,
    int MissingDirectories, int TotalDirectories, string PlanFingerprint,
    bool ProductionWritesEnabled, bool RequiresManualReview);

public static class EiaWorkspaceDestinationInspector
{
    internal static EiaWorkspaceFootprintAuditResult RequireFreshShownDraft(
        EiaCaseWorkspaceRequest request, IReadOnlyList<EiaScenarioSelection> selections,
        EiaWorkspaceUiPreview shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selections);
        if (shown.SupportsFilesystemWrites || !shown.UserApprovalRequired ||
            shown.CompanyPolicyVersion != EiaWorkspaceProfilePlanner.ApprovalUnapproved ||
            shown.Entries is null || shown.UnverifiedGates is null ||
            shown.ExcludedTypes is null)
            throw new InvalidDataException("미승인 읽기 전용 계획만 점검할 수 있습니다.");
        var footprint = EiaWorkspaceFootprintAudit.BuildDraftAudit(request, selections);
        if (footprint.SupportsFilesystemWrites || !footprint.UserApprovalRequired ||
            footprint.RootFolderName != shown.RootFolderName ||
            footprint.FingerprintSha256 != shown.FingerprintSha256 ||
            shown.Entries.Count != footprint.DirectoryEntries.Count ||
            !shown.Entries.Select(x => x.RelativePath).SequenceEqual(
                footprint.DirectoryEntries.Select(x => x.RelativePath), StringComparer.Ordinal))
            throw new InvalidDataException("화면 계획과 현재 업무 입력이 다릅니다. 폴더 계획을 다시 계산하세요.");
        return footprint;
    }

    /// <summary>DEV21: refuse differing first/second filesystem observations.</summary>
    internal static void RequireConsistentObservation(
        EiaFolderPathObservations observed, string footprintFingerprint,
        string strictPlanFingerprint)
        => EiaWorkspaceCreationEngine.RequireSameObservedSnapshot(
            observed, footprintFingerprint, strictPlanFingerprint);

    /// <summary>Per-path observation; never returns an approval or a write plan.</summary>
    public static EiaFolderPathObservations Diagnose(
        string existingBaseDirectory, EiaCaseWorkspaceRequest request,
        IReadOnlyList<EiaScenarioSelection> selections, EiaWorkspaceUiPreview shown,
        CancellationToken cancellationToken = default, Action<int, int>? onProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var footprint = RequireFreshShownDraft(request, selections, shown);
        return EiaWorkspaceCreationEngine.DiagnoseReadOnlyProposedPaths(
            existingBaseDirectory, footprint.RootFolderName,
            footprint.DirectoryEntries.Select(x => x.RelativePath).ToArray(), "기본안",
            cancellationToken, onProgress);
    }

    public static EiaDestinationInspection Inspect(
        string existingBaseDirectory, EiaCaseWorkspaceRequest request,
        IReadOnlyList<EiaScenarioSelection> selections, EiaWorkspaceUiPreview shown,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var footprint = RequireFreshShownDraft(request, selections, shown);
        // The existing planning engine already owns full-path containment, unsafe
        // links/reparse rejection and per-component Win32 filename validation.
        EiaFolderCreationPlan plan = EiaWorkspaceCreationEngine.Prepare(
            existingBaseDirectory, request, selections, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.ProductionWritesEnabled || !plan.RequiresUserApproval ||
            plan.SourceAuditFingerprint != shown.FingerprintSha256 ||
            plan.Items.Count != shown.Entries.Count + 1)
            throw new InvalidDataException("경로 점검의 읽기 전용 계약이 일치하지 않습니다.");
        return new EiaDestinationInspection(plan.BaseDirectory, plan.WorkspacePath,
            plan.Items.Count(x => x.BeforeState == "EXISTS_DIRECTORY"),
            plan.Items.Count(x => x.BeforeState == "MISSING"), plan.Items.Count,
            plan.FingerprintSha256, false, true);
    }
}
