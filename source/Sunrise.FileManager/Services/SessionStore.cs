using System.IO;
using System.Text.Json;

namespace Sunrise.FileManager.Services;

public sealed class PanelSession
{
    public List<string> TabPaths { get; set; } = [];
    public int SelectedTabIndex { get; set; }
    public bool TreeVisible { get; set; } = true;
    public bool PreviewVisible { get; set; }
    public bool DetailsVisible { get; set; }
    public string ViewMode { get; set; } = "Details";
    public double PreviewHeight { get; set; } = 135;
    public bool ShowHidden { get; set; } = true;
    public bool ShowExtensions { get; set; } = true;
    public bool ShowCheckboxes { get; set; }
    public bool ColorCoding { get; set; } = true;
    public List<double> ColumnWidths { get; set; } = [];
    public string SortField { get; set; } = "Name";
    public bool SortDescending { get; set; }
    public string CategoryFilter { get; set; } = "All";
    public string SearchText { get; set; } = string.Empty;
}

public sealed class AppSession
{
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
                panel.SortField is null || panel.SortField.Length > 80 ||
                panel.CategoryFilter is null || panel.CategoryFilter.Length > 80 ||
                panel.SearchText is null || panel.SearchText.Length > 1000 ||
                panel.ColumnWidths.Any(width => !double.IsFinite(width) || width < 0 || width > 4000) ||
                !double.IsFinite(panel.PreviewHeight) || panel.PreviewHeight <= 0))
            throw new ArgumentException("화면 설정 구조 또는 값이 올바르지 않습니다.");
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
