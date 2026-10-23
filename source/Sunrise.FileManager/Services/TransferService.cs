using System.IO;

namespace Sunrise.FileManager.Services;

public enum TransferKind { Copy, Move }
public enum CollisionPolicy { Skip, Rename, Ask }
public enum CollisionChoice { Skip, Rename, Replace, Cancel }
public sealed record TransferConflict(string Source, string Destination, bool IsDirectory,
    long SourceSize, long DestinationSize, DateTime SourceModified, DateTime DestinationModified);
public sealed record CollisionDecision(CollisionChoice Choice, bool ApplyToAll = false);

/// <summary>One operation owns its pause state; cancellation always releases a paused worker.</summary>
public sealed class TransferControl
{
    private volatile TaskCompletionSource<bool>? _resume;
    public bool IsPaused => _resume is not null;
    public void Pause() => Interlocked.CompareExchange(ref _resume,
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), null);
    public void Resume() => Interlocked.Exchange(ref _resume, null)?.TrySetResult(true);
    public Task WaitAsync(CancellationToken token) => _resume?.Task.WaitAsync(token) ?? Task.CompletedTask;
}

public sealed record TransferProgress(
    int Completed, int Total, long BytesDone, long BytesTotal, string CurrentName, string Phase, long BytesTransferred = 0);

public sealed record TransferResult(int Completed, int Skipped, int Failed, bool Canceled, IReadOnlyList<string> Errors)
{
    // Actual destinations, including collision-renamed ones, are required for safe undo.
    public IReadOnlyList<(string Source, string Destination)> MovedPaths { get; init; } = [];
    public IReadOnlyList<(string Source, string Destination)> PublishedPaths { get; init; } = [];
    public IReadOnlyList<string> ReplacementBackups { get; init; } = [];
    public string Summary => $"완료 {Completed}개 · 건너뜀 {Skipped}개 · 실패 {Failed}개" +
        (Canceled ? " · 사용자 취소" : "");
}

/// <summary>
/// Copies through a private sibling staging path and publishes only completed items.
/// All filesystem operations run with the caller's existing Windows permissions.
/// Collisions are skipped, not overwritten. Same-volume moves use atomic renames; cross-volume moves publish a verified copy before source cleanup.
/// </summary>
public static class TransferService
{
    public const int MaxPlannedEntries = 50_000;
    private const int BufferSize = 256 * 1024;

    private sealed record FilePart(string RelativePath, long Length, DateTime LastWriteUtc);
    private sealed record RootPlan(string Source, string Destination, bool IsDirectory,
        IReadOnlyList<string> Subdirectories, IReadOnlyList<FilePart> Files, long TotalBytes);

    public static Task<TransferResult> RunAsync(
        IReadOnlyList<string> sources, string destinationFolder, TransferKind kind,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken,
        CollisionPolicy collision = CollisionPolicy.Skip,
        IReadOnlyDictionary<string, string>? exactDestinations = null,
        Func<TransferConflict, CancellationToken, Task<CollisionDecision>>? resolveConflict = null,
        TransferControl? control = null) =>
        Task.Run(() => ExecuteAsync(sources, destinationFolder, kind, progress, cancellationToken, collision, exactDestinations, resolveConflict, control), cancellationToken);

    private static async Task<TransferResult> ExecuteAsync(
        IReadOnlyList<string> sources, string destinationFolder, TransferKind kind,
        IProgress<TransferProgress>? progress, CancellationToken token, CollisionPolicy collision, IReadOnlyDictionary<string, string>? exactDestinations,
        Func<TransferConflict, CancellationToken, Task<CollisionDecision>>? resolveConflict, TransferControl? control)
    {
        if (sources.Count == 0) throw new ArgumentException("선택된 파일이나 폴더가 없습니다.");
        string destination = Path.GetFullPath(destinationFolder);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException($"대상 폴더를 찾을 수 없습니다: {destination}");
        for (DirectoryInfo? folder = new DirectoryInfo(destination); folder is not null; folder = folder.Parent)
            if (ReparsePointPolicy.IsBlocked(folder.FullName, folder.Attributes)) throw new IOException("링크 폴더 아래로는 전송하지 않습니다.");
        if (sources.Count > 1000) throw new IOException("한 작업에서는 최대 1,000개 최상위 항목을 선택할 수 있습니다.");

        var uniqueSources = sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Nested selections would invalidate child paths when the parent folder is moved.
        foreach (string candidate in uniqueSources)
            foreach (string other in uniqueSources)
                if (!string.Equals(candidate, other, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(other) && IsSameOrDescendant(candidate, other))
                    throw new IOException("상위 폴더와 그 안의 하위 항목을 동시에 선택할 수 없습니다.");

        var plans = new List<RootPlan>(uniqueSources.Count);
        int scanned = 0;
        long totalBytes = 0;
        foreach (string source in uniqueSources)
        {
            token.ThrowIfCancellationRequested();
            RootPlan plan = BuildPlan(source, destination, kind, token, ref scanned);
            if (exactDestinations is not null)
            {
                if (!exactDestinations.TryGetValue(source, out string? exact)) throw new IOException("승인된 대상 이름이 없습니다.");
                exact = Path.GetFullPath(exact);
                if (!string.Equals(Path.GetDirectoryName(exact), destination, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(exact, source, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("승인된 대상 경로가 올바르지 않습니다.");
                FileActionsService.ValidateName(Path.GetFileName(exact));
                plan = plan with { Destination = exact };
            }
            plans.Add(plan);
            totalBytes = checked(totalBytes + plan.TotalBytes);
        }
        int complete = 0, skipped = 0, failed = 0;
        long bytesDone = 0, bytesTransferred = 0;
        void Report(TransferProgress value) => progress?.Report(value with { BytesTransferred = bytesTransferred });
        var errors = new List<string>();
        var movedPaths = new List<(string Source, string Destination)>();
        var backups = new List<string>();
        var publishedPaths = new List<(string Source, string Destination)>();
        CollisionDecision? remembered = null;
        async Task<CollisionDecision> Resolve(TransferConflict conflict, CancellationToken ct)
        {
            if (remembered is not null) return remembered;
            var decision = resolveConflict is null ? new CollisionDecision(CollisionChoice.Skip)
                : await resolveConflict(conflict, ct).ConfigureAwait(false);
            if (decision.ApplyToAll && decision.Choice != CollisionChoice.Cancel) remembered = decision;
            return decision;
        }
        foreach (RootPlan plan in plans)
        {
            if (token.IsCancellationRequested) break;
            string itemName = Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.Source));
            Report(new TransferProgress(complete + skipped + failed, plans.Count,
                bytesDone, totalBytes, itemName, "작업 중"));
            try
            {
                if (control is not null) await control.WaitAsync(token).ConfigureAwait(false);
                RootPlan effectivePlan = plan;
                if (File.Exists(plan.Destination) || Directory.Exists(plan.Destination))
                {
                    if (exactDestinations is not null) throw new IOException("승인 이후 대상 이름이 충돌했습니다.");
                    // Directory contents are merged, while each colliding file gets its own decision.
                    if (plan.IsDirectory && Directory.Exists(plan.Destination) && collision == CollisionPolicy.Ask)
                    {
                        if (ReparsePointPolicy.IsBlocked(plan.Destination))
                            throw new IOException("대상 폴더가 링크이므로 병합하지 않습니다.");
                        var children = Directory.GetFileSystemEntries(plan.Source)
                            .Where(p => !string.Equals(Path.GetFileName(p), FileMemoStore.DirectoryName, StringComparison.OrdinalIgnoreCase)).ToArray();
                        // Memo siblings are handled with their owning file, never independently moved
                        // away from a skipped source file or attached to an unrelated destination.
                        bool mergeSkipped = false, mergeFailed = false;
                        long mergeBase = bytesDone;
                        foreach (string child in children)
                        {
                            token.ThrowIfCancellationRequested();
                            IReadOnlyList<FileMemoStore.MemoRecord> copyMemos = [];
                            if (kind == TransferKind.Copy && File.Exists(child))
                            {
                                copyMemos = FileMemoStore.ListForFile(child, out int unreadableMemos);
                                if (unreadableMemos > 0 || copyMemos.Any(m => m.HasIntegrityWarning))
                                    throw new IOException("병합할 파일의 메모를 검증할 수 없어 원본을 보존합니다.");
                            }
                            long childBase = bytesDone, childTransferredBase = bytesTransferred;
                            var childProgress = new InlineProgress<TransferProgress>(p =>
                            {
                                bytesDone = childBase + p.BytesDone;
                                bytesTransferred = childTransferredBase + p.BytesTransferred;
                                Report(new TransferProgress(complete + skipped + failed, plans.Count,
                                    bytesDone, totalBytes, p.CurrentName, "폴더 병합 중 · " + p.Phase));
                            });
                            var merged = await ExecuteAsync([child], plan.Destination, kind, childProgress,
                                token, collision, null, Resolve, control).ConfigureAwait(false);
                            mergeSkipped |= merged.Skipped > 0;
                            mergeFailed |= merged.Failed > 0;
                            errors.AddRange(merged.Errors);
                            movedPaths.AddRange(merged.MovedPaths);
                            publishedPaths.AddRange(merged.PublishedPaths);
                            foreach (var pair in merged.PublishedPaths.Where(p => string.Equals(p.Source, child, StringComparison.OrdinalIgnoreCase)))
                                foreach (var memo in copyMemos)
                                {
                                    token.ThrowIfCancellationRequested();
                                    string memoTarget = FileMemoStore.MemoPath(pair.Destination, memo.CreatedLocal, memo.UserName);
                                    if (File.Exists(memoTarget)) throw new IOException("대상 메모가 존재하여 덮어쓰지 않습니다.");
                                    FileMemoStore.Save(pair.Destination, memo.Text, memo.CreatedLocal, memo.UserName);
                                }
                            backups.AddRange(merged.ReplacementBackups);
                            if (merged.Canceled) return new TransferResult(complete, skipped, failed, true, errors)
                                { MovedPaths = movedPaths, PublishedPaths = publishedPaths, ReplacementBackups = backups };
                        }
                        // Keep the source root for leaf-level undo; never recursively delete a merge source.
                        if (mergeFailed) failed++; else if (mergeSkipped) skipped++; else complete++;
                        bytesDone = mergeBase + plan.TotalBytes;
                        continue;
                    }
                    CollisionChoice choice = collision switch
                    {
                        CollisionPolicy.Rename => CollisionChoice.Rename,
                        CollisionPolicy.Ask => (await Resolve(CreateConflict(plan), token).ConfigureAwait(false)).Choice,
                        _ => CollisionChoice.Skip
                    };
                    if (choice == CollisionChoice.Cancel)
                        return new TransferResult(complete, skipped, failed, true, errors)
                            { MovedPaths = movedPaths, PublishedPaths = publishedPaths, ReplacementBackups = backups };
                    if (choice == CollisionChoice.Skip) { skipped++; bytesDone += plan.TotalBytes; continue; }
                    if (choice == CollisionChoice.Replace)
                    {
                        string backup = await ReplaceFileAsync(plan, kind, bytes =>
                        {
                            bytesDone += bytes; bytesTransferred += bytes;
                            Report(new TransferProgress(complete + skipped + failed, plans.Count, bytesDone, totalBytes, itemName, "파일 바꾸기 중"));
                        }, token, control).ConfigureAwait(false);
                        backups.Add(backup);
                        publishedPaths.Add((plan.Source, plan.Destination));
                        // Replacement moves are intentionally excluded from ordinary rename undo.
                        // Their old destination remains at the disclosed backup path.
                        complete++;
                        continue;
                    }
                    effectivePlan = plan with { Destination = FindAvailableName(plan.Destination, plan.IsDirectory) };
                }
                if (kind == TransferKind.Move)
                {
                    if (IsDifferentVolume(effectivePlan.Source, effectivePlan.Destination))
                    {
                        await MoveAcrossVolumesAsync(effectivePlan, bytes =>
                        {
                            bytesDone += bytes; bytesTransferred += bytes;
                            Report(new TransferProgress(complete + skipped + failed, plans.Count,
                                bytesDone, totalBytes, itemName, "다른 드라이브로 이동 중"));
                        }, token, control).ConfigureAwait(false);
                    }
                    else
                    {
                        // Same-volume rename preserves existing memo and undo semantics.
                        FileActionsService.MoveEntry(effectivePlan.Source, effectivePlan.Destination);
                        bytesDone += plan.TotalBytes;
                    }
                    movedPaths.Add((effectivePlan.Source, effectivePlan.Destination));
                }
                else
                {
                    await CopyAtomicallyAsync(effectivePlan, bytes =>
                    {
                        bytesDone += bytes; bytesTransferred += bytes;
                        Report(new TransferProgress(complete + skipped + failed, plans.Count,
                            bytesDone, totalBytes, itemName, "복사 중"));
                    }, token, control).ConfigureAwait(false);
                }
                publishedPaths.Add((effectivePlan.Source, effectivePlan.Destination));
                complete++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                // Do not retry denied accesses or silently change Windows security settings.
                string detail = ex is UnauthorizedAccessException
                    ? $"접근 권한이 없어 처리하지 못했습니다. 파일 사용 중 여부와 원본/대상 권한을 확인하세요. ({ex.Message})"
                    : ex.Message;
                errors.Add($"{itemName}: {detail}");
                if (kind == TransferKind.Move && !IsDifferentVolume(plan.Source, plan.Destination))
                    bytesDone += plan.TotalBytes;
            }
            finally
            {
                Report(new TransferProgress(complete + skipped + failed, plans.Count,
                    Math.Min(bytesDone, totalBytes), totalBytes, itemName, "진행 중"));
            }
        }
        bool canceled = token.IsCancellationRequested;
        Report(new TransferProgress(complete + skipped + failed, plans.Count,
            Math.Min(bytesDone, totalBytes), totalBytes, string.Empty, canceled ? "취소됨" : "작업 종료"));
        return new TransferResult(complete, skipped, failed, canceled, errors) { MovedPaths = movedPaths, PublishedPaths = publishedPaths, ReplacementBackups = backups };
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
    private static TransferConflict CreateConflict(RootPlan plan)
    {
        var target = new FileInfo(plan.Destination);
        return new TransferConflict(plan.Source, plan.Destination, plan.IsDirectory,
            plan.TotalBytes, target.Exists ? target.Length : 0,
            File.GetLastWriteTime(plan.Source), File.GetLastWriteTime(plan.Destination));
    }

    private static async Task<string> ReplaceFileAsync(RootPlan plan, TransferKind kind,
        Action<int> onBytes, CancellationToken token, TransferControl? control)
    {
        if (plan.IsDirectory || !File.Exists(plan.Destination) ||
            ReparsePointPolicy.IsBlocked(plan.Destination))
            throw new IOException("파일과 폴더의 종류가 다르거나 대상이 링크입니다. 새 이름을 선택하세요.");
        var sourceMemos = FileMemoStore.ListForFile(plan.Source, out int sourceUnreadable);
        var targetMemos = FileMemoStore.ListForFile(plan.Destination, out int targetUnreadable);
        if (sourceUnreadable != 0 || targetUnreadable != 0 || targetMemos.Count != 0 ||
            sourceMemos.Any(m => m.HasIntegrityWarning))
            throw new IOException("기존 메모 보존을 위해 이 파일은 바꾸지 않습니다. 새 이름으로 전송하세요.");
        var memoSnapshot = kind == TransferKind.Move ? MemoTransferSnapshot.Capture(plan.Source, sourceMemos) : null;
        var original = new FileInfo(plan.Destination);
        long length = original.Length;
        DateTime modified = original.LastWriteTimeUtc;
        string stage = Path.Combine(Path.GetDirectoryName(plan.Destination)!, ".sunrise-replacement-" + Guid.NewGuid().ToString("N"));
        string backup = plan.Destination + ".sunrise-before-" + Guid.NewGuid().ToString("N");
        bool published = false;
        try
        {
            await CopyAtomicallyAsync(plan with { Destination = stage }, onBytes, token, control).ConfigureAwait(false);
            VerifySourceUnchanged(plan);
            original.Refresh();
            if (!original.Exists || original.Length != length || original.LastWriteTimeUtc != modified ||
                ReparsePointPolicy.IsBlocked(original.FullName, original.Attributes))
                throw new IOException("선택 이후 대상이 변경되어 바꾸기를 중단했습니다.");
            var latestTargetMemos = FileMemoStore.ListForFile(plan.Destination, out int unreadableTargetMemos);
            if (unreadableTargetMemos != 0 || latestTargetMemos.Count != 0) throw new IOException("전송 중 대상 메모가 변경되어 바꾸기를 중단했습니다.");
            memoSnapshot?.VerifySource();
            token.ThrowIfCancellationRequested();
            File.Replace(stage, plan.Destination, backup);
            published = true;
            if (memoSnapshot is not null)
            {
                memoSnapshot.CopyTo(plan.Destination, token);
                memoSnapshot.DeleteSource(plan.Destination, () => VerifySourceUnchanged(plan), token);
            }
            return backup;
        }
        catch (Exception ex) when (published)
        {
            throw new IOException($"파일 바꾸기 후 원본 정리를 완료하지 못했습니다. 원본과 대상을 확인하세요. 기존 대상 보존 위치: {backup}. {ex.Message}", ex);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    private static string FindAvailableName(string proposed, bool isDirectory)
    {
        string folder = Path.GetDirectoryName(proposed)!;
        string original = Path.GetFileName(proposed);
        string extension = isDirectory ? string.Empty : Path.GetExtension(original);
        string stem = original[..(original.Length - extension.Length)];
        for (int number = 1; number <= 999; number++)
        {
            string candidate = Path.Combine(folder, $"{stem} ({number}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("자동 이름 변경 후보가 모두 사용 중입니다 (999회 제한).");
    }

    private static RootPlan BuildPlan(string source, string targetFolder, TransferKind kind,
        CancellationToken token, ref int scanned)
    {
        if (!File.Exists(source) && !Directory.Exists(source))
            throw new FileNotFoundException($"원본을 찾을 수 없습니다: {source}");
        if (ReparsePointPolicy.IsBlocked(source))
            throw new IOException($"연결·접합점은 복사 또는 이동 대상으로 지원하지 않습니다: {source}");
        bool isDirectory = Directory.Exists(source);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        if (string.IsNullOrWhiteSpace(name)) throw new IOException("드라이브 루트 자체는 전송할 수 없습니다.");
        string target = Path.Combine(targetFolder, name);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            if (kind == TransferKind.Move) throw new IOException("원본과 대상이 같은 경로입니다.");
            target = FindAvailableName(target, isDirectory);
        }
        if (isDirectory && IsSameOrDescendant(targetFolder, source))
            throw new IOException("폴더를 자기 자신 또는 하위 폴더로 복사·이동할 수 없습니다.");

        var directories = new List<string>();
        var files = new List<FilePart>();
        long sumBytes = 0;
        if (!isDirectory)
        {
            var info = new FileInfo(source);
            files.Add(new FilePart(string.Empty, info.Length, info.LastWriteTimeUtc));
            sumBytes = info.Length;
            if (++scanned > MaxPlannedEntries) throw new IOException("선택한 항목이 최대 처리 건수를 초과했습니다.");
        }
        else
        {
            var pending = new Stack<string>();
            pending.Push(source);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string folder = pending.Pop();
                foreach (string child in Directory.EnumerateFileSystemEntries(folder))
                {
                    token.ThrowIfCancellationRequested();
                    if (++scanned > MaxPlannedEntries) throw new IOException("선택한 항목이 최대 처리 건수를 초과했습니다.");
                    if (ReparsePointPolicy.IsBlocked(child))
                        throw new IOException($"원본 폴더에 연결·접합점이 있습니다: {child}");
                    string relative = Path.GetRelativePath(source, child);
                    if (Directory.Exists(child))
                    {
                        directories.Add(relative);
                        pending.Push(child);
                    }
                    else
                    {
                        var info = new FileInfo(child);
                        sumBytes = checked(sumBytes + info.Length);
                        files.Add(new FilePart(relative, info.Length, info.LastWriteTimeUtc));
                    }
                }
            }
        }
        return new RootPlan(source, target, isDirectory, directories, files, sumBytes);
    }

    /// <summary>Drive/root comparison; rooted UNC paths are treated as separate volumes.</summary>
    internal static bool IsDifferentVolume(string source, string destination) =>
        !string.Equals(Path.GetPathRoot(Path.GetFullPath(source)),
            Path.GetPathRoot(Path.GetFullPath(destination)), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cross-volume cut/move: stage -> publish -> recheck source -> remove original.
    /// A failed verification or source cleanup preserves the completed destination copy and reports
    /// an error instead of claiming that the original was moved. Never delete source on cancellation.
    /// </summary>
    private static async Task MoveAcrossVolumesAsync(RootPlan plan, Action<int> onBytes, CancellationToken token, TransferControl? control = null)
    {
        IReadOnlyList<FileMemoStore.MemoRecord> memos = [];
        if (!plan.IsDirectory)
        {
            memos = FileMemoStore.ListForFile(plan.Source, out int unreadable);
            if (unreadable != 0 || memos.Any(m => m.HasIntegrityWarning))
                throw new IOException("원본 파일의 메모를 안전하게 읽거나 검증할 수 없어 이동을 중단했습니다.");
            foreach (var memo in memos)
                if (File.Exists(FileMemoStore.MemoPath(plan.Destination, memo.CreatedLocal, memo.UserName)))
                    throw new IOException("대상 폴더에 같은 날짜·작성자의 메모가 있어 덮어쓰지 않고 이동을 중단했습니다.");
        }

        var memoSnapshot = !plan.IsDirectory ? MemoTransferSnapshot.Capture(plan.Source, memos) : null;
        // This never modifies source. The destination is only published after all bytes are written.
        await CopyAtomicallyAsync(plan, onBytes, token, control).ConfigureAwait(false);
        try
        {
            if (memoSnapshot is not null)
            {
                memoSnapshot.CopyTo(plan.Destination, token);
                memoSnapshot.DeleteSource(plan.Destination, () => VerifySourceUnchanged(plan), token);
            }
            else
            {
                token.ThrowIfCancellationRequested();
                VerifySourceUnchanged(plan);
                Directory.Delete(plan.Source, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            // After publish, the destination is a user-visible copy and must not be silently removed.
            // The source may still exist (or be partially cleaned); always surface both paths.
            throw new IOException("대상에 복사본을 만들었지만 원본 정리를 완료하지 못했습니다. " +
                "원본과 대상의 파일·메모를 확인한 후 수동으로 정리하세요. " +
                $"원본: {plan.Source} / 대상: {plan.Destination}. 원인: {ex.Message}", ex);
        }
    }

    private static void VerifySourceUnchanged(RootPlan plan)
    {
        if (ReparsePointPolicy.IsBlocked(plan.Source))
            throw new IOException("이동 중 원본이 링크로 변경되었습니다.");
        if (!plan.IsDirectory)
        {
            VerifySourceFile(plan.Source, plan.Files[0]);
            return;
        }
        var expectedFiles = plan.Files.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        var expectedFolders = plan.Subdirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(plan.Source);
        int seenFiles = 0, seenFolders = 0;
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string child in Directory.EnumerateFileSystemEntries(current))
            {
                if (ReparsePointPolicy.IsBlocked(child))
                    throw new IOException("이동 중 원본에 새 링크가 발견되었습니다.");
                string relative = Path.GetRelativePath(plan.Source, child);
                if (Directory.Exists(child))
                {
                    if (!expectedFolders.Contains(relative)) throw new IOException("이동 중 원본 폴더가 변경되었습니다.");
                    seenFolders++;
                    pending.Push(child);
                }
                else
                {
                    if (!expectedFiles.TryGetValue(relative, out var expected))
                        throw new IOException("이동 중 원본에 새 파일이 생겼습니다.");
                    VerifySourceFile(child, expected);
                    seenFiles++;
                }
            }
        }
        if (seenFiles != expectedFiles.Count || seenFolders != expectedFolders.Count)
            throw new IOException("이동 중 원본 항목 개수가 변경되었습니다.");
    }

    private static void VerifySourceFile(string path, FilePart expected)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expected.Length || info.LastWriteTimeUtc != expected.LastWriteUtc)
            throw new IOException("복사 도중 원본이 변경되어 삭제하지 않았습니다: " + path);
    }

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        string relative = Path.GetRelativePath(parent, candidate);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static async Task CopyAtomicallyAsync(RootPlan plan, Action<int> onBytes, CancellationToken token, TransferControl? control = null)
    {
        string parent = Path.GetDirectoryName(plan.Destination)!;
        string temporary = Path.Combine(parent, ".sunrise-staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (plan.IsDirectory)
            {
                Directory.CreateDirectory(temporary);
                foreach (string relative in plan.Subdirectories)
                {
                    token.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.Combine(temporary, relative));
                }
                foreach (FilePart part in plan.Files)
                {
                    token.ThrowIfCancellationRequested();
                    string output = Path.Combine(temporary, part.RelativePath);
                    await CopyFileAsync(Path.Combine(plan.Source, part.RelativePath), output, part.Length, onBytes, token, control)
                        .ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                Directory.Move(temporary, plan.Destination);
            }
            else
            {
                await CopyFileAsync(plan.Source, temporary, plan.Files[0].Length, onBytes, token, control).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, plan.Destination);
            }
        }
        catch (Exception original)
        {
            try
            {
                // Never modify source or existing destination. Staging is uniquely generated.
                if (File.Exists(temporary)) File.Delete(temporary);
                else if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Preserve BOTH errors; otherwise an orphaned staging path could be overlooked.
                // If cancellation fails to clean up, report a real failure instead of "canceled".
                throw new IOException($"복사 중 오류가 발생했고 임시 파일 정리에도 실패했습니다: {temporary}",
                    new AggregateException(original, cleanup));
            }
            throw;
        }
    }

    private static async Task CopyFileAsync(string source, string destination,
        long expectedLength, Action<int> onBytes, CancellationToken token, TransferControl? control = null)
    {
        long copied = 0;
        await using (var reader = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var writer = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            byte[] buffer = new byte[BufferSize];
            int read;
            while ((read = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (control is not null) await control.WaitAsync(token).ConfigureAwait(false);
                await writer.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                copied += read;
                onBytes(read);
            }
            await writer.FlushAsync(token).ConfigureAwait(false);
        }
        if (copied != expectedLength)
            throw new IOException($"복사 도중 원본 파일 크기가 변경되었습니다: {source}");
        // Apply timestamps only after closing the exclusive output stream (Windows sharing rules).
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }
}
