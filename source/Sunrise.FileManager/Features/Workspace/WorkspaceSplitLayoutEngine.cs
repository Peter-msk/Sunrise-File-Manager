namespace Sunrise.FileManager.Services;

public enum SplitLayoutPreset
{
    Single = 0,             // 1분할: 단일 전체 화면 (1x1)
    TwoColumns = 1,         // 2분할: 좌·우 2열 (가로 분할)
    TwoRows = 2,            // 2분할: 상·하 2행 (세로 분할)
    ThreeTop1Bottom2 = 3,   // 3분할: 상단 1행 + 하단 좌·우 2열
    ThreeBottom1Top2 = 4,   // 3분할: 상단 좌·우 2열 + 하단 1행
    ThreeLeft1Right2 = 5,   // 3분할: 좌측 1열 + 우측 상·하 2행
    ThreeRight1Left2 = 6,   // 3분할: 좌측 상·하 2행 + 우측 1열
    ThreeColumns = 7,       // 3분할: 좌·중·우 가로 3열
    ThreeRows = 8,          // 3분할: 상·중·하 세로 3행
    FourGrid2x2 = 9,        // 4분할: 2×2 쿼드 격자 (Q-Dir 기본)
    FourColumns = 10,       // 4분할: 가로 4열 나란히
    FourRows = 11,          // 4분할: 세로 4행 나란히
    FourTop1Bottom3 = 12,   // 4분할: 상단 1행 + 하단 3열
    FourLeft1Right3 = 13    // 4분할: 좌측 1열 + 우측 3행
}

public sealed record SplitPresetOption(
    SplitLayoutPreset Preset,
    int PanelCount,
    string ShortLabel,
    string Description);

public sealed record PaneGridPlacement(
    int PaneIndex,
    int Row,
    int Column,
    int RowSpan,
    int ColumnSpan);

public sealed record SplitterGridPlacement(
    int Row,
    int Column,
    bool Vertical,
    int Span);

public sealed record SplitGridBlueprint(
    SplitLayoutPreset Preset,
    int PanelCount,
    int StarColumns,
    int StarRows,
    IReadOnlyList<PaneGridPlacement> Panes,
    IReadOnlyList<SplitterGridPlacement> Splitters);

/// <summary>
/// Pure workspace split-pane and dock geometry calculator.
/// Provides Q-Dir style predefined row/column layout presets per panel count (1~4)
/// and deterministic grid cell/splitter blueprints.
/// </summary>
public static class WorkspaceSplitLayoutEngine
{
    // The workspace extent and track limits must agree with one another and
    // leave room for each pane's fixed header rows plus an actual file viewport.
    public const double MinimumPaneWidth = 350;
    public const double MinimumPaneHeight = 340;
    public const double SplitterThickness = 11;

    public static (double Width, double Height) GetMinimumWorkspaceSize(SplitLayoutPreset preset)
    {
        var blueprint = BuildBlueprint(preset);
        return (blueprint.StarColumns * MinimumPaneWidth + (blueprint.StarColumns - 1) * SplitterThickness,
            blueprint.StarRows * MinimumPaneHeight + (blueprint.StarRows - 1) * SplitterThickness);
    }

    private static readonly SplitPresetOption[] AllPresetOptions =
    [
        new(SplitLayoutPreset.Single, 1, "단일 전체 화면", "Windows 기본 탐색기와 동일한 단일 폴더 화면"),
        new(SplitLayoutPreset.TwoColumns, 2, "◫ 좌·우 2열", "좌측 창 1 · 우측 창 2 (가로 2열 분할)"),
        new(SplitLayoutPreset.TwoRows, 2, "⬒ 상·하 2행", "상단 창 1 · 하단 창 2 (세로 2행 분할)"),
        new(SplitLayoutPreset.ThreeTop1Bottom2, 3, "⬓ 상1 · 하2", "상단 전체 창 1 + 하단 좌·우 2열 (창 2·3)"),
        new(SplitLayoutPreset.ThreeBottom1Top2, 3, "⬒ 상2 · 하1", "상단 좌·우 2열 (창 1·2) + 하단 전체 창 3"),
        new(SplitLayoutPreset.ThreeLeft1Right2, 3, "◧ 좌1 · 우2", "좌측 전체 창 1 + 우측 상·하 2행 (창 2·3)"),
        new(SplitLayoutPreset.ThreeRight1Left2, 3, "◨ 좌2 · 우1", "좌측 상·하 2행 (창 1·2) + 우측 전체 창 3"),
        new(SplitLayoutPreset.ThreeColumns, 3, "lll 가로 3열", "좌 · 중 · 우 나란히 3열 배치"),
        new(SplitLayoutPreset.ThreeRows, 3, "☰ 세로 3행", "상 · 중 · 하 나란히 3행 배치"),
        new(SplitLayoutPreset.FourGrid2x2, 4, "⊞ 2×2 격자", "상단 2열 + 하단 2열 (Q-Dir 표준 쿼드 격자)"),
        new(SplitLayoutPreset.FourColumns, 4, "llll 가로 4열", "좌→우 4열 나란히 배치"),
        new(SplitLayoutPreset.FourRows, 4, "☰ 세로 4행", "상→하 4행 나란히 배치"),
        new(SplitLayoutPreset.FourTop1Bottom3, 4, "⬓ 상1 · 하3", "상단 전체 창 1 + 하단 3열 (창 2·3·4)"),
        new(SplitLayoutPreset.FourLeft1Right3, 4, "◧ 좌1 · 우3", "좌측 전체 창 1 + 우측 3행 (창 2·3·4)")
    ];

    public static int ClampVisiblePanels(int requestedPanels) =>
        Math.Clamp(requestedPanels, 1, 4);

    public static IReadOnlyList<SplitPresetOption> GetPresetsForCount(int panelCount)
    {
        int clamped = ClampVisiblePanels(panelCount);
        return AllPresetOptions.Where(x => x.PanelCount == clamped).ToArray();
    }

    public static SplitLayoutPreset GetDefaultPreset(int panelCount) => ClampVisiblePanels(panelCount) switch
    {
        1 => SplitLayoutPreset.Single,
        2 => SplitLayoutPreset.TwoColumns,
        3 => SplitLayoutPreset.ThreeTop1Bottom2,
        _ => SplitLayoutPreset.FourGrid2x2
    };

    public static int GetPanelCountForPreset(SplitLayoutPreset preset) => preset switch
    {
        SplitLayoutPreset.Single => 1,
        SplitLayoutPreset.TwoColumns or SplitLayoutPreset.TwoRows => 2,
        SplitLayoutPreset.ThreeTop1Bottom2 or SplitLayoutPreset.ThreeBottom1Top2 or
        SplitLayoutPreset.ThreeLeft1Right2 or SplitLayoutPreset.ThreeRight1Left2 or
        SplitLayoutPreset.ThreeColumns or SplitLayoutPreset.ThreeRows => 3,
        _ => 4
    };

    public static SplitLayoutPreset NormalizePresetForCount(int panelCount, SplitLayoutPreset? candidate)
    {
        int clamped = ClampVisiblePanels(panelCount);
        if (candidate is SplitLayoutPreset p && Enum.IsDefined(p) && GetPanelCountForPreset(p) == clamped)
            return p;
        return GetDefaultPreset(clamped);
    }

    public static SplitLayoutPreset ParsePresetName(string? name, int fallbackPanelCount)
    {
        if (!string.IsNullOrWhiteSpace(name) && Enum.TryParse<SplitLayoutPreset>(name, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) &&
            GetPanelCountForPreset(parsed) == ClampVisiblePanels(fallbackPanelCount))
        {
            return parsed;
        }
        return GetDefaultPreset(fallbackPanelCount);
    }

    public static SplitLayoutPreset CycleNextPreset(int panelCount, SplitLayoutPreset current)
    {
        var list = GetPresetsForCount(panelCount);
        if (list.Count <= 1) return GetDefaultPreset(panelCount);
        int idx = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Preset == current) { idx = i; break; }
        }
        return list[(idx + 1) % list.Count].Preset;
    }

    public static SplitGridBlueprint BuildBlueprint(SplitLayoutPreset preset) => preset switch
    {
        SplitLayoutPreset.Single => new(
            SplitLayoutPreset.Single, 1, 1, 1,
            [new(0, 0, 0, 1, 1)],
            []),

        SplitLayoutPreset.TwoColumns => new(
            SplitLayoutPreset.TwoColumns, 2, 2, 1,
            [new(0, 0, 0, 1, 1), new(1, 0, 2, 1, 1)],
            [new(0, 1, true, 1)]),

        SplitLayoutPreset.TwoRows => new(
            SplitLayoutPreset.TwoRows, 2, 1, 2,
            [new(0, 0, 0, 1, 1), new(1, 2, 0, 1, 1)],
            [new(1, 0, false, 1)]),

        SplitLayoutPreset.ThreeTop1Bottom2 => new(
            SplitLayoutPreset.ThreeTop1Bottom2, 3, 2, 2,
            [new(0, 0, 0, 1, 3), new(1, 2, 0, 1, 1), new(2, 2, 2, 1, 1)],
            [new(1, 0, false, 3), new(2, 1, true, 1)]),

        SplitLayoutPreset.ThreeBottom1Top2 => new(
            SplitLayoutPreset.ThreeBottom1Top2, 3, 2, 2,
            [new(0, 0, 0, 1, 1), new(1, 0, 2, 1, 1), new(2, 2, 0, 1, 3)],
            [new(0, 1, true, 1), new(1, 0, false, 3)]),

        SplitLayoutPreset.ThreeLeft1Right2 => new(
            SplitLayoutPreset.ThreeLeft1Right2, 3, 2, 2,
            [new(0, 0, 0, 3, 1), new(1, 0, 2, 1, 1), new(2, 2, 2, 1, 1)],
            [new(0, 1, true, 3), new(1, 2, false, 1)]),

        SplitLayoutPreset.ThreeRight1Left2 => new(
            SplitLayoutPreset.ThreeRight1Left2, 3, 2, 2,
            [new(0, 0, 0, 1, 1), new(1, 2, 0, 1, 1), new(2, 0, 2, 3, 1)],
            [new(1, 0, false, 1), new(0, 1, true, 3)]),

        SplitLayoutPreset.ThreeColumns => new(
            SplitLayoutPreset.ThreeColumns, 3, 3, 1,
            [new(0, 0, 0, 1, 1), new(1, 0, 2, 1, 1), new(2, 0, 4, 1, 1)],
            [new(0, 1, true, 1), new(0, 3, true, 1)]),

        SplitLayoutPreset.ThreeRows => new(
            SplitLayoutPreset.ThreeRows, 3, 1, 3,
            [new(0, 0, 0, 1, 1), new(1, 2, 0, 1, 1), new(2, 4, 0, 1, 1)],
            [new(1, 0, false, 1), new(3, 0, false, 1)]),

        SplitLayoutPreset.FourColumns => new(
            SplitLayoutPreset.FourColumns, 4, 4, 1,
            [new(0, 0, 0, 1, 1), new(1, 0, 2, 1, 1), new(2, 0, 4, 1, 1), new(3, 0, 6, 1, 1)],
            [new(0, 1, true, 1), new(0, 3, true, 1), new(0, 5, true, 1)]),

        SplitLayoutPreset.FourRows => new(
            SplitLayoutPreset.FourRows, 4, 1, 4,
            [new(0, 0, 0, 1, 1), new(1, 2, 0, 1, 1), new(2, 4, 0, 1, 1), new(3, 6, 0, 1, 1)],
            [new(1, 0, false, 1), new(3, 0, false, 1), new(5, 0, false, 1)]),

        SplitLayoutPreset.FourTop1Bottom3 => new(
            SplitLayoutPreset.FourTop1Bottom3, 4, 3, 2,
            [new(0, 0, 0, 1, 5), new(1, 2, 0, 1, 1), new(2, 2, 2, 1, 1), new(3, 2, 4, 1, 1)],
            [new(1, 0, false, 5), new(2, 1, true, 1), new(2, 3, true, 1)]),

        SplitLayoutPreset.FourLeft1Right3 => new(
            SplitLayoutPreset.FourLeft1Right3, 4, 2, 3,
            [new(0, 0, 0, 5, 1), new(1, 0, 2, 1, 1), new(2, 2, 2, 1, 1), new(3, 4, 2, 1, 1)],
            [new(0, 1, true, 5), new(1, 2, false, 1), new(3, 2, false, 1)]),

        _ => new(
            SplitLayoutPreset.FourGrid2x2, 4, 2, 2,
            [new(0, 0, 0, 1, 1), new(1, 0, 2, 1, 1), new(2, 2, 0, 1, 1), new(3, 2, 2, 1, 1)],
            [new(0, 1, true, 3), new(1, 0, false, 3)])
    };

    /// <summary>
    /// Determines whether shrinking the panel count (e.g. 2 -> 1 or 4 -> 2) should swap the active pane
    /// into the last visible slot so the user's currently active working directory never disappears from view.
    /// </summary>
    public static (bool ShouldSwap, int SurvivingSlotIndex) ResolveShrinkPreservation(
        int previousVisiblePanels, int newVisiblePanels, int activePaneIndex, bool activePaneHasUserNavigation)
    {
        int prev = ClampVisiblePanels(previousVisiblePanels);
        int next = ClampVisiblePanels(newVisiblePanels);
        if (next < prev && activePaneIndex >= next && activePaneIndex < prev && activePaneHasUserNavigation)
        {
            return (true, next - 1);
        }
        return (false, Math.Clamp(activePaneIndex, 0, next - 1));
    }

    public static double CalculateAiDockHeight(bool aiOpen, double fileAreaActualHeight)
    {
        if (!aiOpen || !double.IsFinite(fileAreaActualHeight) || fileAreaActualHeight <= 0)
            return 0;
        return Math.Clamp(fileAreaActualHeight * 0.43, 235, 355);
    }

    public static int ResolveOppositePaneIndex(int visiblePanels, int activePaneIndex, int selectedDestinationIndex)
    {
        if (visiblePanels <= 1) return -1;
        if (selectedDestinationIndex >= 0 && selectedDestinationIndex < visiblePanels && selectedDestinationIndex != activePaneIndex)
            return selectedDestinationIndex;
        return (activePaneIndex + 1) % visiblePanels;
    }

    public static string ResolveOllamaHeaderButtonLabel(string badgeText)
    {
        if (string.IsNullOrWhiteSpace(badgeText)) return "🤖 AI 연결 (온보드 Ollama)";
        if (badgeText.StartsWith("🟢", StringComparison.Ordinal)) return "🟢 AI 연결됨 (Ollama)";
        if (badgeText.StartsWith("🟡", StringComparison.Ordinal)) return "🟡 AI 준비됨 (Ollama)";
        return "🤖 AI 연결 (온보드 Ollama)";
    }
}
