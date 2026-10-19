using System.IO;
using Sunrise.FileManager.Models;

namespace Sunrise.FileManager.Services;

public sealed record ListingResult(
    IReadOnlyList<FileEntry> Entries,
    string? Warning,
    long? DriveFreeBytes = null,
    long? DriveTotalBytes = null,
    string? DriveSummary = null);

public static class DirectoryScanner
{
    public const int MaxEntries = 20_000;

    public static Task<ListingResult> ReadAsync(string path, CancellationToken cancellationToken, bool showHidden = true) =>
        Task.Run(() => Read(path, cancellationToken, showHidden), cancellationToken);

    private static ListingResult Read(string path, CancellationToken cancellationToken, bool showHidden)
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
                if (entries.Count >= MaxEntries)
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
            return StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
        });

        var warnings = new List<string>();
        if (truncated) warnings.Add($"목록이 {MaxEntries:N0}개에서 제한되었습니다.");
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
