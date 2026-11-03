using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// v1.7.9 DEV4: UI-only interpretation of immutable UNAPPROVED company/client drafts.
/// This inspector never enumerates business folders, saves a profile or authorizes writes.
/// Folder removals below describe a PROPOSAL delta; they never instruct a deletion.
/// </summary>
public sealed record EiaWorkspaceProfileUiEntry(string Key, string Source,
    string RelativePath, string MandatoryLabel, string ReviewStatus);
public sealed record EiaWorkspaceProfileUiConflict(string Key, string CompanyPath,
    string ClientPath, string SelectedSource, string Rationale, string ReviewStatus);
public sealed record EiaWorkspaceProfileUiChange(string ChangeType, string Key,
    string Source, string PreviousValue, string CurrentValue);
public sealed record EiaWorkspaceProfileUiInspection(
    string ProfileId, string CompanyCode, string RequirementCode, int RevisionNumber,
    string FingerprintSha256, string ChangeReason, string ApprovalStatus,
    IReadOnlyList<EiaWorkspaceProfileUiEntry> Entries,
    IReadOnlyList<EiaWorkspaceProfileUiConflict> Conflicts,
    IReadOnlyList<EiaWorkspaceProfileUiChange> Changes,
    string Advisory, bool UserApprovalRequired, bool SupportsFilesystemWrites);

public static class EiaWorkspaceProfileUiInspector
{
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;

    public static EiaWorkspaceProfileUiInspection Inspect(
        EiaWorkspaceProfileRevision current, EiaWorkspaceProfileRevision? previous = null)
    {
        EiaWorkspaceProfilePlanner.Verify(current);
        if (previous is not null)
            EiaWorkspaceProfilePlanner.VerifySuccessor(previous, current);
        if (current.CompanyApprovalStatus != EiaWorkspaceProfilePlanner.ApprovalUnapproved ||
            current.SupportsFilesystemWrites)
            throw new InvalidDataException("회사 승인 전 읽기 전용 프로파일만 조회할 수 있습니다.");

        var resolutions = current.Resolutions.ToDictionary(x => x.Key, IgnoreCase);
        var groups = current.Entries.GroupBy(x => x.Key, IgnoreCase)
            .OrderBy(x => x.Key, IgnoreCase).ToArray();
        var conflicts = new List<EiaWorkspaceProfileUiConflict>();
        var entryRows = new List<EiaWorkspaceProfileUiEntry>();
        foreach (var group in groups)
        {
            var sources = group.ToDictionary(x => x.Source, StringComparer.Ordinal);
            sources.TryGetValue("company", out var company);
            sources.TryGetValue("client", out var client);
            bool differs = company is not null && client is not null &&
                !IgnoreCase.Equals(company.RelativePath, client.RelativePath);
            if (differs && !resolutions.ContainsKey(group.Key))
                throw new InvalidDataException("회사와 발주처 경로가 충돌하지만 검토 근거가 없습니다.");
            string chosen = differs ? resolutions[group.Key].SelectedSource :
                company is not null ? "company" : "client";
            foreach (var item in group.OrderBy(x => x.Source, StringComparer.Ordinal))
                entryRows.Add(new(item.Key, item.Source, item.RelativePath,
                    item.CompanyMandatory ? "회사 필수 초안" : "일반 제안",
                    differs ? (item.Source == chosen ? "충돌 시 선택된 초안" : "충돌 시 미선택 초안") :
                    "중복 없이 제안됨 (공식 승인 아님)"));
            if (company is not null && client is not null)
            {
                string status = differs ? "명시적 충돌 결정(미승인)" : "동일 경로 제안(미승인)";
                string reason = differs ? resolutions[group.Key].Rationale :
                    "회사·발주처가 같은 경로를 제안함";
                conflicts.Add(new(group.Key, company.RelativePath, client.RelativePath,
                    chosen, reason, status));
            }
        }

        var changes = new List<EiaWorkspaceProfileUiChange>();
        if (previous is not null)
        {
            var oldEntries = previous.Entries.ToDictionary(x => (x.Key, x.Source),
                new KeySourceComparer());
            var newEntries = current.Entries.ToDictionary(x => (x.Key, x.Source),
                new KeySourceComparer());
            foreach (var key in oldEntries.Keys.Concat(newEntries.Keys)
                         .Distinct(new KeySourceComparer())
                         .OrderBy(x => x.Key, IgnoreCase).ThenBy(x => x.Source, StringComparer.Ordinal))
            {
                bool had = oldEntries.TryGetValue(key, out var before);
                bool has = newEntries.TryGetValue(key, out var after);
                if (!had) changes.Add(new("초안 제안 추가", key.Key, key.Source, "", Display(after!)));
                else if (!has) changes.Add(new("초안 제안 제외 (삭제 아님)", key.Key, key.Source, Display(before!), ""));
                else if (before!.RelativePath != after!.RelativePath ||
                         before.CompanyMandatory != after.CompanyMandatory)
                    changes.Add(new("초안 제안 변경", key.Key, key.Source, Display(before), Display(after)));
            }
            var oldResolutions = previous.Resolutions.ToDictionary(x => x.Key, IgnoreCase);
            var newResolutions = current.Resolutions.ToDictionary(x => x.Key, IgnoreCase);
            foreach (var key in oldResolutions.Keys.Concat(newResolutions.Keys)
                         .Distinct(IgnoreCase).OrderBy(x => x, IgnoreCase))
            {
                oldResolutions.TryGetValue(key, out var before);
                newResolutions.TryGetValue(key, out var after);
                string prior = before is null ? "" : before.SelectedSource + " / " + before.Rationale;
                string next = after is null ? "" : after.SelectedSource + " / " + after.Rationale;
                if (prior != next)
                    changes.Add(new("충돌 검토안 변경", key, "company/client", prior, next));
            }
        }
        return new(current.ProfileId, current.CompanyCode, current.RequirementCode,
            current.RevisionNumber, current.FingerprintSha256, current.ChangeReason,
            current.CompanyApprovalStatus,
            Array.AsReadOnly(entryRows.ToArray()), Array.AsReadOnly(conflicts.ToArray()),
            Array.AsReadOnly(changes.ToArray()),
            "미승인 회사·발주처 업무폴더 제안 비교입니다. 개정 비교는 실제 폴더 삭제·이동 또는 " +
            "법정 절차의 승인·완료를 의미하지 않습니다. SHA-256은 코드 서명이나 회사 승인이 아닙니다.",
            true, false);
    }

    private static string Display(EiaOverlayEntry entry) => entry.RelativePath +
        (entry.CompanyMandatory ? " [회사 필수 초안]" : "");

    private sealed class KeySourceComparer : IEqualityComparer<(string Key, string Source)>
    {
        public bool Equals((string Key, string Source) x, (string Key, string Source) y)
            => IgnoreCase.Equals(x.Key, y.Key) && StringComparer.Ordinal.Equals(x.Source, y.Source);
        public int GetHashCode((string Key, string Source) obj)
            => HashCode.Combine(IgnoreCase.GetHashCode(obj.Key), StringComparer.Ordinal.GetHashCode(obj.Source));
    }
}
