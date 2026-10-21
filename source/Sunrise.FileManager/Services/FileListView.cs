using Sunrise.FileManager.Models;

namespace Sunrise.FileManager.Services;

/// <summary>Pure file-metadata filter and sort, designed to run off the WPF dispatcher.</summary>
public static class FileListView
{
    public static List<FileEntry> Apply(
        IReadOnlyList<FileEntry> entries,
        string query,
        string field,
        bool descending,
        CancellationToken token,
        string categoryFilter = "All",
        bool showHidden = true)
    {
        token.ThrowIfCancellationRequested();
        IEnumerable<FileEntry> rows = entries;
        if (!showHidden)
            rows = rows.Where(item => !item.IsHidden);
        if (!string.IsNullOrEmpty(query))
            rows = rows.Where(item => item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        if (!string.IsNullOrEmpty(categoryFilter) && categoryFilter != "All")
        {
            rows = categoryFilter switch
            {
                "Folder" => rows.Where(item => item.IsDirectory),
                "Memo" => rows.Where(item => item.HasMemo),
                "문서" or "이미지" or "동영상" or "오디오" or "압축파일" or "코드_설정" =>
                    rows.Where(item => !item.IsDirectory && AiLocalPlanner.Category(item.Name, false) == categoryFilter),
                _ => rows
            };
        }

        IOrderedEnumerable<FileEntry> ordered = rows.OrderBy(item => item.IsDirectory ? 0 : 1);
        ordered = field switch
        {
            "TypeName" => descending
                ? ordered.ThenByDescending(item => item.TypeName, StringComparer.CurrentCultureIgnoreCase)
                : ordered.ThenBy(item => item.TypeName, StringComparer.CurrentCultureIgnoreCase),
            "SizeBytes" => descending
                ? ordered.ThenByDescending(item => item.SizeBytes)
                : ordered.ThenBy(item => item.SizeBytes),
            "ModifiedAt" => descending
                ? ordered.ThenByDescending(item => item.ModifiedAt)
                : ordered.ThenBy(item => item.ModifiedAt),
            "CreatedAt" => descending
                ? ordered.ThenByDescending(item => item.CreatedAt)
                : ordered.ThenBy(item => item.CreatedAt),
            "DeletedAt" => descending ? ordered.ThenByDescending(item => item.DeletedAt) : ordered.ThenBy(item => item.DeletedAt),
            "OriginalLocation" => descending ? ordered.ThenByDescending(item => item.OriginalLocation, StringComparer.CurrentCultureIgnoreCase) : ordered.ThenBy(item => item.OriginalLocation, StringComparer.CurrentCultureIgnoreCase),
            "HasMemo" => descending
                ? ordered.ThenByDescending(item => item.HasMemo)
                : ordered.ThenBy(item => item.HasMemo),
            _ => descending
                ? ordered.ThenByDescending(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                : ordered.ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        List<FileEntry> visible = ordered.ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        token.ThrowIfCancellationRequested();
        return visible;
    }
}
