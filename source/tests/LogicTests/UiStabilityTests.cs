using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

static class UiStabilityTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); passed++; Console.WriteLine("PASS P16: " + label); }
        foreach (int panels in Enumerable.Range(1, 4))
        {
            var wide = UiLayoutPolicy.Workspace(1440, 800, panels, true);
            Check(wide.SideDock && wide.AiWidth == 390 && wide.AiHeight == 0, $"wide dock {panels}");
            var narrow = UiLayoutPolicy.Workspace(760, 400, panels, true);
            Check(!narrow.SideDock && narrow.AiHeight <= 280 && narrow.MinimumWidth <= 686, $"narrow dock {panels}");
            Check(UiLayoutPolicy.Workspace(760, 400, panels, false).AiHeight == 0, $"closed dock {panels}");
        }
        Check(UiLayoutPolicy.Workspace(760, 100, 1, true).AiHeight == 0, "short workspace clamp");
        foreach (double bad in new[] { 0d, -1, double.NaN, double.PositiveInfinity })
        {
            bool rejected = false;
            try { UiLayoutPolicy.Workspace(bad, 500, 1, true); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "invalid geometry rejected");
        }
        Check(UiLayoutPolicy.Explorer(350, false, true, true) == new ExplorerLayout(true, true, false), "narrow pane");
        Check(UiLayoutPolicy.Explorer(900, false, true, true) == new ExplorerLayout(false, true, true), "wide pane");
        Check(UiLayoutPolicy.Explorer(900, true, true, true) == new ExplorerLayout(false, true, false), "compact pane");
        Check(UiLayoutPolicy.Explorer(900, false, false, false) == new ExplorerLayout(false, false, false), "preferences respected");
        var retention = new ThumbnailRetention();
        var entries = Enumerable.Range(0, 300).Select(i => new FileEntry { Name = i.ToString(), FullPath = i.ToString(), TypeName = "test", IsDirectory = false, SizeBytes = 0, ModifiedAt = DateTime.MinValue, ThumbnailSource = new object() }).ToArray();
        foreach (var entry in entries) retention.Remember(entry);
        Check(retention.Count == 256, "thumbnail retention bounded");
        Check(entries.Take(44).All(e => e.ThumbnailSource is null), "old bitmaps released");
        Check(entries.Skip(44).All(e => e.ThumbnailSource is not null), "recent bitmaps retained");
        retention.Clear();
        Check(retention.Count == 0 && entries.All(e => e.ThumbnailSource is null), "navigation releases bitmaps");
        retention.Clear(); Check(retention.Count == 0, "clear is idempotent");
        Console.WriteLine($"P16 stability: {passed} passed");
    }
}
