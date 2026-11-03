using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sunrise.FileManager.Services.CloudDrive;

namespace Sunrise.FileManager.Controls;

/// <summary>Account-consented remote browser. Never collects credentials or uses password auth.</summary>
public partial class CloudDriveWindow : Window
{
    public sealed record CloudRow(CloudItem Item)
    {
        public string Name => Item.Name;
        public string TypeLabel => Item.IsFolder ? "폴더" : "파일";
        public string SizeLabel => Item.Size is long size ? $"{size:N0} B" : "—";
    }

    // R16: share short-lived in-memory OAuth sessions with all four split workspace slots.
    private readonly HttpClient _http = CloudSessionHub.Http;
    private readonly CloudOAuthService _oauth = CloudSessionHub.OAuth;
    private CloudProvider _provider = CloudProvider.GoogleDrive;
    private ICloudDriveApi? _api;
    private string? _rootId, _driveId, _folderId;
    private string _folderName = "";
    private readonly Stack<(string Id, string Name)> _parents = new();
    private CancellationTokenSource? _operation;
    private bool _switching, _busy, _closeAfterCancel;
    public bool IsCloudBusy => _busy;

    public CloudDriveWindow()
    {
        InitializeComponent();
        ProviderSelector.SelectedIndex = 0;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (_busy)
            {
                _operation?.Cancel();
                StatusText.Text = "클라우드 작업 취소를 요청했습니다. 실제 파일 상태를 확인하세요.";
            }
            else Close();
        };
        Loaded += (_, _) => { if (_oauth.HasSavedAccount(_provider)) _ = TryAutoOpenAsync(); };
        Closed += (_, _) => { _operation?.Cancel(); _operation?.Dispose(); };
    }

    /// <summary>
    /// Select the service requested by the right utility rail without interrupting a live transfer.
    /// Reuse the same cloud window; account sessions are still owned by CloudOAuthService.
    /// </summary>
    public bool TrySelectProvider(CloudProvider provider)
    {
        if (_busy)
        {
            StatusText.Text = "클라우드 작업 중에는 서비스 전환을 할 수 없습니다. 작업 완료 후 다시 선택하세요.";
            return false;
        }
        ProviderSelector.SelectedIndex = provider == CloudProvider.GoogleDrive ? 0 : 1;
        return true;
    }

    // R61: Explain public-client registration without reading or changing credentials.
    // The help action remains available even when a cloud transfer is busy.
    private void ClientIdHelp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CloudClientIdHelpWindow(_provider) { Owner = this };
        dialog.ShowDialog();
    }

    private CloudOAuthConfig Config() => new(_provider, ClientIdBox.Text.Trim(),
        _provider == CloudProvider.OneDrive && !string.IsNullOrWhiteSpace(TenantBox.Text)
            ? TenantBox.Text.Trim() : null);

    private void ProviderSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderSelector.SelectedItem is not ComboBoxItem { Tag: string name } ||
            !Enum.TryParse<CloudProvider>(name, out var provider)) return;
        _switching = true;
        try
        {
            _provider = provider;
            _api = null;
            _rootId = _driveId = _folderId = null;
            _parents.Clear();
            RemoteFilesGrid.ItemsSource = null;
            FolderLocationText.Text = "연결 후 드라이브 목록 표시";
            var entry = CloudAccountClientSettings.Load(_provider);
            ClientIdBox.Text = entry.ClientId;
            TenantBox.Text = entry.Tenant;
            TenantBox.IsEnabled = _provider == CloudProvider.OneDrive;
            RememberSignInBox.IsChecked = _oauth.HasSavedAccount(_provider);
            StatusText.Text = _oauth.HasSavedAccount(_provider)
                ? "자동 로그인 저장됨. 원격 폴더 새로고침으로 연결할 수 있습니다."
                : "로그인 시 자동 로그인을 직접 선택할 수 있습니다 (기본: 사용 안 함).";
        }
        finally { _switching = false; }
        if (IsLoaded && _oauth.HasSavedAccount(_provider)) _ = TryAutoOpenAsync();
    }

    private void RememberSignIn_Changed(object sender, RoutedEventArgs e)
    {
        if (_switching) return;
        bool remember = RememberSignInBox.IsChecked == true;
        if (remember && _oauth.HasSession(_provider) && !_oauth.HasSavedAccount(_provider))
        {
            try { _oauth.SetRememberSignIn(_provider, true); StatusText.Text = "자동 로그인 사용: 암호화된 갱신 토큰 저장 완료."; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
            { StatusText.Text = "자동 로그인 저장 실패: " + error.Message; RememberSignInBox.IsChecked = false; }
        }
        else if (!remember && _oauth.HasSavedAccount(_provider))
        {
            _oauth.DisableAutoSignIn(_provider);
            StatusText.Text = "자동 로그인 저장을 해제했습니다. 현재 실행 중인 연결만 유지됩니다.";
        }
    }

    private void RunBusy(bool busy)
    {
        _busy = busy;
        ConnectButton.IsEnabled = DisconnectButton.IsEnabled = RefreshButton.IsEnabled = !busy;
        ProviderSelector.IsEnabled = ClientIdBox.IsEnabled = TenantBox.IsEnabled = RememberSignInBox.IsEnabled = !busy;
        UpButton.IsEnabled = NewFolderButton.IsEnabled = RenameButton.IsEnabled =
            CopyButton.IsEnabled = TrashButton.IsEnabled = UploadButton.IsEnabled = DownloadButton.IsEnabled = !busy;
        if (!busy && _closeAfterCancel && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(Close));
    }
    private async Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        RunBusy(true);
        try { await action(operation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "클라우드 작업이 취소되었습니다."; }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or
                                           ArgumentException or InvalidOperationException or JsonException or NotSupportedException or
                                           CryptographicException or System.Net.HttpListenerException or Win32Exception or
                                           System.Net.Sockets.SocketException)
        {
            // Do not print tokens, OAuth redirects, usernames or provider response bodies to UI/logs.
            StatusText.Text = error switch
            {
                UnauthorizedAccessException => "인증이 만료됐거나 권한이 없습니다. 계정을 다시 연결해 주세요.",
                CryptographicException => "암호화된 계정 정보를 읽을 수 없습니다. Windows 사용자 계정을 확인하거나 다시 로그인하세요.",
                _ => error.Message
            };
        }
        finally { _operation = null; RunBusy(false); }
    }

    private async Task TryAutoOpenAsync()
    {
        if (_busy || !_oauth.HasSavedAccount(_provider)) return;
        await ExecuteAsync(async ct =>
        {
            StatusText.Text = "저장된 Windows 사용자 자격증명으로 연결을 확인하고 있습니다…";
            await OpenRootAsync(ct);
        });
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async ct =>
    {
        CloudOAuthConfig config = Config();
        CloudAccountClientSettings.Save(_provider, new CloudClientEntry(config.ClientId, config.MicrosoftTenant ?? ""));
        bool remember = RememberSignInBox.IsChecked == true;
        StatusText.Text = "브라우저에서 공식 계정 인증과 권한 동의를 완료하세요…";
        await _oauth.ConnectAsync(config, url =>
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }), remember, ct);
        await OpenRootAsync(ct);
        CloudSessionHub.NotifyConnectionChanged(_provider);
        StatusText.Text = remember ? "계정 연결됨 · 자동 로그인 사용" : "계정 연결됨 · 이번 실행에서만 로그인 유지";
    });

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (MessageBox.Show(this, "이 계정의 로컬 로그인 정보를 제거하고 연결을 해제할까요? 클라우드 파일은 삭제하지 않습니다.",
                "클라우드 연결 해제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _oauth.Disconnect(_provider);
        CloudSessionHub.NotifyConnectionChanged(_provider);
        _switching = true;
        try { RememberSignInBox.IsChecked = false; }
        finally { _switching = false; }
        _api = null; _rootId = _driveId = _folderId = null;
        RemoteFilesGrid.ItemsSource = null; _parents.Clear();
        FolderLocationText.Text = "연결 해제됨";
        StatusText.Text = "로컬 인증 정보 제거 완료. 클라우드 계정의 서비스 측 권한 철회는 해당 공급자 계정에서 관리하세요.";
    }

    private async Task<(string DriveId, string RootId)> ResolveOneDriveAsync(string token, CancellationToken ct)
    {
        // Fixed allowlisted Microsoft Graph endpoints. Never follow redirects with bearer credentials.
        async Task<JsonDocument> GetAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/drive" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"OneDrive 루트 연결 오류 (HTTP {(int)response.StatusCode}).");
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        using var drive = await GetAsync("");
        using var root = await GetAsync("/root");
        string driveId = drive.RootElement.GetProperty("id").GetString() ?? "";
        string rootId = root.RootElement.GetProperty("id").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(driveId) || string.IsNullOrWhiteSpace(rootId))
            throw new InvalidDataException("OneDrive 드라이브 식별자를 확인하지 못했습니다.");
        return (driveId, rootId);
    }

    private async Task OpenRootAsync(CancellationToken ct)
    {
        CloudOAuthConfig config = Config();
        string access = await _oauth.GetAccessTokenAsync(config, ct);
        _parents.Clear();
        if (_provider == CloudProvider.GoogleDrive)
        {
            _rootId = "root";
            _api = new GoogleDriveApi(_http, token => _oauth.GetAccessTokenAsync(config, token));
        }
        else
        {
            (_driveId, _rootId) = await ResolveOneDriveAsync(access, ct);
            _api = new OneDriveApi(_http, token => _oauth.GetAccessTokenAsync(config, token), _driveId);
        }
        await LoadFolderAsync(_rootId, "내 드라이브", ct);
    }

    private async Task LoadFolderAsync(string id, string name, CancellationToken ct)
    {
        if (_api is null) throw new InvalidOperationException("먼저 클라우드 계정을 연결하세요.");
        var children = await _api.ListChildrenAsync(id, ct);
        _folderId = id;
        _folderName = name;
        FolderLocationText.Text = _provider + " / " + name;
        RemoteFilesGrid.ItemsSource = children.OrderByDescending(x => x.IsFolder)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Select(x => new CloudRow(x)).ToArray();
        StatusText.Text = $"{children.Count:N0}개 항목 · 클라우드에서 직접 조회했습니다.";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async ct =>
    {
        if (_api is null || _folderId is null) await OpenRootAsync(ct);
        else await LoadFolderAsync(_folderId, _folderName, ct);
    });

    private async void RemoteFilesGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RemoteFilesGrid.SelectedItem is not CloudRow { Item.IsFolder: true } selected || _folderId is null) return;
        string previousId = _folderId, previousName = _folderName;
        await ExecuteAsync(async ct =>
        {
            await LoadFolderAsync(selected.Item.Id, selected.Item.Name, ct);
            _parents.Push((previousId, previousName));
        });
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_parents.Count == 0) return;
        var target = _parents.Peek();
        await ExecuteAsync(async ct => { await LoadFolderAsync(target.Id, target.Name, ct); _parents.Pop(); });
    }

    private CloudItem? SelectedOrNotify()
    {
        if (RemoteFilesGrid.SelectedItem is CloudRow row) return row.Item;
        StatusText.Text = "먼저 작업할 파일이나 폴더를 선택하세요.";
        return null;
    }
    private string RequestName(string title, string value)
    {
        var dialog = new NameInputDialog(title, "클라우드 파일 및 폴더 이름", value) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.ResultName : "";
    }
    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _api is null || _folderId is null) return;
        string name = RequestName("새 클라우드 폴더", "새 폴더");
        if (name.Length == 0) return;
        await ExecuteAsync(async ct => { await _api.CreateFolderAsync(_folderId, name, ct); await LoadFolderAsync(_folderId, _folderName, ct); });
    }
    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _api is null || _folderId is null) return;
        CloudItem? item = SelectedOrNotify();
        if (item is null) return;
        string name = RequestName("클라우드 이름 변경", item.Name);
        if (name.Length == 0 || name == item.Name) return;
        await ExecuteAsync(async ct => { await _api.RenameAsync(item.Id, name, ct); await LoadFolderAsync(_folderId, _folderName, ct); });
    }
    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _api is null || _folderId is null) return;
        CloudItem? item = SelectedOrNotify();
        if (item is null) return;
        string name = RequestName("클라우드 파일 복사본 이름", item.Name + " - 복사본");
        if (name.Length == 0) return;
        await ExecuteAsync(async ct =>
        {
            CloudCopyResult result = await _api.CopyAsync(item.Id, _folderId, name, ct);
            if (!result.Completed)
            {
                StatusText.Text = "OneDrive가 비동기 복사를 접수했습니다. 완료 확인 전이므로 복사 완료로 표시하지 않습니다.";
                return;
            }
            await LoadFolderAsync(_folderId, _folderName, ct);
        });
    }
    private CloudFileTransferService TransferService()
    {
        CloudOAuthConfig config = Config();
        return new CloudFileTransferService(_http, ct => _oauth.GetAccessTokenAsync(config, ct), _provider, _driveId);
    }

    private IProgress<CloudTransferProgress> TransferProgress(string verb, string filename)
    {
        TransferProgressBar.Value = 0;
        TransferProgressBar.IsIndeterminate = false;
        TransferProgressBar.Visibility = Visibility.Visible;
        return new Progress<CloudTransferProgress>(p =>
        {
            if (!IsLoaded || Dispatcher.HasShutdownStarted) return;
            TransferProgressBar.IsIndeterminate = p.Total <= 0;
            TransferProgressBar.Value = p.Percent;
            StatusText.Text = p.Total > 0
                ? $"{verb}: {filename} · {p.Percent}% ({p.Completed:N0}/{p.Total:N0} B)"
                : $"{verb}: {filename} · {p.Completed:N0} B";
        });
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _api is null || _folderId is null) return;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "클라우드에 업로드할 파일 선택", CheckFileExists = true, Multiselect = false,
            Filter = "모든 파일|*.*"
        };
        if (picker.ShowDialog(this) != true) return;
        string path = picker.FileName;
        string parent = _folderId, folderName = _folderName;
        ICloudDriveApi api = _api;
        CloudFileTransferService transfer = TransferService();
        await ExecuteAsync(async ct =>
        {
            try
            {
                StatusText.Text = "클라우드 업로드를 시작합니다. 동일 이름은 덮어쓰지 않습니다.";
                string id = await transfer.UploadAsync(parent, path, api,
                    TransferProgress("업로드", Path.GetFileName(path)), ct);
                await LoadFolderAsync(parent, folderName, ct);
                StatusText.Text = "업로드 완료 · 원격 파일 ID가 확인됐습니다.";
            }
            finally { TransferProgressBar.Visibility = Visibility.Collapsed; }
        });
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _api is null || _folderId is null) return;
        CloudItem? item = SelectedOrNotify();
        if (item is null) return;
        if (item.IsFolder) { StatusText.Text = "현재 버전은 파일 내려받기만 지원합니다. 폴더 전체 내려받기는 다음 단계입니다."; return; }
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = "클라우드 파일 저장 위치 선택 (기존 파일 덮어쓰기 금지)",
            FileName = item.Name, OverwritePrompt = false, AddExtension = false,
            Filter = "모든 파일|*.*"
        };
        if (picker.ShowDialog(this) != true) return;
        string target = picker.FileName;
        if (File.Exists(target) || Directory.Exists(target))
        {
            StatusText.Text = "동일 이름의 로컬 파일이 있습니다. 다른 이름이나 폴더를 선택해 주세요.";
            return;
        }
        CloudFileTransferService transfer = TransferService();
        await ExecuteAsync(async ct =>
        {
            try
            {
                StatusText.Text = "내려받는 동안 임시 파일을 사용하며, 완료 시에만 최종 파일명을 적용합니다.";
                await transfer.DownloadAsync(item, target, TransferProgress("다운로드", item.Name), ct);
                StatusText.Text = "다운로드 완료 · 로컬 파일 저장을 확인했습니다.";
            }
            finally { TransferProgressBar.Visibility = Visibility.Collapsed; }
        });
    }

    private async void Trash_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _api is null || _folderId is null) return;
        CloudItem? item = SelectedOrNotify();
        if (item is null) return;
        if (MessageBox.Show(this, $"'{item.Name}' 항목을 {_provider}의 휴지통으로 이동하시겠습니까?",
                "클라우드 휴지통 이동 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await ExecuteAsync(async ct => { await _api.MoveToTrashAsync(item.Id, ct); await LoadFolderAsync(_folderId, _folderName, ct); });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            _closeAfterCancel = true;
            _operation?.Cancel();
            StatusText.Text = "진행 중인 요청을 취소하고 있습니다. 작업 완료 여부는 다시 확인하세요.";
        }
        base.OnClosing(e);
    }
}
