namespace Sunrise.FileManager.Services;
public static class MarqueeSelectionPolicy
{
    public static HashSet<int> Select(IEnumerable<int> baseline, IEnumerable<int> hits, bool control, bool shift)
    {
        var selected = control || shift ? baseline.ToHashSet() : new HashSet<int>();
        if (control) selected.SymmetricExceptWith(hits); else selected.UnionWith(hits);
        return selected;
    }
    public static IEnumerable<int> HitRows(double top, double bottom, double rowHeight, int count)
    {
        if (!double.IsFinite(top) || !double.IsFinite(bottom) || !double.IsFinite(rowHeight) || rowHeight <= 0 || count <= 0 || bottom <= 0 || bottom <= top) yield break;
        int first = (int)Math.Clamp(Math.Floor(top / rowHeight), 0, count);
        int last = (int)Math.Clamp(Math.Ceiling(bottom / rowHeight) - 1, -1, count - 1);
        for (int index = first; index <= last; index++) yield return index;
    }
}
