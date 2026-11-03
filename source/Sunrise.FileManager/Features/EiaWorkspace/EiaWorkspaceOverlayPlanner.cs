using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// DEV4: read-only company / purchaser-ToR overlay negotiation.
/// No statutory decisions, actual filesystem writes, folder renames, deletions, ACL changes,
/// report copying or automatic overriding of mandatory company proposals.
/// </summary>
public sealed record EiaOverlayEntry(string Source, string Key, string RelativePath,
    bool CompanyMandatory = false);
public sealed record EiaOverlayResolution(string Key, string SelectedSource, string Rationale,
    bool UserConfirmed = false);
public sealed record EiaOverlayOutcome(string Key, string Source, string RelativePath,
    string Decision, string Rationale);
public sealed record EiaOverlayWorkspacePreview(
    EiaScenarioWorkspacePreview Baseline, string OverlayPolicyVersion,
    IReadOnlyList<string> DirectoryPaths, IReadOnlyList<EiaOverlayOutcome> Decisions,
    IReadOnlyList<string> PendingSafetyGates, string FingerprintSha256,
    string Advisory, bool UserApprovalRequired, bool SupportsFilesystemWrites);

public sealed class EiaOverlayDraftPolicy
{
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; set; } = "";
    [JsonPropertyName("policy_version")] public string PolicyVersion { get; set; } = "";
    [JsonPropertyName("company_policy_version")] public string CompanyPolicyVersion { get; set; } = "";
    [JsonPropertyName("warning")] public string Warning { get; set; } = "";
    [JsonPropertyName("extension_segment")] public string ExtensionSegment { get; set; } = "";
    [JsonPropertyName("allowed_roots")] public string[] AllowedRoots { get; set; } = [];
}

public static class EiaWorkspaceOverlayPlanner
{
    private const string PolicyResource = "Sunrise.FileManager.EiaWorkspace.OverlayDraftPolicy";
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;

    public static EiaOverlayDraftPolicy LoadDraftPolicy()
    {
        using Stream stream = typeof(EiaWorkspaceOverlayPlanner).Assembly.GetManifestResourceStream(PolicyResource)
            ?? throw new InvalidOperationException("회사/과업지시서 오버레이 초안 리소스가 없습니다.");
        var policy = JsonSerializer.Deserialize<EiaOverlayDraftPolicy>(stream)
            ?? throw new InvalidDataException("오버레이 초안 정책이 비어 있습니다.");
        ValidatePolicy(policy);
        return policy;
    }

    public static EiaOverlayWorkspacePreview BuildPreview(
        EiaCaseWorkspaceRequest request, IReadOnlyList<EiaScenarioSelection> scenarios,
        IReadOnlyList<EiaOverlayEntry> entries, IReadOnlyList<EiaOverlayResolution> resolutions,
        bool UserPolicyReviewConfirmed = false)
        => BuildPreview(request, scenarios, entries, resolutions, UserPolicyReviewConfirmed, LoadDraftPolicy());

    public static EiaOverlayWorkspacePreview BuildPreview(
        EiaCaseWorkspaceRequest request, IReadOnlyList<EiaScenarioSelection> scenarios,
        IReadOnlyList<EiaOverlayEntry> entries, IReadOnlyList<EiaOverlayResolution> resolutions,
        bool UserPolicyReviewConfirmed, EiaOverlayDraftPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(resolutions);
        ValidatePolicy(policy);
        if (entries.Count > 128 || resolutions.Count > 128)
            throw new ArgumentException("업무 오버레이 항목은 각 128건을 넘을 수 없습니다.");
        if (entries.Count > 0 && !UserPolicyReviewConfirmed)
            throw new ArgumentException("회사 표준·과업지시서 차이 검토 확인이 필요합니다.");
        EiaScenarioWorkspacePreview baseline = EiaWorkspaceScenarioPlanner.BuildPreview(request, scenarios);
        if (baseline.SupportsFilesystemWrites || !baseline.UserApprovalRequired)
            throw new InvalidOperationException("읽기 전용 미승인 기준본만 확장할 수 있습니다.");

        var grouped = new Dictionary<string, Dictionary<string, EiaOverlayEntry>>(IgnoreCase);
        foreach (var input in entries)
        {
            ArgumentNullException.ThrowIfNull(input);
            string source = ValidateSource(input.Source);
            string key = EiaWorkspacePreviewPlanner.ValidateSegment(input.Key, "오버레이 식별자", 50);
            if (input.CompanyMandatory && source != "company")
                throw new ArgumentException("발주처 규정은 회사 필수 규정으로 지정할 수 없습니다.");
            string path = ValidateOverlayPath(input.RelativePath, policy);
            if (!grouped.TryGetValue(key, out var perSource))
                grouped.Add(key, perSource = new Dictionary<string, EiaOverlayEntry>(StringComparer.Ordinal));
            if (!perSource.TryAdd(source, new(source, key, path, input.CompanyMandatory)))
                throw new ArgumentException("같은 출처에서 오버레이 식별자가 중복되었습니다: " + key);
        }
        var decisionsByKey = new Dictionary<string, EiaOverlayResolution>(IgnoreCase);
        foreach (var decision in resolutions)
        {
            ArgumentNullException.ThrowIfNull(decision);
            string key = EiaWorkspacePreviewPlanner.ValidateSegment(decision.Key, "충돌 해결 식별자", 50);
            string source = ValidateSource(decision.SelectedSource);
            if (!decision.UserConfirmed || string.IsNullOrWhiteSpace(decision.Rationale)
                || decision.Rationale.Length > 240 || decision.Rationale.Any(char.IsControl))
                throw new ArgumentException("충돌 처리에는 담당자 확인과 근거 기록이 필요합니다.");
            if (!decisionsByKey.TryAdd(key, new(key, source, decision.Rationale.Trim(), true)))
                throw new ArgumentException("같은 충돌을 두 번 판정했습니다: " + key);
        }

        var applied = new List<EiaOverlayOutcome>();
        var chosen = new Dictionary<string, EiaOverlayEntry>(IgnoreCase);
        foreach (var group in grouped.OrderBy(x => x.Key, IgnoreCase))
        {
            var candidates = group.Value;
            bool conflict = candidates.Count == 2 &&
                !IgnoreCase.Equals(candidates["company"].RelativePath, candidates["client"].RelativePath);
            EiaOverlayEntry selected;
            string status;
            string rationale;
            if (conflict)
            {
                if (!decisionsByKey.TryGetValue(group.Key, out var decision))
                    throw new InvalidDataException("회사 표준과 과업지시서가 충돌합니다. 명시적 결정을 기록하세요: " + group.Key);
                if (candidates["company"].CompanyMandatory && decision.SelectedSource != "company")
                    throw new InvalidDataException("회사 필수 경로를 발주처 요청으로 대체할 수 없습니다: " + group.Key);
                selected = candidates[decision.SelectedSource];
                status = "EXPLICIT_CONFLICT_RESOLUTION";
                rationale = decision.Rationale;
            }
            else
            {
                if (decisionsByKey.ContainsKey(group.Key))
                    throw new ArgumentException("충돌이 없는 항목에 불필요한 승인결정이 있습니다: " + group.Key);
                selected = candidates.TryGetValue("company", out var company) ? company : candidates["client"];
                status = candidates.Count == 2 ? "AGREED_IDENTICAL_PATH" : "ADDITIVE_PROPOSAL";
                rationale = candidates.Count == 2 ? "두 출처의 제안 경로가 동일하여 하나로 통합" : "요청 출처의 추가 폴더";
            }
            chosen.Add(group.Key, selected);
            applied.Add(new(group.Key, selected.Source, selected.RelativePath, status, rationale));
        }
        foreach (var decision in decisionsByKey)
            if (!grouped.TryGetValue(decision.Key, out var candidates)
                || candidates.Count != 2
                || IgnoreCase.Equals(candidates["company"].RelativePath, candidates["client"].RelativePath))
                throw new ArgumentException("존재하지 않는 충돌에 대한 승인결정입니다: " + decision.Key);

        // Two distinct business keys must NEVER silently share a folder. Identical paths
        // from separate sources are deduplicated only when they represent the SAME key.
        var assignedPaths = new List<(string Key, string Path)>();
        foreach (var row in chosen)
        {
            string proposed = row.Value.RelativePath;
            foreach (var previous in assignedPaths)
                if (IgnoreCase.Equals(proposed, previous.Path)
                    || proposed.StartsWith(previous.Path + "/", StringComparison.OrdinalIgnoreCase)
                    || previous.Path.StartsWith(proposed + "/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("서로 다른 업무 항목이 동일 경로를 사용합니다: " + row.Key);
            assignedPaths.Add((row.Key, proposed));
        }

        var paths = new HashSet<string>(baseline.DirectoryPaths, IgnoreCase);
        foreach (var row in chosen.OrderBy(x => x.Key, IgnoreCase))
        {
            string path = row.Value.RelativePath;
            if (baseline.Baseline.RootFolderName.Length + 1 + path.Length > 240)
                throw new InvalidDataException("업무 추가 폴더가 프로젝트 경로 예산을 초과했습니다.");
            string[] parts = path.Split('/');
            for (int i = 1; i <= parts.Length; i++)
                paths.Add(string.Join("/", parts.Take(i)));
        }
        var orderedPaths = paths.OrderBy(x => x, IgnoreCase).ToArray();
        var orderedDecisions = applied.OrderBy(x => x.Key, IgnoreCase).ToArray();
        var pending = baseline.Outcomes
            .Where(x => x.OutcomeKind == "FUTURE_SAFETY_GATE_NOT_VERIFIED")
            .Select(x => x.ScenarioId).Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        string canonical = baseline.FingerprintSha256 + "\n" + policy.PolicyVersion + "\n"
            + string.Join("\n", orderedDecisions.Select(x =>
                $"{x.Key.ToUpperInvariant()}|{x.Source}|{x.RelativePath}|{x.Decision}|{x.Rationale}"))
            + "\n" + string.Join("\n", orderedPaths);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new(baseline, policy.PolicyVersion, Array.AsReadOnly(orderedPaths),
            Array.AsReadOnly(orderedDecisions), Array.AsReadOnly(pending), hash,
            policy.Warning + " 실제 경로·ACL·정션·NAS·권한은 미검증. 필수 경로를 삭제·이동하지 않습니다.",
            true, false);
    }

    private static string ValidateSource(string? value)
    {
        if (value is not ("company" or "client"))
            throw new ArgumentException("오버레이 출처는 company 또는 client만 허용합니다.");
        return value;
    }

    private static string ValidateOverlayPath(string path, EiaOverlayDraftPolicy policy)
    {
        string validated = EiaWorkspacePreviewPlanner.ValidateRelativePath(path);
        var pieces = validated.Split('/');
        if (pieces.Length < 3 || pieces.Length > 8
            || !policy.AllowedRoots.Contains(pieces[0], StringComparer.Ordinal)
            || pieces[1] != policy.ExtensionSegment)
            throw new InvalidDataException("회사/발주처 추가 폴더는 지정된 공통업무 영역의 확장 경로만 허용합니다.");
        return validated;
    }

    private static void ValidatePolicy(EiaOverlayDraftPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var draft = EiaWorkspacePreviewPlanner.LoadDraftCatalog();
        if (policy.SchemaVersion != "sunrise.eia.workspace.overlay.v1"
            || policy.PolicyVersion != "0.4.0-DRAFT"
            || policy.CompanyPolicyVersion != "UNAPPROVED"
            || policy.ExtensionSegment != "80_업무표준_확장"
            || policy.AllowedRoots.Length != 8
            || !policy.AllowedRoots.SequenceEqual(draft.CoreRoots)
            || string.IsNullOrWhiteSpace(policy.Warning))
            throw new InvalidDataException("회사/과업지시서 초안의 형식 또는 공통 루트가 맞지 않습니다.");
    }
}
