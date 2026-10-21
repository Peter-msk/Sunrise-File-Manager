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
        ConfigureRecycleBinView();
        SetAddressPath(path);
        UpdateBreadcrumbs(path);
        AttachFolderWatcher(path);
        BackButton.IsEnabled = _activeTab.CanGoBack;
        ForwardButton.IsEnabled = _activeTab.CanGoForward;
        UpButton.IsEnabled = !IsRecycleBin && Directory.GetParent(path) is not null;
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
        foreach (ExplorerTab item in _tabs)
            item.IsSelected = ReferenceEquals(item, tab);
        _activeTab = tab;
        SetAddressPath(tab.CurrentPath);
        UpdateBreadcrumbs(tab.CurrentPath);
        SearchBox.Text = string.Empty;
        _filterTimer.Stop();
        _ = LoadFolderAsync();
    }

    private void UpdateBreadcrumbs(string fullPath)
    {
        if (BreadcrumbPanel is null) return;
        BreadcrumbPanel.Children.Clear();
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
            AllowDrop = !WindowsKnownLocations.IsRecycleBin(targetPath)
        };
        btn.Click += (_, _) => TryNavigate(targetPath);
        btn.PreviewDragOver += (_, e) =>
        {
            if (!WindowsKnownLocations.IsRecycleBin(targetPath) && _actionsEnabled && e.Data.GetDataPresent(DataFormats.FileDrop) &&
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
            if (!WindowsKnownLocations.IsRecycleBin(targetPath) && _actionsEnabled && e.Data.GetDataPresent(DataFormats.FileDrop) &&
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

    private async Task LoadFolderAsync()
    {
        if (_activeTab is null) return;
        _listingCancellation?.Cancel();
        ResetThumbnails(_viewMode is "Thumbnails" or "Tiles");
        var cancellation = new CancellationTokenSource();
        _listingCancellation = cancellation;
        int generation = ++_listingGeneration;

        ExplorerTab tab = _activeTab;
        string path = tab.CurrentPath;
        ConfigureRecycleBinView();
        SetAddressPath(path);
        UpdateBreadcrumbs(path);
        AttachFolderWatcher(path);
        BackButton.IsEnabled = tab.CanGoBack;
        ForwardButton.IsEnabled = tab.CanGoForward;
        UpButton.IsEnabled = !IsRecycleBin && Directory.GetParent(path) is not null;
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
                : await DirectoryScanner.ReadAsync(path, cancellation.Token, showHidden: true);
            if (generation != _listingGeneration || !ReferenceEquals(_activeTab, tab)) return;
            foreach (FileEntry entry in result.Entries)
            {
                entry.ShowExtension = _showExtensions;
                entry.ColorCoding = _colorCoding;
            }
            _allEntries = result.Entries;
            _listingWarning = result.Warning;
            _driveSpaceSummary = result.DriveSummary ?? string.Empty;
            if (DriveSpaceText is not null) DriveSpaceText.Text = _driveSpaceSummary;
            _ = ApplyFilterAndSortAsync();
            PaneStatus.Text = result.Warning is null ? $"{result.Entries.Count:N0}개" : "일부 항목 제한";
            if (!IsRecycleBin && NavigationTree.Visibility == Visibility.Visible)
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

    private void TryNavigate(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath)) return;
        requestedPath = WindowsKnownLocations.ExpandDisplayPath(requestedPath);
        try
        {
            string full = WindowsKnownLocations.IsRecycleBin(requestedPath) ? WindowsKnownLocations.RecycleBin
                : FileActionsService.ExpandAndResolveFolderPath(requestedPath, ActiveFolder);
            if (!WindowsKnownLocations.Exists(full))
            {
                MessageBox.Show($"폴더를 찾을 수 없습니다.\n{full}", "이동 실패", MessageBoxButton.OK, MessageBoxImage.Information);
                if (_activeTab is not null) SetAddressPath(_activeTab.CurrentPath);
                return;
            }
            if (_activeTab is null) return;
            HasUserNavigated = true;
            if (_activeTab.Navigate(full)) _ = LoadFolderAsync();
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
    }
}
