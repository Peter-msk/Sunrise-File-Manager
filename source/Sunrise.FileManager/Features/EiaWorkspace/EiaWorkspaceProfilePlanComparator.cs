using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// DEV5: Compare a project-specific scenario baseline against an immutable UNAPPROVED
/// company/client profile. Differences are proposed directories, NEVER disk operations.
/// Existing baseline paths may not disappear. No I/O, no company/legal approvals.
/// </summary>
public sealed record EiaWorkspaceProfilePathDifference(
    string RelativePath, string ChangeLabel, string Origin, string RuleKeys, string Rationale);
public sealed record EiaWorkspaceProfileDecisionRow(
    string Key, string Source, string RelativePath, string Decision, string Rationale);
public sealed record EiaWorkspaceProfileProjectComparison(
    string ProjectRoot, string ProfileId, int RevisionNumber,
    string BaselineFingerprintSha256, string ProfileFingerprintSha256,
    string AppliedFingerprintSha256, int BaselinePathCount, int AppliedPathCount,
    int AddedPathCount, int RemovedPathCount,
    IReadOnlyList<EiaWorkspaceProfilePathDifference> PathDifferences,
    IReadOnlyList<EiaWorkspaceProfileDecisionRow> RuleDecisions,
    IReadOnlyList<string> PendingSafetyGates,
    string Advisory, bool UserApprovalRequired, bool SupportsFilesystemWrites);

public static class EiaWorkspaceProfilePlanComparator
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static EiaWorkspaceProfileProjectComparison Compare(
        EiaWorkspaceProfileRevision profile, EiaCaseWorkspaceRequest request,
        IReadOnlyList<EiaScenarioSelection> scenarios,
        string expectedCompanyCode, string expectedRequirementCode)
    {
        // Verify the complete profile, company/ToR identity, existing case lineage and
        // scenario scopes in the SAME existing planners used by the original preview.
        EiaWorkspaceProfileAppliedPreview applied = EiaWorkspaceProfilePlanner.BuildPreview(
            profile, request, scenarios, expectedCompanyCode, expectedRequirementCode);
        EiaOverlayWorkspacePreview overlay = applied.Overlay;
        EiaScenarioWorkspacePreview baseline = overlay.Baseline;
        if (applied.SupportsFilesystemWrites || overlay.SupportsFilesystemWrites ||
            baseline.SupportsFilesystemWrites || !applied.UserApprovalRequired ||
            !overlay.UserApprovalRequired || !baseline.UserApprovalRequired ||
            profile.CompanyApprovalStatus != EiaWorkspaceProfilePlanner.ApprovalUnapproved)
            throw new InvalidDataException("미승인·읽기 전용 계획만 비교할 수 있습니다.");

        var original = new HashSet<string>(baseline.DirectoryPaths, PathComparer);
        var proposed = new HashSet<string>(overlay.DirectoryPaths, PathComparer);
        if (original.Count != baseline.DirectoryPaths.Count ||
            proposed.Count != overlay.DirectoryPaths.Count || !original.IsSubsetOf(proposed))
            throw new InvalidDataException("기본 폴더를 제거하거나 경로가 중복된 회사 초안은 적용할 수 없습니다.");
        string root = baseline.Baseline.RootFolderName;
        var decisions = overlay.Decisions.Select(x => new EiaWorkspaceProfileDecisionRow(
            x.Key, x.Source, x.RelativePath, x.Decision, x.Rationale)).ToArray();
        var added = proposed.Except(original, PathComparer).OrderBy(x => x, PathComparer).ToArray();
        var delta = new List<EiaWorkspaceProfilePathDifference>(proposed.Count);
        foreach (var path in proposed.OrderBy(x => x, PathComparer))
        {
            if (original.Contains(path))
            {
                delta.Add(new(path, "기본 계획 유지", "기존 계약·평가업무·상황 모델", "", "원본 제안 구조 유지"));
                continue;
            }
            // New parent directories are attributed to rules whose full target is below them.
            // A distinct key's actual descendant must never be treated as an unrelated rule.
            var sources = decisions.Where(x =>
                PathComparer.Equals(x.RelativePath, path) ||
                x.RelativePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(x.RelativePath + "/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Key, PathComparer).ToArray();
            if (sources.Length == 0)
                throw new InvalidDataException("출처가 확인되지 않은 회사·발주처 폴더 제안이 있습니다.");
            delta.Add(new(path, "회사·발주처 초안 추가 (생성 아님)",
                string.Join(", ", sources.Select(x => x.Source).Distinct(StringComparer.Ordinal)),
                string.Join(", ", sources.Select(x => x.Key).Distinct(PathComparer)),
                string.Join(" / ", sources.Select(x => x.Rationale).Distinct(StringComparer.Ordinal))));
        }
        if (delta.Count != proposed.Count || delta.Count(x => x.ChangeLabel.StartsWith("회사·", StringComparison.Ordinal)) != added.Length)
            throw new InvalidDataException("회사·발주처 비교 집계가 일치하지 않습니다.");
        return new(root, profile.ProfileId, profile.RevisionNumber,
            baseline.FingerprintSha256, profile.FingerprintSha256, applied.AppliedFingerprintSha256,
            original.Count, proposed.Count, added.Length, 0,
            Array.AsReadOnly(delta.ToArray()), Array.AsReadOnly(decisions),
            Array.AsReadOnly(overlay.PendingSafetyGates.ToArray()),
            "미승인 초안의 현재 사업 적용 비교일 뿐입니다. 기존 폴더 제안 제외 0건, " +
            "실제 폴더 삭제·생성·이동·권한 변경 없음. 담당자 검토는 회사 승인·법정 인정이 아닙니다.",
            true, false);
    }
}
