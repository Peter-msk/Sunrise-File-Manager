using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// DEV5: portable, immutable draft PROFILE snapshots. Export/import operates on in-memory
/// JSON text only. A content digest is not a signature, company approval, or a licence to
/// write folders. No file I/O, network, registry, updater or local-settings mutation.
/// </summary>
public sealed record EiaWorkspaceProfileRevision(
    string SchemaVersion,
    string ProfileId,
    string CompanyCode,
    string RequirementCode,
    int RevisionNumber,
    string TemplateVersion,
    string OverlayPolicyVersion,
    string? PreviousRevisionSha256,
    IReadOnlyList<EiaOverlayEntry> Entries,
    IReadOnlyList<EiaOverlayResolution> Resolutions,
    string ChangeReason,
    bool UserPolicyReviewConfirmed,
    string CompanyApprovalStatus,
    string FingerprintSha256,
    bool SupportsFilesystemWrites);

public sealed record EiaWorkspaceProfileAppliedPreview(
    EiaWorkspaceProfileRevision Profile,
    EiaOverlayWorkspacePreview Overlay,
    string AppliedFingerprintSha256,
    string Advisory,
    bool UserApprovalRequired,
    bool SupportsFilesystemWrites);

public static class EiaWorkspaceProfilePlanner
{
    public const string Schema = "sunrise.eia.workspace.profile.draft.v1";
    public const string ApprovalUnapproved = "UNAPPROVED";
    private const int MaxJsonLength = 131072;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    /// <summary>Create an initial draft revision, without granting company approval.</summary>
    public static EiaWorkspaceProfileRevision CreateInitial(string profileId, string companyCode,
        string requirementCode, IReadOnlyList<EiaOverlayEntry> entries,
        IReadOnlyList<EiaOverlayResolution> resolutions, string changeReason,
        bool userPolicyReviewConfirmed)
        => CreateRevision(null, profileId, companyCode, requirementCode, entries,
            resolutions, changeReason, userPolicyReviewConfirmed);

    /// <summary>Append a new revision to an inspected prior draft; never rewrite the prior revision.</summary>
    public static EiaWorkspaceProfileRevision CreateNext(EiaWorkspaceProfileRevision previous,
        IReadOnlyList<EiaOverlayEntry> entries, IReadOnlyList<EiaOverlayResolution> resolutions,
        string changeReason, bool userPolicyReviewConfirmed)
    {
        Verify(previous);
        if (previous.RevisionNumber == int.MaxValue)
            throw new InvalidDataException("프로파일 개정번호 한도를 초과했습니다.");
        var next = CreateRevision(previous, previous.ProfileId, previous.CompanyCode,
            previous.RequirementCode, entries, resolutions, changeReason, userPolicyReviewConfirmed);
        if (EquivalentRules(previous, next))
            throw new InvalidDataException("변경 내용이 없는 개정본은 만들 수 없습니다.");
        return next;
    }

    /// <summary>Export a JSON snapshot as a string. The caller owns storage and approval.</summary>
    public static string ExportDraftJson(EiaWorkspaceProfileRevision revision)
    {
        Verify(revision);
        return JsonSerializer.Serialize(revision, JsonOptions) + "\n";
    }

    /// <summary>Import an untrusted JSON snapshot, reject mismatched contents and unsafe status.</summary>
    public static EiaWorkspaceProfileRevision ImportDraftJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonLength)
            throw new InvalidDataException("프로파일 JSON이 비었거나 크기 제한을 초과했습니다.");
        EiaWorkspaceProfileRevision imported;
        try
        {
            imported = JsonSerializer.Deserialize<EiaWorkspaceProfileRevision>(json, JsonOptions)
                ?? throw new InvalidDataException("프로파일 내용이 없습니다.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("프로파일 JSON 형식이 잘못되었습니다.", ex);
        }
        Verify(imported);
        return imported;
    }

    /// <summary>Check identity, immutable parent link and hash for two successive revisions.</summary>
    public static void VerifySuccessor(EiaWorkspaceProfileRevision previous,
        EiaWorkspaceProfileRevision successor)
    {
        Verify(previous);
        Verify(successor);
        if (successor.RevisionNumber != previous.RevisionNumber + 1
            || successor.ProfileId != previous.ProfileId
            || successor.CompanyCode != previous.CompanyCode
            || successor.RequirementCode != previous.RequirementCode
            || successor.PreviousRevisionSha256 != previous.FingerprintSha256
            || EquivalentRules(previous, successor))
            throw new InvalidDataException("이전 프로파일 개정본과의 연결이 일치하지 않습니다.");
    }

    /// <summary>Apply profile to an actual project as a read-only preview, never as mkdir.</summary>
    public static EiaWorkspaceProfileAppliedPreview BuildPreview(
        EiaWorkspaceProfileRevision profile, EiaCaseWorkspaceRequest request,
        IReadOnlyList<EiaScenarioSelection> scenarios,
        string expectedCompanyCode, string expectedRequirementCode)
    {
        Verify(profile);
        ValidateId(expectedCompanyCode, "적용대상 회사 식별자");
        ValidateId(expectedRequirementCode, "적용대상 과업지시서 식별자");
        if (!string.Equals(profile.CompanyCode, expectedCompanyCode, StringComparison.Ordinal)
            || !string.Equals(profile.RequirementCode, expectedRequirementCode, StringComparison.Ordinal))
            throw new InvalidDataException("프로파일의 회사 또는 과업지시서 식별자가 적용 대상과 다릅니다.");
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scenarios);
        var overlay = EiaWorkspaceOverlayPlanner.BuildPreview(request, scenarios,
            profile.Entries, profile.Resolutions, profile.UserPolicyReviewConfirmed);
        if (overlay.SupportsFilesystemWrites || !overlay.UserApprovalRequired
            || overlay.OverlayPolicyVersion != profile.OverlayPolicyVersion
            || overlay.Baseline.Baseline.TemplateVersion != profile.TemplateVersion)
            throw new InvalidDataException("프로파일에 기록된 원본 템플릿 또는 검토 상태가 달라졌습니다.");
        string hash = Hash(profile.FingerprintSha256 + "\n" + overlay.FingerprintSha256);
        return new(profile, overlay, hash,
            "프로파일 개정본 적용 미리보기입니다. 회사 표준·법정 절차·실제 대상 경로는 승인 전이며 파일을 만들지 않습니다.",
            true, false);
    }

    /// <summary>Validate the draft, all entry rules and the content hash.</summary>
    public static void Verify(EiaWorkspaceProfileRevision profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.SchemaVersion != Schema || profile.CompanyApprovalStatus != ApprovalUnapproved
            || profile.SupportsFilesystemWrites || profile.RevisionNumber < 1
            || (profile.RevisionNumber == 1) != (profile.PreviousRevisionSha256 is null))
            throw new InvalidDataException("승인 전·읽기 전용 프로파일 개정본만 허용합니다.");
        ValidateId(profile.ProfileId, "프로파일 ID");
        ValidateId(profile.CompanyCode, "회사 식별자");
        ValidateId(profile.RequirementCode, "과업지시서 식별자");
        var catalog = EiaWorkspacePreviewPlanner.LoadDraftCatalog();
        var policy = EiaWorkspaceOverlayPlanner.LoadDraftPolicy();
        if (profile.TemplateVersion != catalog.TemplateVersion
            || profile.OverlayPolicyVersion != policy.PolicyVersion)
            throw new InvalidDataException("템플릿/오버레이 기준 버전이 맞지 않습니다. 자동 마이그레이션하지 않습니다.");
        ValidateReason(profile.ChangeReason);
        if (profile.PreviousRevisionSha256 is not null)
            ValidateDigest(profile.PreviousRevisionSha256, "직전 개정 해시");
        if (profile.Entries is null || profile.Resolutions is null
            || profile.Entries.Count > 128 || profile.Resolutions.Count > 128
            || (profile.Entries.Count > 0 && !profile.UserPolicyReviewConfirmed))
            throw new InvalidDataException("프로파일 항목 개수 또는 명시적 검토 조건이 올바르지 않습니다.");
        ValidateCanonicalOrder(profile.Entries, profile.Resolutions);
        // Reuse DEV4's real rule validator rather than creating an inconsistent duplicate.
        var fixture = new EiaCaseWorkspaceRequest("PROFILE-CHECK", "프로파일검증",
            [new EiaServiceContract("C-001", "검증용 계약")],
            [new EiaAssessmentCase("A-001", "C-001", "environmental")],
            CoreMode: "minimal");
        var trial = EiaWorkspaceOverlayPlanner.BuildPreview(fixture, [],
            profile.Entries, profile.Resolutions, profile.UserPolicyReviewConfirmed);
        if (trial.SupportsFilesystemWrites || !trial.UserApprovalRequired)
            throw new InvalidDataException("프로파일 검증이 읽기 전용 조건을 위반했습니다.");
        ValidateDigest(profile.FingerprintSha256, "프로파일 해시");
        if (!string.Equals(profile.FingerprintSha256, ComputeFingerprint(profile), StringComparison.Ordinal))
            throw new InvalidDataException("프로파일 내용 해시가 일치하지 않습니다. 외부 수정 또는 손상 가능성.");
    }

    private static EiaWorkspaceProfileRevision CreateRevision(EiaWorkspaceProfileRevision? previous,
        string profileId, string companyCode, string requirementCode,
        IReadOnlyList<EiaOverlayEntry> entries, IReadOnlyList<EiaOverlayResolution> resolutions,
        string changeReason, bool userPolicyReviewConfirmed)
    {
        ValidateId(profileId, "프로파일 ID");
        ValidateId(companyCode, "회사 식별자");
        ValidateId(requirementCode, "과업지시서 식별자");
        ValidateReason(changeReason);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(resolutions);
        // Freeze and order the arrays so caller mutations cannot retroactively rewrite a revision.
        var entriesCopy = entries.Select(e => e is null ? throw new ArgumentNullException(nameof(entries)) : e)
            .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Source, StringComparer.Ordinal).ToArray();
        var resolutionsCopy = resolutions.Select(r => r is null ? throw new ArgumentNullException(nameof(resolutions)) : r)
            .OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var catalog = EiaWorkspacePreviewPlanner.LoadDraftCatalog();
        var policy = EiaWorkspaceOverlayPlanner.LoadDraftPolicy();
        var proto = new EiaWorkspaceProfileRevision(Schema, profileId, companyCode,
            requirementCode, previous?.RevisionNumber + 1 ?? 1, catalog.TemplateVersion,
            policy.PolicyVersion, previous?.FingerprintSha256,
            Array.AsReadOnly(entriesCopy), Array.AsReadOnly(resolutionsCopy),
            changeReason.Trim(), userPolicyReviewConfirmed, ApprovalUnapproved, "", false);
        var result = proto with { FingerprintSha256 = ComputeFingerprint(proto) };
        Verify(result);
        return result;
    }

    private static bool EquivalentRules(EiaWorkspaceProfileRevision a, EiaWorkspaceProfileRevision b)
        => a.Entries.SequenceEqual(b.Entries) && a.Resolutions.SequenceEqual(b.Resolutions)
            && a.UserPolicyReviewConfirmed == b.UserPolicyReviewConfirmed;

    private static void ValidateCanonicalOrder(IReadOnlyList<EiaOverlayEntry> entries,
        IReadOnlyList<EiaOverlayResolution> resolutions)
    {
        if (!entries.SequenceEqual(entries.OrderBy(e => e?.Key, StringComparer.OrdinalIgnoreCase)
                  .ThenBy(e => e?.Source, StringComparer.Ordinal))
            || !resolutions.SequenceEqual(resolutions.OrderBy(r => r?.Key, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("프로파일 항목 순서가 정규화된 순서와 다릅니다.");
    }

    private static string ComputeFingerprint(EiaWorkspaceProfileRevision draft)
        => Hash(JsonSerializer.Serialize(draft with { FingerprintSha256 = "" }));

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ValidateDigest(string digest, string label)
    {
        if (digest is null || digest.Length != 64 || digest.Any(c => (c < '0' || c > '9') && (c < 'a' || c > 'f')))
            throw new InvalidDataException(label + " 형식이 올바르지 않습니다.");
    }

    private static void ValidateId(string id, string label)
    {
        string normalized = EiaWorkspacePreviewPlanner.ValidateSegment(id, label, 48);
        if (!string.Equals(normalized, id, StringComparison.Ordinal))
            throw new ArgumentException(label + "는 공백·유니코드 정규화가 완료되어 있어야 합니다.");
    }

    private static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 240 || reason.Any(char.IsControl)
            || !string.Equals(reason, reason.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("개정 근거는 1~240자이며 제어문자나 양쪽 공백을 허용하지 않습니다.");
    }
}
