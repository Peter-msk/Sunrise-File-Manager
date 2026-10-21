using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Sunrise.FileManager.Controls;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager;

/// <summary>
/// Core MainWindow shell: 3-column workspace layout, F8 AI popup, and file transfer orchestration.
/// Multi-pane host helpers and cross-pane coordination are modularized in
/// Features/Workspace/MainWindow.PanelCoordination.cs.
/// </summary>
public partial class MainWindow : Window
{
    private SunriseControlService.Policy _controlPolicy = new("allow", "allow", "", "", "fallback");
    private readonly ExplorerPane[] _panes = new ExplorerPane[4];
    private readonly Grid[] _hosts = new Grid[4];
    private int _visiblePanels = 1;
    private SplitLayoutPreset _splitPreset = SplitLayoutPreset.Single;
    private readonly SplitLayoutPreset[] _lastPresetByCount =
    [
        SplitLayoutPreset.Single,
        SplitLayoutPreset.Single,
        SplitLayoutPreset.TwoColumns,
        SplitLayoutPreset.ThreeTop1Bottom2,
        SplitLayoutPreset.FourGrid2x2
    ];
    private bool _updatingSplitSelector;
    private bool _layoutInitialized;
    private int _activePane;
    private bool _aiBusy;
    private bool _aiOpen;
    private readonly AiCommandPanel AiPanel;
    private Window? _aiWindow;
    private bool _mainWindowClosing;
    private bool _inspectorOpen = true;
    private bool _dockPreviewOpen;
    private double _inspectorPreferredWidth = 330;
    private double _previewPreferredWidth = 350;
    private bool _syncingInspector;
    private bool _syncingPinned;
    private bool _themeReady;
    private string _currentTheme = "System";
    private void SystemThemeChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (_currentTheme != "System" || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(() => ApplySelectedTheme()));
    }
    private void ApplySelectedTheme()
    {
        ThemeManager.Apply(_currentTheme);
        ThemeSystemMenuItem.IsChecked = _currentTheme == "System";
        ThemeLightMenuItem.IsChecked = _currentTheme == "Light";
        ThemeDarkMenuItem.IsChecked = _currentTheme == "Dark";
        HeaderThemeButton.Content = _currentTheme switch
        {
            "System" => "◐ PC 테마",
            "Dark" => "☾ 다크",
            _ => "☀ 라이트"
        };
        foreach (var pane in _panes) pane?.RefreshThemeColors();
    }
    private readonly bool _persistSession;
    private readonly System.Windows.Threading.DispatcherTimer _sessionSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(950)
    };
    private CancellationTokenSource? _transferCancellation;
    private bool _working;
    private int _activeRecycleJobs;

    public MainWindow() : this(null) { }

    // UI checks supply an isolated session; normal startup continues to restore the user's session.
    public MainWindow(AppSession? initialSession)
    {
        _persistSession = initialSession is null;
        InitializeComponent();
        AiPanel = new AiCommandPanel();
        _sessionSaveTimer.Tick += (_, _) =>
        {
            _sessionSaveTimer.Stop();
            if (_persistSession && _layoutInitialized) TrySaveSession(showError: false);
        };
        WorkspaceShell.SizeChanged += (_, _) => UpdateWorkspaceLayout();
        WorkspaceViewport.SizeChanged += (_, _) => UpdateWorkspaceLayout();
        Inspector.SizeChanged += (_, _) => UpdateWorkspaceLayout();
        PreviewPane.SizeChanged += (_, _) => UpdateWorkspaceLayout();
        string? settingsWarning = null;
        AppSession session = initialSession ?? SessionStore.Load(out settingsWarning);
        // Restore only sensible, visible window geometry (old sessions simply use defaults).
        if (session.WindowWidth is >= 1010 and <= 12000 && session.WindowHeight is >= 560 and <= 9000)
        {
            Width = session.WindowWidth;
            Height = session.WindowHeight;
            if (session.WindowLeft >= SystemParameters.VirtualScreenLeft - 20 &&
                session.WindowTop >= SystemParameters.VirtualScreenTop - 20 &&
                session.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 120 &&
                session.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = session.WindowLeft;
                Top = session.WindowTop;
            }
        }
        if (session.IsMaximized) Loaded += (_, _) => WindowState = WindowState.Maximized;
        if (session.InspectorWidth is >= 270 and <= 800) _inspectorPreferredWidth = session.InspectorWidth;
        if (session.PreviewWidth is >= 260 and <= 900) _previewPreferredWidth = session.PreviewWidth;
        _currentTheme = ThemeManager.Normalize(session.Theme);
        ThemeSelector.SelectedIndex = _currentTheme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        ApplySelectedTheme();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        _themeReady = true;
        _inspectorOpen = session.LayoutVersion < 18 || session.InspectorVisible;
        _dockPreviewOpen = session.LayoutVersion >= 18 && session.DockPreviewVisible;
        PreviewPane.CloseRequested += () => SetDockPreview(false);
        AiPanel.CloseRequested += HideAiWindow;
        Inspector.MemoSaved += () => { if (_activePane < _panes.Length) _panes[_activePane]?.RefreshCurrent(); };
        Inspector.RequestOllamaSetup = () => AiPanel.PromptAndConnectOllamaAsync();
        for (int i = 0; i < _panes.Length; i++)
        {
            var pane = new ExplorerPane();
            PanelSession? panelState = session.Panels?.ElementAtOrDefault(i);
            if (session.LayoutVersion < 17 && panelState is not null) panelState.DetailsVisible = false;
            pane.Initialize(i + 1, panelState, session.PinnedFolders);
            pane.DropRequested += Pane_DropRequested;
            pane.PasteRequested += Pane_PasteRequested;
            pane.FileActionCompleted += RefreshAllPanes;
            pane.BeforeFileChange = () => !_inspectorOpen || Inspector.ConfirmLeavingMemo();
            pane.PreviewRequested += source => { ActivatePane(source); SetDockPreview(!_dockPreviewOpen); };
            pane.InspectorRequested += source => { ActivatePane(source); SetInspectorVisible(!_inspectorOpen); };
            pane.AiRequested += source =>
            {
                ActivatePane(source);
                OpenAiWindow();
            };
            pane.PinnedFoldersChanged += updated => PropagatePinnedFolders(pane, updated);
            pane.SelectionContextChanged += source =>
            {
                if (Array.IndexOf(_panes, source) == _activePane)
                {
                    SyncInspector();
                    SyncDockPreview();
                    AiPanel.UpdateSelectionSummary(source.ActiveFolder, source.SelectedPaths);
                    ScheduleSessionSave();
                }
            };
            pane.BlockingFileActionChanged += busy =>
            {
                _activeRecycleJobs += busy ? 1 : -1;
                if (!_working) CopyButton.IsEnabled = MoveButton.IsEnabled = _activeRecycleJobs == 0;
            };
            _panes[i] = pane;
            pane.GotKeyboardFocus += (_, _) => ActivatePane(pane);
            pane.PreviewMouseDown += (_, _) => ActivatePane(pane);
            _hosts[i] = BuildHost(i);
        }
        int requested = session.LayoutVersion >= 10 && session.VisiblePanels is >= 1 and <= 4
            ? session.VisiblePanels : 1;
        SplitLayoutPreset initialPreset = WorkspaceSplitLayoutEngine.ParsePresetName(session.SplitLayoutPreset, requested);
        _lastPresetByCount[requested] = initialPreset;
        ArrangePanels(requested, initialPreset);
        _activePane = Math.Clamp(session.ActivePane, 0, requested - 1);
        _layoutInitialized = true;
        SetInspectorVisible(_inspectorOpen);
        SetDockPreview(_dockPreviewOpen);
        Loaded += (_, _) =>
        {
            SyncInspector();
            SyncDockPreview();
            AiPanel.UpdateSelectionSummary(_panes[_activePane].ActiveFolder, _panes[_activePane].SelectedPaths);
        };
        AiPanel.GetContext = () => (_panes[_activePane].ActiveFolder, _panes[_activePane].SelectedPaths);
        AiPanel.GetOppositePaneFolder = ResolveOppositePaneFolder;
        AiPanel.SaveSummaryToActiveMemo = summaryText =>
        {
            if (!_inspectorOpen) SetInspectorVisible(true);
            SyncInspector();
            Inspector.AppendMemoAndSave(summaryText);
        };
        AiPanel.RevealResultFile = path => _ = _panes[_activePane].RevealFileAsync(path);
        AiPanel.ContextStillMatches = (folder, paths) =>
            string.Equals(_panes[_activePane].ActiveFolder, folder, StringComparison.OrdinalIgnoreCase) &&
            _panes[_activePane].SelectedPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        AiPanel.OllamaStatusChanged = UpdateOllamaHeaderButtonBadge;
        AiPanel.SetBusy = busy =>
        {
            _aiBusy = busy;
            foreach (var pane in _panes) { pane.SetFileActionsEnabled(!busy); pane.IsEnabled = !busy; }
            CopyButton.IsEnabled = MoveButton.IsEnabled = !busy && !_working && _activeRecycleJobs == 0;
            OneButton.IsEnabled = TwoButton.IsEnabled = ThreeButton.IsEnabled = FourButton.IsEnabled = !busy;
            if (AiConnectButton is not null) AiConnectButton.IsEnabled = !busy;
        };
        if (_persistSession) Loaded += async (_, _) => await AiPanel.CheckInterruptedAsync();
        AiPanel.FileWorkCompleted = RefreshAllPanes;
        AiPanel.CanStart = () => !_working && _activeRecycleJobs == 0 && !_aiBusy;
        AiPanel.BeforeFileChange = () => !_inspectorOpen || Inspector.ConfirmLeavingMemo();
        if (settingsWarning is not null)
            Dispatcher.BeginInvoke(new Action(() =>
                MessageBox.Show(settingsWarning, "설정 복원 알림", MessageBoxButton.OK, MessageBoxImage.Warning)));
    }

    public void ApplyControlPolicy(SunriseControlService.Policy policy) => _controlPolicy = policy;

    private void Notice_Click(object sender, RoutedEventArgs e) => OpenControlWindow();
    private void UpdateCheck_Click(object sender, RoutedEventArgs e) => OpenControlWindow();
    private void OpenControlWindow()
    {
        var window = new SunriseControlWindow(_controlPolicy) { Owner = this };
        window.ShowDialog();
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeReady || ThemeSelector.SelectedItem is not ComboBoxItem { Tag: string theme }) return;
        _currentTheme = ThemeManager.Normalize(theme);
        ApplySelectedTheme();
        ScheduleSessionSave();
    }

    private void ThemeSystem_Click(object sender, RoutedEventArgs e) => ThemeSelector.SelectedIndex = 0;
    private void ThemeLight_Click(object sender, RoutedEventArgs e) => ThemeSelector.SelectedIndex = 1;
    private void ThemeDark_Click(object sender, RoutedEventArgs e) => ThemeSelector.SelectedIndex = 2;
    private void ThemeCycle_Click(object sender, RoutedEventArgs e) => ThemeSelector.SelectedIndex = (ThemeSelector.SelectedIndex + 1) % 3;

    private void ArrangePanels(int count) => ArrangePanels(count, null);

    private void ArrangePanels(int count, SplitLayoutPreset? explicitPreset)
    {
        int previousCount = _visiblePanels;
        int clampedCount = WorkspaceSplitLayoutEngine.ClampVisiblePanels(count);
        PreserveAndSyncPaneFoldersOnSplitChange(previousCount, clampedCount);

        _visiblePanels = clampedCount;
        SplitLayoutPreset targetPreset = explicitPreset.HasValue
            ? WorkspaceSplitLayoutEngine.NormalizePresetForCount(clampedCount, explicitPreset.Value)
            : WorkspaceSplitLayoutEngine.NormalizePresetForCount(clampedCount, _lastPresetByCount[clampedCount]);
        _splitPreset = targetPreset;
        _lastPresetByCount[clampedCount] = targetPreset;

        if (OneButton is not null)
        {
            var choices = new[] { OneButton, TwoButton, ThreeButton, FourButton };
            for (int i = 0; i < choices.Length; i++)
            {
                bool selected = i + 1 == clampedCount;
                choices[i].SetResourceReference(Control.BackgroundProperty, selected ? "ThemeSplitActive" : "ThemeSplitInactive");
                choices[i].SetResourceReference(Control.ForegroundProperty, selected ? "ThemeSplitActiveText" : "ThemeSplitText");
                choices[i].SetResourceReference(Control.BorderBrushProperty, "PanelBorder");
                choices[i].FontWeight = i + 1 == clampedCount ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
        RefreshSplitPresetSelectorUi(clampedCount, targetPreset);

        int sourceIndex = Math.Clamp(SourceSelector.SelectedIndex, 0, clampedCount - 1);
        int targetIndex = Math.Clamp(DestinationSelector.SelectedIndex, 0, clampedCount - 1);
        if (targetIndex == sourceIndex) targetIndex = (sourceIndex + 1) % clampedCount;
        SourceSelector.Items.Clear();
        DestinationSelector.Items.Clear();
        for (int i = 0; i < clampedCount; i++)
        {
            SourceSelector.Items.Add($"탐색기 {i + 1}");
            DestinationSelector.Items.Add($"탐색기 {i + 1}");
        }
        SourceSelector.SelectedIndex = sourceIndex;
        DestinationSelector.SelectedIndex = targetIndex;

        SplitGridBlueprint blueprint = WorkspaceSplitLayoutEngine.BuildBlueprint(targetPreset);
        ApplyBlueprintToWorkspace(blueprint);

        TransferToolbar.Visibility = Visibility.Collapsed;
        TransferToolbarRow.Height = new GridLength(0);
        for (int i = 0; i < _panes.Length; i++) _panes[i].SetCompactMode(clampedCount > 1);
        _activePane = Math.Min(_activePane, clampedCount - 1);
        UpdateWorkspaceLayout();
        SyncInspector();
        SyncDockPreview();
        ScheduleSessionSave();
    }

    private void AddPane(Grid pane, int row, int column) => AttachPaneToWorkspace(pane, row, column);

    private async void AiConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_aiBusy || _working) return;
        OpenAiWindow();
        await AiPanel.PromptAndConnectOllamaAsync();
    }

    private void OpenAiWindow()
    {
        if (_aiWindow is null)
        {
            var window = new Window
            {
                Title = "Sunrise File Manager · AI 파일 검색 및 정리",
                Owner = this,
                Style = (Style)FindResource("AppWindow"),
                Width = 1060,
                Height = 790,
                MinWidth = 790,
                MinHeight = 620,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Content = AiPanel
            };
            window.Closing += (_, args) =>
            {
                if (_mainWindowClosing) return;
                args.Cancel = true;
                HideAiWindow();
            };
            window.PreviewKeyDown += (_, args) =>
            {
                if (args.Key == Key.F8 && !_aiBusy) { HideAiWindow(); args.Handled = true; }
            };
            _aiWindow = window;
        }
        _aiOpen = true;
        AiToggleButton.Background = (Brush)FindResource("Selected");
        AiPanel.UpdateSelectionSummary(_panes[_activePane].ActiveFolder, _panes[_activePane].SelectedPaths);
        _aiWindow.Show();
        _aiWindow.Activate();
        AiPanel.FocusCommand();
    }

    private void HideAiWindow()
    {
        if (_aiBusy) return;  // Never hide an active file-changing workflow.
        _aiOpen = false;
        _aiWindow?.Hide();
        AiToggleButton.Background = (Brush)FindResource("ControlBackground");
        if (IsActive && _panes[_activePane] is not null) _panes[_activePane].FocusFiles();
    }

    private void AiToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_aiOpen) HideAiWindow();
        else OpenAiWindow();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F8) { AiToggle_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && Inspector.SaveShortcutIfFocused()) e.Handled = true;
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.P) { SetDockPreview(!_dockPreviewOpen); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && !_working && !_aiBusy && e.OriginalSource is not TextBox)
        {
            if (e.Key == Key.D1) { One_Click(sender, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.D2) { Two_Click(sender, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.D3) { Three_Click(sender, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.D4) { Four_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        }
    }

    private void UpdateWorkspaceLayout()
    {
        if (WorkspaceShell.ActualWidth <= 0 || WorkspaceShell.ActualHeight <= 0) return;
        double inspectorPixels = _inspectorOpen
            ? Math.Max(295, InspectorColumn.ActualWidth) + 11 : 0;
        double previewPixels = _dockPreviewOpen
            ? Math.Max(260, PreviewColumn.ActualWidth) + 11 : 0;
        double fallbackWidth = Math.Max(1, WorkspaceShell.ActualWidth - inspectorPixels - previewPixels - 12);
        double viewportWidth = WorkspaceViewport.ActualWidth > 1 ? WorkspaceViewport.ActualWidth : fallbackWidth;
        double viewportHeight = WorkspaceViewport.ActualHeight > 1
            ? WorkspaceViewport.ActualHeight : Math.Max(1, WorkspaceShell.ActualHeight - 12);
        var layout = UiLayoutPolicy.Workspace(Math.Max(1, viewportWidth),
            Math.Max(1, viewportHeight), _visiblePanels, aiOpen: false);
        double targetWidth = Math.Max(layout.MinimumWidth, viewportWidth - 2);
        double targetHeight = Math.Max(layout.MinimumHeight, viewportHeight - 2);
        if (Math.Abs(Workspace.Width - targetWidth) > 1) Workspace.Width = targetWidth;
        if (Math.Abs(Workspace.Height - targetHeight) > 1) Workspace.Height = targetHeight;
    }

    private void SyncInspector()
    {
        if (!_inspectorOpen || _syncingInspector || Inspector is null ||
            _activePane >= _visiblePanels || _panes[_activePane] is null) return;
        _syncingInspector = true;
        try
        {
            if (!Inspector.SelectFile(_panes[_activePane].SelectedFile) && Inspector.CurrentFilePath is string previous)
                _panes[_activePane].RestoreSelection(previous);
        }
        finally { _syncingInspector = false; }
    }

    private void SyncDockPreview()
    {
        if (_dockPreviewOpen && _activePane < _visiblePanels && _panes[_activePane] is not null)
            PreviewPane.SelectFile(_panes[_activePane].SelectedFile);
    }

    private void SetInspectorVisible(bool visible)
    {
        if (!visible && !Inspector.ConfirmLeavingMemo()) return;
        if (!visible && _inspectorOpen && InspectorColumn.ActualWidth >= 295)
            _inspectorPreferredWidth = InspectorColumn.ActualWidth;
        _inspectorOpen = visible;
        Inspector.Visibility = InspectorSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InspectorColumn.Width = visible ? new GridLength(_inspectorPreferredWidth) : new GridLength(0);
        InspectorDividerColumn.Width = visible ? new GridLength(11) : new GridLength(0);
        InspectorToggleButton.Content = visible ? "정보·메모 닫기" : "상세정보 · 메모";
        InspectorToggleButton.Background = (Brush)FindResource(visible ? "Selected" : "ControlBackground");
        UpdateWorkspaceLayout();
        if (visible) SyncInspector();
        ScheduleSessionSave();
    }

    private void SetDockPreview(bool visible)
    {
        if (!visible && _dockPreviewOpen && PreviewColumn.ActualWidth >= 260)
            _previewPreferredWidth = PreviewColumn.ActualWidth;
        _dockPreviewOpen = visible;
        PreviewPane.SetPreviewVisible(visible);
        PreviewPane.Visibility = PreviewColumnSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.Width = visible ? new GridLength(_previewPreferredWidth) : new GridLength(0);
        PreviewDividerColumn.Width = visible ? new GridLength(11) : new GridLength(0);
        MainPreviewButton.Content = visible ? "◧ 미리보기 끄기" : "◧ 미리보기 켜기";
        MainPreviewButton.Background = (Brush)FindResource(visible ? "Selected" : "ControlBackground");
        UpdateWorkspaceLayout();
        if (visible) SyncDockPreview();
        ScheduleSessionSave();
    }

    private void InspectorToggle_Click(object sender, RoutedEventArgs e) => SetInspectorVisible(!_inspectorOpen);
    private void MainPreview_Click(object sender, RoutedEventArgs e) => SetDockPreview(!_dockPreviewOpen);

    private void One_Click(object sender, RoutedEventArgs e) => ArrangePanels(1, SplitLayoutPreset.Single);
    private void Two_Click(object sender, RoutedEventArgs e) =>
        ArrangePanels(2, _visiblePanels == 2 ? WorkspaceSplitLayoutEngine.CycleNextPreset(2, _splitPreset) : _lastPresetByCount[2]);
    private void Three_Click(object sender, RoutedEventArgs e) =>
        ArrangePanels(3, _visiblePanels == 3 ? WorkspaceSplitLayoutEngine.CycleNextPreset(3, _splitPreset) : _lastPresetByCount[3]);
    private void Four_Click(object sender, RoutedEventArgs e) =>
        ArrangePanels(4, _visiblePanels == 4 ? WorkspaceSplitLayoutEngine.CycleNextPreset(4, _splitPreset) : _lastPresetByCount[4]);

    private void SplitPresetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSplitSelector || SplitPresetSelector.SelectedItem is not ComboBoxItem { Tag: SplitLayoutPreset preset }) return;
        ArrangePanels(_visiblePanels, preset);
    }

    private void CycleSplitPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_visiblePanels <= 1) return;
        ArrangePanels(_visiblePanels, WorkspaceSplitLayoutEngine.CycleNextPreset(_visiblePanels, _splitPreset));
    }

    private void SwapPanes_Click(object sender, RoutedEventArgs e) => ExecuteSwapSelectedPanes();
    private void SyncPanes_Click(object sender, RoutedEventArgs e) => ExecuteSyncAllPanesToActive();

    private async void Copy_Click(object sender, RoutedEventArgs e) => await StartTransferAsync(TransferKind.Copy);
    private async void Move_Click(object sender, RoutedEventArgs e) => await StartTransferAsync(TransferKind.Move);

    private async Task StartTransferAsync(TransferKind kind)
    {
        if (_working || _aiBusy || _activeRecycleJobs > 0) return;
        int sourceIndex = SourceSelector.SelectedIndex;
        int targetIndex = DestinationSelector.SelectedIndex;
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex ||
            sourceIndex >= _visiblePanels || targetIndex >= _visiblePanels)
        {
            MessageBox.Show("원본 및 대상 탐색기를 서로 다르게 선택하세요.", "파일 작업",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        IReadOnlyList<string> paths = _panes[sourceIndex].SelectedPaths;
        string? target = _panes[targetIndex].ActiveFolder;
        if (paths.Count == 0 || target is null)
        {
            MessageBox.Show("원본 탐색기에서 파일 또는 폴더를 선택하고 대상 폴더를 지정하세요.",
                "파일 작업", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunTransferAsync(paths, target, kind, sourceIndex, targetIndex);
    }

    private async void Pane_DropRequested(ExplorerPane.ExplorerDropRequest request)
    {
        if (_working || _aiBusy || _activeRecycleJobs > 0) return;
        int source = Array.IndexOf(_panes, request.Source);
        int target = Array.IndexOf(_panes, request.Destination);
        if (source < 0 || target < 0 || source >= _visiblePanels || target >= _visiblePanels) return;
        await RunTransferAsync(request.Paths, request.TargetFolder, request.Kind, source, target);
    }

    private async void Pane_PasteRequested(ExplorerPane.ClipboardPasteRequest request)
    {
        int pane = Array.IndexOf(_panes, request.Pane);
        if (pane < 0 || pane >= _visiblePanels || _working || _aiBusy || _activeRecycleJobs > 0) return;
        await RunTransferAsync(request.Paths, request.TargetFolder, request.Kind, pane, pane);
    }

    private void RefreshAllPanes()
    {
        if (_working || _aiBusy || _activeRecycleJobs > 0) return;
        foreach (ExplorerPane pane in _panes) pane.RefreshCurrent();
    }

    private async Task RunTransferAsync(IReadOnlyList<string> paths, string target, TransferKind kind,
        int sourceIndex, int targetIndex)
    {
        if (_working || _aiBusy || _activeRecycleJobs > 0 || paths.Count == 0) return;
        if (kind == TransferKind.Move)
        {
            string normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
            paths = paths
                .Where(p => !string.Equals(
                    Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))),
                    normalizedTarget,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (paths.Count == 0)
            {
                JobStatus.Text = "선택한 항목이 이미 대상 폴더에 있습니다.";
                return;
            }
            if (_inspectorOpen && !Inspector.ConfirmLeavingMemo()) return;
        }
        string action = kind == TransferKind.Copy ? "복사" : "이동";
        CollisionPolicy collision = CollisionSelector.SelectedIndex == 1 ? CollisionPolicy.Rename : CollisionPolicy.Skip;
        string collisionDescription = collision == CollisionPolicy.Rename ? "겹치는 이름은 번호를 붙여 저장합니다." : "같은 이름이 존재하면 덮어쓰지 않고 건너뜁니다.";
        if (MessageBox.Show($"{paths.Count:N0}개 항목을 다음 폴더로 {action}할까요?\n\n{target}\n\n" +
            collisionDescription + " 접근 거부된 파일은 처리하지 않습니다.",
            action + " 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _working = true;
        CopyButton.IsEnabled = MoveButton.IsEnabled = false;
        SourceSelector.IsEnabled = DestinationSelector.IsEnabled = false;
        CollisionSelector.IsEnabled = false;
        OneButton.IsEnabled = TwoButton.IsEnabled = ThreeButton.IsEnabled = FourButton.IsEnabled = false;
        foreach (ExplorerPane pane in _panes) pane.SetFileActionsEnabled(false);
        CancelJobButton.IsEnabled = true;
        CancelJobButton.Visibility = JobProgress.Visibility = JobDetail.Visibility = Visibility.Visible;
        JobProgress.Value = 0;
        JobStatus.Text = $"{action} 계획 확인 중";
        JobDetail.Text = "선택한 항목을 검사하고 있습니다.";
        using var cancellation = new CancellationTokenSource();
        _transferCancellation = cancellation;
        using var progress = new UiLatestProgress<TransferProgress>(Dispatcher, item =>
        {
            if (!ReferenceEquals(_transferCancellation, cancellation)) return;
            double fraction = item.BytesTotal > 0
                ? (double)item.BytesDone / item.BytesTotal
                : item.Total > 0 ? (double)item.Completed / item.Total : 0;
            JobProgress.Value = Math.Clamp(fraction * 100, 0, 100);
            JobStatus.Text = $"{action}: {item.Completed}/{item.Total}개 · {JobProgress.Value:F0}% · {item.Phase}";
            JobDetail.Text = item.CurrentName;
        });
        try
        {
            TransferResult result = await TransferService.RunAsync(paths, target, kind, progress, cancellation.Token, collision);
            progress.Dispose();
            JobStatus.Text = result.Summary;
            JobDetail.Text = result.Errors.Count == 0 ? "작업 결과를 확인했습니다." : result.Errors[0];
            if (!result.Canceled && result.Failed == 0) JobProgress.Value = 100;
            if (kind == TransferKind.Move && result.Completed > 0)
            {
                var movedPairs = paths
                    .Select(src => (OldPath: src, NewPath: Path.Combine(target, Path.GetFileName(Path.TrimEndingDirectorySeparator(src)))))
                    .Where(p => File.Exists(p.NewPath) || Directory.Exists(p.NewPath))
                    .ToArray();
                if (movedPairs.Length > 0)
                    _panes[targetIndex].RecordCompletedMoves(movedPairs);
            }
            if (result.Errors.Count > 0)
            {
                string firstErrors = string.Join("\n", result.Errors.Take(8));
                MessageBox.Show(result.Summary + "\n\n" + firstErrors +
                    (result.Errors.Count > 8 ? $"\n... 외 {result.Errors.Count - 8}건" : ""),
                    "파일 작업 결과", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            _panes[sourceIndex].RefreshCurrent();
            if (targetIndex != sourceIndex) _panes[targetIndex].RefreshCurrent();
        }
        catch (OperationCanceledException)
        {
            progress.Dispose();
            JobStatus.Text = "작업 준비 중 취소됨 · 원본은 변경되지 않았습니다.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or OverflowException)
        {
            progress.Dispose();
            JobStatus.Text = "작업을 시작하지 못했습니다.";
            JobDetail.Text = exception.Message;
            MessageBox.Show(exception.Message, "파일 작업 제한", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _transferCancellation = null;
            _working = false;
            CopyButton.IsEnabled = MoveButton.IsEnabled = _activeRecycleJobs == 0;
            SourceSelector.IsEnabled = DestinationSelector.IsEnabled = true;
            CollisionSelector.IsEnabled = true;
            OneButton.IsEnabled = TwoButton.IsEnabled = ThreeButton.IsEnabled = FourButton.IsEnabled = true;
            foreach (ExplorerPane pane in _panes) pane.SetFileActionsEnabled(true);
            CancelJobButton.IsEnabled = false;
            CancelJobButton.Visibility = JobProgress.Visibility = JobDetail.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelJob_Click(object sender, RoutedEventArgs e)
    {
        _transferCancellation?.Cancel();
        JobDetail.Text = "현재 작업의 안전한 취소를 요청했습니다.";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_activeRecycleJobs > 0)
        {
            e.Cancel = true;
            MessageBox.Show("휴지통 이동이 진행 중입니다. 완료된 다음 종료할 수 있습니다.",
                "파일 작업 진행 중", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_working || _aiBusy)
        {
            e.Cancel = true;
            if (MessageBox.Show("파일 작업이 진행 중입니다. 작업을 취소하고 종료하시겠습니까?\n" +
                "취소 처리가 끝난 후 창을 다시 닫을 수 있습니다.", "작업 진행 중",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _transferCancellation?.Cancel();
                AiPanel.Cancel();
            }
            return;
        }
        if (!Inspector.ConfirmLeavingMemo()) { e.Cancel = true; return; }
        _sessionSaveTimer.Stop();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemThemeChanged;
        if (_persistSession) TrySaveSession(showError: true);
        foreach (var pane in _panes) pane.CancelPendingOperations();
        AiPanel.Cancel();
        _mainWindowClosing = true;
        _aiWindow?.Close();
        PreviewPane.DisposePending();
    }
    private void ScheduleSessionSave()
    {
        if (!_persistSession || !_layoutInitialized) return;
        _sessionSaveTimer.Stop();
        _sessionSaveTimer.Start();
    }

    private void TrySaveSession(bool showError)
    {
        try
        {
            Rect rect = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            SessionStore.Save(new AppSession
            {
                VisiblePanels = _visiblePanels,
                ActivePane = _activePane,
                SplitLayoutPreset = _splitPreset.ToString(),
                LayoutVersion = 20,
                Theme = _currentTheme,
                InspectorVisible = _inspectorOpen,
                DockPreviewVisible = _dockPreviewOpen,
                InspectorWidth = _inspectorOpen && InspectorColumn.ActualWidth >= 270 ? InspectorColumn.ActualWidth : _inspectorPreferredWidth,
                PreviewWidth = _dockPreviewOpen && PreviewColumn.ActualWidth >= 260 ? PreviewColumn.ActualWidth : _previewPreferredWidth,
                WindowWidth = rect.Width,
                WindowHeight = rect.Height,
                WindowLeft = rect.Left,
                WindowTop = rect.Top,
                IsMaximized = WindowState == WindowState.Maximized,
                PinnedFolders = _panes[_activePane].GetPinnedFolders().ToList(),
                Panels = _panes.Select(pane => pane.GetSession()).ToList()
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (showError)
                MessageBox.Show($"화면 설정을 저장하지 못했습니다.\n{ex.Message}", "Sunrise File Manager",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

}
