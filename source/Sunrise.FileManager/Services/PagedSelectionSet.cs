namespace Sunrise.FileManager.Services;

/// <summary>
/// Explicit, volatile selection across bounded listing pages. Never survives navigation,
/// a filter change, or application restart; only physical paths are accepted by callers.
/// </summary>
public sealed class PagedSelectionSet
{
    public const int MaxSelection = 20_000;
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    public int Count => _paths.Count;
    public bool Contains(string path) => _paths.Contains(path);
    public IReadOnlyList<string> Snapshot() => _paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    public void Clear() => _paths.Clear();
    public bool Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (_paths.Contains(path)) return true;
        return _paths.Count < MaxSelection && _paths.Add(path);
    }
    public void Remove(string path) => _paths.Remove(path);
    public bool ReplaceAll(IEnumerable<string> paths)
    {
        string[] requested = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length > MaxSelection || requested.Any(string.IsNullOrWhiteSpace)) return false;
        _paths.Clear();
        foreach (string path in requested) _paths.Add(path);
        return true;
    }
    public void SyncVisible(IEnumerable<string> visible, IEnumerable<string> selected)
    {
        var visibleSet = visible.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedSet = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in visibleSet)
            if (!selectedSet.Contains(path)) _paths.Remove(path);
        foreach (string path in selectedSet)
            if (visibleSet.Contains(path)) Add(path);
    }
}
