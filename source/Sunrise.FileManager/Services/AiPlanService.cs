using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.ObjectModel;

namespace Sunrise.FileManager.Services;

public sealed record AiOperation(string Operation, string? EntryId = null, string? NewName = null,
    string? TargetId = null, string? Subfolder = null);
public sealed record AiPlan(int SchemaVersion, string ScopeId, IReadOnlyList<AiOperation> Operations);
public sealed record AiEntry(string Id, string Path, string Name, bool Directory, string Fingerprint);
public sealed record AiTarget(string Id, string Path);
public sealed record AiPreviewRow(string Action, string Before, string After, string Status);
internal sealed record AiStep(AiOperation Operation, AiEntry? Source, string Destination, bool Skip);

/// <summary>Opaque scope IDs bind model data to a local, immutable selection. No model-supplied paths.</summary>
public sealed class AiScope
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Folder { get; }
    public IReadOnlyList<AiEntry> Entries { get; }
    public IReadOnlyList<AiTarget> Targets { get; }
    private AiScope(string folder, AiEntry[] entries, AiTarget[] targets)
    { Folder = folder; Entries = Array.AsReadOnly(entries); Targets = Array.AsReadOnly(targets); }

    public static AiScope Capture(string folder, IReadOnlyList<string> selection, string? destination,
        CancellationToken token, bool allowDescendantEntries = false)
    {
        folder = AiPlanService.OrdinaryPath(folder);
        if (!System.IO.Directory.Exists(folder)) throw new IOException("현재 폴더가 없습니다.");
        if (selection.Count > 1000) throw new IOException("최대 1,000개 항목을 선택하세요.");
        var entries = new List<AiEntry>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var budget = new AiPlanService.ScanBudget();
        foreach (string path in selection.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            string full = AiPlanService.OrdinaryPath(path);
            bool direct = string.Equals(Path.GetDirectoryName(full), folder, StringComparison.OrdinalIgnoreCase);
            bool nested = allowDescendantEntries && full.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if ((!direct && !nested) || !unique.Add(full) ||
                full.Split(Path.DirectorySeparatorChar).Any(part => part.Equals("#memo", StringComparison.OrdinalIgnoreCase)) ||
                full.EndsWith(".sunmemo", StringComparison.OrdinalIgnoreCase))
                throw new IOException("선택 범위 밖이거나 내부 메모 파일인 항목은 사용할 수 없습니다.");
            entries.Add(new(Guid.NewGuid().ToString("N"), full, Path.GetFileName(full),
                System.IO.Directory.Exists(full), AiPlanService.Fingerprint(full, budget, token)));
        }
        var targets = new List<AiTarget> { new("current", folder) };
        if (!string.IsNullOrWhiteSpace(destination))
        {
            string target = AiPlanService.OrdinaryPath(destination);
            if (!System.IO.Directory.Exists(target)) throw new IOException("대상 폴더가 없습니다.");
            targets.Add(new("destination", target));
        }
        return new(folder, entries.ToArray(), targets.ToArray());
    }
}

public sealed class AiValidatedPlan
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public AiScope Scope { get; }
    public IReadOnlyList<AiPreviewRow> Rows { get; }
    internal IReadOnlyList<AiStep> Steps { get; }
    private int _claimed;
    internal AiValidatedPlan(AiScope scope, List<AiStep> steps, List<AiPreviewRow> rows)
    { Scope = scope; Steps = steps.AsReadOnly(); Rows = rows.AsReadOnly(); }
    internal void Claim()
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            throw new InvalidOperationException("이미 실행한 계획입니다. 다시 계획을 생성하세요.");
    }
}

public sealed record AiExecutionResult(int Completed, int Skipped, int Failed, bool Canceled,
    IReadOnlyList<AiPreviewRow> Rows, string AuditPath);

public static class AiPlanService
{
    internal sealed class ScanBudget { public int Count; public long Bytes; }
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12
    };
    public static string Serialize(AiPlan plan) => JsonSerializer.Serialize(plan, JsonOptions);
    public static AiPlan Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 1024 * 1024) throw new JsonException("계획은 1MB 이하여야 합니다.");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
        RejectDuplicates(doc.RootElement);
        return JsonSerializer.Deserialize<AiPlan>(json, JsonOptions) ?? throw new JsonException("빈 계획입니다.");
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new JsonException("중복 JSON 필드입니다.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    internal static string OrdinaryPath(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string? part = full;
        // Check every ancestor, including junctions in an otherwise ordinary-looking child path.
        while (!string.IsNullOrEmpty(part))
        {
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("연결·접합점 경로는 AI 작업에서 지원하지 않습니다.");
            part = Path.GetDirectoryName(part);
        }
        return full;
    }
    internal static string Fingerprint(string path, ScanBudget budget, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\0"));
        var pending = new Stack<string>();
        void Schedule(string entry)
        {
            if (++budget.Count > TransferService.MaxPlannedEntries) throw new IOException("최대 50,000개 내부 항목을 초과했습니다.");
            pending.Push(entry);
        }
        Schedule(path);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string current = pending.Pop();
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("선택 폴더에 연결·접합점이 있습니다.");
            bool directory = (attributes & FileAttributes.Directory) != 0;
            Add(Path.GetRelativePath(path, current)); Add(((int)attributes).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(File.GetCreationTimeUtc(current).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(File.GetLastWriteTimeUtc(current).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (directory)
            {
                int remaining = TransferService.MaxPlannedEntries - budget.Count;
                string[] children = System.IO.Directory.EnumerateFileSystemEntries(current).Take(remaining + 1).ToArray();
                if (children.Length > remaining) throw new IOException("최대 50,000개 내부 항목을 초과했습니다.");
                foreach (string child in children.OrderByDescending(x => x, StringComparer.Ordinal)) Schedule(child);
            }
            else
            {
                using var reader = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read);
                budget.Bytes = checked(budget.Bytes + reader.Length);
                if (budget.Bytes > 2L * 1024 * 1024 * 1024) throw new IOException("AI 계획의 내용 검증은 총 2GB까지 지원합니다.");
                byte[] buffer = new byte[256 * 1024]; int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                { token.ThrowIfCancellationRequested(); hash.AppendData(buffer.AsSpan(0, count)); }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal static string Name(string? name)
    {
        if (name is null || name.Length > 240) throw new ArgumentException("사용할 수 없는 이름입니다.");
        FileActionsService.ValidateName(name);
        return name;
    }
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static bool Descendant(string candidate, string parent) =>
        candidate.Equals(parent, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static AiValidatedPlan Validate(AiScope scope, AiPlan plan, CollisionPolicy collision, CancellationToken token)
    {
        if (plan.SchemaVersion != 1 || plan.ScopeId != scope.Id || plan.Operations is null || plan.Operations.Count is < 1 or > 2000)
            throw new ArgumentException("계획 버전·범위·작업 개수가 올바르지 않습니다.");
        var entries = scope.Entries.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var targets = scope.Targets.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steps = new List<AiStep>(); var rows = new List<AiPreviewRow>();
        var mkdirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AiOperation op in plan.Operations)
        {
            token.ThrowIfCancellationRequested();
            if (op is null || op.Operation is not ("rename" or "mkdir" or "copy" or "move" or "recycle"))
                throw new ArgumentException("허용되지 않은 작업입니다.");
            AiEntry? source = null; string destination;
            if (op.Operation == "mkdir")
            {
                if (op.EntryId is not null || op.Subfolder is not null || op.TargetId is null || !targets.TryGetValue(op.TargetId, out var parent))
                    throw new ArgumentException("폴더 생성 대상이 잘못되었습니다.");
                OrdinaryPath(parent.Path);
                destination = Path.Combine(parent.Path, Name(op.NewName));
                if (Exists(destination) || !reserved.Add(destination)) throw new IOException("생성할 폴더 이름이 충돌합니다.");
                mkdirs.Add(destination);
            }
            else
            {
                if (op.EntryId is null || !entries.TryGetValue(op.EntryId, out source) || !consumed.Add(op.EntryId))
                    throw new ArgumentException("선택 범위 밖이거나 중복된 항목입니다.");
                OrdinaryPath(source.Path);
                if (op.Operation == "recycle")
                {
                    if (op.NewName is not null || op.TargetId is not null || op.Subfolder is not null)
                        throw new ArgumentException("휴지통 작업에는 이름·대상 경로를 지정할 수 없습니다.");
                    // A single-file recycle cannot atomically recycle and restore #memo sidecars.
                    // Refuse instead of silently orphaning memo history. Directories carry their own #memo.
                    if (!source.Directory)
                    {
                        var memoRecords = FileMemoStore.ListForFile(source.Path, out int unreadable);
                        if (unreadable > 0 || memoRecords.Count > 0)
                            throw new IOException("이 파일에 연결된 #memo 기록이 있어 AI 휴지통 작업을 중단했습니다. 메모 보존이 가능한 이동 기능을 사용하세요.");
                    }
                    destination = source.Path;
                }
                else if (op.Operation == "rename")
                {
                    if (op.TargetId is not null || op.Subfolder is not null) throw new ArgumentException("이름 변경에 대상 폴더를 지정할 수 없습니다.");
                    destination = Path.Combine(Path.GetDirectoryName(source.Path)!, Name(op.NewName));
                    if (destination.Equals(source.Path, StringComparison.OrdinalIgnoreCase)) throw new IOException("같은 이름 또는 대소문자만 변경하는 계획은 지원하지 않습니다.");
                    if (Exists(destination) || !reserved.Add(destination)) throw new IOException("이름 변경 충돌입니다. 순환 변경은 지원하지 않습니다.");
                }
                else
                {
                    if (op.NewName is not null || op.TargetId is null || !targets.TryGetValue(op.TargetId, out var target))
                        throw new ArgumentException("복사·이동 대상이 잘못되었습니다.");
                    string folder = OrdinaryPath(target.Path);
                    if (op.Subfolder is not null)
                    {
                        folder = Path.Combine(folder, Name(op.Subfolder));
                        if (Directory.Exists(folder)) OrdinaryPath(folder);
                        else if (!mkdirs.Contains(folder)) throw new IOException("대상 하위 폴더를 먼저 생성하도록 계획하세요.");
                    }
                    if (source.Directory && Descendant(folder, source.Path)) throw new IOException("자신 또는 하위 폴더로 전송할 수 없습니다.");
                    destination = Path.Combine(folder, source.Name);
                    if (destination.Equals(source.Path, StringComparison.OrdinalIgnoreCase)) throw new IOException("원본과 대상이 같습니다.");
                    if (op.Operation == "move" && !string.Equals(Path.GetPathRoot(source.Path), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("이동은 동일 드라이브에서만 지원합니다.");
                    bool conflict = Exists(destination) || reserved.Contains(destination);
                    if (conflict && collision == CollisionPolicy.Skip)
                    {
                        steps.Add(new(op, source, destination, true));
                        rows.Add(new(op.Operation, source.Path, destination, "충돌 · 건너뜀")); continue;
                    }
                    if (conflict)
                    {
                        string extension = source.Directory ? "" : Path.GetExtension(source.Name);
                        string stem = source.Name[..(source.Name.Length - extension.Length)];
                        string original = destination; int number = 1;
                        do
                        {
                            if (number > 999) throw new IOException("대체 이름이 999개 모두 사용 중입니다.");
                            destination = Path.Combine(folder, Name($"{stem} ({number++}){extension}"));
                        } while (Exists(destination) || reserved.Contains(destination));
                    }
                    reserved.Add(destination);
                }
            }
            steps.Add(new(op, source, destination, false));
            rows.Add(new(op.Operation, source?.Path ?? "새 폴더", op.Operation == "recycle" ? "Windows 휴지통" : destination, "검증 통과 · 승인 대기"));
        }
        // No destination may lie inside a selected directory: moving that directory would invalidate later steps.
        foreach (var step in steps.Where(x => x.Operation.Operation != "recycle"))
            foreach (var directory in scope.Entries.Where(x => x.Directory))
                if (Descendant(step.Destination, directory.Path)) throw new IOException("선택된 폴더 내부는 작업 대상으로 지정할 수 없습니다.");
        return new(scope, steps, rows);
    }

    public static async Task<AiExecutionResult> ExecuteAsync(AiValidatedPlan plan, bool approved,
        string auditDirectory, IProgress<AiPreviewRow>? progress, CancellationToken token,
        IProgress<AiPreviewRow>? activity = null, AiJournalStore? journal = null, bool recycleApproved = false)
    {
        if (!approved) throw new InvalidOperationException("실행 승인이 필요합니다.");
        if (plan.Steps.Any(x => x.Operation.Operation == "recycle") && !recycleApproved)
            throw new InvalidOperationException("휴지통 작업은 별도의 2차 승인이 필요합니다.");
        plan.Claim();
        var rows = new List<AiPreviewRow>(); int completed = 0, skipped = 0, failed = 0;
        bool canceled = false;
        // Fail closed if the complete selection changed after preview. This phase never writes user files.
        var budget = new ScanBudget();
        OrdinaryPath(plan.Scope.Folder);
        foreach (var target in plan.Scope.Targets) OrdinaryPath(target.Path);
        foreach (var entry in plan.Scope.Entries)
            if (Fingerprint(OrdinaryPath(entry.Path), budget, token) != entry.Fingerprint)
                throw new IOException("미리보기 이후 원본이 변경되었습니다. 다시 계획하고 승인하세요.");
        foreach (var step in plan.Steps.Where(x => !x.Skip && x.Operation.Operation != "recycle"))
            if (Exists(step.Destination)) throw new IOException("미리보기 이후 대상이 생성되었습니다. 다시 계획하세요.");
        Directory.CreateDirectory(auditDirectory);
        string audit = Path.Combine(auditDirectory, plan.Id + ".json");
        void SaveAudit(string state)
        {
            string temp = audit + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(new { plan_id = plan.Id, state,
                    completed, skipped, failed, canceled, utc = DateTimeOffset.UtcNow }));
                File.Move(temp, audit, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        journal ??= new AiJournalStore(Path.Combine(auditDirectory, "Journal"));
        using var jobLock = journal.Lock(plan.Id);
        AiJournalSnapshot snapshot = journal.Begin(plan);
        SaveAudit("approved_running");
        for (int stepIndex = 0; stepIndex < plan.Steps.Count; stepIndex++)
        {
            AiStep step = plan.Steps[stepIndex];
            if (token.IsCancellationRequested) { canceled = true; break; }
            bool intentRecorded = false;
            string status;
            activity?.Report(new(step.Operation.Operation, step.Source?.Path ?? "새 폴더", step.Destination, "처리 중"));
            try
            {
                if (step.Skip) { snapshot = journal.Append(snapshot, "skipped", stepIndex); skipped++; status = "건너뜀"; }
                else
                {
                    string parent = OrdinaryPath(Path.GetDirectoryName(step.Destination)!);
                    if (step.Operation.Operation != "recycle" && Exists(step.Destination)) throw new IOException("대상이 변경되어 중단합니다.");
                    if (step.Source is not null && Fingerprint(OrdinaryPath(step.Source.Path), new ScanBudget(), token) != step.Source.Fingerprint)
                        throw new IOException("실행 직전 원본 변경을 확인했습니다.");
                    token.ThrowIfCancellationRequested();
                    snapshot = journal.Append(snapshot, "started", stepIndex);
                    intentRecorded = true;
                    switch (step.Operation.Operation)
                    {
                        case "recycle": FileActionsService.Recycle(step.Source!.Path); break;
                        case "mkdir": FileActionsService.CreateFolder(parent, Path.GetFileName(step.Destination)); break;
                        case "rename": FileActionsService.Rename(step.Source!.Path, Path.GetFileName(step.Destination)); break;
                        case "copy": case "move":
                            var result = await TransferService.RunAsync([step.Source!.Path], parent,
                                step.Operation.Operation == "copy" ? TransferKind.Copy : TransferKind.Move,
                                activity is null ? null : new InlineTransferProgress(item =>
                                    activity.Report(new(step.Operation.Operation, step.Source.Path, step.Destination,
                                        item.BytesTotal > 0 ? $"{item.Phase} · 파일 {100.0 * item.BytesDone / item.BytesTotal:F0}%" : item.Phase))),
                                token, CollisionPolicy.Skip,
                                new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                { [step.Source.Path] = step.Destination })).ConfigureAwait(false);
                            if (result.Canceled) { canceled = true; break; }
                            if (result.Failed > 0 || result.Skipped > 0 || result.Completed != 1)
                                throw new IOException(result.Errors.FirstOrDefault() ?? "대상 충돌로 실행되지 않았습니다.");
                            break;
                    }
                    if (canceled)
                    {
                        snapshot = journal.Append(snapshot, "uncertain", stepIndex, errorCode: "TRANSFER_CANCELED");
                        break;
                    }
                    string? afterHash = step.Operation.Operation == "recycle" ? null : Fingerprint(OrdinaryPath(step.Destination), new ScanBudget(), CancellationToken.None);
                    snapshot = journal.Append(snapshot, "done", stepIndex, afterHash);
                    completed++; status = "완료";
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (intentRecorded) snapshot = journal.Append(snapshot, "uncertain", stepIndex, errorCode: "CANCELED_AFTER_INTENT");
                canceled = true; break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                snapshot = journal.Append(snapshot, intentRecorded ? "uncertain" : "blocked", stepIndex, errorCode: ex.GetType().Name);
                failed++; status = (intentRecorded ? "확인 필요: " : "실패: ") + ex.Message;
                var errorRow = new AiPreviewRow(step.Operation.Operation, step.Source?.Path ?? "새 폴더", step.Destination, status);
                rows.Add(errorRow); progress?.Report(errorRow);
                // Stop on first failure; do not keep applying a now-stale plan or promise rollback.
                break;
            }
            var row = new AiPreviewRow(step.Operation.Operation, step.Source?.Path ?? "새 폴더", step.Destination, status);
            rows.Add(row); progress?.Report(row);
        }
        if (token.IsCancellationRequested) canceled = true;
        for (int i = rows.Count; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            rows.Add(new(step.Operation.Operation, step.Source?.Path ?? "새 폴더", step.Destination, canceled ? "취소 · 미실행" : "중단 · 미실행"));
        }
        snapshot = journal.Append(snapshot, canceled ? "canceled" : failed > 0 ? "partial_failure" : "completed");
        SaveAudit(canceled ? "canceled" : failed > 0 ? "partial_failure" : "completed");
        return new(completed, skipped, failed, canceled, rows.AsReadOnly(), audit);
    }
    private sealed class InlineTransferProgress(Action<TransferProgress> action) : IProgress<TransferProgress>
    { public void Report(TransferProgress value) => action(value); }

}
