using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// DEV7: review evidence is a human assertion, never proof of statutory compliance,
/// company approval, a signature or permission to create business folders.
/// References are opaque workflow IDs, not people's names or credentials.
/// </summary>
public sealed record EiaWorkspaceReviewEvidence(
    string ReviewReference, string BasisReference,
    bool CompanyStandardReviewed,
    bool ClientTermsReviewed,
    bool StatutoryApplicabilityReviewed,
    bool ContractsAndLineageReviewed,
    bool OriginalDesignAndSurveyDataReviewed,
    bool PendingRuntimeGatesAcknowledged);

/// <summary>
/// Immutable read-only snapshot of the evaluated profile and full proposed folder tree.
/// Its unkeyed SHA-256 detects accidental changes, NOT adversarial forgery or approval.
/// </summary>
public sealed record EiaWorkspaceReadinessSnapshot(
    string SchemaVersion, string RootFolderName,
    string ProfileId, int RevisionNumber, string ProfileFingerprintSha256,
    string AppliedFingerprintSha256, string ReviewReference, string BasisReference,
    IReadOnlyList<string> DirectoryPaths,
    IReadOnlyList<string> OutstandingHumanReviews,
    IReadOnlyList<string> UnverifiedRuntimeGates,
    string ReviewStatus, string FingerprintSha256,
    bool UserApprovalRequired, bool SupportsFilesystemWrites);

public static class EiaWorkspaceReadinessPlanner
{
    public const string SnapshotSchema = "sunrise.eia.workspace.readiness.preview.v1";
    public const string PendingStatus = "HUMAN_REVIEW_INCOMPLETE_READONLY";
    public const string ReviewedStatus = "HUMAN_REVIEW_RECORDED_READONLY";

    // Source-level checks cannot make any of these actual runtime checks PASS.
    private static readonly string[] UnverifiedExecutionChecks =
    [
        "COMPANY_OFFICIAL_APPROVAL_NOT_VERIFIED",
        "TARGET_PATH_ACL_AND_FREE_SPACE_NOT_VERIFIED",
        "JUNCTION_SYMLINK_AND_RACE_NOT_VERIFIED",
        "WINDOWS_NATIVE_EXECUTION_NOT_VERIFIED",
        "NAS_CLOUD_AND_PARTIAL_FAILURE_NOT_VERIFIED",
    ];

    /// <summary>Validate an entire in-memory immutable revision chain before previewing its latest entry.</summary>
    public static EiaWorkspaceReadinessSnapshot BuildFromRevisionChain(
        IReadOnlyList<EiaWorkspaceProfileRevision> revisions,
        EiaCaseWorkspaceRequest request,
        IReadOnlyList<EiaScenarioSelection> scenarios,
        EiaWorkspaceReviewEvidence evidence,
        string expectedCompany, string expectedRequirement)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        if (revisions.Count == 0 || revisions.Count > 1000)
            throw new InvalidDataException("검토할 프로파일 개정 이력은 1~1000개여야 합니다.");
        EiaWorkspaceProfileRevision? previous = null;
        foreach (var revision in revisions)
        {
            ArgumentNullException.ThrowIfNull(revision);
            EiaWorkspaceProfilePlanner.Verify(revision);
            if (previous is null)
            {
                if (revision.RevisionNumber != 1)
                    throw new InvalidDataException("프로파일 개정 검토는 1차 개정부터 시작해야 합니다.");
            }
            else
                EiaWorkspaceProfilePlanner.VerifySuccessor(previous, revision);
            previous = revision;
        }
        return BuildPreview(previous!, request, scenarios, evidence, expectedCompany, expectedRequirement);
    }

    /// <summary>Evaluate a single explicit revision without reading or changing any file.</summary>
    public static EiaWorkspaceReadinessSnapshot BuildPreview(
        EiaWorkspaceProfileRevision profile,
        EiaCaseWorkspaceRequest request,
        IReadOnlyList<EiaScenarioSelection> scenarios,
        EiaWorkspaceReviewEvidence evidence,
        string expectedCompany, string expectedRequirement)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        CheckReference(evidence.ReviewReference, "검토 기록번호");
        CheckReference(evidence.BasisReference, "검토 근거번호");
        EiaWorkspaceProfileAppliedPreview applied = EiaWorkspaceProfilePlanner.BuildPreview(
            profile, request, scenarios, expectedCompany, expectedRequirement);
        if (applied.SupportsFilesystemWrites || !applied.UserApprovalRequired ||
            applied.Overlay.SupportsFilesystemWrites ||
            applied.Overlay.Baseline.SupportsFilesystemWrites)
            throw new InvalidDataException("읽기 전용 미승인 미리보기만 통합 검토할 수 있습니다.");

        var missing = new List<string>();
        if (!evidence.CompanyStandardReviewed) missing.Add("COMPANY_STANDARD_REVIEW_REQUIRED");
        if (!evidence.ClientTermsReviewed) missing.Add("CLIENT_TERMS_REVIEW_REQUIRED");
        if (!evidence.StatutoryApplicabilityReviewed) missing.Add("STATUTORY_APPLICABILITY_REVIEW_REQUIRED");
        if (!evidence.ContractsAndLineageReviewed) missing.Add("CONTRACT_CASE_LINEAGE_REVIEW_REQUIRED");
        if (!evidence.OriginalDesignAndSurveyDataReviewed) missing.Add("ORIGINAL_DATA_SEPARATION_REVIEW_REQUIRED");
        if (!evidence.PendingRuntimeGatesAcknowledged) missing.Add("RUNTIME_GATES_ACKNOWLEDGEMENT_REQUIRED");
        string status = missing.Count > 0 ? PendingStatus : ReviewedStatus;

        // Even if a selected Sxx future safety gate is visible, it remains pending.
        string[] gates = UnverifiedExecutionChecks.Concat(applied.Overlay.PendingSafetyGates
            .Select(id => "SCENARIO_" + id + "_NOT_VERIFIED"))
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        string[] paths = applied.Overlay.DirectoryPaths.ToArray();
        if (paths.Length == 0 || paths.Length != paths.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new InvalidDataException("통합 검토 경로가 비어 있거나 중복됐습니다.");
        // Model-defined paths remain inside the already validated relative project tree.
        foreach (string path in paths)
            EiaWorkspacePreviewPlanner.ValidateRelativePath(path);

        var snapshot = new EiaWorkspaceReadinessSnapshot(SnapshotSchema,
            applied.Overlay.Baseline.Baseline.RootFolderName,
            profile.ProfileId, profile.RevisionNumber, profile.FingerprintSha256,
            applied.AppliedFingerprintSha256, evidence.ReviewReference, evidence.BasisReference,
            Array.AsReadOnly(paths), Array.AsReadOnly(missing.ToArray()), Array.AsReadOnly(gates),
            status, "", true, false);
        return snapshot with { FingerprintSha256 = Fingerprint(snapshot) };
    }

    /// <summary>
    /// Recompute the entire source-derived plan and ensure a previously shown snapshot is
    /// still current. Any changed contract, event, profile revision or review requires refresh.
    /// Call immediately before a later confirmation UI; never interpret as write permission.
    /// </summary>
    public static void VerifyStillCurrent(EiaWorkspaceReadinessSnapshot snapshot,
        EiaWorkspaceProfileRevision currentProfile, EiaCaseWorkspaceRequest currentRequest,
        IReadOnlyList<EiaScenarioSelection> currentScenarios, EiaWorkspaceReviewEvidence currentEvidence,
        string expectedCompany, string expectedRequirement)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != SnapshotSchema || !snapshot.UserApprovalRequired ||
            snapshot.SupportsFilesystemWrites ||
            !string.Equals(snapshot.FingerprintSha256, Fingerprint(snapshot), StringComparison.Ordinal))
            throw new InvalidDataException("미리보기 검토 지문·안전 상태가 변경됐습니다.");
        var current = BuildPreview(currentProfile, currentRequest, currentScenarios,
            currentEvidence, expectedCompany, expectedRequirement);
        if (!string.Equals(snapshot.FingerprintSha256, current.FingerprintSha256, StringComparison.Ordinal)
            || snapshot.RevisionNumber != current.RevisionNumber
            || !snapshot.DirectoryPaths.SequenceEqual(current.DirectoryPaths, StringComparer.Ordinal)
            || !snapshot.OutstandingHumanReviews.SequenceEqual(current.OutstandingHumanReviews, StringComparer.Ordinal)
            || !snapshot.UnverifiedRuntimeGates.SequenceEqual(current.UnverifiedRuntimeGates, StringComparer.Ordinal))
            throw new InvalidDataException("계약·평가·템플릿·검토 근거 중 하나가 변경되어 재검토해야 합니다.");
    }

    private static string Fingerprint(EiaWorkspaceReadinessSnapshot snapshot)
    {
        // Framed fields prevent separator ambiguities; unkeyed hash has no signing authority.
        string[] data =
        [
            snapshot.SchemaVersion, snapshot.RootFolderName, snapshot.ProfileId,
            snapshot.RevisionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            snapshot.ProfileFingerprintSha256, snapshot.AppliedFingerprintSha256,
            snapshot.ReviewReference, snapshot.BasisReference, snapshot.ReviewStatus,
            snapshot.UserApprovalRequired.ToString(), snapshot.SupportsFilesystemWrites.ToString(),
            .. snapshot.DirectoryPaths, "DIRECTORY_PATHS_END",
            .. snapshot.OutstandingHumanReviews, "HUMAN_REVIEWS_END",
            .. snapshot.UnverifiedRuntimeGates,
        ];
        var payload = new StringBuilder();
        foreach (string s in data)
        {
            if (s is null) throw new InvalidDataException("검토 결과 항목이 비어 있습니다.");
            payload.Append(s.Length).Append(':').Append(s);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())))
            .ToLowerInvariant();
    }

    private static void CheckReference(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 ||
            value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new ArgumentException(label + "는 1~64자의 영문·숫자·점·하이픈·밑줄 식별자만 허용합니다.");
    }
}
