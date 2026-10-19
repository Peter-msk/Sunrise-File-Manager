namespace Sunrise.FileManager.Services;

public sealed record WorkspaceLayout(bool SideDock, double AiWidth, double AiHeight, double MinimumWidth, double MinimumHeight);
public sealed record ExplorerLayout(bool SearchBelow, bool Tree, bool Details);

public static class UiLayoutPolicy
{
    public static WorkspaceLayout Workspace(double width, double height, int panels, bool aiOpen)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 || panels is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(width));
        bool side = aiOpen && width >= 1280;
        double aiHeight = !aiOpen || side ? 0 : Math.Max(0, Math.Min(Math.Clamp(height * .55, 260, 380), height - 120));
        return new(side, side ? 390 : 0, aiHeight, panels == 1 ? 350 : 686, panels <= 2 ? 340 : 686);
    }
    public static ExplorerLayout Explorer(double width, bool compact, bool treePreference, bool detailsPreference)
    {
        double available = Math.Max(0, width - 32);
        bool tree = treePreference; // R08: independent pane preference, including 2-4 split layouts
        bool details = !compact && detailsPreference && available - (tree ? 190 : 0) >= 465;
        return new(width < 640, tree, details);
    }
}
