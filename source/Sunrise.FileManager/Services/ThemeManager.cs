using System.Windows;
using Microsoft.Win32;
using System.Windows.Media;

namespace Sunrise.FileManager.Services;

/// <summary>Shared Sunrise Image-inspired light/dark palette; no file I/O beyond SessionStore.</summary>
public static class ThemeManager
{
    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["AppBackground"] = ("#F3F5F8", "#10131C"),
        ["PanelBackground"] = ("#FFFFFF", "#191F2B"),
        ["PanelBorder"] = ("#D6DEE8", "#354054"),
        ["ControlBackground"] = ("#FFFFFF", "#202838"),
        ["TextPrimary"] = ("#172B43", "#EAF0FC"),
        ["TextMuted"] = ("#52657A", "#ACBBD0"),
        ["Accent"] = ("#096B94", "#5DC7E8"),
        ["Selected"] = ("#DCEEF8", "#34536E"),
        ["Brand"] = ("#163A55", "#17475F"),
        ["ThemeHeader"] = ("#FFFFFF", "#191F2B"),
        ["ThemeSurface"] = ("#FFFFFF", "#191F2B"),
        ["ThemeSoft"] = ("#F7F9FC", "#222B3C"),
        ["ThemeSoftAlt"] = ("#F0F4F8", "#273246"),
        ["ThemeTab"] = ("#EEF2F7", "#273044"),
        ["ThemeTabInactive"] = ("#E5EBF2", "#303B50"),
        ["ThemeHover"] = ("#E7EFF6", "#34485D"),
        ["ThemePressed"] = ("#D7E7F2", "#426077"),
        ["ThemeStatus"] = ("#F8FAFC", "#1C2534"),
        ["ThemeTrack"] = ("#D2DFEA", "#36465A"),
        ["ThemeSplitter"] = ("#A4B8CB", "#506984"),
        ["ThemeSplitterHover"] = ("#2679A5", "#6FD6F5"),
        ["ThemeGrip"] = ("#3A769C", "#B0E8FF"),
        ["ThemeMenuBar"] = ("#EBF3F9", "#233043"),
        ["ThemeMenuHover"] = ("#D8ECF8", "#365671"),
        ["ThemeMenuText"] = ("#163A55", "#F2F6FF"),
        ["ThemeMenuPopup"] = ("#FFFFFF", "#253248"),
        ["ThemeMenuSeparator"] = ("#B7CADA", "#526780"),
        ["ThemeInputBorder"] = ("#A9B9CA", "#526580"),
        ["ThemeAltRow"] = ("#F8FAFC", "#20293A"),
        ["ThemeFolderIcon"] = ("#E9B748", "#E4AB4C"),
        ["ThemeFolderStroke"] = ("#C58D25", "#A97B32"),
        ["ThemeFileIcon"] = ("#F6FAFD", "#2D3B4F"),
        ["ThemeFileIconBorder"] = ("#A9B9CA", "#637B99"),
        ["ThemePreviewCanvas"] = ("#F1F3F6", "#161F2C"),
        ["ThemeTreeCanvas"] = ("#F5F7FA", "#202B3A"),
        ["ThemeNotice"] = ("#FFF4D8", "#453521"),
        ["ThemeNoticeText"] = ("#654C13", "#FFE2A5"),
        ["ThemeTransferBar"] = ("#EAF1F7", "#24364B"),
        ["ThemeColumnDivider"] = ("#9AACC2", "#65819D"),
        ["ThemeButtonOnAccent"] = ("#FFFFFF", "#101E2C"),
        // Color-matched split toolbar in light and dark themes.
        ["ThemeSplitInactive"] = ("#E8EFF6", "#253449"),
        ["ThemeSplitActive"] = ("#12678D", "#73D4F2"),
        ["ThemeSplitText"] = ("#20425D", "#E3EEFC"),
        ["ThemeSplitActiveText"] = ("#FFFFFF", "#10293B"),
        ["ThemeScrollThumb"] = ("#A6B9CD", "#667F9B"),
        ["ThemeScrollThumbHover"] = ("#597F9E", "#94C5E2"),
        ["ThemeComboPopup"] = ("#FFFFFF", "#26384E"),
        ["ThemeComboSelected"] = ("#D7EAF7", "#3B5874"),
    };

    public static bool IsDark { get; private set; }

    // Session value represents the user's preference, not the currently resolved palette.
    public static string Normalize(string? requested) =>
        string.Equals(requested, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" :
        string.Equals(requested, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "System";

    public static bool SystemUsesDark()
    {
        // Windows 10/11 per-user *app* color scheme (not the taskbar scheme).
        // Missing/malformed registry keys follow Windows' normal light default.
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int setting && setting == 0;
        }
        catch (System.Security.SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    public static void Apply(string? requested)
    {
        string choice = Normalize(requested);
        bool dark = choice == "Dark" || (choice == "System" && SystemUsesDark());
        IsDark = dark;
        var resources = Application.Current.Resources;
        foreach (var (key, pair) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(dark ? pair.Dark : pair.Light)!;
            if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
                brush.Color = color;
            else
                resources[key] = new SolidColorBrush(color);
        }
        if (resources[SystemColors.HighlightBrushKey] is SolidColorBrush selection && !selection.IsFrozen)
            selection.Color = (Color)ColorConverter.ConvertFromString(dark ? "#28475D" : "#DCEEF8")!;
        if (resources[SystemColors.HighlightTextBrushKey] is SolidColorBrush selectedText && !selectedText.IsFrozen)
            selectedText.Color = (Color)ColorConverter.ConvertFromString(dark ? "#EFF7FF" : "#172B43")!;
    }
}
