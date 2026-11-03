using System.Collections.ObjectModel;
using System.Globalization;

namespace Sunrise.FileManager.Services;

/// <summary>
/// Phase 1: pure, deterministic light-theme palette generator. No WPF dependency,
/// session writes, theme switching, network access or company document reads.
/// Callers must explicitly opt in during a later UI integration phase.
/// </summary>
public static class LightAccentPaletteEngine
{
    private const double NormalTextContrast = 4.5;
    private const double StrongTextContrast = 7.0;

    public static string NormalizeHex(string? input)
    {
        if (input is null) throw new ArgumentException("메인 색상은 #RRGGBB 형식이어야 합니다.", nameof(input));
        string text = input.Trim();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length != 6 || !text.All(Uri.IsHexDigit))
            throw new ArgumentException("메인 색상은 #RRGGBB 형식이어야 합니다. 투명도는 지원하지 않습니다.", nameof(input));
        return "#" + text.ToUpperInvariant();
    }

    public static double ContrastRatio(string foregroundHex, string backgroundHex)
    {
        Rgb foreground = Rgb.Parse(NormalizeHex(foregroundHex));
        Rgb background = Rgb.Parse(NormalizeHex(backgroundHex));
        return Contrast(foreground, background);
    }

    /// <summary>Returns a WCAG-conformant foreground, maintaining the starting color when possible.</summary>
    public static string EnsureTextContrast(string textHex, string backgroundHex, double minimumRatio = NormalTextContrast)
    {
        if (!double.IsFinite(minimumRatio) || minimumRatio < 1 || minimumRatio > 21)
            throw new ArgumentOutOfRangeException(nameof(minimumRatio));
        Rgb text = Rgb.Parse(NormalizeHex(textHex));
        Rgb background = Rgb.Parse(NormalizeHex(backgroundHex));
        return EnsureContrast(text, background, minimumRatio).Hex;
    }

    /// <summary>
    /// Creates all of ThemeManager's 59 light-theme resource colors. Special-purpose
    /// warning/folder/sun colors retain their semantic meaning and official artwork is unchanged.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Generate(string mainHex)
    {
        Rgb seed = Rgb.Parse(NormalizeHex(mainHex));
        Rgb white = Rgb.Parse("#FFFFFF");
        Rgb black = Rgb.Parse("#000000");
        Rgb accent = DarkenForWhiteText(seed, NormalTextContrast);
        Rgb deep = Mix(accent, black, 0.35);

        // Use a small portion of the user's hue on high-luminance surfaces; do not
        // paint the complete UI in the saturated selected color.
        Rgb surface = white;
        Rgb app = Mix(white, seed, 0.055);
        Rgb soft = Mix(white, seed, 0.045);
        Rgb softAlt = Mix(white, seed, 0.085);
        Rgb panelBorder = Mix(white, deep, 0.20);
        Rgb selected = Mix(white, accent, 0.125);
        Rgb hover = Mix(white, accent, 0.09);
        Rgb pressed = Mix(white, accent, 0.18);
        Rgb primary = EnsureContrast(Mix(Rgb.Parse("#26323C"), deep, 0.12), app, StrongTextContrast);
        Rgb muted = EnsureContrast(Mix(Rgb.Parse("#56616B"), deep, 0.12), app, NormalTextContrast);
        // App and panel surface backgrounds are not necessarily identical.
        primary = EnsureContrast(primary, surface, StrongTextContrast);
        muted = EnsureContrast(muted, surface, NormalTextContrast);
        Rgb shell = Mix(white, accent, 0.07);
        Rgb menu = Mix(white, accent, 0.075);
        Rgb utility = Mix(white, accent, 0.065);
        Rgb splitInactive = Mix(white, accent, 0.10);
        Rgb bar = Mix(white, accent, 0.055);
        Rgb borderStrong = Mix(white, deep, 0.34);
        Rgb splitText = EnsureContrast(deep, splitInactive, NormalTextContrast);

        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppBackground"] = app.Hex,
            ["PanelBackground"] = surface.Hex,
            ["PanelBorder"] = panelBorder.Hex,
            ["ControlBackground"] = surface.Hex,
            ["TextPrimary"] = primary.Hex,
            ["TextMuted"] = muted.Hex,
            ["Accent"] = accent.Hex,
            ["Selected"] = selected.Hex,
            ["Brand"] = deep.Hex,
            ["ThemeHeader"] = shell.Hex,
            ["ThemeSurface"] = surface.Hex,
            ["ThemeSoft"] = soft.Hex,
            ["ThemeSoftAlt"] = softAlt.Hex,
            ["ThemeTab"] = Mix(white, accent, 0.085).Hex,
            ["ThemeTabInactive"] = Mix(white, deep, 0.12).Hex,
            ["ThemeHover"] = hover.Hex,
            ["ThemePressed"] = pressed.Hex,
            ["ThemeStatus"] = soft.Hex,
            ["ThemeTrack"] = Mix(white, deep, 0.20).Hex,
            ["ThemeSplitter"] = Mix(white, deep, 0.30).Hex,
            ["ThemeNavigationRail"] = utility.Hex,
            ["ThemeShellBrand"] = shell.Hex,
            ["ThemeShellBrandText"] = EnsureContrast(deep, shell, NormalTextContrast).Hex,
            ["ThemeShellBrandMuted"] = EnsureContrast(muted, shell, NormalTextContrast).Hex,
            ["ThemeShellSun"] = "#F7BA3B",
            ["ThemeShellModeSurface"] = Mix(white, accent, 0.105).Hex,
            ["ThemeShellCommands"] = surface.Hex,
            ["ThemeUtilityRail"] = utility.Hex,
            ["ThemeNavigationRailText"] = EnsureContrast(deep, utility, NormalTextContrast).Hex,
            ["ThemeUtilityRailText"] = EnsureContrast(deep, utility, NormalTextContrast).Hex,
            ["ThemeUtilityRailHover"] = selected.Hex,
            ["ThemeSplitterHover"] = accent.Hex,
            ["ThemeGrip"] = EnsureContrast(deep, soft, 3).Hex,
            ["ThemeMenuBar"] = menu.Hex,
            ["ThemeMenuHover"] = selected.Hex,
            ["ThemeMenuText"] = EnsureContrast(deep, menu, NormalTextContrast).Hex,
            ["ThemeMenuPopup"] = surface.Hex,
            ["ThemeMenuSeparator"] = borderStrong.Hex,
            ["ThemeInputBorder"] = borderStrong.Hex,
            ["ThemeAltRow"] = soft.Hex,
            ["ThemeFolderIcon"] = "#E9B748",
            ["ThemeFolderStroke"] = "#C58D25",
            ["ThemeFileIcon"] = "#F6FAFD",
            ["ThemeFileIconBorder"] = borderStrong.Hex,
            ["ThemePreviewCanvas"] = softAlt.Hex,
            ["ThemeTreeCanvas"] = soft.Hex,
            ["ThemeNotice"] = "#FFF4D8",
            ["ThemeNoticeText"] = "#654C13",
            ["ThemeTransferBar"] = bar.Hex,
            ["ThemeColumnDivider"] = borderStrong.Hex,
            ["ThemeButtonOnAccent"] = white.Hex,
            ["ThemeSplitInactive"] = splitInactive.Hex,
            ["ThemeSplitActive"] = accent.Hex,
            ["ThemeSplitText"] = splitText.Hex,
            ["ThemeSplitActiveText"] = white.Hex,
            ["ThemeScrollThumb"] = Mix(white, deep, 0.28).Hex,
            ["ThemeScrollThumbHover"] = Mix(white, deep, 0.46).Hex,
            ["ThemeComboPopup"] = surface.Hex,
            ["ThemeComboSelected"] = selected.Hex,
        };
        return new ReadOnlyDictionary<string, string>(map);
    }

    private static Rgb DarkenForWhiteText(Rgb seed, double minimumRatio)
    {
        Rgb white = Rgb.Parse("#FFFFFF");
        if (Contrast(white, seed) >= minimumRatio) return seed;
        return MixUntilContrast(seed, Rgb.Parse("#000000"), white, minimumRatio);
    }

    private static Rgb EnsureContrast(Rgb candidate, Rgb background, double minimumRatio)
    {
        if (Contrast(candidate, background) >= minimumRatio) return candidate;
        Rgb black = Rgb.Parse("#000000");
        Rgb white = Rgb.Parse("#FFFFFF");
        bool canDarken = Contrast(black, background) >= minimumRatio;
        bool canLighten = Contrast(white, background) >= minimumRatio;
        if (!canDarken && !canLighten)
            throw new ArgumentOutOfRangeException(nameof(minimumRatio), "배경에 적용할 수 없는 대비 수준입니다.");
        if (!canDarken) return MixUntilContrast(candidate, white, background, minimumRatio);
        if (!canLighten) return MixUntilContrast(candidate, black, background, minimumRatio);
        Rgb dark = MixUntilContrast(candidate, black, background, minimumRatio);
        Rgb light = MixUntilContrast(candidate, white, background, minimumRatio);
        // Prefer the smaller visual change while retaining an accessible foreground.
        return DistanceSquared(candidate, dark) <= DistanceSquared(candidate, light) ? dark : light;
    }

    private static Rgb MixUntilContrast(Rgb source, Rgb target, Rgb background, double ratio)
    {
        double lo = 0, hi = 1;
        for (int i = 0; i < 24; ++i)
        {
            double mid = (lo + hi) * 0.5;
            if (Contrast(Mix(source, target, mid), background) >= ratio) hi = mid;
            else lo = mid;
        }
        // Byte quantization may round back under the threshold: bias slightly toward target.
        for (int step = 0; step <= 255; ++step)
        {
            Rgb result = Mix(source, target, Math.Min(1, hi + step / 255.0));
            if (Contrast(result, background) >= ratio) return result;
        }
        return target;
    }

    private static int DistanceSquared(Rgb x, Rgb y) =>
        (x.R - y.R) * (x.R - y.R) + (x.G - y.G) * (x.G - y.G) + (x.B - y.B) * (x.B - y.B);

    private static Rgb Mix(Rgb a, Rgb b, double weight) => new(
        (byte)Math.Clamp((int)Math.Round(a.R + (b.R - a.R) * weight), 0, 255),
        (byte)Math.Clamp((int)Math.Round(a.G + (b.G - a.G) * weight), 0, 255),
        (byte)Math.Clamp((int)Math.Round(a.B + (b.B - a.B) * weight), 0, 255));

    private static double Contrast(Rgb a, Rgb b)
    {
        double al = Luminance(a), bl = Luminance(b);
        return (Math.Max(al, bl) + 0.05) / (Math.Min(al, bl) + 0.05);
    }

    private static double Luminance(Rgb color) =>
        0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    private static double Linear(byte value)
    {
        double s = value / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private readonly record struct Rgb(byte R, byte G, byte B)
    {
        public string Hex => $"#{R:X2}{G:X2}{B:X2}";
        public static Rgb Parse(string hex) => new(
            byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
