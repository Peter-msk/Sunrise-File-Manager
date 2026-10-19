using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
    private bool _showHidden = true;
    private bool _showExtensions = true;
    private bool _showCheckboxes;
    private bool _colorCoding = true;
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
    private string? _restoreSelectionPath;
    private bool _initializing;

    public event PropertyChangedEventHandler? PropertyChanged;
    public Visibility CheckboxesVisibility => _showCheckboxes ? Visibility.Visible : Visibility.Collapsed;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType,
        string szObjectName, string? szPropertyPage);

    public event Action<ExplorerDropRequest>? DropRequested;
    public event Action? FileActionCompleted;
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
        NavigationTree.GetActiveFolder = () => ActiveFolder;
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
            if (_actionsEnabled && !_recycling && _listingCancellation is null)
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
            _detailsPreference = state?.DetailsVisible ?? false;
            _showHidden = state?.ShowHidden ?? true;
            _showExtensions = state?.ShowExtensions ?? true;
            _showCheckboxes = state?.ShowCheckboxes ?? false;
            _colorCoding = state?.ColorCoding ?? true;
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
            SearchBox.Text = state?.SearchText ?? string.Empty;
            ApplyViewMode();
            if (state?.ColumnWidths is { Count: > 0 } savedWidths)
            {
                for (int col = 0; col < Math.Min(savedWidths.Count, FilesGrid.Columns.Count); col++)
                {
                    double width = savedWidths[col];
                    if (width is >= 40 and <= 4000)
                        FilesGrid.Columns[col].Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);
                }
            }
            string fallback = DefaultFolder();
            if (state?.TabPaths is { Count: > 0 } saved)
            {
                foreach (string? path in saved.Take(12))
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (Directory.Exists(path)) _tabs.Add(new ExplorerTab(path));
                }
            }
            if (_tabs.Count == 0) _tabs.Add(new ExplorerTab(fallback));
            int selected = Math.Clamp(state?.SelectedTabIndex ?? 0, 0, _tabs.Count - 1);
            SelectTab(_tabs[selected]);
        }
        finally
        {
            _initializing = false;
        }
    }

    public void RefreshThemeColors()
    {
        foreach (var item in _allEntries) item.RefreshThemeColors();
    }

    public IReadOnlyList<string> GetPinnedFolders() => NavigationTree.PinnedFolders;
    public void SyncPinnedFolders(IEnumerable<string> folders) => NavigationTree.SetPinnedFolders(folders);

    public string? ActiveFolder => _activeTab?.CurrentPath;
    public FileEntry? SelectedFile => FilesGrid.SelectedItems.Count == 1 ? FilesGrid.SelectedItem as FileEntry : null;

    public bool RestoreSelection(string path)
    {
        var match = FilesGrid.Items.OfType<FileEntry>()
            .FirstOrDefault(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        FilesGrid.SelectedItems.Clear();
        FilesGrid.SelectedItem = match;
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
            var gridSelected = FilesGrid.SelectedItems.OfType<FileEntry>().Select(item => item.FullPath).ToList();
            if (gridSelected.Count > 0) return gridSelected;
            return _allEntries.Where(x => x.IsChecked).Select(x => x.FullPath).ToArray();
        }
    }

    public void RefreshCurrent() => _ = LoadFolderAsync();
    public void FocusFiles() => FilesGrid.Focus();

    public PanelSession GetSession() => new()
    {
        TabPaths = _tabs.Select(tab => tab.CurrentPath).ToList(),
        SelectedTabIndex = _activeTab is null ? 0 : Math.Max(0, _tabs.IndexOf(_activeTab)),
        TreeVisible = _treePreference,
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
        SearchText = SearchBox.Text,
        ColumnWidths = FilesGrid.Columns.Select(column =>
            double.IsFinite(column.ActualWidth) && column.ActualWidth >= 40
                ? Math.Round(Math.Min(column.ActualWidth, 4000)) : 0d).ToList()
    };

    public void CancelPendingOperations()
    {
        DisposeFolderWatcher();
        _watcherDebounceTimer.Stop();
        _listingCancellation?.Cancel();
        _filterCancellation?.Cancel();
        _filterTimer.Stop();
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

    private void NewTab_Click(object sender, RoutedEventArgs e) => OpenTabAt(_activeTab?.CurrentPath ?? DefaultFolder());

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (BeforeFileChange?.Invoke() == false) return;
        if (_activeTab?.Back() == true) { HasUserNavigated = true; _ = LoadFolderAsync(); }
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (BeforeFileChange?.Invoke() == false) return;
        if (_activeTab?.Forward() == true) { HasUserNavigated = true; _ = LoadFolderAsync(); }
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        string? parent = _activeTab is null ? null : Directory.GetParent(_activeTab.CurrentPath)?.FullName;
        if (parent is not null) TryNavigate(parent);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadFolderAsync();
    private void Home_Click(object sender, RoutedEventArgs e) => TryNavigate(DefaultFolder());
    private void Go_Click(object sender, RoutedEventArgs e) => TryNavigate(AddressBox.Text);

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        TryNavigate(AddressBox.Text);
        e.Handled = true;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filterTimer is null) return;
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
        if (e.OriginalSource is not DependencyObject source) return;
        var row = ItemsControl.ContainerFromElement(FilesGrid, source) as DataGridRow;
        if (row?.Item is not FileEntry entry) return;
        e.Handled = true;
        if (entry.IsDirectory)
        {
            TryNavigate(entry.FullPath);
            return;
        }
        OpenOriginal(entry);
    }

    private void TreeToggle_Click(object sender, RoutedEventArgs e)
    {
        // Each pane owns and persists its navigation-tree preference, even in compact mode.
        _treePreference = !_treePreference;
        ApplyResponsiveLayout();
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
        if (FilesGrid.SelectedItem is not FileEntry entry) return;
        if (entry.IsDirectory) { TryNavigate(entry.FullPath); return; }
        OpenOriginal(entry);
    }

    private void ContextOpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not FileEntry entry || entry.IsDirectory) return;
        try { FileActionsService.OpenWithDialog(entry.FullPath); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "연결 프로그램 열기 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ContextOpenInNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileEntry { IsDirectory: true } entry)
            OpenTabAt(entry.FullPath);
        else if (ActiveFolder is string current)
            OpenTabAt(current);
    }

    private void ContextPinFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileEntry { IsDirectory: true } entry)
            NavigationTree.PinFolder(entry.FullPath);
        else if (ActiveFolder is string current)
            NavigationTree.PinFolder(current);
    }

    private void ClipboardCopy_Click(object sender, RoutedEventArgs e) => PutSelectionOnClipboard(false);
    private void ClipboardCut_Click(object sender, RoutedEventArgs e) { if (_actionsEnabled) PutSelectionOnClipboard(true); }
    private void ClipboardPaste_Click(object sender, RoutedEventArgs e) => ExecuteClipboardPaste();

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled || !_undoService.CanUndo) return;
        if (BeforeFileChange?.Invoke() == false) return;
        var (success, message) = _undoService.UndoLast();
        UpdateUndoButtonState();
        FooterText.Text = message;
        if (success)
        {
            if (FileActionCompleted is null) RefreshCurrent();
            else FileActionCompleted.Invoke();
        }
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled || !_undoService.CanRedo) return;
        if (BeforeFileChange?.Invoke() == false) return;
        var (success, message) = _undoService.RedoLast();
        UpdateUndoButtonState();
        FooterText.Text = message;
        if (success)
        {
            if (FileActionCompleted is null) RefreshCurrent();
            else FileActionCompleted.Invoke();
        }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled || SelectedPaths.Count == 0) return;
        DuplicatePaths(SelectedPaths);
    }

    private void ContextCopyPath_Click(object sender, RoutedEventArgs e)
    {
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
        UpdateFooter();
        UpdateDetails();
        if (!_reloadingSelection) SelectionContextChanged?.Invoke(this);
    }

    private void ExplorerPane_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        int current = ViewSelector.SelectedIndex < 0 ? 0 : ViewSelector.SelectedIndex;
        int next = e.Delta > 0 ? Math.Max(0, current - 1) : Math.Min(4, current + 1);
        if (next != current) ViewSelector.SelectedIndex = next;
    }

    private void FilesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DispatchGridShortcut(e)) { e.Handled = true; return; }
        if (TryTypeToSelectInGrid(e)) e.Handled = true;
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e) => ExecuteCreateFolder();
    private void NewFile_Click(object sender, RoutedEventArgs e) => ExecuteCreateFile();
    private void ZipCompress_Click(object sender, RoutedEventArgs e) => ExecuteZipCompress();
    private void ZipExtract_Click(object sender, RoutedEventArgs e) => ExecuteZipExtract();
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
                _undoService.RecordBatchRename(pairs);
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
        var entry = selection[0];
        var dialog = new NameInputDialog("이름 변경", "새 이름 (확장자 포함)", entry.Name) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        if (!entry.IsDirectory && !string.Equals(Path.GetExtension(entry.Name), Path.GetExtension(dialog.ResultName), StringComparison.OrdinalIgnoreCase) &&
            MessageBox.Show(Window.GetWindow(this), "확장자가 변경됩니다. 계속하시겠습니까?", "확장자 변경 확인",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (BeforeFileChange?.Invoke() == false) return;
        try
        {
            string oldPath = entry.FullPath;
            string renamed = FileActionsService.Rename(oldPath, dialog.ResultName);
            if (!string.Equals(oldPath, renamed, StringComparison.Ordinal))
            {
                _undoService.RecordRename(oldPath, renamed);
                UpdateUndoButtonState();
            }
            _restoreSelectionPath = renamed;
            if (FileActionCompleted is null) RefreshCurrent();
            else FileActionCompleted.Invoke();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "이름 변경 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PermanentDelete_Click(object sender, RoutedEventArgs e) => ExecutePermanentDelete();
    private async void Recycle_Click(object sender, RoutedEventArgs e) => await ExecuteRecycleAsync();

    private void FilesGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = e.OriginalSource is DependencyObject element
            ? ItemsControl.ContainerFromElement(FilesGrid, element) as DataGridRow : null;
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

    private void FilesGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
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

    private void FilesGrid_PreviewDrop(object sender, DragEventArgs e)
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
                DuplicatePaths(payload.Paths);
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
