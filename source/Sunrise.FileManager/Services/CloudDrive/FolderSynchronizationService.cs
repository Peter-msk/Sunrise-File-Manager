using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Services.CloudDrive;

public enum FolderDifferenceKind
{
    AddFolder, AddFile, Unchanged, SourceOnly, TargetOnly, Conflict
}

public sealed record FolderSyncEntry(string Relative, string Name, bool IsFolder, long? Size,
    DateTime? LastWriteUtc, string? LocalPath, CloudItem? RemoteItem);
public sealed record FolderSyncDifference(string Relative, FolderDifferenceKind Kind, string Note,
    FolderSyncEntry? Source, FolderSyncEntry? Target);
public sealed record FolderSyncPlan(CrossPaneCopyTarget SourceRoot, CrossPaneCopyTarget TargetRoot,
    IReadOnlyList<FolderSyncDifference> Differences, string SourceFingerprint, string TargetFingerprint)
{
    public int ToCreate => Differences.Count(x => x.Kind is FolderDifferenceKind.AddFile or FolderDifferenceKind.AddFolder);
    public int Conflicts => Differences.Count(x => x.Kind == FolderDifferenceKind.Conflict);
    public int TargetOnly => Differences.Count(x => x.Kind == FolderDifferenceKind.TargetOnly);
    // v1.7.5: content snapshots for local AddFile items. Size and mtime can be forged or
    // restored; the preview MUST pin the actual bytes before the user authorizes writes.
    public IReadOnlyDictionary<string, string> LocalSourceHashes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
public sealed record FolderSyncProgress(int Completed, int Total, string Current);
public sealed record FolderSyncResult(int Created, int Failed, bool Canceled, IReadOnlyList<string> Details);

/// <summary>
/// R29: read-only folder comparison and opt-in, additive, one-way synchronization.
/// No automatic overwrite, deletion, reverse direction, or permission-bypass fallback.
/// Cloud equality cannot be proven from matching sizes; ambiguous items are conflicts.
/// </summary>
public static class FolderSynchronizationService
{
    public const int MaximumEntries = 10_000;
    public const int MaximumDepth = 64;
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    private static string SafeName(string value) => FileActionsService.ValidateName(value);
    private static string Rel(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;
    private static string Parent(string relative)
    {
        int index = relative.LastIndexOf('/');
        return index < 0 ? "" : relative[..index];
    }
    private static int Depth(string path) => path.Count(ch => ch == '/');
    private static bool HasBlockedAncestor(string path, HashSet<string> blocked)
    {
        for (string ancestor = Parent(path); ancestor.Length > 0; ancestor = Parent(ancestor))
            if (blocked.Contains(ancestor)) return true;
        return false;
    }

    private static void ValidateRoot(CrossPaneCopyTarget root)
    {
        if (root.LocalFolder is string local)
        {
            if (root.RemoteApi is not null || root.RemoteTransfer is not null || root.RemoteFolderId is not null)
                throw new ArgumentException("로컬·클라우드 위치를 혼합할 수 없습니다.");
            if (!Directory.Exists(local)) throw new DirectoryNotFoundException("동기화 폴더를 확인할 수 없습니다.");
            if ((File.GetAttributes(local) & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("링크 폴더는 동기화하지 않습니다.");
        }
        else if (string.IsNullOrWhiteSpace(root.RemoteFolderId) || root.RemoteApi is null || root.RemoteTransfer is null)
            throw new ArgumentException("클라우드 폴더·계정 연결이 필요합니다.");
    }
    private static bool Overlap(string a, string b)
    {
        a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static void ValidatePair(CrossPaneCopyTarget source, CrossPaneCopyTarget target)
    {
        ValidateRoot(source); ValidateRoot(target);
        if (source.LocalFolder is not null && target.LocalFolder is not null &&
            Overlap(source.LocalFolder, target.LocalFolder))
            throw new IOException("동일하거나 중첩된 폴더 간 동기화는 금지됩니다.");
        if (source.RemoteFolderId is not null && target.RemoteFolderId is not null &&
            source.RemoteApi?.GetType() == target.RemoteApi?.GetType() &&
            string.Equals(source.RemoteFolderId, target.RemoteFolderId, StringComparison.Ordinal))
            throw new IOException("같은 클라우드 폴더 간 동기화는 금지됩니다.");
    }

    private static async Task<Dictionary<string, FolderSyncEntry>> SnapshotAsync(
        CrossPaneCopyTarget root, CancellationToken token)
    {
        var entries = new Dictionary<string, FolderSyncEntry>(Names);
        if (root.LocalFolder is string local)
        {
            string absolute = Path.GetFullPath(local);
            var stack = new Stack<(string Folder, string Relative, int Level)>();
            stack.Push((absolute, "", 0));
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var current = stack.Pop();
                if (current.Level > MaximumDepth) throw new IOException("폴더 비교 깊이가 64단계를 초과했습니다.");
                foreach (string child in Directory.EnumerateFileSystemEntries(current.Folder))
                {
                    token.ThrowIfCancellationRequested();
                    if (entries.Count >= MaximumEntries) throw new IOException("폴더 비교 항목 10,000개 제한을 초과했습니다.");
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new NotSupportedException("원본 또는 대상에 링크·접합점이 있어 비교를 중단합니다.");
                    string name = SafeName(Path.GetFileName(child));
                    string relative = Rel(current.Relative, name);
                    bool folder = (attributes & FileAttributes.Directory) != 0;
                    var info = folder ? null : new FileInfo(child);
                    var entry = new FolderSyncEntry(relative, name, folder, info?.Length,
                        folder ? null : info?.LastWriteTimeUtc, child, null);
                    if (!entries.TryAdd(relative, entry))
                        throw new IOException("대소문자만 다른 중복 경로가 있어 비교를 중단합니다.");
                    if (folder) stack.Push((child, relative, current.Level + 1));
                }
            }
            return entries;
        }
        var api = root.RemoteApi!;
        var seen = new HashSet<string>(StringComparer.Ordinal) { root.RemoteFolderId! };
        var pending = new Stack<(string Id, string Relative, int Level)>();
        pending.Push((root.RemoteFolderId!, "", 0));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            if (folder.Level > MaximumDepth) throw new IOException("클라우드 폴더 비교 깊이 제한을 초과했습니다.");
            var children = await api.ListChildrenAsync(folder.Id, token).ConfigureAwait(false);
            foreach (var item in children)
            {
                token.ThrowIfCancellationRequested();
                if (entries.Count >= MaximumEntries) throw new IOException("폴더 비교 항목 10,000개 제한을 초과했습니다.");
                string name = SafeName(item.Name);
                string relative = Rel(folder.Relative, name);
                if (!entries.TryAdd(relative, new FolderSyncEntry(relative, name, item.IsFolder,
                    item.Size, null, null, item)))
                    throw new IOException("대상 클라우드에 같은 이름의 항목이 중복됩니다.");
                if (item.IsFolder)
                {
                    if (string.IsNullOrWhiteSpace(item.Id) || !seen.Add(item.Id))
                        throw new IOException("클라우드 폴더 구조가 순환하거나 식별자가 중복됩니다.");
                    pending.Push((item.Id, relative, folder.Level + 1));
                }
            }
        }
        return entries;
    }

    private static string Fingerprint(IReadOnlyDictionary<string, FolderSyncEntry> entries)
    {
        // This records visible metadata, not private content or OAuth credentials.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in entries.Values.OrderBy(x => x.Relative, Names))
        {
            string record = item.Relative + "|" + (item.IsFolder ? "D" : "F") + "|" +
                item.Size + "|" + item.LastWriteUtc?.Ticks + "|" + item.RemoteItem?.Id + "\n";
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(record));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static async Task<string> HashLocalSourceAsync(string path, CancellationToken token)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("동기화 원본이 링크 파일로 변경됐습니다.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static async Task<bool> SameLocalBytesAsync(string a, string b, CancellationToken token)
    {
        using var ha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var hb = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pair in new[] { (a, ha), (b, hb) })
        {
            using var stream = new FileStream(pair.Item1, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            byte[] buffer = new byte[128 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                pair.Item2.AppendData(buffer.AsSpan(0, count));
        }
        return CryptographicOperations.FixedTimeEquals(ha.GetHashAndReset(), hb.GetHashAndReset());
    }
    private static async Task<IReadOnlyList<FolderSyncDifference>> DiffAsync(
        IReadOnlyDictionary<string, FolderSyncEntry> left,
        IReadOnlyDictionary<string, FolderSyncEntry> right, CancellationToken token)
    {
        var differences = new List<FolderSyncDifference>();
        var blocked = new HashSet<string>(Names);
        foreach (var path in left.Keys.Concat(right.Keys).Distinct(Names).OrderBy(x => x, Names))
        {
            token.ThrowIfCancellationRequested();
            if (HasBlockedAncestor(path, blocked))
            {
                left.TryGetValue(path, out var blockedSource);
                right.TryGetValue(path, out var blockedTarget);
                differences.Add(new(path, FolderDifferenceKind.Conflict,
                    "상위 경로 충돌 · 자동 동기화 제외", blockedSource, blockedTarget));
                continue;
            }
            left.TryGetValue(path, out var source);
            right.TryGetValue(path, out var target);
            if (source is null)
                differences.Add(new(path, FolderDifferenceKind.TargetOnly, "대상 전용 · 변경하지 않음", null, target));
            else if (target is null)
            {
                if (!source.IsFolder && source.RemoteItem is { Size: null })
                    differences.Add(new(path, FolderDifferenceKind.Conflict, "원격 파일 크기 불명 · 자동 복사 제외", source, null));
                else
                    differences.Add(new(path, source.IsFolder ? FolderDifferenceKind.AddFolder : FolderDifferenceKind.AddFile,
                        source.IsFolder ? "대상 폴더 생성 예정" : "파일 복사 예정", source, null));
            }
            else if (source.IsFolder != target.IsFolder)
            {
                differences.Add(new(path, FolderDifferenceKind.Conflict, "폴더·파일 유형 충돌 · 덮어쓰기 안 함", source, target));
                blocked.Add(path);
            }
            else if (source.IsFolder)
                differences.Add(new(path, FolderDifferenceKind.Unchanged, "양쪽에 폴더 존재", source, target));
            else if (source.Size != target.Size)
                differences.Add(new(path, FolderDifferenceKind.Conflict, "파일 크기 다름 · 자동 덮어쓰기 안 함", source, target));
            else if (source.LocalPath is string sl && target.LocalPath is string tl &&
                     await SameLocalBytesAsync(sl, tl, token).ConfigureAwait(false))
                differences.Add(new(path, FolderDifferenceKind.Unchanged, "SHA-256 동일", source, target));
            else
                differences.Add(new(path, FolderDifferenceKind.Conflict,
                    source.LocalPath is not null && target.LocalPath is not null
                        ? "SHA-256 다름 · 검토 필요" : "원격 파일 내용 미확인 · 검토 필요", source, target));
        }
        return differences;
    }
    public static async Task<FolderSyncPlan> PreviewAsync(CrossPaneCopyTarget source,
        CrossPaneCopyTarget target, CancellationToken token)
    {
        ValidatePair(source, target);
        var left = await SnapshotAsync(source, token).ConfigureAwait(false);
        // A second panel may have a separate adapter instance for the SAME cloud account.
        // Reject obvious self/descendant synchronization even without adapter reference equality.
        if (source.RemoteFolderId is not null && target.RemoteFolderId is not null &&
            source.RemoteApi?.GetType() == target.RemoteApi?.GetType() &&
            left.Values.Any(x => x.IsFolder && x.RemoteItem?.Id == target.RemoteFolderId))
            throw new IOException("원본 클라우드 폴더의 하위 폴더로 동기화할 수 없습니다.");
        var right = await SnapshotAsync(target, token).ConfigureAwait(false);
        var rows = await DiffAsync(left, right, token).ConfigureAwait(false);
        var fileHashes = new Dictionary<string, string>(Names);
        foreach (var entry in rows.Where(x => x.Kind == FolderDifferenceKind.AddFile &&
                     x.Source?.LocalPath is not null))
        {
            token.ThrowIfCancellationRequested();
            fileHashes.Add(entry.Relative, await HashLocalSourceAsync(entry.Source!.LocalPath!, token)
                .ConfigureAwait(false));
        }
        return new(source, target, rows, Fingerprint(left), Fingerprint(right))
        { LocalSourceHashes = fileHashes };
    }

    /// <summary>Only selected, absent destination entries may be created. No deletes, moves or overwrites.</summary>
    public static async Task<FolderSyncResult> ApplyAsync(FolderSyncPlan plan,
        IProgress<FolderSyncProgress>? progress, CancellationToken token)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        ValidatePair(plan.SourceRoot, plan.TargetRoot);
        var left = await SnapshotAsync(plan.SourceRoot, token).ConfigureAwait(false);
        var right = await SnapshotAsync(plan.TargetRoot, token).ConfigureAwait(false);
        if (Fingerprint(left) != plan.SourceFingerprint || Fingerprint(right) != plan.TargetFingerprint)
            throw new IOException("미리보기 후 폴더 상태가 변경됐습니다. 다시 비교해 주세요.");
        // Metadata is insufficient for detecting an in-place overwrite with preserved size
        // and timestamps. Hash only the local files scheduled for creation, fail closed.
        var localAdds = plan.Differences.Where(x => x.Kind == FolderDifferenceKind.AddFile &&
            x.Source?.LocalPath is not null).ToArray();
        if (plan.LocalSourceHashes.Count != localAdds.Length)
            throw new IOException("동기화 원본의 내용 검증 기록이 없습니다. 다시 비교해 주세요.");
        foreach (var row in localAdds)
        {
            if (!plan.LocalSourceHashes.TryGetValue(row.Relative, out var hash) ||
                !left.TryGetValue(row.Relative, out var current) || current.LocalPath is null ||
                !string.Equals(hash, await HashLocalSourceAsync(current.LocalPath, token)
                    .ConfigureAwait(false), StringComparison.Ordinal))
                throw new IOException("동기화 미리보기 이후 원본 내용이 변경됐습니다. 다시 비교해 주세요.");
        }

        var folders = plan.Differences.Where(x => x.Kind == FolderDifferenceKind.AddFolder)
            .OrderBy(x => Depth(x.Relative)).ThenBy(x => x.Relative, Names).ToArray();
        var files = plan.Differences.Where(x => x.Kind == FolderDifferenceKind.AddFile).ToArray();
        var mapped = new Dictionary<string, string>(Names);
        if (plan.TargetRoot.RemoteFolderId is string remoteRoot)
        {
            mapped[""] = remoteRoot;
            foreach (var entry in right.Values.Where(x => x.IsFolder))
                mapped[entry.Relative] = entry.RemoteItem!.Id;
        }
        int completed = 0, failed = 0;
        var details = new List<string>();
        int total = folders.Length + files.Length;
        bool canceled = false;
        foreach (var row in folders.Concat(files))
        {
            if (token.IsCancellationRequested) { canceled = true; break; }
            try
            {
                string parent = Parent(row.Relative);
                if (plan.TargetRoot.LocalFolder is string localRoot)
                {
                    string folder = Path.Combine(localRoot, parent.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("대상 부모 폴더가 변경됐습니다.");
                    string destination = Path.Combine(folder, row.Source!.Name);
                    if (File.Exists(destination) || Directory.Exists(destination))
                        throw new IOException("대상 경로에 이미 항목이 존재합니다.");
                    if (row.Kind == FolderDifferenceKind.AddFolder)
                        Directory.CreateDirectory(destination);
                    else
                        await CopyFileAsync(row.Source, plan.SourceRoot,
                            new CrossPaneCopyTarget(folder, null, null, null), token).ConfigureAwait(false);
                }
                else
                {
                    if (!mapped.TryGetValue(parent, out string? folderId))
                        throw new IOException("대상 부모 폴더가 없습니다.");
                    var api = plan.TargetRoot.RemoteApi!;
                    if ((await api.ListChildrenAsync(folderId, token).ConfigureAwait(false))
                        .Any(x => x.Name.Equals(row.Source!.Name, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("대상 클라우드에 동일한 이름이 존재합니다.");
                    if (row.Kind == FolderDifferenceKind.AddFolder)
                    {
                        var created = await api.CreateFolderAsync(folderId, row.Source!.Name, token).ConfigureAwait(false);
                        if (!created.IsFolder || string.IsNullOrWhiteSpace(created.Id))
                            throw new IOException("클라우드 폴더 생성 결과가 불확실합니다.");
                        mapped.Add(row.Relative, created.Id);
                    }
                    else
                        await CopyFileAsync(row.Source!, plan.SourceRoot,
                            plan.TargetRoot with { RemoteFolderId = folderId }, token).ConfigureAwait(false);
                }
                completed++;
                details.Add("생성 완료 · " + row.Relative);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or
                InvalidOperationException or NotSupportedException or ArgumentException or CryptographicException)
            {
                failed++;
                // Never echo server exception text containing private paths or signed URLs.
                details.Add("실패 · " + row.Relative + " · 원본 보존 / 대상 확인 필요");
                // When a parent directory failed, children will fail without unsafe fallback.
            }
            progress?.Report(new(completed + failed, total, row.Relative));
        }
        return new(completed, failed, canceled, details);
    }

    private static async Task CopyFileAsync(FolderSyncEntry source, CrossPaneCopyTarget sourceRoot,
        CrossPaneCopyTarget destination, CancellationToken token)
    {
        var copySource = source.LocalPath is string path
            ? new CrossPaneCopySource(path, null, null)
            : new CrossPaneCopySource(null, source.RemoteItem, sourceRoot.RemoteTransfer, sourceRoot.RemoteApi);
        var report = await CrossPaneBatchCopyService.CopyAsync([copySource], destination, null, token)
            .ConfigureAwait(false);
        if (report.Canceled) throw new OperationCanceledException(token);
        if (report.Succeeded != 1 || report.Failed != 0)
            throw new IOException("대상 파일 복사·검증이 완료되지 않았습니다.");
    }
}
