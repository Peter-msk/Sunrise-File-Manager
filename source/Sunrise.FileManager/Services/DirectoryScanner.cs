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
        string? revealPath = null, ExplorerSearchFilter? searchFilter = null) => Task.Run(() =>
    {
        // Normal name-sorted pages do not need file metadata for thousands of
        // off-screen entries. All filtered, hidden and reveal scans retain the
        // original metadata-aware path (and its warnings/selection semantics).
        if (sortField == "Name" && string.IsNullOrWhiteSpace(query) &&
            category == "All" && showHidden && searchFilter is null && revealPath is null)
        {
            var fastPage = TryReadNameOnlyPage(path, after, descending, token);
            if (fastPage is not null) return fastPage;
        }
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
        if (reveal is not null && (!FileListView.Matches(reveal, filter, category, showHidden) ||
            !(searchFilter?.Allows(reveal.IsDirectory, reveal.Name) ?? true))) reveal = null;
        var revealQueue = reveal is null ? null : new PriorityQueue<FileEntry, FileEntry>(reverse);
        FileEntry? revealBefore = null;
        try
        {
            foreach (var info in (searchFilter?.Target == ExplorerSearchTarget.Folders
                ? new DirectoryInfo(path).EnumerateDirectories().Cast<FileSystemInfo>()
                : new DirectoryInfo(path).EnumerateFileSystemInfos()))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (searchFilter is not null && !searchFilter.Allows(info is DirectoryInfo, info.Name)) continue;
                    if (!filter.MayMatchMetadataFree(info.Name, info is DirectoryInfo, info.FullName)) continue;
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

    // R50: one directory enumeration, O(PageSize) candidate memory, and
    // FileEntry metadata only for the visible page. If the folder changes while
    // materializing selected rows, retry through the original metadata-aware
    // implementation instead of returning a partial or incorrectly counted page.
    // This intentionally does NOT cache filesystem snapshots: refresh and the
    // next/previous page always observe current directory state.
    private static ListingResult? TryReadNameOnlyPage(string path, FileEntry? after,
        bool descending, CancellationToken token)
    {
        var comparer = Comparer<FileSystemInfo>.Create((a, b) =>
            FileListView.CompareNameKeys(a is DirectoryInfo, a.Name, a.FullName,
                b is DirectoryInfo, b.Name, b.FullName, descending));
        var reverse = Comparer<FileSystemInfo>.Create((a, b) => comparer.Compare(b, a));
        var candidates = new PriorityQueue<FileSystemInfo, FileSystemInfo>(reverse);
        int total = 0;
        try
        {
            foreach (FileSystemInfo info in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                total++;
                if (after is not null && FileListView.CompareNameKeys(
                    info is DirectoryInfo, info.Name, info.FullName,
                    after.IsDirectory, after.Name, after.FullPath, descending) <= 0) continue;
                candidates.Enqueue(info, info);
                if (candidates.Count > PageSize + 1) candidates.Dequeue();
            }
            token.ThrowIfCancellationRequested();
            var selected = candidates.UnorderedItems.Select(item => item.Element).ToList();
            selected.Sort(comparer);
            bool hasMore = selected.Count > PageSize;
            if (hasMore) selected.RemoveAt(selected.Count - 1);
            var memos = LoadMemoizedFileNames(path);
            var rows = new List<FileEntry>(selected.Count);
            foreach (FileSystemInfo info in selected)
            {
                token.ThrowIfCancellationRequested();
                rows.Add(FileEntry.From(info, info is FileInfo && memos.Contains(info.Name)));
            }
            var (free, size, summary) = GetDriveSpaceSummary(path);
            return new ListingResult(rows, null, free, size, summary)
            { HasMore = hasMore, NextCursor = rows.LastOrDefault(), TotalEntries = total };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            token.ThrowIfCancellationRequested();
            // A moved/deleted/unreadable entry requires the original per-entry
            // error accounting; never silently mark a changed scan as success.
            return null;
        }
    }

    public static Task<IReadOnlyList<string>> ReadMatchingPathsAsync(string folder, string query,
        string category, bool showHidden, int limit, CancellationToken token,
        ExplorerSearchFilter? searchFilter = null) => Task.Run<IReadOnlyList<string>>(() =>
    {
        if (limit is <= 0 or > PagedSelectionSet.MaxSelection) throw new ArgumentOutOfRangeException(nameof(limit));
        var paths = new List<string>();
        var filter = ExplorerQuery.Parse(query);
        var memos = LoadMemoizedFileNames(folder);
        foreach (var item in (searchFilter?.Target == ExplorerSearchTarget.Folders
            ? new DirectoryInfo(folder).EnumerateDirectories().Cast<FileSystemInfo>()
            : new DirectoryInfo(folder).EnumerateFileSystemInfos()))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (searchFilter is not null && !searchFilter.Allows(item is DirectoryInfo, item.Name)) continue;
                if (!filter.MayMatchMetadataFree(item.Name, item is DirectoryInfo, item.FullName)) continue;
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

    internal static HashSet<string> LoadMemoizedFileNames(string folderPath, bool strict = false)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string memoDir = Path.Combine(folderPath, FileMemoStore.DirectoryName);
            if (!Directory.Exists(memoDir)) return result;
            // Do not follow a junction/symlink #memo directory outside the
            // requested search tree. Strict searches disclose skipped coverage.
            if (ReparsePointPolicy.IsBlocked(memoDir))
            {
                if (strict) throw new IOException("메모 폴더가 링크로 연결되어 있어 검색할 수 없습니다.");
                return result;
            }
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
        // Ordinary pane listing remains best-effort; explicit is:memo search must
        // not silently claim an incomplete memo listing is a valid empty result.
        catch (Exception) when (!strict) { }
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
