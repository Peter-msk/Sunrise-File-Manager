using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// Read-only plan and observation engine; isolated sandbox test writer stays
/// compilation-gated. v1.8.1 production folder creation is isolated in a
/// separate, explicitly approved native command with no file operations.
/// Company/client draft profiles remain unapproved, and originals stay intact.
/// </summary>
public sealed record EiaFolderCreationItem(string RelativePath, string AbsolutePath, string BeforeState);
public sealed record EiaFolderCreationPlan(
    string BaseDirectory, string RootFolderName, string WorkspacePath,
    string SourceAuditFingerprint, IReadOnlyList<EiaFolderCreationItem> Items,
    string FingerprintSha256, bool RequiresUserApproval, bool ProductionWritesEnabled);
public sealed record EiaFolderCreationOutcome(string RelativePath, string Outcome, string Detail);
/// <summary>DEV13 observation only. BLOCKED rows cannot authorize a filesystem write.</summary>
public sealed record EiaFolderPathObservation(
    string ScopeLabel, string RelativePath, string AbsolutePath,
    string Status, string Detail)
{
    public string StatusLabel => Status switch
    {
        "BLOCKED" => "충돌/접근 불가",
        "EXISTS_DIRECTORY" => "기존 폴더",
        "MISSING" => "새 폴더 필요",
        "CREATED" => "생성됨",
        "ALREADY_EXISTS" => "기존 폴더 유지",
        "FAILED" => "실패/재점검 필요",
        "NOT_ATTEMPTED" => "중단/미시도",
        _ => "검토 필요"
    };
}
public sealed record EiaFolderPathObservations(
    string BaseDirectory, string WorkspacePath, IReadOnlyList<EiaFolderPathObservation> Rows,
    int Existing, int Missing, int Blocked, bool ProductionWritesEnabled,
    bool RequiresManualReview);

public sealed record EiaFolderCreationResult(
    string RootFolderName, string PlanFingerprintSha256,
    IReadOnlyList<EiaFolderCreationOutcome> Outcomes,
    int Created, int AlreadyExists, int Failed, int NotAttempted,
    bool Completed, bool RequiresManualReview);

public static class EiaWorkspaceCreationEngine
{
    public const string SourceSchema = "sunrise.eia.workspace.creation.preview.v1";
    public const bool ProductionWritesEnabled = false;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Only a source-derived preview. Never creates any directory.</summary>
    public static EiaFolderCreationPlan Prepare(string existingBaseDirectory,
        EiaCaseWorkspaceRequest request, IReadOnlyList<EiaScenarioSelection> scenarios,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scenarios);
        var audit = EiaWorkspaceFootprintAudit.BuildDraftAudit(request, scenarios);
        if (audit.SupportsFilesystemWrites || !audit.UserApprovalRequired ||
            audit.DirectoryEntries.Count == 0 || audit.DirectoryEntries.Count > 5000)
            throw new InvalidDataException("승인 전 읽기 전용 폴더 감사 결과만 사용할 수 있습니다.");
        return PrepareReadOnlyProposedPaths(existingBaseDirectory, audit.RootFolderName,
            audit.DirectoryEntries.Select(x => x.RelativePath).ToArray(), audit.FingerprintSha256,
            cancellationToken);
    }

    /// <summary>
    /// Shared read-only path inspector for baseline and UNAPPROVED profile proposals.
    /// Only planner-derived relative paths may call this internal method. The product
    /// never exposes a writer and does not take an externally supplied approval.
    /// </summary>
    internal static EiaFolderCreationPlan PrepareReadOnlyProposedPaths(
        string existingBaseDirectory, string rootFolderName,
        IReadOnlyList<string> plannerPaths, string sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(plannerPaths);
        if (plannerPaths.Count == 0 || plannerPaths.Count > 5000 ||
            string.IsNullOrWhiteSpace(sourceFingerprint) || sourceFingerprint.Length != 64 ||
            sourceFingerprint.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException("미승인 계획의 경로 수 또는 출처 지문이 잘못됐습니다.");
        // DEV18: a malformed or parentless proposed tree must never reach disk
        // inspection or the sandbox creation path. This is a pure input check.
        RequireClosedProposedPathTree(plannerPaths, cancellationToken);
        string basePath = ValidateBaseDirectory(existingBaseDirectory, cancellationToken);
        string root = EiaWorkspacePreviewPlanner.ValidateSegment(rootFolderName,
            "사업 폴더 이름", 220);
        string workspace = ConstrainChild(basePath, root);
        var rels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var list = new List<EiaFolderCreationItem> { Inspect(root, workspace, cancellationToken) };
        foreach (var candidate in plannerPaths
            .OrderBy(x => x?.Count(ch => ch == '/') ?? -1)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = EiaWorkspacePreviewPlanner.ValidateRelativePath(candidate);
            string full = ConstrainChild(workspace,
                relative.Replace('/', Path.DirectorySeparatorChar));
            if (!rels.Add(root + "/" + relative))
                throw new InvalidDataException("폴더 미리보기에 대소문자 무시 중복이 있습니다.");
            list.Add(Inspect(relative, full, cancellationToken));
        }
        // Existing files, junctions, reparse points and links fail closed.
        if (list.Any(i => i.BeforeState != "MISSING" && i.BeforeState != "EXISTS_DIRECTORY"))
            throw new InvalidDataException("기존 파일 또는 연결 지점과 충돌합니다.");
        cancellationToken.ThrowIfCancellationRequested();
        string hash = ComputeFingerprint(basePath, root, sourceFingerprint, list);
        return new EiaFolderCreationPlan(basePath, root, workspace, sourceFingerprint,
            Array.AsReadOnly(list.ToArray()), hash, true, false);
    }

    /// <summary>
    /// Collects independent per-path observations instead of losing later conflicts
    /// on the first exception. It uses the same Win32 segment, containment and
    /// reparse/file validation as PrepareReadOnlyProposedPaths; NEVER creates files.
    /// A blocked result is descriptive only and MUST NOT become an approval.
    /// </summary>
    internal static EiaFolderPathObservations DiagnoseReadOnlyProposedPaths(
        string existingBaseDirectory, string rootFolderName,
        IReadOnlyList<string> plannerPaths, string scopeLabel,
        CancellationToken cancellationToken = default, Action<int, int>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(plannerPaths);
        if (plannerPaths.Count == 0 || plannerPaths.Count > 5000)
            throw new InvalidDataException("미승인 계획의 경로 수가 올바르지 않습니다.");
        cancellationToken.ThrowIfCancellationRequested();
        // DEV18: a malformed or parentless proposed tree must never reach disk
        // inspection or the sandbox creation path. This is a pure input check.
        RequireClosedProposedPathTree(plannerPaths, cancellationToken);
        string basePath = ValidateBaseDirectory(existingBaseDirectory, cancellationToken);
        string root = EiaWorkspacePreviewPlanner.ValidateSegment(rootFolderName,
            "사업 폴더 이름", 220);
        string workspace = ConstrainChild(basePath, root);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var rows = new List<EiaFolderPathObservation>();
        int total = plannerPaths.Count + 1;
        void Observe(string relative, string absolute, string label)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                bool exists = VerifyExistingChain(absolute, cancellationToken);
                rows.Add(new(label, relative, absolute,
                    exists ? "EXISTS_DIRECTORY" : "MISSING",
                    exists ? "기존 폴더 (수정 없음)" : "신규 폴더 필요 (생성하지 않음)"));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or
                UnauthorizedAccessException or System.Security.SecurityException or
                ArgumentException or NotSupportedException)
            {
                // DEV19: InvalidDataException is NOT an IOException. The shared
                // chain verifier throws it for existing files, reparse points and
                // path races. Record those per-path conflicts as BLOCKED, but keep
                // malformed source plans (validated above) as fatal preflight errors.
                // Do not return 'MISSING' on an inaccessible or ambiguous path.
                rows.Add(new(label, relative, absolute, "BLOCKED",
                    ex.GetType().Name + ": " + ex.Message));
            }
            // Throttle notifications: no per-path WPF dispatch for large workspaces.
            if (rows.Count == total || rows.Count % 25 == 0)
                onProgress?.Invoke(rows.Count, total);
        }
        Observe(root, workspace, "사업 루트");
        foreach (string candidate in plannerPaths
            .OrderBy(x => x?.Count(ch => ch == '/') ?? -1)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Planner paths remain mandatory validated inputs. This API must not
            // disguise malformed or duplicated source plans as mere filesystem errors.
            string relative = EiaWorkspacePreviewPlanner.ValidateRelativePath(candidate);
            string absolute = ConstrainChild(workspace,
                relative.Replace('/', Path.DirectorySeparatorChar));
            if (!seen.Add(root + "/" + relative))
                throw new InvalidDataException("진단 대상에 대소문자 무시 중복이 있습니다.");
            Observe(relative, absolute, scopeLabel);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(basePath, workspace, Array.AsReadOnly(rows.ToArray()),
            rows.Count(x => x.Status == "EXISTS_DIRECTORY"),
            rows.Count(x => x.Status == "MISSING"),
            rows.Count(x => x.Status == "BLOCKED"), false, true);
    }

    /// <summary>
    /// DEV21: compare a complete READ-ONLY observed snapshot with the subsequently
    /// recomputed strict preflight. No filesystem access or write is performed here.
    /// A concurrent user/process can mutate NTFS/SMB between the two scans, and a
    /// mismatch must not be displayed as one consistent verified result.
    /// This is an observation-consistency check, never TOCTOU-free approval.
    /// </summary>
    internal static void RequireSameObservedSnapshot(
        EiaFolderPathObservations observed, string sourceFingerprint,
        string strictPlanFingerprint)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.Rows is null || observed.ProductionWritesEnabled ||
            !observed.RequiresManualReview || observed.Rows.Count < 2 ||
            observed.Rows.Count > 5001 || observed.Blocked != 0 ||
            observed.Existing < 0 || observed.Missing < 0 ||
            observed.Existing + observed.Missing != observed.Rows.Count ||
            string.IsNullOrWhiteSpace(sourceFingerprint) || sourceFingerprint.Length != 64 ||
            sourceFingerprint.Any(ch => !Uri.IsHexDigit(ch)) ||
            string.IsNullOrWhiteSpace(strictPlanFingerprint) ||
            strictPlanFingerprint.Length != 64 ||
            strictPlanFingerprint.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException("읽기 전용 위치 진단의 상태 또는 출처 지문이 일치하지 않습니다.");

        string root = Path.GetFileName(Path.TrimEndingDirectorySeparator(observed.WorkspacePath));
        root = EiaWorkspacePreviewPlanner.ValidateSegment(root, "사업 폴더 이름", 220);
        string expectedWorkspace = ConstrainChild(observed.BaseDirectory, root);
        if (!string.Equals(observed.WorkspacePath, expectedWorkspace, PathComparison))
            throw new InvalidDataException("진단 작업공간 경로가 기준 경로와 일치하지 않습니다.");

        var items = new List<EiaFolderCreationItem>(observed.Rows.Count);
        var relatives = new List<string>(observed.Rows.Count - 1);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < observed.Rows.Count; i++)
        {
            var row = observed.Rows[i];
            if (row is null || row.Status is not ("MISSING" or "EXISTS_DIRECTORY"))
                throw new InvalidDataException("진단에 차단되거나 유효하지 않은 경로가 있습니다.");
            string relative = i == 0 ? root :
                EiaWorkspacePreviewPlanner.ValidateRelativePath(row.RelativePath);
            string absolute = i == 0 ? expectedWorkspace : ConstrainChild(expectedWorkspace,
                relative.Replace('/', Path.DirectorySeparatorChar));
            if (!string.Equals(relative, row.RelativePath, StringComparison.Ordinal) ||
                !string.Equals(absolute, row.AbsolutePath, PathComparison) ||
                !unique.Add(i == 0 ? root : root + "/" + relative))
                throw new InvalidDataException("진단 경로·순서·중복 상태가 일치하지 않습니다.");
            if (i != 0) relatives.Add(relative);
            items.Add(new EiaFolderCreationItem(relative, absolute, row.Status));
        }
        RequireClosedProposedPathTree(relatives);
        if (!string.Equals(ComputeFingerprint(observed.BaseDirectory, root,
                sourceFingerprint, items), strictPlanFingerprint,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "점검 중 폴더 상태가 변경됐습니다. 이전 진단표는 폐기하고 다시 점검하세요.");
    }

    /// <summary>
    /// DEV18: validate the complete UNAPPROVED proposal before any filesystem I/O.
    /// Each nested path must explicitly contain its parent. A missing parent can
    /// otherwise cause a partially created sandbox root before a late failure.
    /// This method does not inspect or modify the filesystem and grants no writes.
    /// </summary>
    internal static void RequireClosedProposedPathTree(IReadOnlyList<string> plannerPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plannerPaths);
        cancellationToken.ThrowIfCancellationRequested();
        if (plannerPaths.Count == 0 || plannerPaths.Count > 5000)
            throw new InvalidDataException("미승인 폴더 목록의 크기가 유효하지 않습니다.");
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in plannerPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = EiaWorkspacePreviewPlanner.ValidateRelativePath(candidate);
            if (!valid.Add(relative))
                throw new InvalidDataException("미승인 폴더 목록에 중복 경로가 있습니다: " + relative);
        }
        foreach (string relative in valid)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int split = relative.LastIndexOf('/');
            if (split >= 0 && !valid.Contains(relative[..split]))
                throw new InvalidDataException("자식 폴더의 상위 경로가 누락됐습니다: " + relative);
        }
    }

    private static string ValidateBaseDirectory(string? input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(input) || !Path.IsPathFullyQualified(input))
            throw new ArgumentException("기존에 존재하는 절대 경로의 기준 폴더가 필요합니다.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        if (!Directory.Exists(full) || File.Exists(full))
            throw new DirectoryNotFoundException("기준 폴더가 존재하지 않습니다.");
        if (full.Equals(Path.GetPathRoot(full), PathComparison))
            throw new InvalidDataException("드라이브 또는 파일시스템 최상위 경로는 허용하지 않습니다.");
        VerifyExistingChain(full, cancellationToken);
        return full;
    }

    private static string ConstrainChild(string parent, string child)
    {
        string full = Path.GetFullPath(Path.Combine(parent, child));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
                PathComparison) || full.Length > 245)
            throw new InvalidDataException("선택한 기준 폴더 밖으로 벗어나거나 실제 경로가 너무 깁니다.");
        return full;
    }

    private static EiaFolderCreationItem Inspect(string relative, string absolute,
        CancellationToken cancellationToken = default)
    {
        bool exists = VerifyExistingChain(absolute, cancellationToken);
        return new EiaFolderCreationItem(relative, absolute,
            exists ? "EXISTS_DIRECTORY" : "MISSING");
    }

    /// <summary>Checks every existing ancestor and rejects links, files, and junctions.</summary>
    internal static bool VerifyExistingChain(string absolute, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetPathRoot(absolute)
            ?? throw new InvalidDataException("경로의 루트를 확인할 수 없습니다.");
        string current = root;
        bool missingParent = false;
        foreach (string part in absolute[root.Length..].Split(Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = Path.Combine(current, part);
            // LinkTarget also catches some dangling links that attribute lookup can
            // otherwise mistake for an absent directory.
            if (new DirectoryInfo(current).LinkTarget is not null)
                throw new InvalidDataException("연결 지점 또는 손상된 링크 경로는 허용하지 않습니다: " + current);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { missingParent = true; continue; }
            catch (DirectoryNotFoundException) { missingParent = true; continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                new DirectoryInfo(current).LinkTarget is not null)
                throw new InvalidDataException("정션 또는 심볼릭 링크 경로는 허용하지 않습니다: " + current);
            if ((attributes & FileAttributes.Directory) == 0)
                throw new InvalidDataException("기존 파일이 폴더 생성을 방해합니다: " + current);
            if (missingParent)
                throw new InvalidDataException("상위 폴더의 상태가 실행 중 변경됐습니다.");
        }
        return !missingParent;
    }

    private static string ComputeFingerprint(string basePath, string root,
        string auditFingerprint, IReadOnlyList<EiaFolderCreationItem> entries)
    {
        var sb = new StringBuilder();
        void Add(string value) => sb.Append(value.Length).Append(':').Append(value);
        foreach (string field in new[] { SourceSchema, basePath, root, auditFingerprint,
            ProductionWritesEnabled.ToString() }) Add(field);
        foreach (var item in entries)
        {
            Add(item.RelativePath);
            Add(item.AbsolutePath);
            Add(item.BeforeState);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))
            .ToLowerInvariant();
    }

#if EIA_WORKSPACE_SANDBOX_TEST
    /// <summary>
    /// Only present in the separate sandbox test assembly. NEVER compiled into the product.
    /// The existing real-world folders cannot be selected as the test destination.
    /// Native relative opens reduce path substitution inside a pinned root, but this
    /// is NOT production approval or a verified TOCTOU-free NTFS/SMB writer.
    /// </summary>
    // Exact approval contract; no disk write before all fields are checked.
    // SHA-256 here is NOT an authorization token or a digital signature.
    // Every submitted field must match the independently recomputed plan.
    private static void RequireIdenticalApprovedPreview(
        EiaFolderCreationPlan shown, EiaFolderCreationPlan current)
    {
        if (shown.Items is null || current.Items is null ||
            !string.Equals(shown.BaseDirectory, current.BaseDirectory, StringComparison.Ordinal) ||
            !string.Equals(shown.RootFolderName, current.RootFolderName, StringComparison.Ordinal) ||
            !string.Equals(shown.WorkspacePath, current.WorkspacePath, StringComparison.Ordinal) ||
            !string.Equals(shown.SourceAuditFingerprint, current.SourceAuditFingerprint, StringComparison.Ordinal) ||
            !string.Equals(shown.FingerprintSha256, current.FingerprintSha256, StringComparison.Ordinal) ||
            shown.RequiresUserApproval != current.RequiresUserApproval ||
            shown.ProductionWritesEnabled != current.ProductionWritesEnabled ||
            shown.Items.Count != current.Items.Count)
            throw new InvalidDataException("DEV5: 표시·승인된 폴더 생성 계획 전체가 현재 계획과 다릅니다.");
        for (int index = 0; index < current.Items.Count; index++)
        {
            var shownItem = shown.Items[index];
            var currentItem = current.Items[index];
            if (shownItem is null ||
                !string.Equals(shownItem.RelativePath, currentItem.RelativePath, StringComparison.Ordinal) ||
                !string.Equals(shownItem.AbsolutePath, currentItem.AbsolutePath, StringComparison.Ordinal) ||
                !string.Equals(shownItem.BeforeState, currentItem.BeforeState, StringComparison.Ordinal))
                throw new InvalidDataException("DEV5: 승인되지 않은 폴더 목록·순서·경로·상태 변경을 차단했습니다.");
        }
    }

    public static EiaFolderCreationResult ExecuteInSandbox(EiaFolderCreationPlan shown,
        string typedConfirmation, string sandboxMarkerPath,
        EiaCaseWorkspaceRequest currentRequest, IReadOnlyList<EiaScenarioSelection> currentScenarios,
        CancellationToken cancellationToken = default,
        Action<int, string>? sandboxBeforeAttempt = null,
        Action<int, string>? sandboxAfterNativeAttempt = null)
    {
        ArgumentNullException.ThrowIfNull(shown);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("격리 폴더 생성 시험은 Windows에서만 지원합니다.");
        ArgumentNullException.ThrowIfNull(currentRequest);
        ArgumentNullException.ThrowIfNull(currentScenarios);
        if (shown.ProductionWritesEnabled || !shown.RequiresUserApproval)
            throw new InvalidDataException("검토와 승인 경계가 유효하지 않습니다.");
        // Both the source name and the path must be the exact isolated test location.
        // A path prefix such as Sunrise_EIA_SANDBOX_existing-work is not sufficient.
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(shown.BaseDirectory));
        string sandboxName = Path.GetFileName(basePath) ?? "";
        const string prefix = "Sunrise_EIA_SANDBOX_";
        string unique = sandboxName.StartsWith(prefix, StringComparison.Ordinal)
            ? sandboxName[prefix.Length..] : "";
        if (!basePath.StartsWith(temp + Path.DirectorySeparatorChar, PathComparison) ||
            !sandboxName.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(unique, "N", out _) ||
            !Path.GetFullPath(sandboxMarkerPath).Equals(
                Path.Combine(basePath, ".sunrise-eia-sandbox-marker"), PathComparison))
            throw new InvalidDataException("격리된 임시 테스트 작업공간만 허용합니다.");
        if (typedConfirmation != "CREATE " + shown.RootFolderName)
            throw new InvalidDataException("사용자의 명시적 확인 문구가 일치하지 않습니다.");
        VerifyExistingChain(basePath);
        if (IsLink(sandboxMarkerPath))
            throw new InvalidDataException("시험 마커의 심볼릭 링크는 허용하지 않습니다.");
        // Sharing NONE serializes cooperating processes and threads on the same marker.
        // This does not prevent unrelated processes from changing other path components;
        // it is NOT a handle-relative, TOCTOU-free NTFS/SMB writer.
        using var lease = new FileStream(sandboxMarkerPath, FileMode.Open, FileAccess.Read,
            FileShare.None);
        using (var reader = new StreamReader(lease, Encoding.UTF8, true, 1024, leaveOpen: true))
        {
            if (reader.ReadToEnd() != "EIA-ISOLATED-SANDBOX-V1")
                throw new InvalidDataException("격리 시험 마커가 일치하지 않습니다.");
        }
        // Do not re-open the marker by pathname while a FileShare.None lease is held:
        // that operation can fail with a sharing violation on Windows. The open lease
        // protects cooperating marker users, but not arbitrary directory swaps.
        VerifyExistingChain(basePath);
        // Recompute inside lease to catch other cooperating writers and stale previews.
        EiaFolderCreationPlan current = Prepare(basePath, currentRequest, currentScenarios);
        // DEV5 approval-boundary repair: a caller can supply a `with`-cloned plan
        // with the original fingerprint but a replaced Items collection. Comparing
        // only the fingerprint allows those unapproved items to be executed.
        // Compare the ENTIRE shown plan to a fresh recomputation before any write.
        // After validation use ONLY the freshly derived immutable snapshot, never
        // the externally supplied Items reference (it may be a mutable List).
        RequireIdenticalApprovedPreview(shown, current);
        // Pinned root + one-component relative NT creates: only compiled in sandbox.
        // No path-based writer fallback on native failure.
        using var nativeRoot = EiaWorkspaceNativeSandboxDirectory.OpenPinnedSandbox(temp, basePath);
        var rows = new List<EiaFolderCreationOutcome>();
        bool interrupted = false;
        for (int index = 0; index < current.Items.Count; index++)
        {
            var item = current.Items[index];
            if (interrupted || cancellationToken.IsCancellationRequested)
            {
                interrupted = true;
                rows.Add(new(item.RelativePath, "NOT_ATTEMPTED", "중단 또는 이전 오류"));
                continue;
            }
            // A cancellation after a native create/open can leave an uncertain result.
            // Never report that attempt as NOT_ATTEMPTED or roll it back automatically.
            bool nativeOperationAttempted = false;
            try
            {
                // Test-only fault/cancellation hook: never compiled into the product.
                sandboxBeforeAttempt?.Invoke(index, item.AbsolutePath);
                if (cancellationToken.IsCancellationRequested)
                {
                    interrupted = true;
                    rows.Add(new(item.RelativePath, "NOT_ATTEMPTED", "실행 전 취소"));
                    continue;
                }
                VerifyExistingChain(basePath);
                bool exists = VerifyExistingChain(item.AbsolutePath);
                if (item.BeforeState == "EXISTS_DIRECTORY")
                {
                    if (!exists) throw new InvalidDataException("기존 폴더가 사라졌습니다.");
                    nativeOperationAttempted = true;
                    nativeRoot.CreateOrVerify(item, current.RootFolderName);
                    sandboxAfterNativeAttempt?.Invoke(index, item.AbsolutePath);
                    rows.Add(new(item.RelativePath, "ALREADY_EXISTS", "기존 폴더 유지"));
                    continue;
                }
                if (exists) throw new InvalidDataException("실행 직전 대상 폴더가 변경됐습니다.");
                string parent = Path.GetDirectoryName(item.AbsolutePath)
                    ?? throw new InvalidDataException("상위 경로가 없습니다.");
                if (!VerifyExistingChain(parent))
                    throw new InvalidDataException("대상 부모 폴더가 없습니다.");
                // DEV3: relative native creation is sandbox-only; actual native NTFS/SMB
                // native adversarial-race tests remain a production BLOCKER.
                VerifyExistingChain(basePath);
                if (VerifyExistingChain(item.AbsolutePath))
                    throw new InvalidDataException("생성 직전 다른 작업이 대상 폴더를 만들었습니다.");
                // DEV3: CREATE only under a verified, open parent directory handle.
                nativeOperationAttempted = true;
                nativeRoot.CreateOrVerify(item, current.RootFolderName);
                sandboxAfterNativeAttempt?.Invoke(index, item.AbsolutePath);
                if (!VerifyExistingChain(item.AbsolutePath))
                    throw new IOException("생성 후 경로 확인 실패");
                rows.Add(new(item.RelativePath, "CREATED", "새 폴더 생성 확인"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Before a native operation there were zero directory writes for
                // this item. After it, even a cancelled post-check cannot prove
                // whether the directory was created: require manual re-inspection.
                rows.Add(new(item.RelativePath,
                    nativeOperationAttempted ? "FAILED" : "NOT_ATTEMPTED",
                    nativeOperationAttempted
                        ? "취소 시 실제 파일시스템 반영 여부 불명확 (상태 재조회 필요)"
                        : "실행 전/검증 중 취소 (미시도)"));
                interrupted = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException or System.Security.SecurityException)
            {
                // A failing I/O call can have taken effect before the exception. Do not
                // guess whether it was created and NEVER auto-remove that directory.
                rows.Add(new(item.RelativePath, "FAILED",
                    ex.GetType().Name + ": " + ex.Message + " (상태 재조회 필요)"));
                interrupted = true;
            }
        }
        return new EiaFolderCreationResult(current.RootFolderName, current.FingerprintSha256,
            rows.AsReadOnly(), rows.Count(x => x.Outcome == "CREATED"),
            rows.Count(x => x.Outcome == "ALREADY_EXISTS"),
            rows.Count(x => x.Outcome == "FAILED"),
            rows.Count(x => x.Outcome == "NOT_ATTEMPTED"),
            !interrupted && rows.All(x => x.Outcome != "FAILED"), true);
    }

    private static bool IsLink(string path)
    {
        // Check both link metadata and reparse attributes. A dangling link must not
        // silently be treated as an absent file. Exceptions fail closed to the caller.
        if (new FileInfo(path).LinkTarget is not null) return true;
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

#endif
}
