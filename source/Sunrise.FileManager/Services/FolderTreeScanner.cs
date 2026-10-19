using System.IO;

namespace Sunrise.FileManager.Services;

public sealed record FolderChildren(IReadOnlyList<string> Paths, bool Truncated, string? Warning);

/// <summary>Bounded, cancellation-aware, one-level directory enumeration for the navigation tree.</summary>
public static class FolderTreeScanner
{
    public const int MaxChildFolders = 350;

    public static Task<FolderChildren> ReadAsync(string parent, CancellationToken token) =>
        Task.Run(() => Read(parent, token), token);

    private static FolderChildren Read(string parent, CancellationToken token)
    {
        var paths = new List<string>();
        bool truncated = false;
        string? warning = null;
        try
        {
            foreach (string path in Directory.EnumerateDirectories(parent))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(path);
                    // Do not traverse junctions/symlinks in the navigation tree.
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    paths.Add(path);
                    if (paths.Count >= MaxChildFolders)
                    {
                        truncated = true;
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // Individual child cannot be inspected; parent remains usable.
                    warning = "일부 하위 폴더에 접근할 수 없습니다.";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            warning = $"하위 폴더 조회 불가: {ex.Message}";
        }
        token.ThrowIfCancellationRequested();
        paths.Sort(StringComparer.CurrentCultureIgnoreCase);
        return new FolderChildren(paths, truncated, warning);
    }
}
