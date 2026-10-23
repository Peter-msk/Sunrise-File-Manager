using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;
namespace Sunrise.FileManager.Controls;
public partial class ExplorerPane
{
    private ContextMenu? _normalFilesMenu;
    private bool _restoringRecycle;
    private void ShowRecycleItemHint() => MessageBox.Show(Window.GetWindow(this),
        "휴지통 항목은 우클릭 메뉴의 '원래 위치로 복원'으로 복원한 뒤 열 수 있습니다.", "휴지통", MessageBoxButton.OK, MessageBoxImage.Information);
    private void ConfigureRecycleBinView()
    {
        _normalFilesMenu ??= FilesGrid.ContextMenu;
        if (IsRecycleBin)
        {
            var menu = new ContextMenu();
            menu.Resources[typeof(MenuItem)] = FindResource("CompactContextMenuItem");
            var restore = new MenuItem { Header = "원래 위치로 복원" };
            restore.Click += RestoreRecycle_Click;
            var permanent = new MenuItem { Header = "선택 항목 영구 삭제…" };
            permanent.Click += PermanentDeleteRecycle_Click;
            var refresh = new MenuItem { Header = "새로 고침" };
            refresh.Click += (_, _) => RefreshCurrent();
            menu.Items.Add(restore); menu.Items.Add(permanent); menu.Items.Add(new Separator()); menu.Items.Add(refresh);
            FilesGrid.ContextMenu = menu;
        }
        else if (IsThisPc)
        {
            var menu = new ContextMenu();
            menu.Resources[typeof(MenuItem)] = FindResource("CompactContextMenuItem");
            var open = new MenuItem { Header = "드라이브 열기" }; open.Click += ContextOpen_Click;
            var refresh = new MenuItem { Header = "새로 고침" }; refresh.Click += (_, _) => RefreshCurrent();
            menu.Items.Add(open); menu.Items.Add(refresh); FilesGrid.ContextMenu = menu;
        }
        else FilesGrid.ContextMenu = _normalFilesMenu;
        FilesGrid.AllowDrop = !IsRecycleBin && !IsThisPc;
        FileCommandMenu.IsEnabled = true;
        EditCommandMenu.IsEnabled = !IsRecycleBin && !IsThisPc;
        ApplyViewMode(resetIconWidth: false);
    }
    private async void RestoreRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (!IsRecycleBin || !_actionsEnabled || _restoringRecycle) return;
        var selected = FilesGrid.SelectedItems.OfType<FileEntry>().Where(x => x.IsVirtual).ToArray();
        if (selected.Length == 0 || BeforeFileChange?.Invoke() == false) return;
        if (MessageBox.Show(Window.GetWindow(this), $"선택한 {selected.Length}개 항목을 원래 위치로 복원할까요?", "휴지통 복원", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _restoringRecycle = true;
        BlockingFileActionChanged?.Invoke(true);
        try
        {
            FooterText.Text = "Windows에 휴지통 복원을 요청하는 중...";
            var result = await RecycleBinService.RestoreAsync(selected.Select(x => x.FullPath).ToArray(), CancellationToken.None);
            MessageBox.Show(Window.GetWindow(this), $"Windows에 {result.Requested}개 복원을 요청했습니다.\n이미 이동되거나 찾을 수 없는 항목: {result.Missing}개\n처리 후 새로 고침으로 결과를 확인하세요." +
                (result.Errors.Count == 0 ? "" : "\n\n" + string.Join("\n", result.Errors)), "휴지통 복원 요청 결과", MessageBoxButton.OK,
                result.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            FileActionCompleted?.Invoke();
            RefreshCurrent();
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "휴지통 복원", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _restoringRecycle = false; BlockingFileActionChanged?.Invoke(false); }
    }
    private async void PermanentDeleteRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (!IsRecycleBin || !_actionsEnabled || _restoringRecycle) return;
        var selected = FilesGrid.SelectedItems.OfType<FileEntry>().Where(x => x.IsVirtual).ToArray();
        if (selected.Length == 0 || selected.Length > 100 || BeforeFileChange?.Invoke() == false) return;
        string preview = string.Join("\n", selected.Take(5).Select(x => x.Name));
        if (MessageBox.Show(Window.GetWindow(this),
                $"휴지통에서 선택한 {selected.Length:N0}개 항목을 영구 삭제할까요?\n\n{preview}\n\n이 작업은 원래 위치로 복원할 수 없습니다.\nWindows Shell에 삭제를 요청하기 직전 항목을 다시 확인합니다.",
                "휴지통 영구 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _restoringRecycle = true;
        BlockingFileActionChanged?.Invoke(true);
        try
        {
            FooterText.Text = "Windows Shell에 영구 삭제 요청 중…";
            var result = await RecycleBinService.PermanentlyDeleteAsync(selected.Select(x => x.FullPath).ToArray(), CancellationToken.None);
            MessageBox.Show(Window.GetWindow(this), $"Shell 삭제 요청 {result.Requested}개 · 찾지 못함 {result.Missing}개\n" +
                (result.Errors.Count == 0 ? "새로 고침 후 남은 항목을 확인하세요." : string.Join("\n", result.Errors)),
                "휴지통 영구 삭제 요청 결과", MessageBoxButton.OK, result.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            RefreshCurrent();
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "휴지통 영구 삭제", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _restoringRecycle = false; BlockingFileActionChanged?.Invoke(false); }
    }
}
