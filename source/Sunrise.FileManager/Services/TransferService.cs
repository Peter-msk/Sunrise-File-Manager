using System.IO;

namespace Sunrise.FileManager.Services;

public enum TransferKind { Copy, Move }
public enum CollisionPolicy { Skip, Rename }

public sealed record TransferProgress(
    int Completed, int Total, long BytesDone, long BytesTotal, string CurrentName, string Phase);

public sealed record TransferResult(int Completed, int Skipped, int Failed, bool Canceled, IReadOnlyList<string> Errors)
{
    public string Summary => $"완료 {Completed}개 · 건너뜀 {Skipped}개 · 실패 {Failed}개" +
        (Canceled ? " · 사용자 취소" : "");
}

/// <summary>
/// Copies through a private sibling staging path and publishes only completed items.
/// All filesystem operations run with the caller's existing Windows permissions.
/// Collisions are skipped, not overwritten. Moves use same-volume atomic renames only.
/// </summary>
public static class TransferService
{
    public const int MaxPlannedEntries = 50_000;
    private const int BufferSize = 256 * 1024;

    private sealed record FilePart(string RelativePath, long Length);
    private sealed record RootPlan(string Source, string Destination, bool IsDirectory,
        IReadOnlyList<string> Subdirectories, IReadOnlyList<FilePart> Files, long TotalBytes);

    public static Task<TransferResult> RunAsync(
        IReadOnlyList<string> sources, string destinationFolder, TransferKind kind,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken,
        CollisionPolicy collision = CollisionPolicy.Skip,
        IReadOnlyDictionary<string, string>? exactDestinations = null) =>
        Task.Run(() => ExecuteAsync(sources, destinationFolder, kind, progress, cancellationToken, collision, exactDestinations), cancellationToken);

    private static async Task<TransferResult> ExecuteAsync(
        IReadOnlyList<string> sources, string destinationFolder, TransferKind kind,
        IProgress<TransferProgress>? progress, CancellationToken token, CollisionPolicy collision, IReadOnlyDictionary<string, string>? exactDestinations)
    {
        if (sources.Count == 0) throw new ArgumentException("선택된 파일이나 폴더가 없습니다.");
        string destination = Path.GetFullPath(destinationFolder);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException($"대상 폴더를 찾을 수 없습니다: {destination}");
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
        long bytesDone = 0;
        var errors = new List<string>();
        foreach (RootPlan plan in plans)
        {
            if (token.IsCancellationRequested) break;
            string itemName = Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.Source));
            progress?.Report(new TransferProgress(complete + skipped + failed, plans.Count,
                bytesDone, totalBytes, itemName, "작업 중"));
            try
            {
                RootPlan effectivePlan = plan;
                if (File.Exists(plan.Destination) || Directory.Exists(plan.Destination))
                {
                    if (exactDestinations is not null) throw new IOException("승인 이후 대상 이름이 충돌했습니다.");
                    if (collision == CollisionPolicy.Skip)
                    {
                        skipped++;
                        bytesDone += plan.TotalBytes;
                        continue;
                    }
                    effectivePlan = plan with { Destination = FindAvailableName(plan.Destination, plan.IsDirectory) };
                }
                if (kind == TransferKind.Move)
                {
                    // Same-volume rename avoids copying and deleting source data on failures.
                    FileActionsService.MoveEntry(effectivePlan.Source, effectivePlan.Destination);
                    bytesDone += plan.TotalBytes;
                }
                else
                {
                    await CopyAtomicallyAsync(effectivePlan, bytes =>
                    {
                        bytesDone += bytes;
                        progress?.Report(new TransferProgress(complete + skipped + failed, plans.Count,
                            bytesDone, totalBytes, itemName, "복사 중"));
                    }, token).ConfigureAwait(false);
                }
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
                errors.Add($"{itemName}: {ex.Message}");
                bytesDone += kind == TransferKind.Move ? plan.TotalBytes : 0;
            }
            finally
            {
                progress?.Report(new TransferProgress(complete + skipped + failed, plans.Count,
                    Math.Min(bytesDone, totalBytes), totalBytes, itemName, "진행 중"));
            }
        }
        bool canceled = token.IsCancellationRequested;
        progress?.Report(new TransferProgress(complete + skipped + failed, plans.Count,
            Math.Min(bytesDone, totalBytes), totalBytes, string.Empty, canceled ? "취소됨" : "작업 종료"));
        return new TransferResult(complete, skipped, failed, canceled, errors);
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
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"연결·접합점은 복사 또는 이동 대상으로 지원하지 않습니다: {source}");
        bool isDirectory = Directory.Exists(source);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        if (string.IsNullOrWhiteSpace(name)) throw new IOException("드라이브 루트 자체는 전송할 수 없습니다.");
        string target = Path.Combine(targetFolder, name);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            throw new IOException("원본과 대상이 같은 경로입니다.");
        if (isDirectory && IsSameOrDescendant(targetFolder, source))
            throw new IOException("폴더를 자기 자신 또는 하위 폴더로 복사·이동할 수 없습니다.");
        if (kind == TransferKind.Move && !string.Equals(Path.GetPathRoot(source),
                Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
            throw new IOException("이동은 동일 드라이브에서만 지원합니다. 다른 드라이브로는 복사를 이용하세요.");

        var directories = new List<string>();
        var files = new List<FilePart>();
        long sumBytes = 0;
        if (!isDirectory)
        {
            files.Add(new FilePart(string.Empty, new FileInfo(source).Length));
            sumBytes = files[0].Length;
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
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException($"원본 폴더에 연결·접합점이 있습니다: {child}");
                    string relative = Path.GetRelativePath(source, child);
                    if (Directory.Exists(child))
                    {
                        directories.Add(relative);
                        pending.Push(child);
                    }
                    else
                    {
                        long length = new FileInfo(child).Length;
                        sumBytes = checked(sumBytes + length);
                        files.Add(new FilePart(relative, length));
                    }
                }
            }
        }
        return new RootPlan(source, target, isDirectory, directories, files, sumBytes);
    }

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        string relative = Path.GetRelativePath(parent, candidate);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static async Task CopyAtomicallyAsync(RootPlan plan, Action<int> onBytes, CancellationToken token)
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
                    await CopyFileAsync(Path.Combine(plan.Source, part.RelativePath), output, part.Length, onBytes, token)
                        .ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                Directory.Move(temporary, plan.Destination);
            }
            else
            {
                await CopyFileAsync(plan.Source, temporary, plan.Files[0].Length, onBytes, token).ConfigureAwait(false);
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
        long expectedLength, Action<int> onBytes, CancellationToken token)
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
