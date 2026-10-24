using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>
/// Feature module for ExplorerPane: tab lifecycle, breadcrumb bar rendering (with drag-drop targets),
/// directory loading, path navigation, and real-time FileSystemWatcher synchronization.
/// </summary>
public partial class ExplorerPane
{
    public void SyncToFolderIfUntouched(string? folderPath)
    {
        if (HasUserNavigated || _hasInitialSessionPath || string.IsNullOrWhiteSpace(folderPath) || !WindowsKnownLocations.Exists(folderPath))
            return;
        if (_activeTab is not null && !string.Equals(_activeTab.CurrentPath, folderPath, StringComparison.OrdinalIgnoreCase))
        {
            _activeTab.Navigate(folderPath);
            _ = LoadFolderAsync();
        }
    }

    public void SyncToFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !WindowsKnownLocations.Exists(folderPath) || _activeTab is null)
            return;
        HasUserNavigated = true;
        if (_activeTab.Navigate(folderPath))
            _ = LoadFolderAsync();
    }

    public void SwapWorkingStateWith(ExplorerPane other)
    {
        if (other is null || ReferenceEquals(this, other)) return;

        var myTabs = _tabs.ToArray();
        var otherTabs = other._tabs.ToArray();
        var myActive = _activeTab;
        var otherActive = other._activeTab;
        bool myNav = HasUserNavigated;
        bool otherNav = other.HasUserNavigated;
        bool myInit = _hasInitialSessionPath;
        bool otherInit = other._hasInitialSessionPath;
        var myEntries = _allEntries;
        var otherEntries = other._allEntries;
        string mySort = _sortField;
        string otherSort = other._sortField;
        bool myDesc = _sortDescending;
        bool otherDesc = other._sortDescending;
        string mySearch = SearchBox.Text;
        string otherSearch = other.SearchBox.Text;

        _tabs.Clear();
        foreach (var t in otherTabs) _tabs.Add(t);
        _activeTab = otherActive ?? _tabs.FirstOrDefault();
        HasUserNavigated = otherNav;
        _hasInitialSessionPath = otherInit;
        _allEntries = otherEntries;
        _sortField = otherSort;
        _sortDescending = otherDesc;
        SearchBox.Text = otherSearch;

        other._tabs.Clear();
        foreach (var t in myTabs) other._tabs.Add(t);
        other._activeTab = myActive ?? other._tabs.FirstOrDefault();
        other.HasUserNavigated = myNav;
        other._hasInitialSessionPath = myInit;
        other._allEntries = myEntries;
        other._sortField = mySort;
        other._sortDescending = myDesc;
        other.SearchBox.Text = mySearch;

        RefreshSwappedPaneUi();
        other.RefreshSwappedPaneUi();
    }

    private void RefreshSwappedPaneUi()
    {
        if (_activeTab is null) return;
        foreach (ExplorerTab item in _tabs)
            item.IsSelected = ReferenceEquals(item, _activeTab);
        string path = _activeTab.CurrentPath;
        // A real location change closes previously visited branches; F5/watcher refresh does not.
        if (!string.Equals(_lastTreeLocation, path, StringComparison.OrdinalIgnoreCase))
        {
            _lastTreeLocation = path;
            NavigationTree.CollapseInactiveBranches(_skipNavigationTreeReveal ? null : path);
        }
        ConfigureRecycleBinView();
        SetAddressPath(path);
        UpdateBreadcrumbs(path);
        AttachFolderWatcher(path);
        BackButton.IsEnabled = _activeTab.CanGoBack;
        ForwardButton.IsEnabled = _activeTab.CanGoForward;
        UpButton.IsEnabled = ExplorerLocationService.Parent(path, WindowsKnownLocations.Resolve("Desktop")) is not null;
        _ = ApplyFilterAndSortAsync();
    }

    private void AttachFolderWatcher(string path)
    {
        DisposeFolderWatcher();
        try
        {
            if (!Directory.Exists(path)) return;
            var watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler onChanged = (_, _) =>
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    _watcherDebounceTimer.Stop();
                    _watcherDebounceTimer.Start();
                }));
            };
            RenamedEventHandler onRenamed = (_, _) =>
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    _watcherDebounceTimer.Stop();
                    _watcherDebounceTimer.Start();
                }));
            };
            watcher.Created += onChanged;
            watcher.Deleted += onChanged;
            watcher.Renamed += onRenamed;
            _folderWatcher = watcher;
        }
        catch
        {
        }
    }

    private void DisposeFolderWatcher()
    {
        if (_folderWatcher is null) return;
        try { _folderWatcher.EnableRaisingEvents = false; _folderWatcher.Dispose(); }
        catch { }
        finally { _folderWatcher = null; }
    }

    private static string DefaultFolder()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents) && Directory.Exists(documents)) return documents;
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(profile) ? profile : Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
    }

    private void SelectTab(ExplorerTab tab)
    {
        if (_activeTab is not null && !_initializing) _activeTab.SearchText = SearchBox.Text;
        foreach (ExplorerTab item in _tabs)
            item.IsSelected = ReferenceEquals(item, tab);
        _activeTab = tab;
        _skipNavigationTreeReveal = false;
        SetAddressPath(tab.CurrentPath);
        UpdateBreadcrumbs(tab.CurrentPath);
        SearchBox.Text = tab.SearchText;
        _filterTimer.Stop();
        _ = LoadFolderAsync();
    }

    private void UpdateBreadcrumbs(string fullPath)
    {
        if (BreadcrumbPanel is null) return;
        BreadcrumbPanel.Children.Clear();
        if (WindowsKnownLocations.IsThisPc(fullPath)) { AddBreadcrumbButton("바탕화면", WindowsKnownLocations.Resolve("Desktop")); AddBreadcrumbButton("내 PC", WindowsKnownLocations.ThisPc); return; }
        if (WindowsKnownLocations.IsRecycleBin(fullPath)) { AddBreadcrumbButton("휴지통", WindowsKnownLocations.RecycleBin); return; }
        if (string.IsNullOrWhiteSpace(fullPath)) return;

        try
        {
            string normalized = Path.GetFullPath(fullPath);
            string? root = Path.GetPathRoot(normalized);
            if (string.IsNullOrEmpty(root)) return;

            var known = WindowsKnownLocations.Match(normalized);
            if (known is { } location) root = location.Path;
            AddBreadcrumbButton(known?.Label ?? root.TrimEnd('\\') + "\\", root);
            string remainder = normalized[root.Length..];
            string accumulated = root;
            foreach (string part in remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                BreadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = "▸",
                    Foreground = (Brush)FindResource("TextMuted"),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 2, 0)
                });
                accumulated = Path.Combine(accumulated, part);
                AddBreadcrumbButton(part, accumulated);
            }
            BreadcrumbScroll?.ScrollToRightEnd();
        }
        catch { }
    }

    private void AddBreadcrumbButton(string label, string targetPath)
    {
        var btn = new Button
        {
            Content = label,
            ToolTip = $"{targetPath} (클릭: 이동 · 파일 드래그 앤 드롭: 이 폴더로 이동/복사)",
            Padding = new Thickness(6, 1, 6, 1),
            MinHeight = 20,
            FontSize = 11.5,
            AllowDrop = !WindowsKnownLocations.IsVirtual(targetPath)
        };
        btn.Click += (_, _) => TryNavigate(targetPath);
        btn.PreviewDragOver += (_, e) =>
        {
            if (!WindowsKnownLocations.IsVirtual(targetPath) && _actionsEnabled && e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 &&
                !string.Equals(targetPath, ActiveFolder, StringComparison.OrdinalIgnoreCase))
            {
                bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 || (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 || (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                TransferKind kind = ExplorerCommandRouter.ResolveDragTransferKind(files, targetPath, ctrl, shift);
                e.Effects = kind == TransferKind.Move ? DragDropEffects.Move : DragDropEffects.Copy;
                e.Handled = true;
            }
        };
        btn.PreviewDrop += (_, e) =>
        {
            if (!WindowsKnownLocations.IsVirtual(targetPath) && _actionsEnabled && e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 &&
                !string.Equals(targetPath, ActiveFolder, StringComparison.OrdinalIgnoreCase))
            {
                e.Handled = true;
                bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 || (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 || (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                TransferKind kind = ExplorerCommandRouter.ResolveDragTransferKind(files, targetPath, ctrl, shift);
                ExplorerPane sourcePane = (e.Data.GetDataPresent(typeof(InternalDragPayload)) &&
                    e.Data.GetData(typeof(InternalDragPayload)) is InternalDragPayload p) ? p.Source : this;
                DropRequested?.Invoke(new ExplorerDropRequest(sourcePane, this, files, targetPath, kind));
            }
        };
        BreadcrumbPanel.Children.Add(btn);
    }

    private async Task LoadFolderAsync(bool resetPage = true)
    {
        string selectionKey = string.Join("\0", _activeTab?.CurrentPath, SearchBox.Text.Trim(), _categoryFilter, _searchScope.ToString());
        bool preserveSelection = ReferenceEquals(_selectionListingTab, _activeTab) &&
            string.Equals(_selectionListingKey, selectionKey, StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<string> selectedBeforeRefresh = preserveSelection ? _pagedSelection.Snapshot() : [];
        _selectionListingKey = selectionKey;
        _selectionListingTab = _activeTab;
        if (resetPage)
        {
            _directoryAfter = null; _directoryPageHistory.Clear(); _searchPageOffset = 0;
            _pagedSelection.Clear(); _selectAllCancellation?.Cancel();
            if (preserveSelection)
            {
                _pagedSelection.ReplaceAll(selectedBeforeRefresh);
                if (_restoreSelectionPath is null && selectedBeforeRefresh.Count == 1)
                    _restoreSelectionPath = selectedBeforeRefresh[0];
            }
        }
        PreviousPageButton.IsEnabled = NextPageButton.IsEnabled = false;
        _searchCache = null; _searchCacheKey = null;
        if (_activeTab is null) return;
        EndMarquee();
        _listingCancellation?.Cancel();
        _filterCancellation?.Cancel();
        ResetThumbnails(_viewMode is "Thumbnails" or "Tiles");
        var cancellation = new CancellationTokenSource();
        _listingCancellation = cancellation;
        int generation = ++_listingGeneration;

        ExplorerTab tab = _activeTab;
        string path = tab.CurrentPath;
        if (_restoreSelectionPath is string pendingSelection && !WindowsKnownLocations.IsVirtual(path) &&
            !string.Equals(Path.GetDirectoryName(pendingSelection), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
            _restoreSelectionPath = null;
        bool newLocation = !string.Equals(_lastListingPath, path, StringComparison.OrdinalIgnoreCase);
        _lastListingPath = path;
        if (newLocation && !_initializing)
        {
            // New folder / Back / Forward / tab switch: always start the file list at
            // the left edge. An old saved viewport must not leak into a new location.
            _restoreViewportPending = false;
            _horizontalResetListingGeneration = generation;
            GetFilesScrollViewer()?.ScrollToHorizontalOffset(0);
        }
        else if (!newLocation && !_restoreViewportPending && GetFilesScrollViewer() is { } currentScroll)
        {
            // Refreshing the same folder should keep the user's scroll position.
            _savedVerticalOffset = currentScroll.VerticalOffset;
            _savedHorizontalOffset = currentScroll.HorizontalOffset;
            _restoreViewportPending = true;
        }
        // A real location change closes previously visited branches; F5/watcher refresh does not.
        if (!string.Equals(_lastTreeLocation, path, StringComparison.OrdinalIgnoreCase))
        {
            _lastTreeLocation = path;
            NavigationTree.CollapseInactiveBranches(_skipNavigationTreeReveal ? null : path);
        }
        ConfigureRecycleBinView();
        SetAddressPath(path);
        UpdateBreadcrumbs(path);
        AttachFolderWatcher(path);
        BackButton.IsEnabled = tab.CanGoBack;
        ForwardButton.IsEnabled = tab.CanGoForward;
        UpButton.IsEnabled = ExplorerLocationService.Parent(path, WindowsKnownLocations.Resolve("Desktop")) is not null;
        PaneStatus.Text = "조회 중…";
        FooterText.Text = path;
        _allEntries = [];
        _listingWarning = null;
        _reloadingSelection = true;
        try { FilesGrid.ItemsSource = null; }
        finally { _reloadingSelection = false; }
        UpdateDetails();

        try
        {
            ListingResult result = IsRecycleBin
                ? await RecycleBinService.ReadAsync(cancellation.Token)
                : IsThisPc ? await ExplorerLocationService.ReadThisPcAsync(cancellation.Token)
                : await DirectoryScanner.ReadPageAsync(path, _directoryAfter,
                    _searchScope == ExplorerSearchScope.Current ? SearchBox.Text.Trim() : "",
                    _sortField, _sortDescending, _categoryFilter, _showHidden, cancellation.Token, _restoreSelectionPath);
            if (generation != _listingGeneration || !ReferenceEquals(_activeTab, tab)) return;
            if (resetPage && preserveSelection && !result.HasMore && !result.IsRevealPage && !UsesRecursiveResults)
            {
                var present = result.Entries.Select(entry => entry.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _pagedSelection.ReplaceAll(_pagedSelection.Snapshot().Where(present.Contains));
            }
            foreach (FileEntry entry in result.Entries)
            {
                entry.ShowExtension = _showExtensions;
                entry.ColorCoding = _colorCoding;
            }
            _allEntries = result.Entries;
            if (result.IsRevealPage)
            {
                _directoryPageHistory.Push(null);
                _directoryAfter = result.RevealBefore;
            }
            _directoryNext = result.NextCursor; _directoryHasMore = result.HasMore;
            _directoryTotal = result.TotalEntries;
            _listingWarning = result.Warning;
            _driveSpaceSummary = result.DriveSummary ?? string.Empty;
            if (DriveSpaceText is not null) DriveSpaceText.Text = _driveSpaceSummary;
            _ = ApplyFilterAndSortAsync(listingReady: true);
            PaneStatus.Text = result.Warning is null ? $"{result.Entries.Count:N0}개" : "일부 항목 제한";
            if (newLocation && !_skipNavigationTreeReveal && !WindowsKnownLocations.IsVirtual(path) && NavigationTree.Visibility == Visibility.Visible)
                _ = NavigationTree.HighlightPathAsync(path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (generation != _listingGeneration) return;
            _allEntries = [];
            _reloadingSelection = true;
            try { FilesGrid.ItemsSource = Array.Empty<FileEntry>(); }
            finally { _reloadingSelection = false; }
            SelectionContextChanged?.Invoke(this);
            PaneStatus.Text = "목록 조회 실패";
            FooterText.Text = $"{path} | {exception.Message}";
            DetailsSummary.Text = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_listingCancellation, cancellation)) _listingCancellation = null;
            cancellation.Dispose();
        }
    }

    private void SetAddressPath(string path)
    {
        AddressBox.ToolTip = path;
        AddressBox.Text = AddressBox.IsKeyboardFocusWithin ? path : WindowsKnownLocations.DisplayPath(path);
    }

    // Editable address field remains separate from the on-demand navigation tree.
    private void AddressBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!AddressPopup.IsOpen) ShowAddressDropdown();
    }

    private void AddressDropButton_Click(object sender, RoutedEventArgs e)
    {
        if (AddressPopup.IsOpen) AddressPopup.IsOpen = false;
        else ShowAddressDropdown();
    }

    private void ShowAddressDropdown()
    {
        if (_activeTab is null) return;
        if (_addressNavigationTree is null)
        {
            var tree = new FolderTree { Focusable = true };
            tree.GetActiveFolder = () => ActiveFolder;
            tree.PathChosen += NavigateFromAddressTree;
            tree.QuickPathChosen += NavigateFromAddressTree;
            tree.PinnedFoldersChanged += paths =>
            {
                NavigationTree.SetPinnedFolders(paths);
                PinnedFoldersChanged?.Invoke(paths);
            };
            _addressNavigationTree = tree;
            AddressPopupContent.Content = tree;
        }
        _addressNavigationTree.SetPinnedFolders(NavigationTree.PinnedFolders);
        AddressPopupSurface.Width = Math.Clamp(AddressHost.ActualWidth, 285, 415);
        AddressPopup.IsOpen = true;
    }

    private void NavigateFromAddressTree(string path)
    {
        AddressPopup.IsOpen = false;
        TryNavigate(path, revealInTree: false);
    }

    private void TryNavigate(string requestedPath, bool revealInTree = true)
    {
        if (string.IsNullOrWhiteSpace(requestedPath)) return;
        requestedPath = WindowsKnownLocations.ExpandDisplayPath(requestedPath);
        try
        {
            string full = WindowsKnownLocations.IsVirtual(requestedPath) ? requestedPath
                : FileActionsService.ExpandAndResolveFolderPath(requestedPath, ActiveFolder);
            if (!WindowsKnownLocations.Exists(full))
            {
                MessageBox.Show($"폴더를 찾을 수 없습니다.\n{full}", "이동 실패", MessageBoxButton.OK, MessageBoxImage.Information);
                if (_activeTab is not null) SetAddressPath(_activeTab.CurrentPath);
                return;
            }
            if (_activeTab is null) return;
            HasUserNavigated = true;
            if (_activeTab.Navigate(full))
            {
                _skipNavigationTreeReveal = !revealInTree;
                if (!revealInTree) NavigationTree.CancelAutomaticReveal();
                _ = LoadFolderAsync();
            }
            else SetAddressPath(_activeTab.CurrentPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            MessageBox.Show(exception.Message, "폴더 경로 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseTab(ExplorerTab tab)
    {
        if (_tabs.Count <= 1 || !_tabs.Contains(tab)) return;
        bool active = ReferenceEquals(tab, _activeTab);
        int index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        SettingsChanged?.Invoke();
        if (active) SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
    }

    private void OpenTabAt(string folderPath)
    {
        if (_tabs.Count >= 12)
        {
            PaneStatus.Text = "패널당 최대 12개 탭";
            return;
        }
        HasUserNavigated = true;
        var newTab = new ExplorerTab(WindowsKnownLocations.Exists(folderPath) ? folderPath : DefaultFolder());
        _tabs.Add(newTab);
        SelectTab(newTab);
        SettingsChanged?.Invoke();
    }
}
