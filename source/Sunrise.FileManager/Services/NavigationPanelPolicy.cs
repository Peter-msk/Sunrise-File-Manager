namespace Sunrise.FileManager.Services;
public static class NavigationPanelPolicy
{
    // Per-pane adjustable width. Preserve the legacy default on existing session files.
    public const double DefaultWidth = 272;
    public const double FixedWidth = DefaultWidth; // compatibility for existing callers
    public const double MinimumWidth = 145;
    public const double MaximumWidth = 520;
    public const double RailWidth = 22;
    public static double NormalizeWidth(double width) =>
        double.IsFinite(width) ? Math.Clamp(width, MinimumWidth, MaximumWidth) : DefaultWidth;
    public static double FitWidth(double width, double paneWidth, bool autoHide)
    {
        double desired = NormalizeWidth(width);
        if (!double.IsFinite(paneWidth) || paneWidth <= 0) return desired;
        // In pinned mode leave a useful file list at every supported split layout.
        // Pinned layout also spends 6 DIP on the pane border and 10 on the list
        // margins, in addition to the 100-DIP minimum file column.
        double allowance = paneWidth - RailWidth - (autoHide ? 35 : 116);
        return Math.Min(desired, Math.Max(MinimumWidth, allowance));
    }
    public static double ReservedWidth(bool visible, bool autoHide, double width = DefaultWidth) =>
        visible && !autoHide ? NormalizeWidth(width) : 0;
    public static bool DrawerVisible(bool visible, bool autoHide, bool revealed) => visible && (!autoHide || revealed);
}
