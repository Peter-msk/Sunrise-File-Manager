namespace Sunrise.FileManager.Services;
public static class NavigationPanelPolicy
{
    // Per-pane adjustable width. Preserve the legacy default on existing session files.
    public const double DefaultWidth = 272;
    public const double FixedWidth = DefaultWidth; // compatibility for existing callers
    public const double MinimumWidth = 145;
    public const double MaximumWidth = 520;
    public const double RailWidth = 22;
    // Available room for the file-list surface in a narrow, pinned multi-pane.
    // This is only a transient display budget; the session keeps the user's original tree width.
    public const double MinimumFileAreaBudget = 176;
    public static double NormalizeWidth(double width) =>
        double.IsFinite(width) ? Math.Clamp(width, MinimumWidth, MaximumWidth) : DefaultWidth;
    public static double FitWidth(double width, double paneWidth, bool autoHide)
    {
        double desired = NormalizeWidth(width);
        if (!double.IsFinite(paneWidth) || paneWidth <= 0) return desired;
        // A pinned tree must not consume almost the entire 350-DIP split pane.
        // The cap never writes back to the user's saved preferred tree width.
        // Auto-hidden navigation is an overlay and has its own less restrictive cap.
        double allowance = paneWidth - RailWidth - (autoHide ? 35 : MinimumFileAreaBudget);
        return Math.Min(desired, Math.Max(MinimumWidth, allowance));
    }
    public static double ReservedWidth(bool visible, bool autoHide, double width = DefaultWidth) =>
        visible && !autoHide ? NormalizeWidth(width) : 0;
    public static bool DrawerVisible(bool visible, bool autoHide, bool revealed) => visible && (!autoHide || revealed);
}
