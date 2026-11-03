using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sunrise.FileManager.Controls;

namespace Sunrise.FileManager;

/// <summary>
/// Official A-layout workspace command surface. UI modes change only the command
/// shortcuts. They never replace/clear files, tabs, selections or memo state.
/// A command always targets the single active ExplorerPane.
/// </summary>
public partial class MainWindow
{
    private SmartSearchWindow? _smartSearchWindow;
    private CloudDriveWindow? _cloudDriveWindow;

    // R31: Header action modes were removed, but all file actions remain menu accessible.
    private void SmartSearch_Click(object sender, RoutedEventArgs e) => ShowSmartSearchWindow();
    private void MenuShellCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string command }) ExecuteShellCommand(command);
    }

    private void CloudDriveManage_Click(object sender, RoutedEventArgs e) => OpenCloudDrive(null);

    private void RailGoogleDrive_Click(object sender, RoutedEventArgs e) => OpenCloudDrive(Services.CloudDrive.CloudProvider.GoogleDrive);

    private void RailOneDrive_Click(object sender, RoutedEventArgs e) => OpenCloudDrive(Services.CloudDrive.CloudProvider.OneDrive);

    private void OpenCloudDrive(Services.CloudDrive.CloudProvider? provider)
    {
        if (_cloudDriveWindow is { IsVisible: true } existing)
        {
            if (provider is { } selected) existing.TrySelectProvider(selected);
            existing.Activate();
            return;
        }
        var window = new CloudDriveWindow { Owner = this };
        if (provider is { } initial) window.TrySelectProvider(initial);
        _cloudDriveWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_cloudDriveWindow, window)) _cloudDriveWindow = null; };
        window.Show();
    }

    private void ShowSmartSearchWindow()
    {
        if (_smartSearchWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            existing.FocusSearchInput();
            return;
        }
        string? folder = _activePane >= 0 && _activePane < _visiblePanels ? _panes[_activePane]?.ActiveFolder : null;
        var search = new SmartSearchWindow(folder, (path, isFolder) =>
        {
            if (_activePane < 0 || _activePane >= _visiblePanels || _panes[_activePane] is not { } pane) return;
            // Search results never bypass the current file's unsaved memo gate.
            if (_memoOpen && !MemoPanel.ConfirmLeavingMemo()) return;
            if (isFolder) pane.SyncToFolder(path);
            else _ = pane.RevealFileAsync(path);
            Activate();
        }) { Owner = this };
        _smartSearchWindow = search;
        search.Closed += (_, _) => { if (ReferenceEquals(_smartSearchWindow, search)) _smartSearchWindow = null; };
        search.Show();
    }

    private void ExecuteShellCommand(string command)
    {
        if (command == "Filter") { ShowSmartSearchWindow(); return; }
        if (_working || _aiBusy || _activeRecycleJobs > 0 || IsCloudPaneActive) return;
        if (_activePane < 0 || _activePane >= _visiblePanels) return;
        _panes[_activePane]?.ExecuteOfficialAShellCommand(command);
    }
}
