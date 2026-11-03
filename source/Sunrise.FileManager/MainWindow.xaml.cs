using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private readonly CloudWorkspacePane?[] _cloudSlots = new CloudWorkspacePane?[4];
    private readonly ComboBox?[] _cloudLocationSelectors = new ComboBox?[4];
    private readonly string[] _paneLocationModes = ["Local", "Local", "Local", "Local"];
    private bool _switchingCloudPane;
    private bool _crossPaneCopyBusy;
    private bool IsCloudPaneActive => _activePane >= 0 && _activePane < _visiblePanels &&
                                      _paneLocationModes[_activePane] != "Local";
    private bool AnyCloudOperationBusy => _crossPaneCopyBusy || _cloudSlots.Any(slot => slot?.IsBusy == true) ||
                                           _cloudDriveWindow?.IsCloudBusy == true;

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
    private bool _updateExitApproved;
    internal bool UpdateShutdownAccepted => _mainWindowClosing;

    internal bool ConfirmUpdateExit()
    {
        if (_working || _aiBusy || _activeRecycleJobs > 0 || AnyCloudOperationBusy)
        {
            foreach (var pane in _panes) pane?.CancelLocalFileJob();
            _transferCancellation?.Cancel();
            AiPanel.Cancel();
            MessageBox.Show("진행 중인 작업에 취소를 요청했습니다. 이름 변경·이동·휴지통 작업은 완료를 기다려야 합니다.\n처리가 끝나면 종료 또는 업데이트를 다시 선택하십시오.",
                "작업 진행 중", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        _updateExitApproved = MemoPanel.ConfirmLeavingMemo();
        return _updateExitApproved;
    }
    private bool _memoOpen = true;
    private bool _detailsOpen = true;
    private bool _singleUtilityPanel;
    private string _lastUtilityPanel = "Memo";
    private double _detailsPreferredWidth = 300;
    private bool _dockPreviewOpen;
    private double _memoPreferredWidth = 330;
    private double _previewPreferredWidth = 350;
    private bool _syncingMemo;
    private bool _syncingPinned;
    private bool _themeReady;
    private string _currentTheme = "System";
    private string? _committedLightAccentHex;
    private bool _syncingThemeSelector;
    private static bool _lightPickerOpen;
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
            "System" => "PC 테마",
            "Dark" => "다크",
            _ => "라이트"
        };
        foreach (var pane in _panes) pane?.RefreshThemeColors();
        foreach (var other in Application.Current.Windows.OfType<MainWindow>())
        {
            if (ReferenceEquals(other, this)) continue;
            other.ThemeSystemMenuItem.IsChecked = other._currentTheme == "System";
            other.ThemeLightMenuItem.IsChecked = other._currentTheme == "Light";
            other.ThemeDarkMenuItem.IsChecked = other._currentTheme == "Dark";
            other.HeaderThemeButton.Content = other._currentTheme switch
            {
                "System" => "PC 테마",
                "Dark" => "다크",
                _ => "라이트"
            };
            foreach (var pane in other._panes) pane?.RefreshThemeColors();
        }
    }
    // R35: previews are intentionally transient. All autosaves use the committed
    // value, never the temporarily rendered ThemeManager.LightAccentHex.
    internal void PreviewLightAccentForWorkspace(string? colorHex)
    {
        ThemeManager.ConfigureLightAccent(colorHex);
        ApplySelectedTheme();
    }

    private bool CommitLightAccentForWorkspace(string? colorHex)
    {
        // ThemeManager/App.Resources are process-wide. Commit must synchronize every
        // workspace before the single session writer persists the accepted value.
        var workspaces = Application.Current.Windows.OfType<MainWindow>().ToArray();
        string? old = _committedLightAccentHex;
        MainWindow writer = workspaces.FirstOrDefault(w => w._persistSession) ?? this;
        try
        {
            PreviewLightAccentForWorkspace(colorHex);
            string? accepted = ThemeManager.LightAccentHex;
            foreach (var workspace in workspaces) workspace._committedLightAccentHex = accepted;
            if (writer._layoutInitialized && !writer.TrySaveSession(showError: true))
            {
                foreach (var workspace in workspaces) workspace._committedLightAccentHex = old;
                PreviewLightAccentForWorkspace(old);
                return false;
            }
            writer.ScheduleSessionSave();
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            foreach (var workspace in workspaces) workspace._committedLightAccentHex = old;
            PreviewLightAccentForWorkspace(old);
            MessageBox.Show($"색상을 적용할 수 없습니다.\n{error.Message}", "맞춤 색상", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void LightAccentSettings_Click(object sender, RoutedEventArgs e)
    {
        if (ThemeManager.IsDark)
        {
            MessageBox.Show("맞춤 색상은 라이트 모드에서만 사용합니다. 먼저 라이트 모드로 전환해 주세요.",
                "라이트 맞춤 색상", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // A modal dialog disables only its owner: reject concurrent pickers in
        // other workspaces to avoid overlapping preview/rollback transactions.
        if (_lightPickerOpen)
        {
            MessageBox.Show("다른 창에서 색상을 편집 중입니다. 해당 창에서 적용하거나 취소해 주세요.",
                "라이트 맞춤 색상", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _lightPickerOpen = true;
        try
        {
            var dialog = new LightAccentPickerWindow(
                _committedLightAccentHex, PreviewLightAccentForWorkspace, CommitLightAccentForWorkspace)
            { Owner = this };
            dialog.ShowDialog();
        }
        finally { _lightPickerOpen = false; }
    }

    // An additional window may become the sole session owner after the original
    // main window closes; preserve the save contract without racing two writers.
    internal void AssumeSessionPersistence()
    {
        if (_persistSession) return;
        _persistSession = true;
        ScheduleSessionSave();
    }

    private bool _persistSession;
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
        // Nested explorer controls may request BringIntoView when focus/selection changes.
        // Their own ScrollViewers handle in-pane movement; do not let those requests
        // propagate to the workspace-level scrollbars and pan every explorer pane.
        Workspace.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler((_, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, Workspace)) e.Handled = true;
            }));
        AiPanel = new AiCommandPanel();
        _sessionSaveTimer.Tick += (_, _) =>
        {
            _sessionSaveTimer.Stop();
            if (_persistSession && _layoutInitialized) TrySaveSession(showError: false);
        };
        WorkspaceShell.SizeChanged += (_, _) => UpdateWorkspaceLayout();
        AuxiliaryLayoutViewport.SizeChanged += (_, _) => UpdateWorkspaceLayout();
        WorkspaceViewport.SizeChanged += (_, _) => UpdateWorkspaceLayout();
        WorkspaceViewport.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) UpdateWorkspaceLayout();
        };
        MemoPanel.SizeChanged += (_, _) => UpdateWorkspaceLayout();
        FileDetails.SizeChanged += (_, _) => UpdateWorkspaceLayout();
        PreviewPane.SizeChanged += (_, _) => UpdateWorkspaceLayout();
        string? settingsWarning = null;
        AppSession session = initialSession ?? SessionStore.Load(out settingsWarning);
        foreach (int scale in UiScalePolicy.Choices) UiScaleSelector.Items.Add(new ComboBoxItem { Content = $"{scale}%", Tag = scale });
        UiScaleSelector.SelectedIndex = Array.IndexOf(UiScalePolicy.Choices, UiScalePolicy.Normalize(session.UiScalePercent));
        UiScaleService.Set(session.UiScalePercent);
        RestoreUserPreferences(session);
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
        if (session.LayoutVersion >= 23)
        {
            if (session.MemoPanelWidth is >= 295 and <= 800) _memoPreferredWidth = session.MemoPanelWidth;
            if (session.DetailsPanelWidth is >= 280 and <= 800) _detailsPreferredWidth = session.DetailsPanelWidth;
        }
        else if (session.InspectorWidth is >= 295 and <= 800) _memoPreferredWidth = session.InspectorWidth;
        if (session.PreviewWidth is >= 260 and <= 900) _previewPreferredWidth = session.PreviewWidth;
        _committedLightAccentHex = session.LightAccentHex;
        ThemeManager.ConfigureLightAccent(_committedLightAccentHex);
        _currentTheme = ThemeManager.Normalize(session.Theme);
        ThemeSelector.SelectedIndex = _currentTheme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        ApplySelectedTheme();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        _themeReady = true;
        // v1.5.6 stored a combined inspector. Preserve both sections when migrating old sessions.
        // A-layout first launch: start with the whole file workspace, without discarding a real legacy session.
        bool firstLaunch = session.LayoutVersion == 1 && (session.Panels?.Count ?? 0) == 0;
        _memoOpen = !firstLaunch && (session.LayoutVersion >= 23 ? session.MemoPanelVisible : session.LayoutVersion < 18 || session.InspectorVisible);
        _detailsOpen = !firstLaunch && (session.LayoutVersion >= 23 ? session.DetailsPanelVisible : session.LayoutVersion < 18 || session.InspectorVisible);
        _singleUtilityPanel = session.LayoutVersion >= 25 && session.SingleUtilityPanel;
        _lastUtilityPanel = session.LayoutVersion >= 25 ? session.SingleUtilityActive : "Memo";
        _dockPreviewOpen = session.LayoutVersion >= 18 && session.DockPreviewVisible;
        // A saved single-panel session exposes precisely one auxiliary view on startup.
        // Prefer the previously active view when it is still enabled; no file/memo is discarded.
        if (_singleUtilityPanel)
        {
            string keeper = _lastUtilityPanel switch
            {
                "Preview" when _dockPreviewOpen => "Preview",
                "Details" when _detailsOpen => "Details",
                "Memo" when _memoOpen => "Memo",
                _ when _memoOpen => "Memo",
                _ when _detailsOpen => "Details",
                _ when _dockPreviewOpen => "Preview",
                _ => ""
            };
            _memoOpen &= keeper == "Memo";
            _detailsOpen &= keeper == "Details";
            _dockPreviewOpen &= keeper == "Preview";
        }
        MemoPanel.CloseRequested += () => SetMemoVisible(false);
        FileDetails.CloseRequested += () => SetDetailsPanelVisible(false);
        FileDetails.AttributesChanged += () => { if (_activePane < _panes.Length) _panes[_activePane]?.RefreshCurrent(); };
        PreviewPane.CloseRequested += () => SetDockPreview(false);
        AiPanel.CloseRequested += HideAiWindow;
        MemoPanel.MemoSaved += () => { if (_activePane < _panes.Length) _panes[_activePane]?.RefreshCurrent(); };
        MemoPanel.RequestOllamaSetup = () => AiPanel.PromptAndConnectOllamaAsync();
        for (int i = 0; i < _panes.Length; i++)
        {
            var pane = new ExplorerPane();
            PanelSession? panelState = session.Panels?.ElementAtOrDefault(i);
            pane.Initialize(i + 1, panelState, session.PinnedFolders);
            pane.DropRequested += Pane_DropRequested;
            pane.CloudDragDropRequested += Pane_CloudDragDropRequested;
            pane.PasteRequested += Pane_PasteRequested;
            pane.CloudPasteRequested += Pane_CloudPasteRequested;
            pane.FileActionCompleted += RefreshAllPanes;
            pane.SettingsChanged += ScheduleSessionSave;
            pane.BeforeFileChange = () => _activeRecycleJobs == 0 && (!_memoOpen || MemoPanel.ConfirmLeavingMemo());
            pane.PreviewRequested += source => { ActivatePane(source); SetDockPreview(!_dockPreviewOpen); };
            pane.DetailsRequested += source =>
            {
                ActivatePane(source);
                if (ReferenceEquals(_panes[_activePane], source)) SetDetailsPanelVisible(!_detailsOpen);
            };
            pane.MemoRequested += source =>
            {
                ActivatePane(source);
                if (ReferenceEquals(_panes[_activePane], source)) SetMemoVisible(!_memoOpen);
            };
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
                    SyncMemo();
                    UpdateActivePaneIndicator();
                    SyncDetailsPanel();
                    SyncDockPreview();
                    AiPanel.UpdateSelectionSummary(source.ActiveFolder, source.SelectedPaths);
                    ScheduleSessionSave();
                }
            };
            pane.BlockingFileActionChanged += busy =>
            {
                _activeRecycleJobs += busy ? 1 : -1;
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
        UpdateActivePaneIndicator();
        SetDetailsPanelVisible(_detailsOpen);
        SetMemoVisible(_memoOpen);
        SetDockPreview(_dockPreviewOpen);
        UpdateToolRailVisuals();
        Loaded += (_, _) =>
        {
            SyncMemo();
            SyncDetailsPanel();
            SyncDockPreview();
            AiPanel.UpdateSelectionSummary(_panes[_activePane].ActiveFolder, _panes[_activePane].SelectedPaths);
        };
        AiPanel.GetContext = () => (_panes[_activePane].ActiveFolder, _panes[_activePane].SelectedPaths);
        AiPanel.GetOppositePaneFolder = ResolveOppositePaneFolder;
        AiPanel.SaveSummaryToActiveMemo = summaryText =>
        {
            if (!_memoOpen) SetMemoVisible(true);
            SyncMemo();
            // A cancelled dirty-memo switch must not append AI output to the previous file.
            if (string.Equals(MemoPanel.CurrentFilePath, _panes[_activePane].SelectedFile?.FullPath, StringComparison.OrdinalIgnoreCase))
                MemoPanel.AppendMemoAndSave(summaryText);
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
            OneButton.IsEnabled = TwoButton.IsEnabled = ThreeButton.IsEnabled = FourButton.IsEnabled = !busy;
            if (AiConnectButton is not null) AiConnectButton.IsEnabled = !busy;
        };
        if (_persistSession) Loaded += async (_, _) => await AiPanel.CheckInterruptedAsync();
        AiPanel.FileWorkCompleted = RefreshAllPanes;
        AiPanel.CanStart = () => !_working && _activeRecycleJobs == 0 && !_aiBusy;
        AiPanel.BeforeFileChange = () => !_memoOpen || MemoPanel.ConfirmLeavingMemo();
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

    private void UiScale_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UiScaleSelector.SelectedItem is not ComboBoxItem { Tag: int percent }) return;
        UiScaleService.Set(percent);
        if (_layoutInitialized) { UpdateWorkspaceLayout(); ScheduleSessionSave(); }
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeReady || _syncingThemeSelector || ThemeSelector.SelectedItem is not ComboBoxItem { Tag: string theme }) return;
        // Palette and selected mode are application-wide, not per-window.
        // Reflect the preference in every open window without recursive events.
        string requested = ThemeManager.Normalize(theme);
        int selectedIndex = requested switch { "Light" => 1, "Dark" => 2, _ => 0 };
        var workspaces = Application.Current.Windows.OfType<MainWindow>().ToArray();
        foreach (var workspace in workspaces)
        {
            workspace._currentTheme = requested;
            workspace._syncingThemeSelector = true;
            try { workspace.ThemeSelector.SelectedIndex = selectedIndex; }
            finally { workspace._syncingThemeSelector = false; }
        }
        ApplySelectedTheme();
        (workspaces.FirstOrDefault(w => w._persistSession) ?? this).ScheduleSessionSave();
    }

    private void ThemeSystem_Click(object sender, RoutedEventArgs e) => ThemeSelector.SelectedIndex = 0;
    private void ThemeLight_Click(object sender, RoutedEventArgs e) => ThemeSelector.SelectedIndex = 1;
    private void ThemeDark_Click(object sender, RoutedEventArgs e) => ThemeSelector.SelectedIndex = 2;
    private void ThemeCycle_Click(object sender, RoutedEventArgs e) => ThemeSelector.SelectedIndex = (ThemeSelector.SelectedIndex + 1) % 3;

    private void ArrangePanels(int count) => ArrangePanels(count, null);

    private void ArrangePanels(int count, SplitLayoutPreset? explicitPreset)
    {
        // A running transfer, AI mutation, or recycle operation must keep the pane
        // geometry and working selection stable. Startup layout is exempt.
        if (_layoutInitialized && !CanChangeSplitLayout) return;
        // A count reduction can swap a pane working state: never do that before a dirty memo is confirmed.
        if (_layoutInitialized && count < _visiblePanels && _memoOpen && !MemoPanel.ConfirmLeavingMemo()) return;
        CaptureWorkspaceRatios();
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
                choices[i].Tag = selected ? "True" : "False";
                choices[i].FontWeight = i + 1 == clampedCount ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
        RefreshSplitPresetSelectorUi(clampedCount, targetPreset);

        SplitGridBlueprint blueprint = WorkspaceSplitLayoutEngine.BuildBlueprint(targetPreset);
        ApplyBlueprintToWorkspace(blueprint);

        for (int i = 0; i < _panes.Length; i++) _panes[i].SetCompactMode(clampedCount > 1);
        _activePane = Math.Min(_activePane, clampedCount - 1);
        UpdateActivePaneIndicator();
        UpdateWorkspaceLayout();
        SyncMemo();
        SyncDetailsPanel();
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
                Width = Math.Clamp(_restoredPreferences.AiWindowWidth, 790, 12000),
                Height = Math.Clamp(_restoredPreferences.AiWindowHeight, 620, 9000),
                MinWidth = 790,
                MinHeight = 620,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Content = AiPanel
            };
            if (_restoredPreferences.AiWindowLeft is double savedLeft && _restoredPreferences.AiWindowTop is double savedTop &&
                savedLeft >= SystemParameters.VirtualScreenLeft && savedLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
                savedTop >= SystemParameters.VirtualScreenTop && savedTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60)
            { window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = savedLeft; window.Top = savedTop; }
            if (_restoredPreferences.AiWindowMaximized) window.Loaded += (_, _) => window.WindowState = WindowState.Maximized;
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
        UpdateToolRailVisuals();
        AiPanel.UpdateSelectionSummary(_panes[_activePane].ActiveFolder, _panes[_activePane].SelectedPaths);
        _aiWindow.Show();
        _aiWindow.Activate();
        AiPanel.FocusCommand();
        ScheduleSessionSave();
    }

    private void HideAiWindow()
    {
        if (_aiBusy) return;  // Never hide an active file-changing workflow.
        _aiOpen = false;
        _aiWindow?.Hide();
        ScheduleSessionSave();
        AiToggleButton.Background = (Brush)FindResource("ControlBackground");
        UpdateToolRailVisuals();
        if (IsActive && _activePane >= 0 && _activePane < _visiblePanels)
        {
            // The local ExplorerPane remains in memory when its slot shows cloud files.
            // Never send keyboard focus into that hidden local list.
            if (IsCloudPaneActive) _cloudSlots[_activePane]?.FocusFiles();
            else _panes[_activePane]?.FocusFiles();
        }
    }

    private void AiToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_aiOpen) HideAiWindow();
        else OpenAiWindow();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F6 && !e.IsRepeat &&
            (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift) &&
            Keyboard.FocusedElement is not ComboBox { IsDropDownOpen: true })
        {
            if (CycleWorkspaceKeyboardFocus(Keyboard.Modifiers == ModifierKeys.Shift)) e.Handled = true;
            return;
        }
        if (e.Key == Key.F8) { AiToggle_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.W) { NewWindow_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && MemoPanel.SaveShortcutIfFocused()) e.Handled = true;
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.P) { SetDockPreview(!_dockPreviewOpen); e.Handled = true; }
        else if (Keyboard.Modifiers == (ModifierKeys.Alt | ModifierKeys.Shift) && e.Key == Key.P) { SetDetailsPanelVisible(!_detailsOpen); e.Handled = true; }
        else if (Keyboard.Modifiers == (ModifierKeys.Alt | ModifierKeys.Shift) && e.Key == Key.M) { SetMemoVisible(!_memoOpen); e.Handled = true; }
        else if (Keyboard.Modifiers == (ModifierKeys.Alt | ModifierKeys.Shift) && e.Key == Key.F) { FocusWorkspace_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (CanRouteSplitShortcut(e))
        {
            // Split shortcuts belong to the file workspace. In preview Ctrl+1 is
            // already the existing fit-width command; text editors also own their keys.
            if (e.Key is Key.D1 or Key.NumPad1) { ApplyLastLayoutForCount(1); e.Handled = true; }
            else if (e.Key is Key.D2 or Key.NumPad2) { ApplyLastLayoutForCount(2); e.Handled = true; }
            else if (e.Key is Key.D3 or Key.NumPad3) { ApplyLastLayoutForCount(3); e.Handled = true; }
            else if (e.Key is Key.D4 or Key.NumPad4) { ApplyLastLayoutForCount(4); e.Handled = true; }
        }
    }

    private bool CanRouteSplitShortcut(KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || !CanChangeSplitLayout) return false;
        // Window.PreviewKeyDown tunnels before the nested preview/editor receives it.
        // Check keyboard focus, not just OriginalSource, which can be a template child.
        if (MemoPanel.IsKeyboardFocusWithin || PreviewPane.IsKeyboardFocusWithin || FileDetails.IsKeyboardFocusWithin)
            return false;
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox ||
            Keyboard.FocusedElement is ComboBox { IsEditable: true }) return false;
        return e.OriginalSource is not TextBoxBase and not PasswordBox;
    }

    private void UpdateWorkspaceLayout()
    {
        if (WorkspaceShell.ActualHeight <= 0 || AuxiliaryLayoutViewport.ActualWidth <= 0) return;
        // R54: The inspector can be wider than a small/DPI-reduced window.
        // Preserve its minimum/readable width and the file viewport; scroll the
        // combined surface instead of silently hiding panels or changing session widths.
        double inspectorPixels = (_memoOpen ? Math.Max(295, MemoColumn.ActualWidth > 1 ? MemoColumn.ActualWidth : _memoPreferredWidth) + 11 : 0)
            + (_detailsOpen ? Math.Max(280, DetailsPanelColumn.ActualWidth > 1 ? DetailsPanelColumn.ActualWidth : _detailsPreferredWidth) + 11 : 0);
        double previewPixels = _dockPreviewOpen
            ? Math.Max(260, PreviewColumn.ActualWidth > 1 ? PreviewColumn.ActualWidth : _previewPreferredWidth) + 11 : 0;
        double availableShellWidth = Math.Max(1, AuxiliaryLayoutViewport.ActualWidth);
        double requiredShellWidth = Math.Max(availableShellWidth,
            WorkspaceSplitLayoutEngine.MinimumPaneWidth + inspectorPixels + previewPixels);
        if (!double.IsFinite(WorkspaceShell.Width) || Math.Abs(WorkspaceShell.Width - requiredShellWidth) > 1)
            WorkspaceShell.Width = requiredShellWidth;
        double fallbackWidth = Math.Max(1, requiredShellWidth - inspectorPixels - previewPixels - 12);
        double viewportWidth = WorkspaceViewport.ViewportWidth > 1 && double.IsFinite(WorkspaceViewport.ViewportWidth)
            ? WorkspaceViewport.ViewportWidth : WorkspaceViewport.ActualWidth > 1 ? WorkspaceViewport.ActualWidth : fallbackWidth;
        double viewportHeight = WorkspaceViewport.ViewportHeight > 1 && double.IsFinite(WorkspaceViewport.ViewportHeight)
            ? WorkspaceViewport.ViewportHeight : WorkspaceViewport.ActualHeight > 1 ? WorkspaceViewport.ActualHeight : Math.Max(1, WorkspaceShell.ActualHeight - 12);
        var minimum = WorkspaceSplitLayoutEngine.GetMinimumWorkspaceSize(_splitPreset);
        double targetWidth = Math.Max(minimum.Width, viewportWidth - 2);
        double targetHeight = Math.Max(minimum.Height, viewportHeight - 2);
        if (Math.Abs(Workspace.Width - targetWidth) > 1) Workspace.Width = targetWidth;
        if (Math.Abs(Workspace.Height - targetHeight) > 1) Workspace.Height = targetHeight;

        // R53: Surface the existing split-layout fit assessment without altering the
        // approved minimum pane geometry or stealing space from the file viewport.
        var fit = WorkspaceSplitLayoutEngine.AssessViewportFit(_splitPreset, viewportWidth, viewportHeight);
        bool scrollX = fit.NeedsHorizontalScroll == true;
        bool scrollY = fit.NeedsVerticalScroll == true;
        bool auxiliaryScrollX = requiredShellWidth > availableShellWidth + 1;
        if (auxiliaryScrollX) scrollX = true;
        string cue = scrollX && scrollY ? "↔↕" : scrollX ? "↔" : scrollY ? "↕" : "";
        if (WorkspaceOverflowIndicator.Text != cue)
        {
            WorkspaceOverflowIndicator.Text = cue;
            WorkspaceOverflowIndicator.Visibility = cue.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        string fitDescription = auxiliaryScrollX && !scrollY && fit.NeedsHorizontalScroll != true
            ? "보조 패널이 현재 화면 폭보다 넓습니다" : fit.DisplayText;
        string hint = cue.Length == 0 ? "현재 분할 배치가 작업 영역에 맞습니다."
            : $"{fitDescription}{(auxiliaryScrollX ? ". 하단 가로 스크롤로 메모·상세정보·미리보기를 확인할 수 있습니다" : "")}. 오른쪽 도구의 '파일 작업 집중'(Alt+Shift+F)으로 보조 패널을 안전하게 닫을 수 있습니다.";
        if (!Equals(WorkspaceOverflowIndicator.ToolTip, hint))
        {
            WorkspaceOverflowIndicator.ToolTip = hint;
            System.Windows.Automation.AutomationProperties.SetHelpText(WorkspaceViewport, hint);
        }
    }

    private void SyncMemo()
    {
        if (!_memoOpen || _syncingMemo || MemoPanel is null ||
            _activePane >= _visiblePanels || _panes[_activePane] is null) return;
        if (IsCloudPaneActive) { MemoPanel.SelectFile(null); return; }
        _syncingMemo = true;
        try
        {
            if (!MemoPanel.SelectFile(_panes[_activePane].SelectedFile) && MemoPanel.CurrentFilePath is string previous)
            {
                _panes[_activePane].RestoreSelection(previous);
                return;
            }
        }
        finally { _syncingMemo = false; }
    }

    private void UpdateActivePaneIndicator()
    {
        if (ActivePaneIndicator is null) return;
        for (int i = 0; i < _panes.Length; ++i)
            _panes[i]?.SetActivePaneIndicator(i == _activePane && i < _visiblePanels);
        ActivePaneIndicator.Text = $"{_activePane + 1}/{_visiblePanels}";
        if (IsCloudPaneActive)
        {
            ActivePaneIndicator.ToolTip = $"탐색기 {_activePane + 1} · {_paneLocationModes[_activePane]} 원격 파일";
            return;
        }
        string? path = _activePane < _visiblePanels ? _panes[_activePane]?.SelectedFile?.FullPath : null;
        ActivePaneIndicator.ToolTip = path is null
            ? $"탐색기 {_activePane + 1}이 활성화되었습니다. 파일을 선택하면 우측 공용 패널이 갱신됩니다."
            : $"탐색기 {_activePane + 1} · {path}";
    }

    private void SyncDetailsPanel()
    {
        if (_detailsOpen && _activePane < _visiblePanels && _panes[_activePane] is not null)
        {
            if (IsCloudPaneActive) FileDetails.SelectFile(null);
            else FileDetails.SelectFile(_panes[_activePane].SelectedFile);
        }
    }

    private void SyncDockPreview()
    {
        if (_dockPreviewOpen && _activePane < _visiblePanels && _panes[_activePane] is not null)
        {
            if (IsCloudPaneActive) PreviewPane.SelectFile(null);
            else PreviewPane.SelectFile(_panes[_activePane].SelectedFile);
        }
    }

    private void SetMemoVisible(bool visible)
    {
        if (visible && !_memoOpen && _singleUtilityPanel)
        {
            if (_detailsOpen) SetDetailsPanelVisible(false);
            if (_dockPreviewOpen) SetDockPreview(false);
        }
        if (!visible && !MemoPanel.ConfirmLeavingMemo()) return;
        if (visible) _lastUtilityPanel = "Memo";
        if (!visible && _memoOpen && MemoColumn.ActualWidth >= 295)
            _memoPreferredWidth = MemoColumn.ActualWidth;
        _memoOpen = visible;
        MemoPanel.Visibility = MemoSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MemoColumn.Width = visible ? new GridLength(_memoPreferredWidth) : new GridLength(0);
        MemoDividerColumn.Width = visible ? new GridLength(11) : new GridLength(0);
        MemoToggleButton.Content = visible ? "메모 닫기" : "스마트 파일 메모";
        MemoToggleButton.Background = (Brush)FindResource(visible ? "Selected" : "ControlBackground");
        MemoMenuItem.IsChecked = visible;
        UpdateToolRailVisuals();
        UpdateWorkspaceLayout();
        if (visible) SyncMemo();
        ScheduleSessionSave();
    }

    private void SetDetailsPanelVisible(bool visible)
    {
        if (visible && !_detailsOpen && _singleUtilityPanel)
        {
            if (_memoOpen) { SetMemoVisible(false); if (_memoOpen) return; }
            if (_dockPreviewOpen) SetDockPreview(false);
        }
        if (visible) _lastUtilityPanel = "Details";
        if (!visible && _detailsOpen && DetailsPanelColumn.ActualWidth >= 280)
            _detailsPreferredWidth = DetailsPanelColumn.ActualWidth;
        _detailsOpen = visible;
        FileDetails.Visibility = DetailsPanelSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DetailsPanelColumn.Width = visible ? new GridLength(_detailsPreferredWidth) : new GridLength(0);
        DetailsPanelDividerColumn.Width = visible ? new GridLength(11) : new GridLength(0);
        DetailsMenuItem.IsChecked = visible;
        UpdateToolRailVisuals();
        UpdateWorkspaceLayout();
        if (visible) SyncDetailsPanel();
        ScheduleSessionSave();
    }

    private void SetDockPreview(bool visible)
    {
        if (visible && !_dockPreviewOpen && _singleUtilityPanel)
        {
            if (_memoOpen) { SetMemoVisible(false); if (_memoOpen) return; }
            if (_detailsOpen) SetDetailsPanelVisible(false);
        }
        if (visible) _lastUtilityPanel = "Preview";
        if (!visible && _dockPreviewOpen && PreviewColumn.ActualWidth >= 260)
            _previewPreferredWidth = PreviewColumn.ActualWidth;
        _dockPreviewOpen = visible;
        PreviewPane.SetPreviewVisible(visible);
        PreviewPane.Visibility = PreviewColumnSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.Width = visible ? new GridLength(_previewPreferredWidth) : new GridLength(0);
        PreviewDividerColumn.Width = visible ? new GridLength(11) : new GridLength(0);
        MainPreviewButton.Content = visible ? "◧ 미리보기 끄기" : "◧ 미리보기 켜기";
        MainPreviewButton.Background = (Brush)FindResource(visible ? "Selected" : "ControlBackground");
        UpdateToolRailVisuals();
        UpdateWorkspaceLayout();
        if (visible) SyncDockPreview();
        ScheduleSessionSave();
    }

    private void UpdateToolRailVisuals()
    {
        // Rail is presentation-only. Each command routes to its existing feature owner.
        if (RailDetailsButton is null || RailMemoButton is null || RailPreviewButton is null || RailAiButton is null) return;
        if (SingleUtilityMenuItem is not null) SingleUtilityMenuItem.IsChecked = _singleUtilityPanel;
        if (RailSingleUtilityButton is not null)
        {
            RailSingleUtilityButton.SetResourceReference(Control.BackgroundProperty,
                _singleUtilityPanel ? "ThemeSplitActive" : "ThemeUtilityRail");
            RailSingleUtilityButton.SetResourceReference(Control.ForegroundProperty,
                _singleUtilityPanel ? "ThemeSplitActiveText" : "ThemeUtilityRailText");
            RailSingleUtilityButton.Tag = _singleUtilityPanel ? "True" : "False";
            RailSingleUtilityButton.FontWeight = _singleUtilityPanel ? FontWeights.SemiBold : FontWeights.Normal;
        }
        foreach (var (button, selected) in new[]
        {
            (RailDetailsButton, _detailsOpen), (RailMemoButton, _memoOpen),
            (RailPreviewButton, _dockPreviewOpen), (RailAiButton, _aiOpen)
        })
        {
            button.SetResourceReference(Control.BackgroundProperty, selected ? "ThemeSplitActive" : "ThemeUtilityRail");
            button.SetResourceReference(Control.ForegroundProperty, selected ? "ThemeSplitActiveText" : "ThemeUtilityRailText");
            button.Tag = selected ? "True" : "False";
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void SingleUtilityPanel_Click(object sender, RoutedEventArgs e)
    {
        if (_singleUtilityPanel)
        {
            // The mode only controls presentation. Existing visible panels stay open.
            _singleUtilityPanel = false;
        }
        else
        {
            // An explicit user action requests one auxiliary panel at a time.
            // Keep the memo when open so an unsaved draft is never silently discarded.
            if (_memoOpen)
            {
                if (_detailsOpen) SetDetailsPanelVisible(false);
                if (_dockPreviewOpen) SetDockPreview(false);
                _lastUtilityPanel = "Memo";
            }
            else if (_detailsOpen)
            {
                if (_dockPreviewOpen) SetDockPreview(false);
                _lastUtilityPanel = "Details";
            }
            else if (_dockPreviewOpen) _lastUtilityPanel = "Preview";
            _singleUtilityPanel = true;
        }
        UpdateToolRailVisuals();
        UpdateWorkspaceLayout();
        ScheduleSessionSave();
    }

    private void FocusWorkspace_Click(object sender, RoutedEventArgs e)
    {
        // Never change active view ownership while file transfer, Recycle Bin or AI work is running.
        if (_working || _aiBusy || _activeRecycleJobs > 0) return;
        // Confirm unsaved memo before closing any other auxiliary view.
        if (_memoOpen)
        {
            SetMemoVisible(false);
            if (_memoOpen) return; // User canceled the unsaved memo prompt.
        }
        if (_detailsOpen) SetDetailsPanelVisible(false);
        if (_dockPreviewOpen) SetDockPreview(false);
        if (_aiOpen && !_aiBusy) HideAiWindow();
        _panes[_activePane]?.FocusFiles();
    }

    private void MemoToggle_Click(object sender, RoutedEventArgs e) => SetMemoVisible(!_memoOpen);
    private void DetailsPanelToggle_Click(object sender, RoutedEventArgs e) => SetDetailsPanelVisible(!_detailsOpen);
    private void MainPreview_Click(object sender, RoutedEventArgs e) => SetDockPreview(!_dockPreviewOpen);

    private void One_Click(object sender, RoutedEventArgs e)
    {
        if (_openSplitGalleryMenu is { IsOpen: true }) _openSplitGalleryMenu.IsOpen = false;
        ApplyLastLayoutForCount(1);
    }
    // The count buttons now open an explicit layout gallery; they never silently cycle.
    private void Two_Click(object sender, RoutedEventArgs e) => ShowSplitLayoutGallery(2, TwoButton);
    private void Three_Click(object sender, RoutedEventArgs e) => ShowSplitLayoutGallery(3, ThreeButton);
    private void Four_Click(object sender, RoutedEventArgs e) => ShowSplitLayoutGallery(4, FourButton);

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

    private async void Pane_DropRequested(ExplorerPane.ExplorerDropRequest request)
    {
        if (_working || _aiBusy || _activeRecycleJobs > 0) return;
        int source = Array.IndexOf(_panes, request.Source);
        int target = Array.IndexOf(_panes, request.Destination);
        if (source < 0 || target < 0 || source >= _visiblePanels || target >= _visiblePanels) return;
        if (_paneLocationModes[source] != "Local" || _paneLocationModes[target] != "Local") return;
        await RunTransferAsync(request.Paths, request.TargetFolder, request.Kind, source, target);
    }

    private async void Pane_PasteRequested(ExplorerPane.ClipboardPasteRequest request)
    {
        int pane = Array.IndexOf(_panes, request.Pane);
        if (pane < 0 || pane >= _visiblePanels || _working || _aiBusy || _activeRecycleJobs > 0) return;
        if (_paneLocationModes[pane] != "Local") return;
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
        if (_working || _aiBusy || _activeRecycleJobs > 0 || paths.Count == 0 ||
            _paneLocationModes[sourceIndex] != "Local" || _paneLocationModes[targetIndex] != "Local") return;
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
            if (_memoOpen && !MemoPanel.ConfirmLeavingMemo()) return;
        }
        string action = kind == TransferKind.Copy ? "복사" : "이동";
        CollisionPolicy collision = CollisionPolicy.Ask;
        // Windows Explorer interaction: copy/move (including clipboard and drag/drop)
        // starts immediately. TransferService still validates paths, prevents overwrite,
        // and preserves source data on failure; destructive deletion remains confirmed.

        _working = true;
        OneButton.IsEnabled = TwoButton.IsEnabled = ThreeButton.IsEnabled = FourButton.IsEnabled = false;
        foreach (ExplorerPane pane in _panes) pane.SetFileActionsEnabled(false);
        CancelJobButton.IsEnabled = true;
        CancelJobButton.Visibility = JobProgress.Visibility = JobDetail.Visibility = Visibility.Visible;
        JobProgress.Value = 0;
        JobStatus.Text = $"{action} 작업 준비 중";
        JobDetail.Text = "선택한 항목을 검사하고 있습니다.";
        using var cancellation = new CancellationTokenSource();
        _transferControl = new TransferControl();
        PauseJobButton.Content = "일시 중지";
        PauseJobButton.Visibility = Visibility.Visible;
        var transferClock = System.Diagnostics.Stopwatch.StartNew();
        _transferCancellation = cancellation;
        using var progress = new UiLatestProgress<TransferProgress>(Dispatcher, item =>
        {
            if (!ReferenceEquals(_transferCancellation, cancellation)) return;
            if (_transferControl?.IsPaused == true) { JobDetail.Text = "일시 중지됨"; return; }
            double fraction = item.BytesTotal > 0
                ? (double)item.BytesDone / item.BytesTotal
                : item.Total > 0 ? (double)item.Completed / item.Total : 0;
            JobProgress.Value = Math.Clamp(fraction * 100, 0, 100);
            JobStatus.Text = $"{action}: {item.Completed}/{item.Total}개 · {JobProgress.Value:F0}% · {item.Phase}";
            double seconds = Math.Max(0.1, transferClock.Elapsed.TotalSeconds - _pausedSeconds);
            double speed = item.BytesTransferred / seconds;
            string remaining = speed > 0 && item.BytesTotal > item.BytesDone
                ? TimeSpan.FromSeconds(Math.Min(864000, (item.BytesTotal - item.BytesDone) / speed)).ToString(@"hh\:mm\:ss") : "—";
            JobDetail.Text = $"{item.CurrentName} · {Sunrise.FileManager.Models.FileEntry.HumanSize((long)speed)}/초 · 남은 시간 {remaining}";
        });
        try
        {
            // Transaction plan is persisted before copy/move starts. Interrupted plans are review-only.
            var recoveryPlan = TransferRecoveryJournal.Begin(paths, target, kind);
            TransferResult result = await TransferService.RunAsync(paths, target, kind, progress, cancellation.Token, collision,
                resolveConflict: (conflict, token) => Dispatcher.InvokeAsync(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var dialog = new TransferConflictWindow(conflict) { Owner = this };
                    using var registration = token.Register(() => { _ = Dispatcher.BeginInvoke(new Action(() => dialog.Close())); });
                    var wait = System.Diagnostics.Stopwatch.StartNew();
                    try { dialog.ShowDialog(); }
                    finally { _pausedSeconds += wait.Elapsed.TotalSeconds; }
                    return dialog.Decision;
                }).Task, control: _transferControl,
                onItemCompleted: (sourcePath, outputPath) => TransferRecoveryJournal.MarkCompleted(recoveryPlan, sourcePath, outputPath));
            try { TransferRecoveryJournal.Finish(recoveryPlan, result); }
            catch (IOException ex) { System.Diagnostics.Trace.TraceWarning("Transfer recovery record cleanup failed: {0}", ex.GetType().Name); }
            progress.Dispose();
            JobStatus.Text = result.Summary;
            JobDetail.Text = result.Errors.Count == 0 ? "작업 결과를 확인했습니다." : result.Errors[0];
            if (result.ReplacementBackups.Count > 0)
                MessageBox.Show("기존 파일을 다음 위치에 보존했습니다. 확인 후 필요에 따라 정리하세요.\n\n" +
                    string.Join("\n", result.ReplacementBackups.Take(12)), "파일 바꾸기 결과", MessageBoxButton.OK, MessageBoxImage.Information);
            if (!result.Canceled && result.Failed == 0) JobProgress.Value = 100;
            if (kind == TransferKind.Move && result.MovedPaths.Count > 0)
            {
                var movedPairs = result.MovedPaths
                    .Select(p => (OldPath: p.Source, NewPath: p.Destination))
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
            if (kind == TransferKind.Move)
            {
                // A cut/paste may originate in any other local or network pane even when
                // clipboard paste is requested by the destination pane. Refresh every pane
                // to clear stale cut highlighting and removed source rows.
                for (int paneIndex = 0; paneIndex < _visiblePanels; paneIndex++)
                    if (_paneLocationModes[paneIndex] == "Local") _panes[paneIndex].RefreshCurrent();
            }
            else
            {
                _panes[sourceIndex].RefreshCurrent();
                if (targetIndex != sourceIndex) _panes[targetIndex].RefreshCurrent();
            }
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
            string explanation = NetworkTransferDiagnostics.Explain(exception, paths.FirstOrDefault(), target);
            string shown = string.IsNullOrEmpty(explanation) ? exception.Message : explanation;
            JobDetail.Text = shown;
            MessageBox.Show(shown, "파일 작업 제한", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _transferControl?.Resume();
            _transferControl = null;
            _pausedSeconds = 0;
            PauseJobButton.Visibility = Visibility.Collapsed;
            _transferCancellation = null;
            _working = false;
            OneButton.IsEnabled = TwoButton.IsEnabled = ThreeButton.IsEnabled = FourButton.IsEnabled = true;
            foreach (ExplorerPane pane in _panes) pane.SetFileActionsEnabled(true);
            CancelJobButton.IsEnabled = false;
            CancelJobButton.Visibility = JobProgress.Visibility = JobDetail.Visibility = Visibility.Collapsed;
        }
    }

    private TransferControl? _transferControl;
    private DateTime _pauseStarted;
    private double _pausedSeconds;
    private void PauseJob_Click(object sender, RoutedEventArgs e)
    {
        if (_transferControl is null) return;
        if (_transferControl.IsPaused)
        {
            _pausedSeconds += (DateTime.UtcNow - _pauseStarted).TotalSeconds;
            _transferControl.Resume();
            PauseJobButton.Content = "일시 중지";
        }
        else
        {
            _pauseStarted = DateTime.UtcNow;
            _transferControl.Pause();
            PauseJobButton.Content = "다시 시작";
            JobDetail.Text = "일시 중지됨";
        }
    }

    private void CancelJob_Click(object sender, RoutedEventArgs e)
    {
        _transferCancellation?.Cancel();
        JobDetail.Text = "현재 작업의 안전한 취소를 요청했습니다.";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_activeRecycleJobs > 0 || AnyCloudOperationBusy)
        {
            foreach (var pane in _panes) pane?.CancelLocalFileJob();
            e.Cancel = true;
            MessageBox.Show("파일 작업의 취소를 요청했습니다. 이름 변경·이동·휴지통 작업은 완료를 기다려야 합니다. 처리 후 다시 종료하십시오.",
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
        if (!_updateExitApproved && !MemoPanel.ConfirmLeavingMemo()) { e.Cancel = true; return; }
        _sessionSaveTimer.Stop();
        if (_persistSession && !TrySaveSession(showError: true)) { e.Cancel = true; return; }
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemThemeChanged;
        foreach (var pane in _panes) pane.CancelPendingOperations();
        foreach (var cloud in _cloudSlots) cloud?.Dispose();
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

    private bool TrySaveSession(bool showError)
    {
        try
        {
            CaptureWorkspaceRatios();
            Rect aiRect = _aiWindow is null ? Rect.Empty : _aiWindow.WindowState == WindowState.Normal ? new Rect(_aiWindow.Left, _aiWindow.Top, _aiWindow.Width, _aiWindow.Height) : _aiWindow.RestoreBounds;
            Rect rect = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            SessionStore.SaveUiSnapshot(new AppSession
            {
                VisiblePanels = _visiblePanels,
                ActivePane = _activePane,
                SplitLayoutPreset = _splitPreset.ToString(),
                LayoutVersion = 25,
                SingleUtilityPanel = _singleUtilityPanel,
                SingleUtilityActive = _lastUtilityPanel,
                SplitRatios = _splitRatios,
                LastSplitPresets = _lastPresetByCount.Select(x => x.ToString()).ToList(),
                AiSettings = AiPanel.GetPreferences(),
                AiWindowVisible = _aiOpen,
                AiWindowWidth = aiRect.IsEmpty ? _restoredPreferences.AiWindowWidth : aiRect.Width,
                AiWindowHeight = aiRect.IsEmpty ? _restoredPreferences.AiWindowHeight : aiRect.Height,
                AiWindowLeft = aiRect.IsEmpty ? _restoredPreferences.AiWindowLeft : aiRect.Left,
                AiWindowTop = aiRect.IsEmpty ? _restoredPreferences.AiWindowTop : aiRect.Top,
                AiWindowMaximized = _aiWindow is null ? _restoredPreferences.AiWindowMaximized : _aiWindow.WindowState == WindowState.Maximized,
                PreviewFitWidth = PreviewPane.FitWidthPreference,
                PreviewZoom = PreviewPane.ZoomPreference,
                MemoAuthor = MemoPanel.PreferredAuthor,
                UiScalePercent = UiScaleService.Percent,
                Theme = _currentTheme,
                LightAccentHex = _committedLightAccentHex,
                InspectorVisible = _memoOpen || _detailsOpen, // legacy combined toggle
                MemoPanelVisible = _memoOpen,
                DetailsPanelVisible = _detailsOpen,
                MemoPanelWidth = _memoOpen && MemoColumn.ActualWidth >= 295 ? MemoColumn.ActualWidth : _memoPreferredWidth,
                DetailsPanelWidth = _detailsOpen && DetailsPanelColumn.ActualWidth >= 280 ? DetailsPanelColumn.ActualWidth : _detailsPreferredWidth,
                DockPreviewVisible = _dockPreviewOpen,
                InspectorWidth = _memoOpen && MemoColumn.ActualWidth >= 270 ? MemoColumn.ActualWidth : _memoPreferredWidth,
                PreviewWidth = _dockPreviewOpen && PreviewColumn.ActualWidth >= 260 ? PreviewColumn.ActualWidth : _previewPreferredWidth,
                WindowWidth = rect.Width,
                WindowHeight = rect.Height,
                WindowLeft = rect.Left,
                WindowTop = rect.Top,
                IsMaximized = WindowState == WindowState.Maximized,
                PinnedFolders = _panes[_activePane].GetPinnedFolders().ToList(),
                Panels = _panes.Select(pane => pane.GetSession()).ToList()
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (showError)
                MessageBox.Show($"화면 설정을 저장하지 못했습니다.\n{ex.Message}", "Sunrise File Manager",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return false;
    }

}
