using System.Windows;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager;

public partial class MainWindow
{
    private void NewWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_working || _aiBusy || _activeRecycleJobs > 0) return;
        var current = _panes[_activePane];
        PanelSession pane = current.GetSession();
        var session = new AppSession
        {
            LayoutVersion = 22, VisiblePanels = 1, ActivePane = 0,
            UiScalePercent = UiScaleService.Percent,
            Theme = _currentTheme,
            InspectorVisible = _inspectorOpen,
            DockPreviewVisible = _dockPreviewOpen,
            PinnedFolders = current.GetPinnedFolders().ToList(),
            Panels = [pane],
            WindowWidth = Math.Max(MinWidth, ActualWidth),
            WindowHeight = Math.Max(MinHeight, ActualHeight)
        };
        ((App)Application.Current).OpenAdditionalWindow(session, _controlPolicy);
    }
}
