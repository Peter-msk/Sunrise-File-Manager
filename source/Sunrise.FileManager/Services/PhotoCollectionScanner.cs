using System.IO;
using Sunrise.FileManager.Models;

namespace Sunrise.FileManager.Services;

public sealed record PhotoCollection(IReadOnlyList<FileEntry> Entries, int Total, int Unreadable);
public static class PhotoCollectionScanner
{
    public static Task<PhotoCollection> ReadAsync(IReadOnlyList<string> roots, Func<string, bool> include,
        CancellationToken token, int capacity = 20_000) => Task.Run(() =>
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        var comparer = Comparer<FileEntry>.Create((a, b) =>
        {
            int order = a.ModifiedAt.CompareTo(b.ModifiedAt);
            return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath);
        });
        var queue = new PriorityQueue<FileEntry, FileEntry>(comparer);
        var pending = new Stack<string>(roots);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int total = 0, unreadable = 0;
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string current = pending.Pop();
            if (!visited.Add(Path.GetFullPath(current))) continue;
            try
            {
                if (ReparsePointPolicy.IsBlocked(current)) { unreadable++; continue; }
                foreach (var item in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (ReparsePointPolicy.IsBlocked(item.FullName, item.Attributes)) { unreadable++; continue; }
                        if (item is DirectoryInfo) pending.Push(item.FullName);
                        else if (include(item.FullName))
                        {
                            total++;
                            var entry = FileEntry.From(item);
                            queue.Enqueue(entry, entry);
                            if (queue.Count > capacity) queue.Dequeue();
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; }
        }
        var rows = queue.UnorderedItems.Select(p => p.Element).ToList();
        rows.Sort((a, b) => comparer.Compare(b, a));
        return new PhotoCollection(rows, total, unreadable);
    }, token);
}
