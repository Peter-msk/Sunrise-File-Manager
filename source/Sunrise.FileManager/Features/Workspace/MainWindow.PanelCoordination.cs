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
            _visiblePanels, _activePane, DestinationSelector.SelectedIndex);
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
        var host = new Grid();
        host.Children.Add(_panes[index]);
        return host;
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
        if (_visiblePanels <= 1 || _working || _aiBusy) return;
        int src = Math.Clamp(SourceSelector.SelectedIndex, 0, _visiblePanels - 1);
        int dst = Math.Clamp(DestinationSelector.SelectedIndex, 0, _visiblePanels - 1);
        if (src == dst) dst = (src + 1) % _visiblePanels;
        _panes[src].SwapWorkingStateWith(_panes[dst]);
        SyncInspector();
        SyncDockPreview();
        JobStatus.Text = $"탐색기 {src + 1} ↔ 탐색기 {dst + 1}의 작업 폴더 및 탭 상태를 맞바꾸었습니다.";
    }

    private void ExecuteSyncAllPanesToActive()
    {
        if (_visiblePanels <= 1 || _working || _aiBusy) return;
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
        if (_inspectorOpen && !Inspector.SelectFile(pane.SelectedFile)) return;
        _activePane = index;
        SyncDockPreview();
        AiPanel.UpdateSelectionSummary(pane.ActiveFolder, pane.SelectedPaths);
        ScheduleSessionSave();
    }
}
