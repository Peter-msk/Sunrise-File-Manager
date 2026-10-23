using System.IO;
using Sunrise.FileManager.Models;

namespace Sunrise.FileManager.Services;

public sealed record ListingResult(
    IReadOnlyList<FileEntry> Entries,
    string? Warning,
    long? DriveFreeBytes = null,
    long? DriveTotalBytes = null,
    string? DriveSummary = null)
{
    public bool HasMore { get; init; }
    public FileEntry? NextCursor { get; init; }
    public int TotalEntries { get; init; }
    public bool IsRevealPage { get; init; }
    public FileEntry? RevealBefore { get; init; }
}

public static class DirectoryScanner
{
    // Compatibility API for callers needing a full snapshot. The interactive pane uses
    // ReadPageAsync so ordinary folder rows are retained in bounded pages instead.
    public const int MaxEntries = int.MaxValue;

    public static Task<ListingResult> ReadAsync(string path, CancellationToken cancellationToken, bool showHidden = true, int maxEntries = MaxEntries) =>
        Task.Run(() => Read(path, cancellationToken, showHidden, maxEntries), cancellationToken);

    public const int PageSize = 2000;
    public static Task<ListingResult> ReadPageAsync(string path, FileEntry? after, string query,
        string sortField, bool descending, string category, bool showHidden, CancellationToken token,
        string? revealPath = null) => Task.Run(() =>
    {
        var comparer = FileListView.CreateComparer(sortField, descending);
        var filter = ExplorerQuery.Parse(query);
        var reverse = Comparer<FileEntry>.Create((a, b) => comparer.Compare(b, a));
        var queue = new PriorityQueue<FileEntry, FileEntry>(reverse);
        var memos = LoadMemoizedFileNames(path);
        int total = 0, unreadable = 0;
        string? error = null;
        FileEntry? reveal = null;
        if (after is null && revealPath is not null && (File.Exists(revealPath) || Directory.Exists(revealPath)))
            reveal = FileEntry.From(Directory.Exists(revealPath) ? new DirectoryInfo(revealPath) : new FileInfo(revealPath), memos.Contains(Path.GetFileName(revealPath)));
        if (reveal is not null && !FileListView.Matches(reveal, filter, category, showHidden)) reveal = null;
        var revealQueue = reveal is null ? null : new PriorityQueue<FileEntry, FileEntry>(reverse);
        FileEntry? revealBefore = null;
        try
        {
            foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var entry = FileEntry.From(info, info is FileInfo && memos.Contains(info.Name));
                    if (!FileListView.Matches(entry, filter, category, showHidden)) continue;
                    total++;
                    if (after is not null && comparer.Compare(entry, after) <= 0) continue;
                    queue.Enqueue(entry, entry);
                    if (queue.Count > PageSize + 1) queue.Dequeue();
                    if (reveal is not null)
                    {
                        if (comparer.Compare(entry, reveal) >= 0)
                        {
                            revealQueue!.Enqueue(entry, entry);
                            if (revealQueue.Count > PageSize + 1) revealQueue.Dequeue();
                        }
                        else if (revealBefore is null || comparer.Compare(entry, revealBefore) > 0) revealBefore = entry;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.Message; }
        var rows = queue.UnorderedItems.Select(p => p.Element).ToList(); rows.Sort(comparer);
        bool revealPage = reveal is not null && !rows.Take(PageSize).Any(row => string.Equals(row.FullPath, reveal.FullPath, StringComparison.OrdinalIgnoreCase)) && revealQueue!.Count > 0;
        if (revealPage) { rows = revealQueue!.UnorderedItems.Select(p => p.Element).ToList(); rows.Sort(comparer); }
        token.ThrowIfCancellationRequested();
        bool more = rows.Count > PageSize;
        if (more) rows.RemoveAt(rows.Count - 1);
        if (error is not null && rows.Count == 0) throw new IOException("폴더 조회 실패: " + error);
        var warnings = new List<string>();
        if (unreadable > 0) warnings.Add($"읽기 불가 {unreadable}개");
        if (error is not null) warnings.Add("일부 조회 중단: " + error);
        var (free, size, summary) = GetDriveSpaceSummary(path);
        return new ListingResult(rows, warnings.Count > 0 ? string.Join(" · ", warnings) : null, free, size, summary)
        { HasMore = more, NextCursor = rows.LastOrDefault(), TotalEntries = total, IsRevealPage = revealPage, RevealBefore = revealPage ? revealBefore : null };
    }, token);

    public static Task<IReadOnlyList<string>> ReadMatchingPathsAsync(string folder, string query,
        string category, bool showHidden, int limit, CancellationToken token) => Task.Run<IReadOnlyList<string>>(() =>
    {
        if (limit is <= 0 or > PagedSelectionSet.MaxSelection) throw new ArgumentOutOfRangeException(nameof(limit));
        var paths = new List<string>();
        var filter = ExplorerQuery.Parse(query);
        var memos = LoadMemoizedFileNames(folder);
        foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var entry = FileEntry.From(item, item is FileInfo && memos.Contains(item.Name));
                if (!FileListView.Matches(entry, filter, category, showHidden)) continue;
                if (paths.Count == limit) throw new InvalidOperationException($"조건 일치 항목이 {limit:N0}개를 초과합니다. 검색 조건을 좁혀주세요.");
                paths.Add(entry.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable items must not be silently called all */ throw new IOException("전체 선택에 필요한 항목을 읽지 못했습니다.", ex); }
        }
        return paths;
    }, token);

    private static ListingResult Read(string path, CancellationToken cancellationToken, bool showHidden, int maxEntries)
    {
        var entries = new List<FileEntry>();
        int unreadable = 0;
        bool truncated = false;
        string? enumerationFailure = null;

        var memoizedFiles = LoadMemoizedFileNames(path);

        try
        {
            foreach (FileSystemInfo item in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count >= maxEntries)
                {
                    truncated = true;
                    break;
                }
                try
                {
                    if (!showHidden)
                    {
                        FileAttributes attr = item.Attributes;
                        if ((attr & FileAttributes.Hidden) != 0) continue;
                    }
                    bool hasMemo = item is FileInfo && memoizedFiles.Contains(item.Name);
                    entries.Add(FileEntry.From(item, hasMemo));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    unreadable++;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            enumerationFailure = exception.Message;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (enumerationFailure is not null && entries.Count == 0)
            throw new IOException($"폴더의 목록을 읽을 수 없습니다: {enumerationFailure}");
        entries.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            return NaturalNameComparer.Instance.Compare(a.Name, b.Name);
        });

        var warnings = new List<string>();
        if (truncated) warnings.Add($"목록이 {maxEntries:N0}개에서 제한되었습니다.");
        if (unreadable > 0) warnings.Add($"읽을 수 없는 항목 {unreadable:N0}개를 생략했습니다.");
        if (enumerationFailure is not null) warnings.Add($"목록 조회 중단: {enumerationFailure}");

        var (freeBytes, totalBytes, driveSummary) = GetDriveSpaceSummary(path);
        return new ListingResult(
            entries,
            warnings.Count == 0 ? null : string.Join(" ", warnings),
            freeBytes,
            totalBytes,
            driveSummary);
    }

    private static HashSet<string> LoadMemoizedFileNames(string folderPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string memoDir = Path.Combine(folderPath, FileMemoStore.DirectoryName);
            if (!Directory.Exists(memoDir)) return result;
            foreach (string memoFile in Directory.EnumerateFiles(memoDir, "*" + FileMemoStore.Extension))
            {
                string fileName = Path.GetFileName(memoFile);
                int marker = fileName.LastIndexOf("_memo_", StringComparison.OrdinalIgnoreCase);
                if (marker > 0)
                {
                    result.Add(fileName[..marker]);
                }
            }
        }
        catch { }
        return result;
    }

    private static (long? Free, long? Total, string? Summary) GetDriveSpaceSummary(string folderPath)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folderPath));
            if (string.IsNullOrEmpty(root)) return (null, null, null);
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return (null, null, null);
            long free = drive.AvailableFreeSpace;
            long total = drive.TotalSize;
            string summary = $"{drive.Name.TrimEnd('\\')} 여유 {FileEntry.HumanSize(free)} / {FileEntry.HumanSize(total)}";
            return (free, total, summary);
        }
        catch
        {
            return (null, null, null);
        }
    }
}
