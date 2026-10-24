using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>
/// Core ExplorerPane UserControl: state initialization, public surface, and XAML event bindings.
/// Feature logic is modularized into:
/// - Features/Explorer/ExplorerCommandRouter.cs
/// - Features/Explorer/ExplorerPane.NavigationAndWatcher.cs
/// - Features/Explorer/ExplorerPane.ViewAndFiltering.cs
/// - Features/Explorer/ExplorerPane.FileOperations.cs
/// - Features/Explorer/ExplorerPane.ClipboardAndDragDrop.cs
/// </summary>
public partial class ExplorerPane : UserControl, INotifyPropertyChanged
{
    private readonly ObservableCollection<ExplorerTab> _tabs = [];
    private readonly DispatcherTimer _filterTimer;
    private readonly DispatcherTimer _watcherDebounceTimer;
    private readonly ExplorerUndoService _undoService = new();
    private FileSystemWatcher? _folderWatcher;
    private bool _treePreference = true;
    private bool _detailsPreference;
    private bool _compactMode;
    private string _viewMode = "Details";
    private string _categoryFilter = "All";
    private bool _showHidden;
    private bool _showExtensions;
    private bool _showCheckboxes;
    private bool _colorCoding;
    private bool _hasInitialSessionPath;
    public bool HasUserNavigated { get; private set; }
    private ExplorerTab? _activeTab;
    private CancellationTokenSource? _listingCancellation;
    private CancellationTokenSource? _filterCancellation;
    private int _filterGeneration;
    private bool _recycling;
    private IReadOnlyList<FileEntry> _allEntries = [];
    private string _sortField = "Name";
    private bool _sortDescending;
    private string? _listingWarning;
    private string _footerBase = string.Empty;
    private string _driveSpaceSummary = string.Empty;
    private int _listingGeneration;
    private Point? _dragOrigin;
    private string? _dragSourcePath;
    private string[]? _dragCandidatePaths;
    private bool _actionsEnabled = true;
    private CancellationTokenSource? _thumbnailCancellation;
    private int _thumbnailGeneration;
    private readonly ThumbnailRetention _retainedThumbnails = new();
    private bool _reloadingSelection;
    private readonly PagedSelectionSet _pagedSelection = new();
    private CancellationTokenSource? _selectAllCancellation;
    private readonly HashSet<int> _hiddenDetailColumns = [];
    private FileEntry? _renamingEntry;
    private bool _renameShowsExtension;
    private string? _restoreSelectionPath;
    private string? _selectionListingKey;
    private ExplorerTab? _selectionListingTab;
    private bool _initializing;
    private double _savedVerticalOffset, _savedHorizontalOffset;
    private bool _restoreViewportPending;
    private string? _lastListingPath;
    private int _horizontalResetListingGeneration = -1;
    // A pointer selection may cause WPF to make its current cell horizontally
    // visible. Preserve the user's existing horizontal scroll for that gesture.
    // Keyboard navigation and explicit Shift+wheel scrolling remain native.
    private int _pointerSelectionScrollVersion;
    // The address popup reuses the existing FolderTree UI and is created on demand.
    private FolderTree? _addressNavigationTree;
    // Quick links open folder contents without revealing physical C:\Users ancestors.
    private bool _skipNavigationTreeReveal;
    private double _navigationWidth = NavigationPanelPolicy.DefaultWidth;
    private string? _lastTreeLocation;
    // A marquee takes the first click; track a bounded second blank click explicitly.
    private long _lastBlankClickTick;
    private Point _lastBlankClickPoint;
    private string? _lastBlankClickFolder;

    public event PropertyChangedEventHandler? PropertyChanged;
    public Visibility CheckboxesVisibility => _showCheckboxes ? Visibility.Visible : Visibility.Collapsed;

    // Respect the user-configured Windows double-click timing and hit rectangle.
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int metric);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType,
        string szObjectName, string? szPropertyPage);

    public event Action<ExplorerDropRequest>? DropRequested;
    public event Action? FileActionCompleted;
    public event Action? SettingsChanged;
    public event Action<ExplorerPane>? SelectionContextChanged;
    public event Action<ExplorerPane>? PreviewRequested;
    public event Action<ExplorerPane>? InspectorRequested;
    public event Action<ExplorerPane>? AiRequested;
    public event Action<IReadOnlyList<string>>? PinnedFoldersChanged;
    public event Action<bool>? BlockingFileActionChanged;
    public event Action<ClipboardPasteRequest>? PasteRequested;
    public Func<bool>? BeforeFileChange { get; set; }

    public void SetFileActionsEnabled(bool enabled)
    {
        _actionsEnabled = enabled;
        NewFolderButton.IsEnabled = enabled;
        NewFileButton.IsEnabled = enabled;
        RenameButton.IsEnabled = enabled;
        RecycleButton.IsEnabled = enabled;
        DuplicateButton.IsEnabled = enabled;
        ZipCompressButton.IsEnabled = ZipExtractButton.IsEnabled = ExportListButton.IsEnabled = enabled;
        CutSelectionButton.IsEnabled = PasteSelectionButton.IsEnabled = enabled;
        UpdateUndoButtonState();
    }

    private void UpdateUndoButtonState()
    {
        if (UndoButton is not null)
        {
            UndoButton.IsEnabled = _actionsEnabled && _undoService.CanUndo;
            UndoButton.ToolTip = _undoService.NextUndoDescription is string desc
                ? $"실행 취소 (Ctrl+Z): {desc}"
                : "최근 이름 변경·이동·생성·복제 실행 취소 (Ctrl+Z)";
        }
        if (RedoButton is not null)
        {
            RedoButton.IsEnabled = _actionsEnabled && _undoService.CanRedo;
            RedoButton.ToolTip = _undoService.NextRedoDescription is string rdesc
                ? $"다시 실행 (Ctrl+Y): {rdesc}"
                : "실행 취소한 작업 다시 실행 (Ctrl+Y)";
        }
    }

    public ExplorerPane()
    {
        InitializeComponent();
        DataContext = this;
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        TabStrip.ItemsSource = _tabs;
        FilesGrid.PreviewMouseLeftButtonUp += FinishMarquee;
        FilesGrid.LostMouseCapture += (_, _) => EndMarquee();
        FilesGrid.ColumnReordered += (_, _) => SettingsChanged?.Invoke();
        FilesGrid.Loaded += (_, _) => QueueFileViewportLayoutRefresh();
        FilesGrid.Unloaded += (_, _) => { EndMarquee(); ++_pointerSelectionScrollVersion; };
        FilesGrid.SizeChanged += (_, _) => QueueFileViewportLayoutRefresh();
        NavigationTree.GetActiveFolder = () => ActiveFolder;
        AddressBox.GotKeyboardFocus += (_, _) => { if (_activeTab is not null) { AddressBox.Text = _activeTab.CurrentPath; AddressBox.SelectAll(); } };
        AddressBox.LostKeyboardFocus += (_, _) =>
        {
            if (_activeTab is not null && AddressBox.Text == _activeTab.CurrentPath) SetAddressPath(_activeTab.CurrentPath);
        };
        NavigationTree.PinnedFoldersChanged += list => PinnedFoldersChanged?.Invoke(list);
        NavigationTree.FolderDropRequested += (paths, targetFolder, kind) =>
        {
            if (!_actionsEnabled) return;
            DropRequested?.Invoke(new ExplorerDropRequest(this, this, paths, targetFolder, kind));
        };
        NavigationTree.PathChosen += path =>
        {
            if (_activeTab is null || string.Equals(_activeTab.CurrentPath, path, StringComparison.OrdinalIgnoreCase)) return;
            TryNavigate(path);
        };
        NavigationTree.QuickPathChosen += path => TryNavigate(path, revealInTree: false);
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            _ = ApplyFilterAndSortAsync();
        };
        _watcherDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _watcherDebounceTimer.Tick += (_, _) =>
        {
            _watcherDebounceTimer.Stop();
            if (_actionsEnabled && _renamingEntry is null && !_recycling && _listingCancellation is null)
                _ = LoadFolderAsync();
        };
    }

    public void Initialize(int panelNumber, PanelSession? state, IEnumerable<string>? pinnedFolders = null)
    {
        _initializing = true;
        try
        {
            PaneTitle.Text = $"탐색기 {panelNumber}";
            _treePreference = state?.TreeVisible ?? true;
            _treeAutoHide = state?.TreeAutoHide ?? false;
            _navigationWidth = NavigationPanelPolicy.NormalizeWidth(state?.TreeWidth ?? NavigationPanelPolicy.DefaultWidth);
            _detailsPreference = state?.DetailsVisible ?? false;
            _showHidden = state?.ShowHidden ?? false;
            _showExtensions = state?.ShowExtensions ?? false;
            _showCheckboxes = state?.ShowCheckboxes ?? false;
            _colorCoding = state?.ColorCoding ?? false;
            _hiddenDetailColumns.Clear();
            foreach (int index in state?.HiddenDetailColumns ?? [])
                if (index is >= 2 and <= 6 or 9) _hiddenDetailColumns.Add(index);
            _hasInitialSessionPath = state?.TabPaths is { Count: > 0 };
            HasUserNavigated = false;
            if (HiddenToggle is not null) HiddenToggle.IsChecked = _showHidden;
            if (ExtensionsToggle is not null) ExtensionsToggle.IsChecked = _showExtensions;
            if (CheckboxesToggle is not null) CheckboxesToggle.IsChecked = _showCheckboxes;
            if (ColorFilterToggle is not null) ColorFilterToggle.IsChecked = _colorCoding;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckboxesVisibility)));

            NavigationTree.SetPinnedFolders(pinnedFolders);
            SetTreeVisible(_treePreference);
            SetDetailsVisible(false);
            _viewMode = ExplorerCommandRouter.NormalizeViewMode(state?.ViewMode);
            ViewSelector.SelectedIndex = ExplorerCommandRouter.ViewModeToSelectorIndex(_viewMode);
            _sortField = state?.SortField is { Length: > 0 } sf ? sf : "Name";
            _sortDescending = state?.SortDescending ?? false;
            _categoryFilter = state?.CategoryFilter is { Length: > 0 } category ? category : "All";
            int categoryIndex = CategorySelector.Items.OfType<ComboBoxItem>().ToList()
                .FindIndex(item => string.Equals(item.Tag as string, _categoryFilter, StringComparison.Ordinal));
            CategorySelector.SelectedIndex = Math.Max(0, categoryIndex);
            if (categoryIndex < 0) _categoryFilter = "All";
            if (Enum.TryParse<ExplorerSearchScope>(state?.SearchScope, out var searchScope) && Enum.IsDefined(searchScope)) _searchScope = searchScope;
            _groupField = state?.GroupField ?? "";
            SearchModeLabel.Text = _searchScope switch { ExplorerSearchScope.Descendants => "하위 폴더 검색", ExplorerSearchScope.Contents => "이름·본문 검색", ExplorerSearchScope.Index => "인덱스 검색", _ => "현재 폴더에서 검색" };
            _filterTimer.Interval = TimeSpan.FromMilliseconds(_searchScope == ExplorerSearchScope.Current ? 90 : 600);
            SearchBox.Text = state?.SearchText ?? string.Empty;
            ApplyViewMode();
            SyncColumnVisibilityMenu();
            if (state?.ColumnWidths is { Count: > 0 } savedWidths)
            {
                for (int col = 0; col < Math.Min(savedWidths.Count, FilesGrid.Columns.Count); col++)
                {
                    double width = savedWidths[col];
                    if (width is >= 40 and <= 4000)
                        FilesGrid.Columns[col].Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);
                }
            }
            if (state?.ColumnSettings is { Count: > 0 } columns)
                for (int i = 0; i < Math.Min(columns.Count, FilesGrid.Columns.Count); i++)
                    if (Enum.TryParse<DataGridLengthUnitType>(columns[i].Unit, out var unit)) FilesGrid.Columns[i].Width = new DataGridLength(columns[i].Value, unit);
            if (state?.ColumnOrder is { Count: > 0 } order && order.Count <= FilesGrid.Columns.Count && order.All(x => x < FilesGrid.Columns.Count))
                for (int i = 0; i < order.Count; i++) FilesGrid.Columns[i].DisplayIndex = order[i];
            foreach (var column in FilesGrid.Columns)
                if (column.SortMemberPath == _sortField) column.SortDirection = _sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            _savedVerticalOffset = state?.FileVerticalOffset ?? 0;
            _savedHorizontalOffset = state?.FileHorizontalOffset ?? 0;
            _restoreViewportPending = true;
            if (state is not null)
            {
                string? activePath = state.TabPaths.ElementAtOrDefault(Math.Clamp(state.SelectedTabIndex, 0, Math.Max(0, state.TabPaths.Count - 1)));
                _ = NavigationTree.RestoreExpandedPathsAsync(state.ExpandedTreePaths, state.TreeScrollOffset, activePath);
            }
            string fallback = WindowsKnownLocations.ThisPc;
            if (state?.TabPaths is { Count: > 0 } saved)
            {
                foreach (string? path in saved.Take(12))
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    _tabs.Add(new ExplorerTab(path) { SearchText = state.TabSearchTexts.ElementAtOrDefault(_tabs.Count) ?? "" });
                }
            }
            if (_tabs.Count == 0) _tabs.Add(new ExplorerTab(fallback));
            int selected = Math.Clamp(state?.SelectedTabIndex ?? 0, 0, _tabs.Count - 1);
            if (_tabs[selected].SearchText.Length == 0) _tabs[selected].SearchText = state?.SearchText ?? "";
            SelectTab(_tabs[selected]);
        }
        finally
        {
            _initializing = false;
        }
    }

    public void RefreshThemeColors()
    {
        foreach (var item in _allEntries) item.RefreshThemeColors(ThemeManager.IsDark);
    }

    public IReadOnlyList<string> GetPinnedFolders() => NavigationTree.PinnedFolders;
    public void SyncPinnedFolders(IEnumerable<string> folders) => NavigationTree.SetPinnedFolders(folders);

    public bool IsRecycleBin => WindowsKnownLocations.IsRecycleBin(_activeTab?.CurrentPath);
    public bool IsThisPc => WindowsKnownLocations.IsThisPc(_activeTab?.CurrentPath);
    public string? CurrentLocation => _activeTab?.CurrentPath;
    public string? ActiveFolder => WindowsKnownLocations.IsVirtual(CurrentLocation) ? null : _activeTab?.CurrentPath;
    public FileEntry? SelectedFile => !WindowsKnownLocations.IsVirtual(CurrentLocation) && SelectedPaths.Count == 1 && FilesGrid.SelectedItems.Count == 1 ? FilesGrid.SelectedItem as FileEntry : null;

    public bool RestoreSelection(string path)
    {
        var match = FilesGrid.Items.OfType<FileEntry>()
            .FirstOrDefault(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        _pagedSelection.Clear();
        FilesGrid.SelectedItems.Clear();
        FilesGrid.SelectedItem = match;
        _pagedSelection.Add(match.FullPath);
        FilesGrid.ScrollIntoView(match);
        return true;
    }

    public async Task RevealFileAsync(string filePath)
    {
        string full = Path.GetFullPath(filePath);
        string? folder = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || _activeTab is null) return;
        if (BeforeFileChange?.Invoke() == false) return;
        HasUserNavigated = true;
        _restoreSelectionPath = full;
        _activeTab.Navigate(folder);
        await LoadFolderAsync();
    }

    public IReadOnlyList<string> SelectedPaths
    {
        get
        {
            if (IsRecycleBin || IsThisPc) return Array.Empty<string>();
            if (_pagedSelection.Count > 0) return _pagedSelection.Snapshot();
            return FilesGrid.SelectedItems.OfType<FileEntry>().Select(item => item.FullPath).ToArray();
        }
    }

    public void RefreshCurrent() => _ = LoadFolderAsync();
    public void FocusFiles() => FilesGrid.Focus();

    public PanelSession GetSession() => new()
    {
        TabPaths = _tabs.Select(tab => tab.CurrentPath).ToList(),
        SelectedTabIndex = _activeTab is null ? 0 : Math.Max(0, _tabs.IndexOf(_activeTab)),
        TreeVisible = _treePreference,
        TreeAutoHide = _treeAutoHide,
        TreeWidth = NavigationPanelPolicy.NormalizeWidth(_navigationWidth),
        TabSearchTexts = _tabs.Select(tab => ReferenceEquals(tab, _activeTab) ? SearchBox.Text : tab.SearchText).ToList(),
        DetailsVisible = _detailsPreference,
        ViewMode = _viewMode,
        PreviewVisible = false,
        ShowHidden = _showHidden,
        ShowExtensions = _showExtensions,
        ShowCheckboxes = _showCheckboxes,
        ColorCoding = _colorCoding,
        SortField = _sortField,
        SortDescending = _sortDescending,
        CategoryFilter = _categoryFilter,
        SearchScope = _searchScope.ToString(),
        GroupField = _groupField,
        SearchText = SearchBox.Text,
        ExpandedTreePaths = NavigationTree.GetExpandedPaths(),
        TreeScrollOffset = SessionStore.CaptureScrollOffset(NavigationTree.ScrollOffset),
        FileVerticalOffset = SessionStore.CaptureScrollOffset(_restoreViewportPending ? _savedVerticalOffset : GetFilesScrollViewer()?.VerticalOffset ?? 0),
        FileHorizontalOffset = SessionStore.CaptureScrollOffset(_restoreViewportPending ? _savedHorizontalOffset : GetFilesScrollViewer()?.HorizontalOffset ?? 0),
        ColumnSettings = FilesGrid.Columns.Select(column => SessionStore.CaptureColumnWidth(column.Width.UnitType.ToString(), column.Width.Value)).ToList(),
        ColumnOrder = SessionStore.CaptureDisplayOrder(FilesGrid.Columns.Select(column => column.DisplayIndex)),
        HiddenDetailColumns = _hiddenDetailColumns.Order().ToList(),
        ColumnWidths = FilesGrid.Columns.Select(column =>
            double.IsFinite(column.ActualWidth) && column.ActualWidth >= 40
                ? Math.Round(Math.Min(column.ActualWidth, 4000)) : 0d).ToList()
    };

    public void CancelPendingOperations()
    {
        _windowsShare.Dispose();
        _navigationHideTimer?.Stop();
        EndMarquee();
        DisposeFolderWatcher();
        _watcherDebounceTimer.Stop();
        _listingCancellation?.Cancel();
        _filterCancellation?.Cancel();
        _filterTimer.Stop();
        _selectAllCancellation?.Cancel();
        ResetThumbnails(false);
        NavigationTree.CancelPendingOperations();
    }

    private void ApplyResponsiveLayout()
    {
        if (AddressBox is null || ActualWidth <= 0) return;
        var layout = UiLayoutPolicy.Explorer(ActualWidth, _compactMode, _treePreference, _detailsPreference);
        Grid.SetRow(SearchHost, layout.SearchBelow ? 1 : 0);
        ApplyResponsiveSubPanels(layout);
        ApplyCompactCommandBar();
    }

    // R12: a narrow pane must not lose its list area to toolbar overflow.
    // Commands hidden in the compact toolbar remain available through each pane's File/Edit/View menus.
    private void ApplyCompactCommandBar()
    {
        double width = ActualWidth;
        if (width <= 0) return;
        bool compact = width < 760;
        bool veryCompact = width < 460;
        NewFileButton.Visibility = veryCompact ? Visibility.Collapsed : Visibility.Visible;
        CutSelectionButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RenameButton.Visibility = veryCompact ? Visibility.Collapsed : Visibility.Visible;
        CategorySelector.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ViewSelector.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ViewModeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } && int.TryParse(value, out int selected) && selected is >= 0 and <= 4)
            ViewSelector.SelectedIndex = selected;
    }

    private void CategoryMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } && int.TryParse(value, out int selected) && selected is >= 0 and <= 7)
            CategorySelector.SelectedIndex = selected;
    }

    // --- XAML Event Handlers ---
    private void SelectTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ExplorerTab tab }) SelectTab(tab);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ExplorerTab tab } || _tabs.Count <= 1) return;
        CloseTab(tab);
    }

    private void Tabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroll.ScrollToHorizontalOffset(Math.Max(0, TabScroll.HorizontalOffset - e.Delta));
        e.Handled = true;
    }

    // R17: restored from the Drive R12 audited baseline. The breadcrumb's wheel
    // scroll is horizontal; regular navigation tree and file-grid wheels are untouched.
    private void Breadcrumb_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (BreadcrumbScroll is null) return;
        BreadcrumbScroll.ScrollToHorizontalOffset(Math.Max(0, BreadcrumbScroll.HorizontalOffset - e.Delta));
        e.Handled = true;
    }

    // R17: when the compact breadcrumb row hides its quick toggles, keep every
    // setting reachable through View > Display items. Existing checkbox events
    // persist changes to the per-pane session as before.
    private void ToggleHiddenMenu_Click(object sender, RoutedEventArgs e)
        => HiddenToggle.IsChecked = HiddenToggle.IsChecked != true;
    private void ToggleExtensionsMenu_Click(object sender, RoutedEventArgs e)
        => ExtensionsToggle.IsChecked = ExtensionsToggle.IsChecked != true;
    private void ToggleCheckboxesMenu_Click(object sender, RoutedEventArgs e)
        => CheckboxesToggle.IsChecked = CheckboxesToggle.IsChecked != true;
    private void ToggleColorCodingMenu_Click(object sender, RoutedEventArgs e)
        => ColorFilterToggle.IsChecked = ColorFilterToggle.IsChecked != true;

    private void NewTab_Click(object sender, RoutedEventArgs e) => OpenTabAt(WindowsKnownLocations.ThisPc);

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (BeforeFileChange?.Invoke() == false) return;
        if (_activeTab?.Back() == true) { HasUserNavigated = true; _skipNavigationTreeReveal = false; _ = LoadFolderAsync(); }
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (BeforeFileChange?.Invoke() == false) return;
        if (_activeTab?.Forward() == true) { HasUserNavigated = true; _skipNavigationTreeReveal = false; _ = LoadFolderAsync(); }
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        string? parent = _activeTab is null ? null : ExplorerLocationService.Parent(_activeTab.CurrentPath, WindowsKnownLocations.Resolve("Desktop"));
        if (parent is not null) TryNavigate(parent);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadFolderAsync();
    private void Home_Click(object sender, RoutedEventArgs e) => TryNavigate(DefaultFolder());
    private void Go_Click(object sender, RoutedEventArgs e)
    {
        AddressPopup.IsOpen = false;
        TryNavigate(AddressBox.Text);
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F4 || (e.Key == Key.System && e.SystemKey == Key.Down))
        {
            ShowAddressDropdown();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && AddressPopup.IsOpen)
        {
            AddressPopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Down && AddressPopup.IsOpen)
        {
            _addressNavigationTree?.FocusDriveList();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            string requested = AddressBox.Text;
            AddressPopup.IsOpen = false;
            TryNavigate(requested);
            e.Handled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filterTimer is null) return;
        _filterCancellation?.Cancel();
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Text = string.Empty;
            FilesGrid.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _filterTimer.Stop();
            _ = ApplyFilterAndSortAsync();
            FilesGrid.Focus();
            e.Handled = true;
        }
    }

    private void CategorySelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || CategorySelector.SelectedItem is not ComboBoxItem item) return;
        _categoryFilter = item.Tag as string ?? "All";
        _ = ApplyFilterAndSortAsync();
    }

    private void ViewOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showHidden = HiddenToggle?.IsChecked == true;
        _showExtensions = ExtensionsToggle?.IsChecked == true;
        _showCheckboxes = CheckboxesToggle?.IsChecked == true;
        _colorCoding = ColorFilterToggle?.IsChecked != false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckboxesVisibility)));
        foreach (FileEntry entry in _allEntries)
        {
            entry.ShowExtension = _showExtensions;
            entry.ColorCoding = _colorCoding;
        }
        _ = ApplyFilterAndSortAsync();
    }

    private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: FileEntry entry })
        {
            if (entry.IsChecked && !FilesGrid.SelectedItems.Contains(entry))
                FilesGrid.SelectedItems.Add(entry);
            else if (!entry.IsChecked && FilesGrid.SelectedItems.Contains(entry))
                FilesGrid.SelectedItems.Remove(entry);
        }
    }

    private void FilesGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (e.Column.SortMemberPath is not { Length: > 0 } property) return;
        e.Handled = true;
        bool same = property == _sortField;
        _sortDescending = same && !_sortDescending;
        _sortField = property;
        foreach (DataGridColumn column in FilesGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = _sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        _ = ApplyFilterAndSortAsync();
    }

    private void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_renamingEntry is not null) return;
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source) return;
        var row = ItemsControl.ContainerFromElement(FilesGrid, source) as DataGridRow;
        if (row?.Item is FileEntry entry)
        {
            if (IsRecycleBin) { ShowRecycleItemHint(); e.Handled = true; return; }
            e.Handled = true;
            if (entry.IsDirectory) TryNavigate(entry.FullPath);
            else OpenOriginal(entry);
            return;
        }
        // Fallback for a mouse driver or view mode that raises MouseDoubleClick.
        // The preview handler normally owns empty area clicks because marquee consumes them.
        if (Keyboard.Modifiers != ModifierKeys.None || !IsBlankViewportPoint(e.GetPosition(FilesGrid))) return;
        _lastBlankClickTick = 0;
        e.Handled = true;
        Up_Click(this, e);
    }

    private bool IsBlankViewportPoint(Point point)
    {
        if (!FileViewport().Contains(point)) return false;
        // HitTest uses the physical point, not OriginalSource (which can change to the
        // DataGrid after Mouse.Capture during marquee selection).
        DependencyObject? actual = VisualTreeHelper.HitTest(FilesGrid, point)?.VisualHit;
        if (actual is null) return false;
        for (DependencyObject? node = actual; node is not null;)
        {
            if (node is DataGridRow or DataGridColumnHeader or ScrollBar or Thumb or RepeatButton
                or ButtonBase or DataGridCell)
                return false;
            if (ReferenceEquals(node, FilesGrid)) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void TreeToggle_Click(object sender, RoutedEventArgs e)
    {
        // Each pane owns and persists its navigation-tree preference, even in compact mode.
        _treePreference = _treeAutoHide || !_treePreference;
        _treeAutoHide = false;
        _navigationRevealed = false;
        ApplyResponsiveLayout();
        SettingsChanged?.Invoke();
    }

    private void DetailsToggle_Click(object sender, RoutedEventArgs e) => InspectorRequested?.Invoke(this);
    private void SendToAi_Click(object sender, RoutedEventArgs e) => AiRequested?.Invoke(this);

    private void ViewSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewMode = ExplorerCommandRouter.SelectorIndexToViewMode(ViewSelector.SelectedIndex);
        ResetThumbnails(_viewMode is "Thumbnails" or "Tiles");
        ApplyViewMode();
    }

    private async void FilesGrid_LoadingRow(object? sender, DataGridRowEventArgs e) =>
        await LoadRowThumbnailAsync(e);

    private void FilesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject element) return;
        var row = ItemsControl.ContainerFromElement(FilesGrid, element) as DataGridRow;
        if (row?.Item is FileEntry entry && !row.IsSelected)
        {
            FilesGrid.SelectedItems.Clear();
            FilesGrid.SelectedItem = entry;
        }
    }

    private void ContextOpen_Click(object sender, RoutedEventArgs e)
    {
        if (IsRecycleBin) { ShowRecycleItemHint(); return; }
        if (FilesGrid.SelectedItem is not FileEntry entry) return;
        if (entry.IsDirectory) { TryNavigate(entry.FullPath); return; }
        OpenOriginal(entry);
    }

    private void ContextOpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (IsRecycleBin) { ShowRecycleItemHint(); return; }
        if (FilesGrid.SelectedItem is not FileEntry entry || entry.IsDirectory) return;
        try { FileActionsService.OpenWithDialog(entry.FullPath); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "연결 프로그램 열기 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ContextOpenInNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (IsRecycleBin) { ShowRecycleItemHint(); return; }
        if (FilesGrid.SelectedItem is FileEntry { IsDirectory: true } entry)
            OpenTabAt(entry.FullPath);
        else if (ActiveFolder is string current)
            OpenTabAt(current);
    }

    private void ContextPinFolder_Click(object sender, RoutedEventArgs e)
    {
        if (IsRecycleBin) { ShowRecycleItemHint(); return; }
        if (FilesGrid.SelectedItem is FileEntry { IsDirectory: true } entry)
            NavigationTree.PinFolder(entry.FullPath);
        else if (ActiveFolder is string current)
            NavigationTree.PinFolder(current);
    }

    private void ClipboardCopy_Click(object sender, RoutedEventArgs e) => PutSelectionOnClipboard(false);
    private void ClipboardCut_Click(object sender, RoutedEventArgs e) { if (_actionsEnabled) PutSelectionOnClipboard(true); }
    private async void ClipboardPaste_Click(object sender, RoutedEventArgs e) => await ExecuteClipboardPasteAsync();

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled || !_undoService.CanUndo) return;
        if (BeforeFileChange?.Invoke() == false) return;
        SetFileActionsEnabled(false);
        BlockingFileActionChanged?.Invoke(true);
        try
        {
            // A cross-drive undo copies bytes; keep WPF responsive during the operation.
            var (success, message) = await Task.Run(_undoService.UndoLast);
            UpdateUndoButtonState();
            FooterText.Text = message;
            if (success)
            {
                if (FileActionCompleted is null) RefreshCurrent();
                else FileActionCompleted.Invoke();
            }
        }
        finally { SetFileActionsEnabled(true); BlockingFileActionChanged?.Invoke(false); }
    }

    private async void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled || !_undoService.CanRedo) return;
        if (BeforeFileChange?.Invoke() == false) return;
        SetFileActionsEnabled(false);
        BlockingFileActionChanged?.Invoke(true);
        try
        {
            var (success, message) = await Task.Run(_undoService.RedoLast);
            UpdateUndoButtonState();
            FooterText.Text = message;
            if (success)
            {
                if (FileActionCompleted is null) RefreshCurrent();
                else FileActionCompleted.Invoke();
            }
        }
        finally { SetFileActionsEnabled(true); BlockingFileActionChanged?.Invoke(false); }
    }

    private async void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled || SelectedPaths.Count == 0) return;
        await DuplicatePathsAsync(SelectedPaths.ToArray());
    }

    private void ContextCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (IsRecycleBin) { ShowRecycleItemHint(); return; }
        var items = FilesGrid.SelectedItems.OfType<FileEntry>().Select(x => x.FullPath).ToArray();
        if (items.Length == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, items)); FooterText.Text = "선택 항목의 전체 경로를 복사했습니다."; }
        catch (ExternalException ex)
        { MessageBox.Show(ex.Message, "클립보드 오류", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ContextCopyName_Click(object sender, RoutedEventArgs e)
    {
        var items = FilesGrid.SelectedItems.OfType<FileEntry>().Select(x => x.Name).ToArray();
        if (items.Length == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, items)); FooterText.Text = "선택 항목의 파일 이름을 복사했습니다."; }
        catch (ExternalException ex)
        { MessageBox.Show(ex.Message, "클립보드 오류", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ContextProperties_Click(object sender, RoutedEventArgs e)
    {
        if (IsRecycleBin) { RecycleProperties_Click(sender, e); return; }
        string? targetPath = (FilesGrid.SelectedItem as FileEntry)?.FullPath ?? ActiveFolder;
        if (string.IsNullOrWhiteSpace(targetPath)) return;
        try
        {
            if (!SHObjectProperties(IntPtr.Zero, 0x2, targetPath, null))
                MessageBox.Show("Windows 파일 속성 창을 열지 못했습니다.", "속성", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
        { MessageBox.Show(ex.Message, "속성 열기 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OpenCmd_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveFolder is not string folder) return;
        try { FileActionsService.OpenCmdPromptHere(folder); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "CMD 열기 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OpenWindowsExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (IsRecycleBin) { OpenNativeRecycle_Click(sender, e); return; }
        string? target = (FilesGrid.SelectedItem as FileEntry)?.FullPath ?? ActiveFolder;
        if (string.IsNullOrWhiteSpace(target)) return;
        try { FileActionsService.OpenInWindowsExplorer(target); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "탐색기 열기 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => FilesGrid.SelectAll();

    private void InvertSelection_Click(object sender, RoutedEventArgs e)
    {
        var current = FilesGrid.SelectedItems.OfType<FileEntry>().ToHashSet();
        FilesGrid.SelectedItems.Clear();
        foreach (FileEntry item in FilesGrid.Items.OfType<FileEntry>())
        {
            if (!current.Contains(item)) FilesGrid.SelectedItems.Add(item);
        }
    }

    private void PreviewToggle_Click(object sender, RoutedEventArgs e) => PreviewRequested?.Invoke(this);

    private void FilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (FileEntry added in e.AddedItems.OfType<FileEntry>()) added.IsChecked = true;
        foreach (FileEntry removed in e.RemovedItems.OfType<FileEntry>()) removed.IsChecked = false;
        // Bulk page rebinds can fire 2,000 SelectionChanged events. Status is refreshed once after binding.
        if (_reloadingSelection || _marqueeUpdating) return;
        if (!IsRecycleBin && !IsThisPc)
        {
            foreach (FileEntry removed in e.RemovedItems.OfType<FileEntry>()) _pagedSelection.Remove(removed.FullPath);
            foreach (FileEntry added in e.AddedItems.OfType<FileEntry>())
            {
                if (!_pagedSelection.Add(added.FullPath))
                {
                    _reloadingSelection = true;
                    try { FilesGrid.SelectedItems.Remove(added); }
                    finally { _reloadingSelection = false; }
                    PaneStatus.Text = $"선택은 최대 {PagedSelectionSet.MaxSelection:N0}개까지 가능합니다.";
                }
            }
        }
        UpdateFooter(); UpdateDetails();
        SelectionContextChanged?.Invoke(this);
    }

    private void ExplorerPane_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Let the inline editor own Enter/Escape/typing instead of pane shortcuts.
        if (_renamingEntry is not null) return;
        if (_marqueeOrigin is not null && e.Key == Key.Escape) { EndMarquee(restore: true); e.Handled = true; return; }
        if (DispatchPaneShortcut(e)) e.Handled = true;
    }

    private void ExplorerPane_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
        {
            Back_Click(this, e);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            Forward_Click(this, e);
            e.Handled = true;
        }
    }

    private void FilesGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            int current = ViewSelector.SelectedIndex < 0 ? 0 : ViewSelector.SelectedIndex;
            int next = e.Delta > 0 ? Math.Max(0, current - 1) : Math.Min(4, current + 1);
            if (next != current) ViewSelector.SelectedIndex = next;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || e.Delta == 0) return;
        var scroll = GetFilesScrollViewer();
        if (scroll is null || scroll.ScrollableWidth <= 0) return;
        scroll.ScrollToHorizontalOffset(Math.Clamp(scroll.HorizontalOffset - e.Delta / 120.0 * 64.0, 0, scroll.ScrollableWidth));
        e.Handled = true;
    }

    private void FilesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_renamingEntry is not null) return;
        if (DispatchGridShortcut(e)) { e.Handled = true; return; }
        if (TryTypeToSelectInGrid(e)) e.Handled = true;
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e) => ExecuteCreateFolder();
    private void NewFile_Click(object sender, RoutedEventArgs e) => ExecuteCreateFile();
    private async void ZipCompress_Click(object sender, RoutedEventArgs e) => await ExecuteZipCompressAsync();
    private async void ZipExtract_Click(object sender, RoutedEventArgs e) => await ExecuteZipExtractAsync();
    private void ExportList_Click(object sender, RoutedEventArgs e) => ExecuteExportDirectoryListing();

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled) return;
        var selection = FilesGrid.SelectedItems.OfType<FileEntry>().ToArray();
        if (selection.Length == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "이름을 변경할 항목을 선택하세요.", "이름 변경", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (selection.Length > 1)
        {
            if (BeforeFileChange?.Invoke() == false) return;
            var batchDialog = new BatchRenameDialog(selection.Select(x => x.FullPath).ToArray()) { Owner = Window.GetWindow(this) };
            if (batchDialog.ShowDialog() != true || batchDialog.ApprovedRenames.Count == 0) return;
            try
            {
                var pairs = FileActionsService.BatchRename(batchDialog.ApprovedRenames);
                _undoService.RecordBatchRename(pairs, safeFileRename: pairs.All(p => File.Exists(p.NewPath)));
                UpdateUndoButtonState();
                if (pairs.Count > 0) _restoreSelectionPath = pairs[^1].NewPath;
                if (FileActionCompleted is null) RefreshCurrent();
                else FileActionCompleted.Invoke();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                MessageBox.Show(Window.GetWindow(this), ex.Message, "일괄 이름 변경 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }
        BeginInlineRename(selection[0]);
    }

    public void ExecuteEiassRename()
    {
        if (!_actionsEnabled) return;
        var selected = FilesGrid.SelectedItems.OfType<FileEntry>().ToHashSet();
        string[] paths = FilesGrid.Items.OfType<FileEntry>().Where(selected.Contains).Select(x => x.FullPath).ToArray();
        int pdfCount = paths.Count(p => File.Exists(p) && Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdfCount is < 1 or > 1000)
        {
            MessageBox.Show(Window.GetWindow(this), "문서명을 맞출 PDF 파일을 1~1,000개 선택하세요.", "EIASS 제출용 문서명 설정", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (BeforeFileChange?.Invoke() == false) return;
        var dialog = new EiassRenameDialog(paths) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.ApprovedRenames.Count == 0) return;
        try
        {
            var pairs = FileActionsService.BatchRenameFilesSafely(dialog.ApprovedRenames);
            _undoService.RecordBatchRename(pairs, safeFileRename: true);
            UpdateUndoButtonState();
            if (pairs.Count > 0) _restoreSelectionPath = pairs[^1].NewPath;
            if (FileActionCompleted is null) RefreshCurrent(); else FileActionCompleted.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "EIASS 문서명 변경 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void EiassRename_Click(object sender, RoutedEventArgs e) => ExecuteEiassRename();
    private void PermanentDelete_Click(object sender, RoutedEventArgs e) => ExecutePermanentDelete();
    private async void Recycle_Click(object sender, RoutedEventArgs e)
    {
        if (IsRecycleBin) { PermanentDeleteRecycle_Click(sender, e); return; }
        await ExecuteRecycleAsync();
    }

    private void FilesGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_renamingEntry is not null) return;
        // PreviewMouseDown is consumed by marquee on its first click. Hit-test the
        // real viewport point and recognize the second click before starting marquee.
        Point point = e.GetPosition(FilesGrid);
        bool blank = IsBlankViewportPoint(point) && Keyboard.Modifiers == ModifierKeys.None;
        long now = Environment.TickCount64;
        double dpiX = VisualTreeHelper.GetDpi(FilesGrid).DpiScaleX;
        double dpiY = VisualTreeHelper.GetDpi(FilesGrid).DpiScaleY;
        // SM_CXDOUBLECLK=36, SM_CYDOUBLECLK=37 (pixel bounds, converted to WPF DIPs).
        double halfWidth = Math.Max(2, GetSystemMetrics(36) / Math.Max(1, dpiX) / 2.0);
        double halfHeight = Math.Max(2, GetSystemMetrics(37) / Math.Max(1, dpiY) / 2.0);
        bool secondBlankClick = blank && _lastBlankClickTick != 0 &&
            string.Equals(_lastBlankClickFolder, ActiveFolder, StringComparison.OrdinalIgnoreCase) &&
            now - _lastBlankClickTick <= GetDoubleClickTime() &&
            Math.Abs(point.X - _lastBlankClickPoint.X) <= halfWidth &&
            Math.Abs(point.Y - _lastBlankClickPoint.Y) <= halfHeight;
        if (blank && (e.ClickCount >= 2 || secondBlankClick))
        {
            _lastBlankClickTick = 0;
            EndMarquee();
            e.Handled = true;
            Up_Click(this, e);
            return;
        }
        _lastBlankClickTick = blank ? now : 0;
        _lastBlankClickPoint = point;
        _lastBlankClickFolder = blank ? ActiveFolder : null;
        if (BeginMarquee(e)) return;
        var row = e.OriginalSource is DependencyObject element
            ? ItemsControl.ContainerFromElement(FilesGrid, element) as DataGridRow : null;
        if (row?.Item is FileEntry) PreservePointerSelectionHorizontalOffset();
        _dragSourcePath = row?.Item is FileEntry entry ? entry.FullPath : null;
        _dragOrigin = _dragSourcePath is null ? null : e.GetPosition(FilesGrid);
        if (row is { IsSelected: true } && FilesGrid.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _dragCandidatePaths = SelectedPaths.ToArray();
        }
        else
        {
            _dragCandidatePaths = null;
        }
    }

    private void PreservePointerSelectionHorizontalOffset()
    {
        if (GetFilesScrollViewer() is not { } scroll) return;
        double previousOffset = scroll.HorizontalOffset;
        int listingGeneration = _listingGeneration;
        int version = ++_pointerSelectionScrollVersion;
        // This is limited to pointer selection of an already visible row. It must
        // not reset horizontal scrolling on keyboard navigation, folder changes,
        // scrollbar drags, Shift+wheel or intentional scroll-to-file operations.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (version != _pointerSelectionScrollVersion || listingGeneration != _listingGeneration ||
                !ReferenceEquals(scroll, GetFilesScrollViewer())) return;
            if (Math.Abs(scroll.HorizontalOffset - previousOffset) > 0.1)
                scroll.ScrollToHorizontalOffset(Math.Clamp(previousOffset, 0, scroll.ScrollableWidth));
        }));
    }

    private void FilesGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (UpdateMarquee(e)) return;
        if (IsRecycleBin || IsThisPc) return;
        if (!_actionsEnabled || _dragOrigin is not Point start || e.LeftButton != MouseButtonState.Pressed)
        {
            _dragOrigin = null;
            _dragSourcePath = null;
            _dragCandidatePaths = null;
            return;
        }
        Point current = e.GetPosition(FilesGrid);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragOrigin = null;
        string[] paths = _dragCandidatePaths is { Length: > 1 } saved ? saved : SelectedPaths.ToArray();
        if (_dragSourcePath is not null && !paths.Contains(_dragSourcePath, StringComparer.OrdinalIgnoreCase))
            paths = [_dragSourcePath];
        _dragSourcePath = null;
        _dragCandidatePaths = null;
        if (paths.Length == 0) return;
        var data = new DataObject();
        data.SetData(typeof(InternalDragPayload), new InternalDragPayload(this, paths));
        var fileDrop = new System.Collections.Specialized.StringCollection();
        fileDrop.AddRange(paths);
        data.SetFileDropList(fileDrop);
        DragDrop.DoDragDrop(FilesGrid, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void FilesGrid_PreviewDragOver(object sender, DragEventArgs e)
    {
        var dropInfo = GetDropInfo(e);
        if (dropInfo is null)
        {
            e.Effects = DragDropEffects.None;
        }
        else
        {
            string target = ResolveDropTargetFolder(e);
            TransferKind kind = ResolveDropTransferKind(e, dropInfo.Paths, target);
            e.Effects = kind == TransferKind.Move ? DragDropEffects.Move : DragDropEffects.Copy;
        }
        FilesGrid.BorderThickness = e.Effects == DragDropEffects.None ? new Thickness(0) : new Thickness(2);
        FilesGrid.BorderBrush = (Brush)FindResource(e.Effects == DragDropEffects.None ? "PanelBorder" : "Accent");
        e.Handled = true;
    }

    private async void FilesGrid_PreviewDrop(object sender, DragEventArgs e)
    {
        var payload = GetDropInfo(e);
        FilesGrid.BorderThickness = new Thickness(0);
        e.Handled = true;
        if (payload is null) { e.Effects = DragDropEffects.None; return; }
        string target = ResolveDropTargetFolder(e);
        TransferKind kind = ResolveDropTransferKind(e, payload.Paths, target);
        if (ReferenceEquals(payload.Source, this) && string.Equals(target, ActiveFolder, StringComparison.OrdinalIgnoreCase))
        {
            if (kind == TransferKind.Copy)
            {
                await DuplicatePathsAsync(payload.Paths);
                return;
            }
            e.Effects = DragDropEffects.None;
            return;
        }
        DropRequested?.Invoke(new ExplorerDropRequest(payload.Source, this, payload.Paths, target, kind));
    }

    private void FilesGrid_DragLeave(object sender, DragEventArgs e)
    {
        FilesGrid.BorderThickness = new Thickness(0);
    }
}
