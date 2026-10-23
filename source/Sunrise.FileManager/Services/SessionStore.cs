using System.IO;
using System.Text.Json;

namespace Sunrise.FileManager.Services;

public sealed class ColumnWidthSetting
{
    public string Unit { get; set; } = "Pixel";
    public double Value { get; set; } = 100;
}
public sealed class SplitRatioSetting
{
    public List<double> Columns { get; set; } = [];
    public List<double> Rows { get; set; } = [];
}
public sealed class AiPanelSettings
{
    public List<ColumnWidthSetting> PlanColumnSettings { get; set; } = [];
    public List<int> PlanColumnOrder { get; set; } = [];
    public int Scope { get; set; }
    public int QuickAction { get; set; }
    public int Provider { get; set; }
    public int Collision { get; set; }
    public int DataGrade { get; set; }
    public bool ContentReadAllowed { get; set; }
    public bool EngineExpanded { get; set; }
    public bool HelpExpanded { get; set; }
    public string Destination { get; set; } = "";
    public string Command { get; set; } = "";
}
public sealed class PanelSession
{
    public string SearchScope { get; set; } = "Current";
    public string GroupField { get; set; } = "";
    public List<string> TabPaths { get; set; } = [];
    public int SelectedTabIndex { get; set; }
    public bool TreeVisible { get; set; } = true;
    public bool TreeAutoHide { get; set; }
    public double TreeWidth { get; set; } = NavigationPanelPolicy.DefaultWidth;
    public List<string> TabSearchTexts { get; set; } = [];
    public List<string> ExpandedTreePaths { get; set; } = [];
    public double TreeScrollOffset { get; set; }
    public double FileVerticalOffset { get; set; }
    public double FileHorizontalOffset { get; set; }
    public List<ColumnWidthSetting> ColumnSettings { get; set; } = [];
    public List<int> ColumnOrder { get; set; } = [];
    public List<int> HiddenDetailColumns { get; set; } = [];
    public bool PreviewVisible { get; set; }
    public bool DetailsVisible { get; set; }
    public string ViewMode { get; set; } = "Details";
    public double PreviewHeight { get; set; } = 135;
    public bool ShowHidden { get; set; }
    public bool ShowExtensions { get; set; }
    public bool ShowCheckboxes { get; set; }
    public bool ColorCoding { get; set; }
    public List<double> ColumnWidths { get; set; } = [];
    public string SortField { get; set; } = "Name";
    public bool SortDescending { get; set; }
    public string CategoryFilter { get; set; } = "All";
    public string SearchText { get; set; } = string.Empty;
}

public sealed class AppSession
{
    public Dictionary<string, SplitRatioSetting> SplitRatios { get; set; } = [];
    public List<string> LastSplitPresets { get; set; } = [];
    public AiPanelSettings AiSettings { get; set; } = new();
    public bool AiWindowVisible { get; set; }
    public double AiWindowWidth { get; set; } = 1060;
    public double AiWindowHeight { get; set; } = 790;
    public double? AiWindowLeft { get; set; }
    public double? AiWindowTop { get; set; }
    public bool AiWindowMaximized { get; set; }
    public bool PreviewFitWidth { get; set; } = true;
    public double PreviewZoom { get; set; } = 1;
    public double InspectorDetailRatio { get; set; } = .5;
    public string MemoAuthor { get; set; } = "";
    public int TransferSourceIndex { get; set; }
    public int TransferDestinationIndex { get; set; } = 1;
    public int TransferCollisionIndex { get; set; }
    public int UiScalePercent { get; set; } = UiScalePolicy.DefaultPercent;
    public int VisiblePanels { get; set; } = 1;
    public int ActivePane { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public bool IsMaximized { get; set; }
    public double InspectorWidth { get; set; } = 330;
    public double PreviewWidth { get; set; } = 350;
    public string SplitLayoutPreset { get; set; } = "Single";
    public int LayoutVersion { get; set; } = 1;
    public string Theme { get; set; } = "System";
    public bool InspectorVisible { get; set; } = true;
    public bool DockPreviewVisible { get; set; }
    public List<string> PinnedFolders { get; set; } = [];
    public List<PanelSession> Panels { get; set; } = [];
}

public static class SessionStore
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunrise", "FileManager", "session.json");

    public static AppSession Load(out string? warning) => Load(FilePath, out warning);

    internal static AppSession Load(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path)) return new AppSession();
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("화면 설정은 1MB 이하여야 합니다.");
            var session = JsonSerializer.Deserialize<AppSession>(File.ReadAllText(path), new JsonSerializerOptions { MaxDepth = 16 })
                ?? throw new JsonException("화면 설정이 비어 있습니다.");
            Validate(session);
            return session;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            warning = $"이전 화면 설정을 읽지 못해 기본 배치로 시작합니다. {exception.Message}";
            return new AppSession();
        }
    }

    private static void Validate(AppSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.PinnedFolders ??= [];
        session.SplitRatios ??= [];
        session.LastSplitPresets ??= [];
        session.AiSettings ??= new();
        session.AiSettings.PlanColumnSettings ??= [];
        session.AiSettings.PlanColumnOrder ??= [];
        if (session.AiSettings.PlanColumnSettings.Count > 20 || session.AiSettings.PlanColumnSettings.Any(x => x is null || !double.IsFinite(x.Value) || x.Value is < 0 or > 4000 || x.Unit is not ("Auto" or "Pixel" or "Star" or "SizeToCells" or "SizeToHeader")) ||
            session.AiSettings.PlanColumnOrder.Count > 20 || session.AiSettings.PlanColumnOrder.Any(x => x is < 0 or > 19) || session.AiSettings.PlanColumnOrder.Distinct().Count() != session.AiSettings.PlanColumnOrder.Count)
            throw new ArgumentException("AI 결과 열 설정이 올바르지 않습니다.");
        static bool Offset(double value) => double.IsFinite(value) && value is >= 0 and <= 1_000_000_000;
        foreach (var panel in session.Panels ?? [])
        {
            if (panel is null) continue;
            if (!Enum.TryParse<ExplorerSearchScope>(panel.SearchScope, out var searchScope) || !Enum.IsDefined(searchScope)) panel.SearchScope = "Current";
            if (panel.GroupField is not ("" or "TypeName" or "ModifiedGroup" or "SizeGroup" or "ParentFolder")) panel.GroupField = "";
            panel.TabSearchTexts ??= [];
            panel.ColumnSettings ??= [];
            panel.ColumnOrder ??= [];
            panel.ExpandedTreePaths ??= [];
            if (panel.TabSearchTexts.Count > 12 || panel.TabSearchTexts.Any(x => x is null || x.Length > 1000))
                throw new ArgumentException("탐색기 검색 설정 값이 올바르지 않습니다.");
            if (panel.ExpandedTreePaths.Count > 200 || panel.ExpandedTreePaths.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 32767 || x.Contains('\0')))
                throw new ArgumentException("탐색기 폴더 트리 설정 값이 올바르지 않습니다.");
            if (!double.IsFinite(panel.TreeWidth) || panel.TreeWidth < NavigationPanelPolicy.MinimumWidth || panel.TreeWidth > NavigationPanelPolicy.MaximumWidth)
                throw new ArgumentException("탐색창 너비 설정 값이 올바르지 않습니다.");
            if (!Offset(panel.TreeScrollOffset) || !Offset(panel.FileVerticalOffset) || !Offset(panel.FileHorizontalOffset))
                throw new ArgumentException("탐색기 스크롤 설정 값이 올바르지 않습니다.");
            if (panel.ColumnSettings.Count > 12 || panel.ColumnSettings.Any(x => x is null || !Offset(x.Value) || x.Value > 4000 || x.Unit is not ("Auto" or "Pixel" or "Star" or "SizeToCells" or "SizeToHeader")))
                throw new ArgumentException("파일 목록 열 너비 설정 값이 올바르지 않습니다.");
            if (panel.ColumnOrder.Count > 12 || panel.ColumnOrder.Any(x => x is < 0 or > 11) || panel.ColumnOrder.Distinct().Count() != panel.ColumnOrder.Count)
                throw new ArgumentException("파일 목록 열 순서 설정 값이 올바르지 않습니다.");
        }
        if (session.SplitRatios.Count > 32 || session.SplitRatios.Any(x => x.Key.Length > 80 || x.Value is null || x.Value.Columns is null || x.Value.Rows is null ||
                x.Value.Columns.Count > 4 || x.Value.Rows.Count > 4 || x.Value.Columns.Concat(x.Value.Rows).Any(v => !double.IsFinite(v) || v <= 0 || v > 100000)) ||
            session.LastSplitPresets.Count > 5 || session.LastSplitPresets.Any(x => x is null || x.Length > 80) ||
            session.AiSettings.Command is null || session.AiSettings.Command.Length > 4000 || session.AiSettings.Destination is null || session.AiSettings.Destination.Length > 32767 ||
            session.AiSettings.Scope is < 0 or > 1 || session.AiSettings.Provider is < 0 or > 1 || session.AiSettings.Collision is < 0 or > 1 || session.AiSettings.QuickAction is < 0 or > 7 || session.AiSettings.DataGrade is < 0 or > 2 ||
            (session.AiWindowLeft is double aiLeft && !double.IsFinite(aiLeft)) || (session.AiWindowTop is double aiTop && !double.IsFinite(aiTop)) ||
            !double.IsFinite(session.AiWindowWidth) || !double.IsFinite(session.AiWindowHeight) || !double.IsFinite(session.PreviewZoom) || session.PreviewZoom is < 0.1 or > 4 ||
            !double.IsFinite(session.InspectorDetailRatio) || session.InspectorDetailRatio is < .05 or > .95 || session.MemoAuthor is null || session.MemoAuthor.Length > 40 || session.TransferSourceIndex is < 0 or > 3 || session.TransferDestinationIndex is < 0 or > 3 || session.TransferCollisionIndex is < 0 or > 1)
            throw new ArgumentException("사용자 환경 설정 값이 올바르지 않습니다.");
        session.UiScalePercent = UiScalePolicy.Normalize(session.UiScalePercent);
        if (!double.IsFinite(session.WindowWidth) || !double.IsFinite(session.WindowHeight) ||
            !double.IsFinite(session.WindowLeft) || !double.IsFinite(session.WindowTop) ||
            !double.IsFinite(session.InspectorWidth) || !double.IsFinite(session.PreviewWidth) ||
            session.ActivePane is < 0 or > 3)
            throw new ArgumentException("창 크기 및 위치 설정이 올바르지 않습니다.");
        if (session.VisiblePanels is < 1 or > 4 || session.Panels is null || session.Panels.Count > 4 ||
            session.PinnedFolders.Count > 50 ||
            session.PinnedFolders.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 32767 || p.Contains('\0')) ||
            session.Panels.Any(panel => panel is null || panel.TabPaths is null || panel.TabPaths.Count > 12 ||
                panel.TabPaths.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path.Contains('\0')) ||
                panel.ColumnWidths is null || panel.ColumnWidths.Count > 12 ||
                panel.HiddenDetailColumns is null || panel.HiddenDetailColumns.Count > 6 ||
                panel.HiddenDetailColumns.Any(i => i is not (>= 2 and <= 6 or 9)) ||
                panel.HiddenDetailColumns.Distinct().Count() != panel.HiddenDetailColumns.Count ||
                panel.SortField is null || panel.SortField.Length > 80 ||
                panel.CategoryFilter is null || panel.CategoryFilter.Length > 80 ||
                panel.SearchText is null || panel.SearchText.Length > 1000 ||
                panel.ColumnWidths.Any(width => !double.IsFinite(width) || width < 0 || width > 4000) ||
                !double.IsFinite(panel.PreviewHeight) || panel.PreviewHeight <= 0))
            throw new ArgumentException("화면 설정 구조 또는 값이 올바르지 않습니다.");
    }

    // A newly constructed or never displayed WPF DataGrid can report DisplayIndex=-1.
    // This is an uninitialised UI layout, not corrupt user data; serialise its natural order.
    internal static List<int> CaptureDisplayOrder(IEnumerable<int> indices)
    {
        int[] order = indices.ToArray();
        if (order.Length > 20) throw new ArgumentException("표시 열 개수가 지원 범위를 넘었습니다.");
        return order.All(index => index >= 0 && index < order.Length) && order.Distinct().Count() == order.Length
            ? order.ToList()
            : Enumerable.Range(0, order.Length).ToList();
    }

    // WPF ScrollViewer may temporarily report an invalid offset while an inactive pane
    // is being arranged. Do not let a transient layout value prevent normal shutdown.
    internal static double CaptureScrollOffset(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1_000_000_000) : 0;

    internal static ColumnWidthSetting CaptureColumnWidth(string unit, double value)
    {
        if (unit is not ("Auto" or "Pixel" or "Star" or "SizeToCells" or "SizeToHeader"))
            return new ColumnWidthSetting { Unit = "Auto", Value = 1 };
        return new ColumnWidthSetting { Unit = unit, Value = double.IsFinite(value) ? Math.Clamp(value, 0, 4000) : 1 };
    }

    // DEFECT-FM-SESSION-001: the 1.3.5 update exit captured unopened WPF grid columns
    // with DisplayIndex=-1. Only a LIVE UI snapshot may be repaired here; direct Save()
    // continues to reject invalid persisted data. Never silently suppress I/O failures.
    public static void SaveUiSnapshot(AppSession snapshot) => SaveUiSnapshot(snapshot, FilePath);

    internal static void SaveUiSnapshot(AppSession snapshot, string path)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.AiSettings is not null)
        {
            if (snapshot.AiSettings.PlanColumnOrder is not null)
                snapshot.AiSettings.PlanColumnOrder = CaptureDisplayOrder(snapshot.AiSettings.PlanColumnOrder);
            if (snapshot.AiSettings.PlanColumnSettings is not null)
                snapshot.AiSettings.PlanColumnSettings = snapshot.AiSettings.PlanColumnSettings
                    .Select(width => width is null ? null! : CaptureColumnWidth(width.Unit, width.Value)).ToList();
        }
        if (snapshot.Panels is not null)
        {
            foreach (PanelSession panel in snapshot.Panels)
            {
                if (panel is null) continue;
                panel.TreeWidth = NavigationPanelPolicy.NormalizeWidth(panel.TreeWidth);
                panel.TreeScrollOffset = CaptureScrollOffset(panel.TreeScrollOffset);
                panel.FileVerticalOffset = CaptureScrollOffset(panel.FileVerticalOffset);
                panel.FileHorizontalOffset = CaptureScrollOffset(panel.FileHorizontalOffset);
                if (panel.ColumnOrder is not null)
                    panel.ColumnOrder = CaptureDisplayOrder(panel.ColumnOrder);
                if (panel.ColumnSettings is not null)
                    panel.ColumnSettings = panel.ColumnSettings
                        .Select(width => width is null ? null! : CaptureColumnWidth(width.Unit, width.Value)).ToList();
                if (panel.ColumnWidths is not null)
                    panel.ColumnWidths = panel.ColumnWidths.Select(width =>
                        double.IsFinite(width) ? Math.Clamp(width, 0, 4000) : 0).ToList();
            }
        }
        // Validate still enforces array bounds, path safety and all other persisted contracts.
        Save(snapshot, path);
    }

    public static void Save(AppSession session) => Save(session, FilePath);

    internal static void Save(AppSession session, string path)
    {
        Validate(session);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(session, new JsonSerializerOptions { WriteIndented = true });
        if (data.Length > 1024 * 1024) throw new ArgumentException("화면 설정은 1MB 이하여야 합니다.");
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
