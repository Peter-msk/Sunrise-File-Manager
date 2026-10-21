using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>
/// Feature module for ExplorerPane: asynchronous filtering & sorting, view mode layout branching,
/// responsive toolbar/sidebar layout, bounded row thumbnail loading, and status/detail updates.
/// </summary>
public partial class ExplorerPane
{
    private async Task ApplyFilterAndSortAsync()
    {
        _filterCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _filterCancellation = cancellation;
        int filterGeneration = ++_filterGeneration;
        int listingGeneration = _listingGeneration;
        IReadOnlyList<FileEntry> snapshot = _allEntries;
        string query = SearchBox.Text.Trim();
        string field = _sortField;
        bool descending = _sortDescending;
        string category = _categoryFilter;
        bool showHidden = _showHidden;
        try
        {
            List<FileEntry> visible = await Task.Run(
                () => FileListView.Apply(snapshot, query, field, descending, cancellation.Token, category, showHidden), cancellation.Token);
            if (cancellation.IsCancellationRequested || filterGeneration != _filterGeneration ||
                listingGeneration != _listingGeneration) return;
            foreach (FileEntry item in visible) { item.ShowExtension = _showExtensions; item.ColorCoding = _colorCoding; item.RefreshThemeColors(ThemeManager.IsDark); }
            _reloadingSelection = true;
            try
            {
                FilesGrid.ItemsSource = visible;
                if (_restoreSelectionPath is string restore)
                {
                    RestoreSelection(restore);
                    _restoreSelectionPath = null;
                }
            }
            finally { _reloadingSelection = false; }
            PaneStatus.Text = _listingWarning is null ? $"{visible.Count:N0}개 항목" : "일부 생략됨";
            _footerBase = $"{visible.Count:N0}개 표시 / {snapshot.Count:N0}개 조회"
                + (string.IsNullOrEmpty(query) ? "" : " · 검색 필터")
                + (category == "All" ? "" : $" · {category} 필터")
                + (_listingWarning is null ? "" : " · " + _listingWarning);
            UpdateFooter();
            UpdateDetails();
            SelectionContextChanged?.Invoke(this);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PaneStatus.Text = "표시 오류";
            FooterText.Text = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_filterCancellation, cancellation)) _filterCancellation = null;
            cancellation.Dispose();
        }
    }

    public void SetCompactMode(bool compact)
    {
        _compactMode = compact;
        ApplyResponsiveLayout();
    }

    private void ResetThumbnails(bool enabled)
    {
        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation?.Dispose();
        _thumbnailCancellation = enabled ? new CancellationTokenSource() : null;
        ++_thumbnailGeneration;
        _retainedThumbnails.Clear();
    }

    private void ApplyResponsiveSubPanels(ExplorerLayout layout)
    {
        SetTreeVisible(layout.Tree);
        SetDetailsVisible(false);
        // R17: restored compact-width layout from audited Drive R12.
        // Keep the breadcrumb itself visible; only the crowded quick toggles hide.
        if (DisplayOptionsPanel is not null)
            DisplayOptionsPanel.Visibility = (_compactMode || ActualWidth < 580) ? Visibility.Collapsed : Visibility.Visible;
        AddressRow.Height = new GridLength(layout.SearchBelow ? 78 : (_compactMode ? 38 : 42));
        SearchColumn.Width = new GridLength(layout.SearchBelow ? 0 : (_compactMode ? 165 : 230));
        Grid.SetColumn(SearchHost, layout.SearchBelow ? 0 : 6);
        Grid.SetColumnSpan(SearchHost, layout.SearchBelow ? 7 : 1);
        SearchHost.Margin = layout.SearchBelow ? new Thickness(0, 4, 0, 0) : new Thickness(8, 0, 0, 0);
    }

    private void SetTreeVisible(bool visible)
    {
        NavigationTree.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TreeDivider.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TreeColumn.Width = visible ? new GridLength(_compactMode ? 150 : 215) : new GridLength(0);
        TreeDividerColumn.Width = visible ? new GridLength(6) : new GridLength(0);
        TreeToggle.Content = visible ? "탐색 창 닫기" : "탐색 창";
        if (visible && !IsRecycleBin && _activeTab is not null) _ = NavigationTree.HighlightPathAsync(_activeTab.CurrentPath);
    }

    private void SetDetailsVisible(bool visible)
    {
        DetailsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DetailsDivider.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DetailsColumn.Width = visible ? new GridLength(220) : new GridLength(0);
        DetailsDividerColumn.Width = visible ? new GridLength(1) : new GridLength(0);
        DetailsToggle.Content = "ⓘ 자세히";
    }

    private void ApplyViewMode()
    {
        if (FilesGrid is null || FilesGrid.Columns.Count < 5) return;
        ExplorerViewSpec spec = ExplorerCommandRouter.GetViewSpec(_viewMode);
        FilesGrid.RowHeight = spec.RowHeight;
        FilesGrid.Columns[0].Width = new DataGridLength(spec.IconColumnWidth);
        for (int index = 2; index < Math.Min(7, FilesGrid.Columns.Count); index++)
        {
            if (spec.ShowTileColumns && (index == 3 || index == 4))
                FilesGrid.Columns[index].Visibility = Visibility.Visible;
            else
                FilesGrid.Columns[index].Visibility = spec.ShowAllDetailColumns ? Visibility.Visible : Visibility.Collapsed;
        }
        RecycleDeletedColumn.Visibility = RecycleOriginColumn.Visibility =
            IsRecycleBin && spec.ShowAllDetailColumns ? Visibility.Visible : Visibility.Collapsed;
        FilesGrid.Items.Refresh();
    }

    private async Task LoadRowThumbnailAsync(DataGridRowEventArgs e)
    {
        if (_viewMode is not ("Thumbnails" or "Tiles") || e.Row.Item is not FileEntry entry ||
            entry.IsVirtual || entry.IsDirectory || entry.ThumbnailLoading || entry.ThumbnailSource is not null ||
            !ImageThumbnailService.CanPreview(entry.FullPath))
            return;
        CancellationTokenSource? cancellation = _thumbnailCancellation;
        if (cancellation is null) return;
        int generation = _thumbnailGeneration;
        entry.ThumbnailLoading = true;
        try
        {
            var thumb = await ImageThumbnailService.LoadAsync(entry.FullPath, cancellation.Token);
            if (thumb is not null && !cancellation.IsCancellationRequested &&
                generation == _thumbnailGeneration && _viewMode is "Thumbnails" or "Tiles")
            {
                entry.ThumbnailSource = thumb;
                _retainedThumbnails.Remember(entry);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"Thumbnail skipped for {entry.Name}: {ex.GetType().Name}: {ex.Message}");
        }
        finally { entry.ThumbnailLoading = false; }
    }

    private void UpdateDetails()
    {
        if (DetailsName is null || DetailsMeta is null || DetailsSummary is null) return;
        if (FilesGrid.SelectedItems.Count != 1 || FilesGrid.SelectedItem is not FileEntry entry)
        {
            DetailsName.Text = FilesGrid.SelectedItems.Count > 1 ? $"{FilesGrid.SelectedItems.Count:N0}개 항목 선택" : "파일을 선택하세요";
            DetailsMeta.Text = _activeTab?.CurrentPath ?? string.Empty;
            DetailsSummary.Text = "선택한 파일의 유형, 크기 및 수정한 날짜를 확인합니다.";
            return;
        }
        DetailsName.Text = entry.Name;
        DetailsMeta.Text = $"{entry.TypeName}\n\n{(entry.IsDirectory ? "폴더" : entry.SizeDisplay)}\n\n{entry.ModifiedDisplay}";
        DetailsSummary.Text = entry.IsVirtual ? "휴지통의 항목입니다. 우클릭 메뉴에서 원래 위치로 복원할 수 있습니다." : entry.IsDirectory ? "폴더를 두 번 클릭하면 이동합니다." :
            "열기: Windows 연결 앱에서 원본을 엽니다. 미리보기: 읽기 전용으로 지원되는 내용만 표시합니다.";
    }

    private void UpdateFooter()
    {
        int selected = FilesGrid.SelectedItems.Count;
        if (selected == 0)
        {
            FooterText.Text = _footerBase;
            return;
        }
        long selectedBytes = FilesGrid.SelectedItems.OfType<FileEntry>().Where(x => !x.IsDirectory).Sum(x => x.SizeBytes);
        string sizePart = selectedBytes > 0 ? $" ({FileEntry.HumanSize(selectedBytes)})" : "";
        FooterText.Text = _footerBase + $" · {selected:N0}개 선택{sizePart}";
    }
}
