using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

public partial class ExplorerPane
{
    private readonly WindowsShareService _windowsShare = new();
    private void WindowsLocations_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string location }) return;
        var dialog = new WindowsLocationsWindow(location == "shell:Recent" ? "최근 파일 · 관련 폴더" : location == "shell:Libraries" ? "Windows 라이브러리" : "네트워크 장치 · 공유", location) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedPath is string path)
        {
            if (Directory.Exists(path)) TryNavigate(path);
            else if (File.Exists(path)) OpenOriginal(FileEntry.From(new FileInfo(path)));
        }
    }
    private void ConnectDrive_Click(object sender, RoutedEventArgs e) => RunDriveDialog(true);
    private void DisconnectDrive_Click(object sender, RoutedEventArgs e) => RunDriveDialog(false);
    private void RunDriveDialog(bool connect)
    {
        if (!_actionsEnabled || BeforeFileChange?.Invoke() == false) return;
        try
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)).Handle;
            if (connect) WindowsCollectionsService.ConnectDrive(hwnd); else WindowsCollectionsService.DisconnectDrive(hwnd);
            NavigationTree.ReloadRoots(); RefreshCurrent();
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "네트워크 드라이브"); }
    }
    private void OneDriveAccounts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var accounts = WindowsCloudFilesService.Accounts();
            if (accounts.Count == 0) { MessageBox.Show(Window.GetWindow(this), "Windows OneDrive에 연결된 계정 폴더가 없습니다. Windows OneDrive에서 계정을 연결하세요.", "OneDrive"); return; }
            var menu = new ContextMenu();
            foreach (var account in accounts)
            {
                var item = new MenuItem { Header = account.Path };
                item.Click += (_, _) => TryNavigate(account.Path); menu.Items.Add(item);
            }
            menu.PlacementTarget = this; menu.IsOpen = true;
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "OneDrive"); }
    }
    private async void CloudOffline_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled || sender is not MenuItem { Tag: string mode } || SelectedPaths.Count != 1) return;
        string path = SelectedPaths[0];
        if (Directory.Exists(path)) { MessageBox.Show(Window.GetWindow(this), "파일을 하나 선택하세요. 폴더 전체 설정은 Windows 확장 메뉴에서 지원됩니다.", "클라우드 파일"); return; }
        bool keep = mode == "keep";
        if (!keep && MessageBox.Show(Window.GetWindow(this), "고정을 해제하고 공간 확보를 요청하시겠습니까? 실제 전환 시점은 동기화 공급자가 결정하며, 이후 열 때 인터넷 연결이 필요할 수 있습니다.", "공간 확보", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (BeforeFileChange?.Invoke() == false) return;
        try
        {
            await RunLocalFileJobAsync(token => { WindowsCloudFilesService.SetOfflineAsync(path, keep, token).GetAwaiter().GetResult(); return path; }, keep ? "클라우드 파일 다운로드 중…" : "클라우드 파일 공간 확보 중…");
            RefreshCurrent();
            if (!keep) MessageBox.Show(Window.GetWindow(this), "공간 확보를 요청했습니다. 실제 온라인 전용 전환은 Windows 동기화 공급자 정책에 따릅니다.", "클라우드 파일");
        }
        catch (OperationCanceledException) { PaneStatus.Text = "클라우드 작업 취소 요청됨 · 공급자 상태를 확인하세요."; }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "클라우드 파일 처리 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void PhotoGallery_Click(object sender, RoutedEventArgs e) => new PhotoGalleryWindow(ActiveFolder) { Owner = Window.GetWindow(this) }.ShowDialog();
    private void MetadataEdit_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled || FilesGrid.SelectedItem is not FileEntry { IsDirectory: false, IsVirtual: false } entry || BeforeFileChange?.Invoke() == false) return;
        try { new MetadataEditorWindow(entry.FullPath) { Owner = Window.GetWindow(this) }.ShowDialog(); RefreshCurrent(); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "메타데이터 편집", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void SearchScope_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } || !Enum.TryParse(value, out ExplorerSearchScope scope)) return;
        _searchScope = scope;
        SearchBox.ToolTip = scope switch
        {
            ExplorerSearchScope.Descendants => "하위 폴더 포함 · 파일 이름 검색 (Esc: 취소)",
            ExplorerSearchScope.Contents => "하위 폴더 포함 · 파일 이름과 지원 문서 본문 검색 (Esc: 취소)",
            ExplorerSearchScope.Index => "Windows 인덱스에 등록된 파일 이름과 본문 검색",
            _ => "현재 폴더의 파일 이름 필터"
        };
        SearchModeLabel.Text = scope switch { ExplorerSearchScope.Descendants => "하위 폴더 검색", ExplorerSearchScope.Contents => "이름·본문 검색", ExplorerSearchScope.Index => "인덱스 검색", _ => "현재 폴더에서 검색" };
        _filterTimer.Interval = TimeSpan.FromMilliseconds(scope == ExplorerSearchScope.Current ? 90 : 600);
        _ = ApplyFilterAndSortAsync();
        SettingsChanged?.Invoke();
    }
    private void GroupField_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string field }) return;
        _groupField = field;
        _ = ApplyFilterAndSortAsync();
        SettingsChanged?.Invoke();
    }
    private async void TarCompress_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled || ActiveFolder is not string folder || SelectedPaths.Count == 0) return;
        bool sevenZip = sender is MenuItem { Tag: "7z" };
        string format = sevenZip ? "7z" : "tar";
        var dialog = new NameInputDialog(format + " 압축", "생성할 압축 파일 이름", "압축." + format) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        string[] paths = SelectedPaths.ToArray();
        try
        {
            string name = dialog.ResultName.EndsWith("." + format, StringComparison.OrdinalIgnoreCase) ? dialog.ResultName : dialog.ResultName + "." + format;
            string created = await RunLocalFileJobAsync(token => sevenZip ? ArchiveService.CompressSevenZip(paths, Path.Combine(folder, FileActionsService.ValidateName(name)), token) : ArchiveService.CompressTar(paths, Path.Combine(folder, FileActionsService.ValidateName(name)), token), format + " 압축 중…");
            _undoService.RecordCreated(created, format + " 압축"); UpdateUndoButtonState(); RefreshCurrent();
        }
        catch (OperationCanceledException) { PaneStatus.Text = "압축 취소됨"; }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, format + " 압축 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void WindowsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (IsRecycleBin) { RecycleWindowsMenu_Click(sender, e); return; }
        if (!_actionsEnabled || BeforeFileChange?.Invoke() == false) return;
        try { WindowsShellContextMenu.Show(Window.GetWindow(this), SelectedPaths); RefreshCurrent(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or System.ComponentModel.Win32Exception)
        { MessageBox.Show(Window.GetWindow(this), ex.Message, "Windows 메뉴", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void WindowsShare_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled) return;
        try { _windowsShare.Show(Window.GetWindow(this), SelectedPaths); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "파일 공유", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
