using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sunrise.FileManager.Controls;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager;

/// <summary>
/// Feature module for MainWindow: multi-pane host construction, predefined row/column blueprint rendering,
/// split-count working folder state preservation, Q-Dir pane swap/sync, and Onboard AI header badge updates.
/// </summary>
public partial class MainWindow
{
    private void PropagatePinnedFolders(ExplorerPane sourcePane, IReadOnlyList<string> updated)
    {
        if (_syncingPinned) return;
        _syncingPinned = true;
        try
        {
            foreach (var other in _panes)
            {
                if (!ReferenceEquals(other, sourcePane))
                    other?.SyncPinnedFolders(updated);
            }
        }
        finally { _syncingPinned = false; }
    }

    private string? ResolveOppositePaneFolder()
    {
        int targetIdx = WorkspaceSplitLayoutEngine.ResolveOppositePaneIndex(
            _visiblePanels, _activePane, -1);
        if (targetIdx < 0 || targetIdx >= _panes.Length) return null;
        return _panes[targetIdx]?.ActiveFolder;
    }

    private void UpdateOllamaHeaderButtonBadge(string badge)
    {
        if (AiConnectButton is null) return;
        AiConnectButton.Content = WorkspaceSplitLayoutEngine.ResolveOllamaHeaderButtonLabel(badge);
    }

    private Grid BuildHost(int index)
    {
        // Never rewrite the local ExplorerPane's working folder as a cloud URL.
        // Two genuinely separate surfaces share only a compact location switcher.
        var host = new Grid();
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.SetResourceReference(Panel.BackgroundProperty, "ThemeSoftAlt");
        var indexText = new TextBlock
        {
            Text = $"창 {index + 1}", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 8, 0), FontSize = 11
        };
        indexText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        header.Children.Add(indexText);
        var chooser = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 5, 2), MinHeight = 27,
            ToolTip = "이 분할창의 파일 위치: 내 PC·로컬·네트워크 / Google Drive / OneDrive"
        };
        chooser.Items.Add(new ComboBoxItem { Content = "내 PC · 네트워크", Tag = "Local" });
        chooser.Items.Add(new ComboBoxItem { Content = "Google Drive", Tag = "GoogleDrive" });
        chooser.Items.Add(new ComboBoxItem { Content = "OneDrive", Tag = "OneDrive" });
        _cloudLocationSelectors[index] = chooser;
        chooser.SelectionChanged += (_, _) => ChangePaneLocation(index);
        Grid.SetColumn(chooser, 1); header.Children.Add(chooser);
        host.Children.Add(header);
        var local = _panes[index];
        Grid.SetRow(local, 1); host.Children.Add(local);
        var remote = new CloudWorkspacePane(provider => OpenCloudDrive(provider)) { Visibility = Visibility.Collapsed };
        remote.GotKeyboardFocus += (_, _) => ActivatePane(local);
        remote.PreviewMouseDown += (_, _) => ActivatePane(local);
        remote.ClipboardPasteRequested += CloudPane_ClipboardPasteRequested;
        remote.DragCopyRequested += CloudPane_DragCopyRequested;
        _cloudSlots[index] = remote;
        Grid.SetRow(remote, 1); host.Children.Add(remote);
        chooser.SelectedIndex = 0;
        return host;
    }

    private void RestorePaneLocationSelector(int index, string previous)
    {
        if (_cloudLocationSelectors[index] is not { } selector) return;
        _switchingCloudPane = true;
        try { selector.SelectedIndex = previous switch { "GoogleDrive" => 1, "OneDrive" => 2, _ => 0 }; }
        finally { _switchingCloudPane = false; }
    }

    private void ChangePaneLocation(int index)
    {
        if (_switchingCloudPane || _cloudLocationSelectors[index]?.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        string previous = _paneLocationModes[index];
        if (mode == previous) return;
        if (_working || _aiBusy || _activeRecycleJobs > 0 || _crossPaneCopyBusy || _cloudSlots[index]?.IsBusy == true ||
            (_memoOpen && !MemoPanel.ConfirmLeavingMemo()))
        {
            RestorePaneLocationSelector(index, previous);
            return;
        }
        if (_cloudSlots[index] is not { } remote)
        {
            RestorePaneLocationSelector(index, previous);
            return;
        }
        // A modal unsaved-memo confirmation may pump dispatcher operations. An in-flight
        // cloud request can become busy after the initial guard; keep selector and actual
        // pane mode identical even when provider change is refused.
        if (mode != "Local" && !remote.SetProvider(mode == "GoogleDrive"
                ? Services.CloudDrive.CloudProvider.GoogleDrive : Services.CloudDrive.CloudProvider.OneDrive))
        {
            RestorePaneLocationSelector(index, previous);
            JobStatus.Text = $"탐색기 {index + 1}: 클라우드 작업 중 위치 전환을 취소했습니다.";
            return;
        }
        _paneLocationModes[index] = mode;
        _panes[index].Visibility = mode == "Local" ? Visibility.Visible : Visibility.Collapsed;
        remote.Visibility = mode == "Local" ? Visibility.Collapsed : Visibility.Visible;
        if (index < _visiblePanels)
        {
            _activePane = index;
            UpdateActivePaneIndicator();
            SyncMemo(); SyncDetailsPanel(); SyncDockPreview();
        }
        JobStatus.Text = mode == "Local" ? $"탐색기 {index + 1}: 내 PC 작업 폴더 복원" :
            $"탐색기 {index + 1}: {mode} 원격 폴더 · 로컬 파일 작업과 분리";
        // Remote sessions are deliberately ephemeral; persisted local explorer state remains intact.
    }

    private void AttachPaneToWorkspace(Grid pane, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        Grid.SetRow(pane, row);
        Grid.SetColumn(pane, column);
        Grid.SetRowSpan(pane, rowSpan);
        Grid.SetColumnSpan(pane, columnSpan);
        Workspace.Children.Add(pane);
    }

    private void AddSplitter(int row, int column, bool vertical, int span = 1)
    {
        var splitter = new GridSplitter
        {
            Style = (Style)FindResource(vertical ? "SunriseVerticalSplitter" : "SunriseHorizontalSplitter"),
            ResizeDirection = vertical ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ShowsPreview = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        splitter.DragCompleted += (_, _) => { CaptureWorkspaceRatios(); ScheduleSessionSave(); };
        Grid.SetRow(splitter, row);
        Grid.SetColumn(splitter, column);
        Grid.SetRowSpan(splitter, vertical ? span : 1);
        Grid.SetColumnSpan(splitter, vertical ? 1 : span);
        Workspace.Children.Add(splitter);
    }

    private void ApplyBlueprintToWorkspace(SplitGridBlueprint blueprint)
    {
        Workspace.Children.Clear();
        Workspace.ColumnDefinitions.Clear();
        Workspace.RowDefinitions.Clear();

        int totalCols = blueprint.StarColumns * 2 - 1;
        int totalRows = blueprint.StarRows * 2 - 1;

        for (int c = 0; c < totalCols; c++)
        {
            bool isSplitterCol = c % 2 == 1;
            Workspace.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = isSplitterCol ? new GridLength(WorkspaceSplitLayoutEngine.SplitterThickness) : new GridLength(1, GridUnitType.Star),
                MinWidth = isSplitterCol ? WorkspaceSplitLayoutEngine.SplitterThickness : WorkspaceSplitLayoutEngine.MinimumPaneWidth
            });
        }

        for (int r = 0; r < totalRows; r++)
        {
            bool isSplitterRow = r % 2 == 1;
            Workspace.RowDefinitions.Add(new RowDefinition
            {
                Height = isSplitterRow ? new GridLength(WorkspaceSplitLayoutEngine.SplitterThickness) : new GridLength(1, GridUnitType.Star),
                MinHeight = isSplitterRow ? WorkspaceSplitLayoutEngine.SplitterThickness : WorkspaceSplitLayoutEngine.MinimumPaneHeight
            });
        }

        RestoreWorkspaceRatios();
        foreach (PaneGridPlacement p in blueprint.Panes)
        {
            AttachPaneToWorkspace(_hosts[p.PaneIndex], p.Row, p.Column, p.RowSpan, p.ColumnSpan);
        }

        foreach (SplitterGridPlacement s in blueprint.Splitters)
        {
            AddSplitter(s.Row, s.Column, s.Vertical, s.Span);
        }
    }

    private void RefreshSplitPresetSelectorUi(int count, SplitLayoutPreset activePreset)
    {
        if (SplitPresetSelector is null || CycleSplitPresetButton is null) return;
        _updatingSplitSelector = true;
        try
        {
            var options = WorkspaceSplitLayoutEngine.GetPresetsForCount(count);
            SplitPresetSelector.Items.Clear();
            int selectedIdx = 0;
            for (int i = 0; i < options.Count; i++)
            {
                var opt = options[i];
                SplitPresetSelector.Items.Add(new ComboBoxItem
                {
                    Content = opt.ShortLabel,
                    Tag = opt.Preset,
                    ToolTip = opt.Description
                });
                if (opt.Preset == activePreset) selectedIdx = i;
            }
            SplitPresetSelector.SelectedIndex = selectedIdx;
            Visibility vis = count > 1 ? Visibility.Visible : Visibility.Collapsed;
            SplitPresetSelector.Visibility = vis;
            CycleSplitPresetButton.Visibility = vis;
        }
        finally
        {
            _updatingSplitSelector = false;
        }
    }

    private void PreserveAndSyncPaneFoldersOnSplitChange(int previousCount, int newCount)
    {
        if (!_layoutInitialized) return;
        string? activeFolder = _panes[_activePane]?.ActiveFolder;
        // Cloud view is not a local session state: never swap a hidden local pane on its behalf.
        if (_paneLocationModes[_activePane] != "Local") return;

        // 1. When shrinking split count (e.g. 2->1 or 4->2), if the user was actively working in a pane
        // that is about to be hidden, swap its working state into the surviving slot so the active folder stays on screen!
        var (shouldSwap, survivingSlot) = WorkspaceSplitLayoutEngine.ResolveShrinkPreservation(
            previousCount, newCount, _activePane, _panes[_activePane]?.HasUserNavigated == true);
        if (shouldSwap)
        {
            _panes[survivingSlot].SwapWorkingStateWith(_panes[_activePane]);
            _activePane = survivingSlot;
            return;
        }

        // 2. When expanding split count (e.g. 1->2, 2->3, 3->4), any newly revealed pane that has NOT yet
        // been navigated by the user adopts the current active working folder instead of resetting to default Documents.
        if (newCount > previousCount && !string.IsNullOrWhiteSpace(activeFolder))
        {
            for (int i = previousCount; i < newCount && i < _panes.Length; i++)
            {
                _panes[i]?.SyncToFolderIfUntouched(activeFolder);
            }
        }
    }

    private void ExecuteSwapSelectedPanes()
    {
        if (_visiblePanels <= 1 || _working || _aiBusy ||
            _paneLocationModes.Take(_visiblePanels).Any(mode => mode != "Local"))
        {
            JobStatus.Text = "클라우드 분할창 상태를 보존하기 위해 창 맞바꾸기는 로컬 탐색창에서만 지원합니다.";
            return;
        }
        // Swapping working state can change the file owned by the shared memo editor.
        // Abort the entire swap if the user cancels the unsaved-memo confirmation.
        if (_memoOpen && !MemoPanel.ConfirmLeavingMemo()) return;
        int src = _activePane;
        int dst = (src + 1) % _visiblePanels;
        _panes[src].SwapWorkingStateWith(_panes[dst]);
        UpdateActivePaneIndicator();
        SyncMemo();
        SyncDetailsPanel();
        SyncDockPreview();
        JobStatus.Text = $"탐색기 {src + 1} ↔ 탐색기 {dst + 1}의 작업 폴더 및 탭 상태를 맞바꾸었습니다.";
    }

    private void ExecuteSyncAllPanesToActive()
    {
        if (_visiblePanels <= 1 || _working || _aiBusy ||
            _paneLocationModes.Take(_visiblePanels).Any(mode => mode != "Local")) return;
        string? folder = _panes[_activePane]?.ActiveFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        for (int i = 0; i < _visiblePanels; i++)
        {
            if (i != _activePane) _panes[i].SyncToFolder(folder);
        }
        JobStatus.Text = $"표시 중인 {_visiblePanels}개 분할 창을 '{folder}' 경로로 동기화했습니다.";
    }

    private void ActivatePane(ExplorerPane pane)
    {
        int index = Array.IndexOf(_panes, pane);
        if (index < 0 || index >= _visiblePanels || _activePane == index) return;
        if (_memoOpen)
        {
            if (_paneLocationModes[index] == "Local")
            {
                if (!MemoPanel.SelectFile(pane.SelectedFile)) return;
            }
            else if (!MemoPanel.SelectFile(null)) return;
        }
        _activePane = index;
        UpdateActivePaneIndicator();
        SyncDetailsPanel();
        SyncDockPreview();
        if (!IsCloudPaneActive) AiPanel.UpdateSelectionSummary(pane.ActiveFolder, pane.SelectedPaths);
        ScheduleSessionSave();
    }
}
