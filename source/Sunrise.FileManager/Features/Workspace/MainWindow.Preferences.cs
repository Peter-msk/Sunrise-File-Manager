using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Services;
namespace Sunrise.FileManager;
public partial class MainWindow
{
    private Dictionary<string, SplitRatioSetting> _splitRatios = [];
    private AppSession _restoredPreferences = new();
    private void CaptureWorkspaceRatios()
    {
        if (!_layoutInitialized || Workspace.ColumnDefinitions.Count == 0 || Workspace.RowDefinitions.Count == 0) return;
        var columns = Workspace.ColumnDefinitions.Where((_, i) => i % 2 == 0).Select(x => Math.Max(1, x.ActualWidth)).ToList();
        var rows = Workspace.RowDefinitions.Where((_, i) => i % 2 == 0).Select(x => Math.Max(1, x.ActualHeight)).ToList();
        if (columns.Any(x => x <= 1) || rows.Any(x => x <= 1)) return;
        _splitRatios[_splitPreset.ToString()] = new SplitRatioSetting { Columns = columns, Rows = rows };
    }
    private void RestoreWorkspaceRatios()
    {
        if (!_splitRatios.TryGetValue(_splitPreset.ToString(), out var ratio)) return;
        var columns = Workspace.ColumnDefinitions.Where((_, i) => i % 2 == 0).ToArray();
        var rows = Workspace.RowDefinitions.Where((_, i) => i % 2 == 0).ToArray();
        if (columns.Length == ratio.Columns.Count)
            for (int i = 0; i < columns.Length; i++) columns[i].Width = new GridLength(ratio.Columns[i], GridUnitType.Star);
        if (rows.Length == ratio.Rows.Count)
            for (int i = 0; i < rows.Length; i++) rows[i].Height = new GridLength(ratio.Rows[i], GridUnitType.Star);
    }
    private void RestoreUserPreferences(AppSession session)
    {
        _restoredPreferences = session;
        _splitRatios = session.SplitRatios;
        for (int count = 1; count <= 4 && count < session.LastSplitPresets.Count; count++)
            _lastPresetByCount[count] = WorkspaceSplitLayoutEngine.ParsePresetName(session.LastSplitPresets[count], count);
        AiPanel.RestorePreferences(session.AiSettings);
        PreviewPane.RestorePreferences(session.PreviewFitWidth, session.PreviewZoom);
        Inspector.PreferredAuthor = session.MemoAuthor;
        Inspector.DetailSectionRatio = session.InspectorDetailRatio;
        AiPanel.PreferencesChanged += ScheduleSessionSave;
        PreviewPane.PreferencesChanged += ScheduleSessionSave;
        InspectorSplitter.DragCompleted += (_, _) => ScheduleSessionSave();
        PreviewColumnSplitter.DragCompleted += (_, _) => ScheduleSessionSave();
        Loaded += (_, _) =>
        {
            SourceSelector.SelectedIndex = Math.Clamp(session.TransferSourceIndex, 0, _visiblePanels - 1);
            DestinationSelector.SelectedIndex = Math.Clamp(session.TransferDestinationIndex, 0, _visiblePanels - 1);
            CollisionSelector.SelectedIndex = Math.Clamp(session.TransferCollisionIndex, 0, 1);
            if (session.AiWindowVisible) OpenAiWindow();
        };
    }
}
