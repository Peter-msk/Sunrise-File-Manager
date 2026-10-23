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
    private ExplorerSearchScope _searchScope;
    private string _groupField = "";
    private FileEntry? _directoryAfter, _directoryNext;
    private readonly Stack<FileEntry?> _directoryPageHistory = new();
    private int _directoryTotal, _searchPageOffset;
    private bool _directoryHasMore;
    private (string Folder, string Query, ExplorerSearchScope Scope, bool Hidden, int Generation)? _searchCacheKey;
    private ExplorerSearchResult? _searchCache;
    private bool UsesRecursiveResults => _searchScope != ExplorerSearchScope.Current && SearchBox.Text.Trim().Length > 0 && ActiveFolder is not null;
    private async void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (BeforeFileChange?.Invoke() == false) return;
        if (UsesRecursiveResults) { _searchPageOffset = Math.Max(0, _searchPageOffset - DirectoryScanner.PageSize); await ApplyFilterAndSortAsync(true); }
        else if (_directoryPageHistory.Count > 0) { _directoryAfter = _directoryPageHistory.Pop(); await LoadFolderAsync(false); }
    }
    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (BeforeFileChange?.Invoke() == false) return;
        if (UsesRecursiveResults) { _searchPageOffset += DirectoryScanner.PageSize; await ApplyFilterAndSortAsync(true); }
        else if (_directoryHasMore && _directoryNext is not null)
        { _directoryPageHistory.Push(_directoryAfter); _directoryAfter = _directoryNext; await LoadFolderAsync(false); }
    }
    private async Task ApplyFilterAndSortAsync(bool listingReady = false)
    {
        if (!listingReady) { _searchPageOffset = 0; _pagedSelection.Clear(); _selectAllCancellation?.Cancel(); }
        if (!listingReady && ActiveFolder is not null && !UsesRecursiveResults)
        { await LoadFolderAsync(); return; }
        _filterCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _filterCancellation = cancellation;
        int filterGeneration = ++_filterGeneration;
        int listingGeneration = _listingGeneration;
        IReadOnlyList<FileEntry> snapshot = _allEntries;
        string query = SearchBox.Text.Trim();
        string field = _sortField;
        bool descending = _sortDescending;
        string category = IsThisPc ? "All" : _categoryFilter;
        bool showHidden = _showHidden;
        try
        {
            string effectiveQuery = query;
            string? searchWarning = null;
            if (_searchScope != ExplorerSearchScope.Current && query.Length > 0 && ActiveFolder is string searchFolder)
            {
                PaneStatus.Text = "하위 폴더 검색 중… (Esc: 취소)";
                var key = (searchFolder, query, _searchScope, showHidden, listingGeneration);
                var result = _searchCacheKey == key && _searchCache is not null ? _searchCache
                    : await ExplorerSearchService.SearchAsync(searchFolder, query, _searchScope, showHidden, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (filterGeneration != _filterGeneration || listingGeneration != _listingGeneration) return;
                _searchCache = result; _searchCacheKey = key;
                snapshot = result.Entries;
                searchWarning = result.Warning;
                effectiveQuery = "";
            }
            List<FileEntry> visible = await Task.Run(
                () => FileListView.Apply(snapshot, effectiveQuery, field, descending, cancellation.Token, category, showHidden), cancellation.Token);
            if (cancellation.IsCancellationRequested || filterGeneration != _filterGeneration ||
                listingGeneration != _listingGeneration) return;
            int filteredTotal = visible.Count;
            if (UsesRecursiveResults)
            {
                _searchPageOffset = Math.Min(_searchPageOffset, Math.Max(0, (filteredTotal - 1) / DirectoryScanner.PageSize * DirectoryScanner.PageSize));
                visible = visible.Skip(_searchPageOffset).Take(DirectoryScanner.PageSize).ToList();
                PreviousPageButton.IsEnabled = _searchPageOffset > 0;
                NextPageButton.IsEnabled = _searchPageOffset + visible.Count < filteredTotal;
            }
            else
            {
                PreviousPageButton.IsEnabled = _directoryPageHistory.Count > 0;
                NextPageButton.IsEnabled = _directoryHasMore;
            }
            foreach (FileEntry item in visible) { item.ShowExtension = _showExtensions; item.ColorCoding = _colorCoding; item.RefreshThemeColors(ThemeManager.IsDark); }
            _reloadingSelection = true;
            try
            {
                EndMarquee();
                var view = new System.Windows.Data.ListCollectionView(visible);
                if (_groupField.Length > 0) view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(_groupField));
                FilesGrid.ItemsSource = view;
                foreach (FileEntry row in visible)
                    if (_pagedSelection.Contains(row.FullPath))
                    {
                        row.IsChecked = true;
                        FilesGrid.SelectedItems.Add(row);
                    }
                if (_horizontalResetListingGeneration == listingGeneration)
                {
                    // WPF can scroll to the previously focused cell during layout.
                    // Apply once after the newly bound list is measured, not on each
                    // user-driven horizontal scroll or when a later folder has loaded.
                    _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
                    {
                        if (listingGeneration != _listingGeneration ||
                            _horizontalResetListingGeneration != listingGeneration) return;
                        GetFilesScrollViewer()?.ScrollToHorizontalOffset(0);
                        _horizontalResetListingGeneration = -1;
                    }));
                }
                if (_restoreViewportPending)
                {
                    _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                    {
                        if (listingGeneration != _listingGeneration) return;
                        TryRestoreFileViewport();
                    }));
                }
                if (_restoreSelectionPath is string restore)
                {
                    RestoreSelection(restore);
                    _restoreSelectionPath = null;
                }
            }
            finally { _reloadingSelection = false; }
            PaneStatus.Text = _listingWarning is null ? $"{visible.Count:N0}개 항목" : "일부 생략됨";
            _footerBase = $"현재 페이지 {visible.Count:N0}개 / 전체 조건 일치 {(UsesRecursiveResults ? filteredTotal : ActiveFolder is not null ? _directoryTotal : snapshot.Count):N0}개"
                + " · Ctrl+A는 현재 페이지 · 전체 결과 선택은 편집 메뉴"
                + (string.IsNullOrEmpty(query) ? "" : " · 검색 필터")
                + (category == "All" ? "" : $" · {category} 필터")
                + (_listingWarning is null ? "" : " · " + _listingWarning)
                + (string.IsNullOrEmpty(searchWarning) ? "" : " · " + searchWarning);
            UpdateFooter();
            UpdateDetails();
            SelectionContextChanged?.Invoke(this);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (filterGeneration != _filterGeneration || listingGeneration != _listingGeneration) return;
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
        bool modeChanged = _compactMode != compact;
        EndMarquee();
        ++_pointerSelectionScrollVersion;
        _compactMode = compact;
        ApplyResponsiveLayout();
        QueueFileViewportLayoutRefresh(resetHorizontal: modeChanged);
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

    private void SetTreeVisible(bool visible) => ApplyNavigationState();

    private void SetDetailsVisible(bool visible)
    {
        DetailsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DetailsDivider.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DetailsColumn.Width = visible ? new GridLength(220) : new GridLength(0);
        DetailsDividerColumn.Width = visible ? new GridLength(1) : new GridLength(0);
        DetailsToggle.Content = "ⓘ 자세히";
    }

    private void ApplyViewMode(bool resetIconWidth = true)
    {
        if (FilesGrid is null || FilesGrid.Columns.Count < 5) return;
        ExplorerViewSpec spec = ExplorerCommandRouter.GetViewSpec(_viewMode);
        FilesGrid.RowHeight = spec.RowHeight;
        if (resetIconWidth) FilesGrid.Columns[0].Width = new DataGridLength(spec.IconColumnWidth);
        for (int index = 2; index < Math.Min(7, FilesGrid.Columns.Count); index++)
        {
            if (spec.ShowTileColumns && (index == 3 || index == 4))
                FilesGrid.Columns[index].Visibility = Visibility.Visible;
            else
                FilesGrid.Columns[index].Visibility = spec.ShowAllDetailColumns && !_hiddenDetailColumns.Contains(index) ? Visibility.Visible : Visibility.Collapsed;
        }
        RecycleDeletedColumn.Visibility = RecycleOriginColumn.Visibility =
            IsRecycleBin && spec.ShowAllDetailColumns ? Visibility.Visible : Visibility.Collapsed;
        CloudStatusColumn.Visibility = spec.ShowAllDetailColumns && !_hiddenDetailColumns.Contains(9) ? Visibility.Visible : Visibility.Collapsed;
        FilesGrid.Items.Refresh();
    }

    private void SyncColumnVisibilityMenu()
    {
        if (ColumnVisibilityMenu is null) return;
        foreach (MenuItem item in ColumnVisibilityMenu.Items.OfType<MenuItem>())
            if (int.TryParse(item.Tag as string, out int index)) item.IsChecked = !_hiddenDetailColumns.Contains(index);
    }

    private void DetailColumn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } || !int.TryParse(value, out int index) || index is not (>= 2 and <= 6 or 9)) return;
        if (!_hiddenDetailColumns.Add(index)) _hiddenDetailColumns.Remove(index);
        SyncColumnVisibilityMenu();
        ApplyViewMode(resetIconWidth: false);
        SettingsChanged?.Invoke();
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
        // Count includes selected items on other pages; do not mistake one visible row for the whole selection.
        int selectedCount = IsRecycleBin || IsThisPc ? FilesGrid.SelectedItems.Count : _pagedSelection.Count;
        if (selectedCount != 1 || FilesGrid.SelectedItems.Count != 1 || FilesGrid.SelectedItem is not FileEntry entry)
        {
            DetailsName.Text = selectedCount > 0 ? $"{selectedCount:N0}개 항목 선택" : "파일을 선택하세요";
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
        int selected = IsRecycleBin || IsThisPc ? FilesGrid.SelectedItems.Count : _pagedSelection.Count;
        if (selected == 0)
        {
            FooterText.Text = _footerBase;
            return;
        }
        // Off-page metadata is not cached. Do not report a misleading partial size.
        bool allOnPage = selected == FilesGrid.SelectedItems.Count;
        long selectedBytes = allOnPage ? FilesGrid.SelectedItems.OfType<FileEntry>().Where(x => !x.IsDirectory).Sum(x => x.SizeBytes) : 0;
        string sizePart = selectedBytes > 0 ? $" ({FileEntry.HumanSize(selectedBytes)})" : "";
        FooterText.Text = _footerBase + $" · 전체 페이지 합계 {selected:N0}개 선택{sizePart}";
    }
}
