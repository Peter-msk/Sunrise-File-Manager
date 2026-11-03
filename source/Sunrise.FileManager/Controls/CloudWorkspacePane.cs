using System.Runtime.InteropServices;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sunrise.FileManager.Services.CloudDrive;

namespace Sunrise.FileManager.Controls;

/// <summary>
/// A genuinely remote workspace slot for one of the four split panes. It never represents a remote ID as
/// a Windows file path and never delegates cloud mutations to the local transfer/undo engine.
/// Local ExplorerPane remains alive behind it, preserving tabs/selection/session settings on mode switch.
/// </summary>
public sealed class CloudWorkspacePane : UserControl
{
    private sealed record RemoteRow(CloudItem Item)
    {
        public string Name => Item.Name;
        public string Kind => Item.IsFolder ? "폴더" : "파일";
        public string Size => Item.Size is long n ? $"{n:N0} B" : "—";
    }

    private readonly Action<CloudProvider> _openAccountManager;
    private readonly DataGrid _items = new() { AutoGenerateColumns = false, IsReadOnly = true,
        SelectionMode = DataGridSelectionMode.Extended, GridLinesVisibility = DataGridGridLinesVisibility.None };
    private readonly TextBlock _path = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly ProgressBar _progress = new() { Height = 5, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly List<Button> _operations = [];
    private readonly Stack<(string Id, string Name)> _parents = new();
    private CancellationTokenSource? _task;
    private ICloudDriveApi? _api;
    private string? _folderId, _driveId;
    private string _folderName = "내 드라이브";
    private CloudProvider _provider;
    private Point? _dragOrigin;
    private RemoteRow? _dragRow;
    public bool IsBusy { get; private set; }
    public bool FocusFiles() => _items.Focus();
    public CloudProvider Provider => _provider;
    public event Action<CloudWorkspacePane, IReadOnlyList<CrossPaneCopySource>>? ClipboardPasteRequested;
    public event Action<CloudWorkspacePane, IReadOnlyList<CrossPaneCopySource>, string>? DragCopyRequested;

    public CloudWorkspacePane(Action<CloudProvider> openAccountManager)
    {
        _openAccountManager = openAccountManager;
        SetResourceReference(BackgroundProperty, "ThemeSurface");
        _path.SetResourceReference(ForegroundProperty, "TextPrimary");
        _status.SetResourceReference(ForegroundProperty, "TextMuted");
        _items.SetResourceReference(DataGrid.BackgroundProperty, "ThemeSurface");
        _items.SetResourceReference(DataGrid.ForegroundProperty, "TextPrimary");
        _items.SetResourceReference(FrameworkElement.StyleProperty, "PopupResultDataGrid");
        _items.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "PopupCenteredColumnHeader");
        var centeredCell = (Style)Application.Current.FindResource("PopupCenteredCellText");
        _items.Columns.Add(new DataGridTextColumn { Header = "파일 및 폴더", ElementStyle = centeredCell, Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _items.Columns.Add(new DataGridTextColumn { Header = "구분", ElementStyle = centeredCell, Binding = new System.Windows.Data.Binding("Kind"), Width = new DataGridLength(54) });
        _items.MouseDoubleClick += ItemDoubleClicked;
        _items.AllowDrop = true;
        _items.PreviewMouseLeftButtonDown += CloudGrid_MouseDown;
        _items.PreviewMouseMove += CloudGrid_MouseMove;
        _items.PreviewDragOver += CloudGrid_DragOver;
        _items.PreviewDrop += CloudGrid_Drop;
        _items.DragLeave += (_, _) => _items.BorderThickness = new Thickness(0);
        _items.PreviewKeyDown += ClipboardShortcutKeyDown;

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        AddButton(tools, "계정", (_, _) => _openAccountManager(_provider));
        AddButton(tools, "상위", async (_, _) => await GoUpAsync());
        AddButton(tools, "새로고침", async (_, _) => await RefreshAsync());
        AddButton(tools, "복사", (_, _) => CopySelectionToClipboard());
        AddButton(tools, "붙여넣기", (_, _) => PasteFromClipboard());
        AddButton(tools, "새 폴더", async (_, _) => await NewFolderAsync());
        AddButton(tools, "이름 변경", async (_, _) => await RenameAsync());
        AddButton(tools, "업로드", async (_, _) => await UploadAsync());
        AddButton(tools, "다운로드", async (_, _) => await DownloadAsync());
        AddButton(tools, "휴지통", async (_, _) => await TrashAsync());
        var commands = new ScrollViewer { Content = tools, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(4, 3, 4, 2) };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _path.Margin = new Thickness(7, 4, 7, 4);
        Grid.SetRow(_path, 0); root.Children.Add(_path);
        Grid.SetRow(commands, 1); root.Children.Add(commands);
        Grid.SetRow(_items, 2); root.Children.Add(_items);
        var footer = new StackPanel { Margin = new Thickness(7, 4, 7, 6) };
        footer.Children.Add(_progress); footer.Children.Add(_status);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;
        CloudSessionHub.ConnectionChanged += AccountChanged;
        Unloaded += (_, _) => { /* Preserve in-flight writes; workspace switching is blocked while busy. */ };
    }

    private void CloudGrid_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = null;
        _dragRow = null;
        if (IsBusy || _api is null || _folderId is null || e.OriginalSource is not DependencyObject element)
            return;
        if (ItemsControl.ContainerFromElement(_items, element) is DataGridRow { Item: RemoteRow row })
        {
            _dragRow = row;
            _dragOrigin = e.GetPosition(_items);
        }
    }

    private void CloudGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || IsBusy ||
            _dragOrigin is not Point start || _dragRow is not { } dragRow)
        { _dragOrigin = null; _dragRow = null; return; }
        Point now = e.GetPosition(_items);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragOrigin = null;
        _dragRow = null;
        if (!_items.SelectedItems.Contains(dragRow)) return;
        var sources = SnapshotCopySources();
        if (sources.Count == 0) return;
        try
        {
            // The marker is only meaningful to this running Sunrise process. Never publish
            // remote IDs to the shell or synthesize a fake CF_HDROP path for cloud files.
            var payload = WorkspaceCloudDragSession.Begin(_provider, sources);
            DragDrop.DoDragDrop(_items, payload, DragDropEffects.Copy);
        }
        catch (ArgumentException) { _status.Text = "클라우드 드래그 원본을 확인할 수 없습니다."; }
        finally { WorkspaceCloudDragSession.Clear(); }
    }

    private string? ResolveDropFolder(DragEventArgs e)
    {
        if (_folderId is null) return null;
        if (e.OriginalSource is DependencyObject element &&
            ItemsControl.ContainerFromElement(_items, element) is DataGridRow
                { Item: RemoteRow { Item.IsFolder: true } folder })
            return folder.Item.Id;
        return _folderId;
    }

    private bool TryResolveDrop(DragEventArgs e, bool checkPaths, out IReadOnlyList<CrossPaneCopySource> sources)
    {
        sources = [];
        if (IsBusy || _api is null || ResolveDropFolder(e) is null) return false;
        if (e.Data.GetDataPresent(WorkspaceCloudDragSession.Format))
            return WorkspaceCloudDragSession.TryResolve(e.Data, out sources);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths ||
            paths.Length is < 1 or > CrossPaneBatchCopyService.MaximumFiles ||
            // A network share may stall for seconds. Inspect existence on DROP, not on
            // every DragOver event, while preserving no-overwrite checks in copy engine.
            (checkPaths && paths.Any(path => !File.Exists(path) && !Directory.Exists(path)))) return false;
        sources = paths.Select(path => new CrossPaneCopySource(path, null, null)).ToArray();
        return true;
    }

    private void CloudGrid_DragOver(object sender, DragEventArgs e)
    {
        try { e.Effects = TryResolveDrop(e, checkPaths: false, out _) ? DragDropEffects.Copy : DragDropEffects.None; }
        catch (ExternalException) { e.Effects = DragDropEffects.None; }
        catch (ArgumentException) { e.Effects = DragDropEffects.None; }
        e.Handled = true;
    }

    private void CloudGrid_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        try
        {
            if (!TryResolveDrop(e, checkPaths: true, out var sources) ||
                ResolveDropFolder(e) is not string destinationId) return;
            // Moving/deleting originals is never inferred from Shift or preferred effect.
            e.Effects = DragDropEffects.Copy;
            DragCopyRequested?.Invoke(this, sources, destinationId);
        }
        catch (ExternalException) { _status.Text = "파일 드래그 정보를 읽을 수 없습니다."; }
        catch (ArgumentException) { _status.Text = "드롭한 파일의 경로가 올바르지 않습니다."; }
        catch (IOException) { _status.Text = "드롭한 네트워크 파일 경로에 접근할 수 없습니다."; }
        catch (UnauthorizedAccessException) { _status.Text = "드롭한 파일의 접근 권한을 확인해 주세요."; }
    }

    private void ClipboardShortcutKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.C) { CopySelectionToClipboard(); e.Handled = true; }
        else if (e.Key == Key.V) { PasteFromClipboard(); e.Handled = true; }
        else if (e.Key == Key.X)
        {
            _status.Text = "클라우드 잘라내기는 안전 이동 검증 전까지 비활성화돼 있습니다. 복사를 사용하세요.";
            e.Handled = true;
        }
    }

    private void CopySelectionToClipboard()
    {
        var items = SnapshotCopySources();
        if (items.Count == 0)
        {
            _status.Text = "클라우드 파일·폴더를 1~128개 선택해 주세요.";
            return;
        }
        try
        {
            WorkspaceCloudClipboard.WriteCloudCopy(_provider, items);
            _status.Text = $"{items.Count}개 선택 · 다른 분할창에서 Ctrl+V로 복사할 수 있습니다. 잘라내기는 아직 지원하지 않습니다.";
        }
        catch (ExternalException) { _status.Text = "클립보드가 다른 프로그램에서 사용 중입니다. 다시 시도하세요."; }
        catch (ArgumentException) { _status.Text = "클립보드에 복사할 항목을 확인하세요."; }
    }

    private void PasteFromClipboard()
    {
        if (IsBusy || _api is null || _folderId is null)
        {
            _status.Text = "대상 클라우드 계정과 폴더를 먼저 열어 주세요.";
            return;
        }
        try
        {
            IReadOnlyList<CrossPaneCopySource> sources;
            if (Clipboard.ContainsData(WorkspaceCloudClipboard.Format))
            {
                if (!WorkspaceCloudClipboard.TryReadCloudCopy(out sources))
                {
                    _status.Text = "클라우드 복사 정보가 만료됐습니다. 원본에서 다시 Ctrl+C를 누르세요.";
                    return;
                }
            }
            else if (Clipboard.ContainsFileDropList())
            {
                if (WorkspaceCloudClipboard.IsCutFromClipboard())
                {
                    _status.Text = "로컬 잘라내기를 클라우드에 이동하는 기능은 아직 없습니다. 원본에서 복사(Ctrl+C)한 뒤 붙여넣으세요.";
                    return;
                }
                var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
                if (paths.Length == 0 || paths.Length > CrossPaneBatchCopyService.MaximumFiles ||
                    paths.Any(path => !File.Exists(path) && !Directory.Exists(path)))
                {
                    _status.Text = "유효한 로컬 파일·폴더 1~128개를 선택하세요.";
                    return;
                }
                sources = paths.Select(path => new CrossPaneCopySource(path, null, null)).ToArray();
            }
            else
            {
                _status.Text = "복사된 파일·폴더가 없습니다. 원본에서 Ctrl+C를 누르세요.";
                return;
            }
            ClipboardPasteRequested?.Invoke(this, sources);
        }
        catch (ExternalException) { _status.Text = "클립보드를 읽지 못했습니다. 다시 시도하세요."; }
        catch (IOException) { _status.Text = "원본 파일을 확인할 수 없습니다."; }
    }

    private void AddButton(Panel parent, string title, RoutedEventHandler click)
    {
        var button = new Button { Content = title, Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(7, 3, 7, 3), MinHeight = 28 };
        button.Click += click;
        _operations.Add(button);
        parent.Children.Add(button);
    }

    public void Dispose()
    {
        CloudSessionHub.ConnectionChanged -= AccountChanged;
        _task?.Cancel();
    }

    private void AccountChanged(CloudProvider provider)
    {
        if (provider != _provider) return;
        // A disconnect or account replacement invalidates all old remote IDs immediately;
        // otherwise a previous account's file names could remain visible after logout.
        _api = null; _folderId = _driveId = null; _parents.Clear();
        _items.ItemsSource = null; _folderName = "내 드라이브";
        _status.Text = "계정 상태가 변경됐습니다. 원격 항목을 다시 확인하세요.";
        if (Visibility == Visibility.Visible && !IsBusy &&
            (CloudSessionHub.OAuth.HasSession(provider) || CloudSessionHub.OAuth.HasSavedAccount(provider)))
            _ = RefreshAsync();
    }

    public bool SetProvider(CloudProvider provider)
    {
        if (IsBusy) return false;
        _provider = provider;
        _api = null; _folderId = _driveId = null;
        _folderName = "내 드라이브";
        _parents.Clear(); _items.ItemsSource = null;
        _path.Text = provider == CloudProvider.GoogleDrive ? "Google Drive" : "OneDrive";
        _status.Text = "계정 연결 후 원격 파일을 표시합니다. 자동 로그인은 사용자가 선택할 수 있습니다.";
        if (CloudSessionHub.OAuth.HasSession(provider) || CloudSessionHub.OAuth.HasSavedAccount(provider))
            _ = RefreshAsync();
        return true;
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy) return;
        using var task = new CancellationTokenSource();
        _task = task;
        IsBusy = true;
        foreach (var button in _operations.Skip(1)) button.IsEnabled = false;
        try { await operation(task.Token); }
        catch (OperationCanceledException) { _status.Text = "작업 취소됨. 원격 변경 여부는 새로고침으로 확인해 주세요."; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or
                                  InvalidOperationException or ArgumentException or JsonException or
                                  CryptographicException or NotSupportedException or System.Net.Sockets.SocketException)
        {
            // Do not surface OAuth secrets, remote paths, signed URLs or raw provider error bodies.
            _status.Text = ex is CloudUploadReviewRequiredException ?
                "업로드 완료 여부를 확인할 수 없습니다. 클라우드 대상 파일을 확인하고 '중단 업로드 세션 정리'를 이용하세요. 자동 재전송하지 않았습니다." :
                ex is UnauthorizedAccessException ? "계정 인증이 필요합니다. 계정 버튼에서 연결하세요." :
                ex is NotSupportedException ? "이 클라우드 작업은 아직 지원되지 않습니다." :
                "클라우드 요청을 완료하지 못했습니다. 연결·권한·네트워크를 확인하고 다시 시도하세요.";
        }
        finally
        {
            _task = null;
            IsBusy = false;
            _progress.Visibility = Visibility.Collapsed;
            foreach (var button in _operations.Skip(1)) button.IsEnabled = true;
        }
    }

    public Task RefreshAsync() => ExecuteAsync(async ct =>
    {
        if (_api is null || _folderId is null)
        {
            if (!CloudSessionHub.OAuth.HasSession(_provider) && !CloudSessionHub.OAuth.HasSavedAccount(_provider))
            { _status.Text = "미연결 계정입니다. 계정 버튼에서 공식 로그인을 진행하세요."; return; }
            (ICloudDriveApi api, string root, string? drive) = await CloudSessionHub.OpenRootAsync(_provider, ct);
            _api = api; _folderId = root; _driveId = drive; _parents.Clear();
            _folderName = "내 드라이브";
        }
        await ReadFolderAsync(_folderId, _folderName, ct);
    });

    private async Task ReadFolderAsync(string id, string name, CancellationToken ct)
    {
        if (_api is null) throw new InvalidOperationException("클라우드 미연결");
        var rows = await _api.ListChildrenAsync(id, ct);
        _items.ItemsSource = rows.OrderByDescending(x => x.IsFolder)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Select(x => new RemoteRow(x)).ToArray();
        _folderId = id; _folderName = name;
        _path.Text = (_provider == CloudProvider.GoogleDrive ? "Google Drive" : "OneDrive") + " / " + name;
        _status.Text = $"{rows.Count:N0}개 · 원격 API 조회 · 로컬 파일과 별개";
    }

    private async void ItemDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (_items.SelectedItem is not RemoteRow { Item.IsFolder: true } row || _folderId is null) return;
        string previous = _folderId, name = _folderName;
        await ExecuteAsync(async ct => { await ReadFolderAsync(row.Item.Id, row.Item.Name, ct); _parents.Push((previous, name)); });
    }

    private Task GoUpAsync() => ExecuteAsync(async ct =>
    {
        if (_parents.Count == 0) return;
        var parent = _parents.Peek();
        await ReadFolderAsync(parent.Id, parent.Name, ct);
        _parents.Pop();
    });

    private string? AskName(string title, string initial)
    {
        var input = new NameInputDialog(title, "파일 또는 폴더 이름", initial) { Owner = Window.GetWindow(this) };
        return input.ShowDialog() == true ? input.ResultName : null;
    }

    private CloudItem? Selected => _items.SelectedItems.Count == 1 ? (_items.SelectedItem as RemoteRow)?.Item : null;

    /// <summary>Snapshot one remote item, preserving provider IDs instead of fake Windows paths.</summary>
    internal CrossPaneCopySource? SnapshotCopySource() => !IsBusy && _api is not null &&
        Selected is { } item
            ? new CrossPaneCopySource(null, item, CreateTransfer(), _api) : null;

    /// <summary>R21: capture files and folders without converting remote IDs into fake paths.</summary>
    internal IReadOnlyList<CrossPaneCopySource> SnapshotCopySources()
    {
        if (IsBusy || _api is null || _folderId is null) return [];
        var rows = _items.SelectedItems.OfType<RemoteRow>().ToArray();
        if (rows.Length == 0 || rows.Length > CrossPaneBatchCopyService.MaximumFiles) return [];
        var transfer = CreateTransfer();
        return rows.Select(row => new CrossPaneCopySource(null, row.Item, transfer, _api)).ToArray();
    }

    /// <summary>Snapshot the currently displayed remote folder as a copy destination.</summary>
    internal CrossPaneCopyTarget? SnapshotCopyTarget() => !IsBusy && _api is not null && _folderId is not null
        ? new CrossPaneCopyTarget(null, _folderId, _api, CreateTransfer()) : null;


    private Task NewFolderAsync()
    {
        if (_api is null || _folderId is null || IsBusy) return Task.CompletedTask;
        string? name = AskName("새 클라우드 폴더", "새 폴더");
        if (name is null) return Task.CompletedTask;
        var api = _api; string folder = _folderId, folderName = _folderName;
        return ExecuteAsync(async ct =>
        {
            if ((await api.ListChildrenAsync(folder, ct)).Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("같은 이름이 있습니다.");
            await api.CreateFolderAsync(folder, name, ct);
            await ReadFolderAsync(folder, folderName, ct);
        });
    }

    private Task RenameAsync()
    {
        if (_api is null || _folderId is null || Selected is not { } item || IsBusy) return Task.CompletedTask;
        string? name = AskName("클라우드 이름 변경", item.Name);
        if (name is null || name == item.Name) return Task.CompletedTask;
        var api = _api; string folder = _folderId, folderName = _folderName;
        return ExecuteAsync(async ct =>
        {
            if ((await api.ListChildrenAsync(folder, ct)).Any(x => x.Id != item.Id && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("같은 이름이 있습니다.");
            await api.RenameAsync(item.Id, name, ct);
            await ReadFolderAsync(folder, folderName, ct);
        });
    }

    private CloudFileTransferService CreateTransfer() => new(CloudSessionHub.Http,
        ct => CloudSessionHub.OAuth.GetAccessTokenAsync(CloudSessionHub.Configuration(_provider), ct), _provider, _driveId);

    private IProgress<CloudTransferProgress> Track(string verb, string name)
    {
        _progress.Value = 0; _progress.Visibility = Visibility.Visible;
        return new Progress<CloudTransferProgress>(progress =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            _progress.IsIndeterminate = progress.Total <= 0;
            _progress.Value = progress.Percent;
            _status.Text = $"{verb}: {name} · {progress.Percent}%";
        });
    }

    private Task UploadAsync()
    {
        if (_api is null || _folderId is null || IsBusy) return Task.CompletedTask;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "클라우드 업로드 (덮어쓰기 금지)", CheckFileExists = true };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return Task.CompletedTask;
        var api = _api; string folder = _folderId, name = _folderName, path = picker.FileName;
        CloudFileTransferService service = CreateTransfer();
        return ExecuteAsync(async ct =>
        {
            await service.UploadAsync(folder, path, api, Track("업로드", Path.GetFileName(path)), ct);
            await ReadFolderAsync(folder, name, ct);
        });
    }

    private Task DownloadAsync()
    {
        if (_api is null || Selected is not { IsFolder: false } file || IsBusy) return Task.CompletedTask;
        var picker = new Microsoft.Win32.SaveFileDialog { Title = "클라우드 파일 다운로드", FileName = file.Name,
            OverwritePrompt = false, AddExtension = false };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return Task.CompletedTask;
        string path = picker.FileName;
        if (File.Exists(path) || Directory.Exists(path))
        { _status.Text = "로컬 파일이 이미 있습니다. 다른 이름을 지정해 주세요."; return Task.CompletedTask; }
        CloudFileTransferService service = CreateTransfer();
        return ExecuteAsync(async ct =>
        {
            await service.DownloadAsync(file, path, Track("다운로드", file.Name), ct);
            _status.Text = "다운로드 완료 · 최종 파일 저장 확인";
        });
    }

    private Task TrashAsync()
    {
        if (_api is null || _folderId is null || Selected is not { } item || IsBusy) return Task.CompletedTask;
        if (MessageBox.Show(Window.GetWindow(this), $"'{item.Name}'을(를) 클라우드 휴지통으로 이동할까요?",
                "휴지통 이동 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return Task.CompletedTask;
        var api = _api; string folder = _folderId, name = _folderName;
        return ExecuteAsync(async ct =>
        {
            await api.MoveToTrashAsync(item.Id, ct);
            await ReadFolderAsync(folder, name, ct);
        });
    }
}
