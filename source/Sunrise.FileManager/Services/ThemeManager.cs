using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Media;
using Sunrise.FileManager.Models;

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
        ["ThemeNavigationRail"] = ("#EFF4F8", "#506984"),
        ["ThemeShellBrand"] = ("#F5F9FC", "#101D30"),
        ["ThemeShellBrandText"] = ("#173B56", "#F7FBFF"),
        ["ThemeShellBrandMuted"] = ("#526B83", "#B6CDE2"),
        ["ThemeShellSun"] = ("#F7BA3B", "#F7BA3B"),
        ["ThemeShellModeSurface"] = ("#EEF4F9", "#1C2A3A"),
        ["ThemeShellCommands"] = ("#FFFFFF", "#1B2636"),
        ["ThemeUtilityRail"] = ("#EFF4F8", "#15344C"),
        ["ThemeNavigationRailText"] = ("#315B7A", "#EFF7FF"),
        ["ThemeUtilityRailText"] = ("#315B78", "#F3FBFF"),
        ["ThemeUtilityRailHover"] = ("#DCEBF4", "#295976"),
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

    // R33 (phase 2): shared light-only accent for the main shell, all Explorer panes,
    // and the right tool rail.  This is deliberately IN-MEMORY until the settings
    // editor / durable preferences are implemented in phase 4.
    // No account, document, registry or network data is touched.
    private static string? _lightAccentHex;
    public static string? LightAccentHex => _lightAccentHex;

    /// <summary>Read-only preview of a candidate palette; does not update WPF resources.</summary>
    public static IReadOnlyDictionary<string, string> PreviewLightAccentPalette(string mainHex) =>
        LightAccentPaletteEngine.Generate(mainHex);

    /// <summary>
    /// Configure an optional custom light accent. Null/empty restores the official
    /// light palette. In dark mode this value is retained but NEVER applied.
    /// Rejected HEX input leaves the previous selection intact.
    /// Caller should subsequently call Apply(requestedTheme) on the UI dispatcher.
    /// </summary>
    public static void ConfigureLightAccent(string? mainHex)
    {
        string? normalized = string.IsNullOrWhiteSpace(mainHex)
            ? null : LightAccentPaletteEngine.NormalizeHex(mainHex);
        // Validate the entire candidate before replacing an active accent.
        if (normalized is not null) ValidateCustomPalette(LightAccentPaletteEngine.Generate(normalized));
        _lightAccentHex = normalized;
    }

    private static void ValidateCustomPalette(IReadOnlyDictionary<string, string> colors)
    {
        if (colors.Count != Palette.Count || Palette.Keys.Any(key => !colors.ContainsKey(key)))
            throw new InvalidOperationException("맞춤 테마 색상 항목이 기존 리소스 계약과 일치하지 않습니다.");
    }

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
        // A single resolved palette is applied to Application.Resources, which is
        // shared by all four panes and by the main window's right-side tool rail.
        IReadOnlyDictionary<string, string>? custom = null;
        if (!dark && _lightAccentHex is not null)
        {
            custom = LightAccentPaletteEngine.Generate(_lightAccentHex);
            ValidateCustomPalette(custom);
        }
        var resources = Application.Current?.Resources
            ?? throw new InvalidOperationException("WPF Application 리소스가 아직 초기화되지 않았습니다.");
        foreach (var (key, pair) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(
                dark ? pair.Dark : custom is null ? pair.Light : custom[key])!;
            // Mutating existing shared brushes updates both StaticResource users and
            // DynamicResource bindings; a frozen brush must be replaced instead.
            if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
                brush.Color = color;
            else
                resources[key] = new SolidColorBrush(color);
        }
        // Ordinary file names are bound to FileEntry.NameForeground, not a
        // DynamicResource: update its plain-string light colors before panes
        // publish their NameForeground change notifications. Category-coded
        // icons retain semantic file-type colors.
        if (!dark)
            FileEntry.ConfigureLightNameColors(
                custom is null ? Palette["TextPrimary"].Light : custom["TextPrimary"],
                custom is null ? Palette["TextMuted"].Light : custom["TextMuted"],
                custom is null ? Palette["ThemeShellBrandText"].Light : custom["ThemeShellBrandText"]);

        // WPF selection colors also follow the customized *light* palette.
        var highlightColor = (Color)ColorConverter.ConvertFromString(
            dark ? "#28475D" : custom is null ? "#DCEEF8" : custom["Selected"])!;
        var highlightTextColor = (Color)ColorConverter.ConvertFromString(
            dark ? "#EFF7FF" : custom is null ? "#172B43" : custom["TextPrimary"])!;
        if (resources[SystemColors.HighlightBrushKey] is SolidColorBrush selection && !selection.IsFrozen)
            selection.Color = highlightColor;
        else
            resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(highlightColor);
        if (resources[SystemColors.HighlightTextBrushKey] is SolidColorBrush selectedText && !selectedText.IsFrozen)
            selectedText.Color = highlightTextColor;
        else
            resources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush(highlightTextColor);
        IsDark = dark;
    }
}
