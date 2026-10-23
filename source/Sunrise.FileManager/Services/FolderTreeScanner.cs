using System.IO;

namespace Sunrise.FileManager.Services;

public sealed record FolderChildren(IReadOnlyList<string> Paths, bool Truncated, string? Warning);

/// <summary>
/// One-level, cancellation-aware navigation scanner. A page contains at most 350
/// paths regardless of the total sibling count. Re-enumeration is intentional:
/// keeping every folder of a huge network directory in a persistent cache would
/// defeat the navigation panel's memory limit.
/// </summary>
public static class FolderTreeScanner
{
    public const int MaxChildFolders = 350;

    public static Task<FolderChildren> ReadAsync(string parent, CancellationToken token) =>
        ReadPageAsync(parent, null, token);

    // Cursor is the last displayed *full path* in the same natural-name ordering.
    // A refresh starts at null, so changes to earlier names cannot be silently hidden.
    public static Task<FolderChildren> ReadPageAsync(string parent, string? afterPath, CancellationToken token) =>
        Task.Run(() => ReadPage(parent, afterPath, token), token);

    private static FolderChildren ReadPage(string parent, string? afterPath, CancellationToken token)
    {
        // The reverse comparer makes the queue's head the greatest (worst) path.
        // We retain only pageSize+1 candidates, even with very large folders.
        var reverse = Comparer<string>.Create((a, b) => ComparePathNames(b, a));
        var candidates = new PriorityQueue<string, string>(reverse);
        string? warning = null;
        try
        {
            foreach (string path in Directory.EnumerateDirectories(parent))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (ReparsePointPolicy.IsBlocked(path)) continue;
                    if (afterPath is not null && ComparePathNames(path, afterPath) <= 0) continue;
                    candidates.Enqueue(path, path);
                    if (candidates.Count > MaxChildFolders + 1)
                        candidates.Dequeue();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    warning = "일부 하위 폴더에 접근할 수 없습니다.";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            warning = $"하위 폴더 조회 불가: {ex.Message}";
        }
        token.ThrowIfCancellationRequested();
        List<string> paths = candidates.UnorderedItems.Select(entry => entry.Element).ToList();
        paths.Sort(ComparePathNames);
        bool hasMore = paths.Count > MaxChildFolders;
        if (hasMore) paths.RemoveAt(paths.Count - 1);
        return new FolderChildren(paths, hasMore, warning);
    }

    private static int ComparePathNames(string a, string b)
    {
        int cmp = NaturalNameComparer.Instance.Compare(Path.GetFileName(a), Path.GetFileName(b));
        if (cmp != 0) return cmp;
        cmp = StringComparer.OrdinalIgnoreCase.Compare(a, b);
        return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a, b);
    }
}
