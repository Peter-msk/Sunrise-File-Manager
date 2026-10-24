using System.Runtime.InteropServices;
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
        var filter = ExplorerQuery.Parse(query);
        var visible = entries.Where(item => { token.ThrowIfCancellationRequested(); return Matches(item, filter, categoryFilter, showHidden); }).ToList();
        visible.Sort(CreateComparer(field, descending));
        token.ThrowIfCancellationRequested();
        return visible;
    }

    public static bool Matches(FileEntry item, string query, string category, bool showHidden)
        => Matches(item, ExplorerQuery.Parse(query), category, showHidden);
    public static bool Matches(FileEntry item, ExplorerQuery filter, string category, bool showHidden)
    {
        if (!showHidden && item.IsHidden) return false;
        if (!filter.Matches(item)) return false;
        return category switch
        {
            "Folder" => item.IsDirectory,
            "Memo" => item.HasMemo,
            "문서" or "이미지" or "동영상" or "오디오" or "압축파일" or "코드_설정" => !item.IsDirectory && AiLocalPlanner.Category(item.Name, false) == category,
            _ => true
        };
    }
    public static IComparer<FileEntry> CreateComparer(string field, bool descending) =>
        Comparer<FileEntry>.Create((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            int order = field switch
            {
                "TypeName" => StringComparer.CurrentCultureIgnoreCase.Compare(a.TypeName, b.TypeName),
                "SizeBytes" => a.SizeBytes.CompareTo(b.SizeBytes),
                "ModifiedAt" => a.ModifiedAt.CompareTo(b.ModifiedAt),
                "CreatedAt" => a.CreatedAt.CompareTo(b.CreatedAt),
                "DeletedAt" => a.DeletedAt.CompareTo(b.DeletedAt),
                "OriginalLocation" => StringComparer.CurrentCultureIgnoreCase.Compare(a.OriginalLocation, b.OriginalLocation),
                "HasMemo" => a.HasMemo.CompareTo(b.HasMemo),
                _ => NaturalNameComparer.Instance.Compare(a.Name, b.Name)
            };
            if (descending) order = order > 0 ? -1 : order < 0 ? 1 : 0;
            if (order == 0) order = NaturalNameComparer.Instance.Compare(a.Name, b.Name);
            return order != 0 ? order : StringComparer.Ordinal.Compare(a.FullPath, b.FullPath);
        });

}

/// <summary>Windows Explorer-style natural order: 2 before 10, including embedded numbers.</summary>
internal sealed class NaturalNameComparer : IComparer<string>
{
    public static readonly NaturalNameComparer Instance = new();

    [DllImport("shlwapi.dll", EntryPoint = "StrCmpLogicalW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CompareWindows(string left, string right);

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        if (OperatingSystem.IsWindows()) return CompareWindows(left, right);
        return ComparePortable(left, right);
    }

    // Portable fallback for non-Windows logic tests, not a replacement for Shell collation.
    private static int ComparePortable(string left, string right)
    {
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (char.IsAsciiDigit(left[i]) && char.IsAsciiDigit(right[j]))
            {
                int iEnd = i, jEnd = j;
                while (iEnd < left.Length && char.IsAsciiDigit(left[iEnd])) iEnd++;
                while (jEnd < right.Length && char.IsAsciiDigit(right[jEnd])) jEnd++;
                ReadOnlySpan<char> a = left.AsSpan(i, iEnd - i).TrimStart('0');
                ReadOnlySpan<char> b = right.AsSpan(j, jEnd - j).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                int digits = a.CompareTo(b, StringComparison.Ordinal);
                if (digits != 0) return digits;
                i = iEnd;
                j = jEnd;
                continue;
            }
            int order = char.ToUpperInvariant(left[i]).CompareTo(char.ToUpperInvariant(right[j]));
            if (order != 0) return order;
            i++;
            j++;
        }
        return left.Length == i ? (right.Length == j ? 0 : -1) : 1;
    }
}
