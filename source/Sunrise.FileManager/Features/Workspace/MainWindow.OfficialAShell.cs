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

    // UI-only gating: folder planning stays available as a read-only operation,
    // while PDF rename requires an active local explorer and an idle job owner.
    private void EiassMainMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        bool local = _activePane >= 0 && _activePane < _visiblePanels &&
                     !IsCloudPaneActive && _panes[_activePane] is not null;
        bool busy = _working || _aiBusy || _activeRecycleJobs > 0;
        EiassRenameShellMenuItem.IsEnabled = local && !busy;
        EiassRenameShellMenuItem.ToolTip = !local
            ? "EIASS PDF 이름 변경은 로컬 파일 탐색창을 활성화한 후 이용할 수 있습니다."
            : busy ? "현재 작업이 종료된 후 EIASS PDF 이름 변경을 시작할 수 있습니다."
            : "활성 탐색창에서 PDF 1~1,000개를 선택한 후 이름을 검토합니다.";
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
        // Inform only on the EIASS local-only route; preserve the original
        // shared shell guard verbatim for all other commands and prior contracts.
        if (command == "Eiass" && IsCloudPaneActive)
        {
            JobStatus.Text = "EIASS PDF 이름 변경은 로컬 탐색창에서만 가능합니다. 먼저 로컬 분할창을 선택하세요.";
            return;
        }
        if (_working || _aiBusy || _activeRecycleJobs > 0 || IsCloudPaneActive) return;
        if (_activePane < 0 || _activePane >= _visiblePanels) return;
        _panes[_activePane]?.ExecuteOfficialAShellCommand(command);
    }
}
