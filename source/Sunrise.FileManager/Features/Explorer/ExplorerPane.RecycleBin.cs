using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;
namespace Sunrise.FileManager.Controls;
public partial class ExplorerPane
{
    private ContextMenu? _normalFilesMenu;
    private bool _restoringRecycle;
    private string[] RecycleSelection() => FilesGrid.SelectedItems.OfType<FileEntry>()
        .Where(x => x.IsVirtual).Select(x => x.FullPath).Distinct(StringComparer.Ordinal).ToArray();
    private IntPtr RecycleOwner => new WindowInteropHelper(Window.GetWindow(this)).Handle;
    private void ShowRecycleItemHint() => MessageBox.Show(Window.GetWindow(this),
        "휴지통 항목은 '원래 위치로 복원'으로 복원한 뒤 열 수 있습니다.", "휴지통", MessageBoxButton.OK, MessageBoxImage.Information);
    private void ConfigureRecycleBinView()
    {
        _normalFilesMenu ??= FilesGrid.ContextMenu;
        RecycleCommandMenu.Visibility = IsRecycleBin ? Visibility.Visible : Visibility.Collapsed;
        if (IsRecycleBin)
        {
            var menu = new ContextMenu();
            menu.Resources[typeof(MenuItem)] = FindResource("CompactContextMenuItem");
            AddRecycleCommand(menu.Items, "원래 위치로 복원", RestoreRecycle_Click, selection: true);
            AddRecycleCommand(menu.Items, "영구 삭제…", PermanentDeleteRecycle_Click, selection: true, gesture: "Delete");
            AddRecycleCommand(menu.Items, "속성", RecycleProperties_Click, selection: true, gesture: "Alt+Enter");
            AddRecycleCommand(menu.Items, "Windows 기본 메뉴…", RecycleWindowsMenu_Click, selection: true);
            menu.Items.Add(new Separator());
            AddRecycleCommand(menu.Items, "모든 항목 복원…", RestoreAllRecycle_Click);
            AddRecycleCommand(menu.Items, "휴지통 비우기…", EmptyRecycle_Click);
            menu.Items.Add(new Separator());
            AddRecycleCommand(menu.Items, "모두 선택", SelectAll_Click, gesture: "Ctrl+A");
            AddRecycleCommand(menu.Items, "새로 고침", Refresh_Click, gesture: "F5");
            AddRecycleCommand(menu.Items, "휴지통 속성·설정…", RecycleBinProperties_Click);
            AddRecycleCommand(menu.Items, "Windows 휴지통 열기", OpenNativeRecycle_Click);
            menu.Opened += (_, _) => UpdateRecycleCommands(menu.Items);
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
        RecycleButton.ToolTip = IsRecycleBin ? "선택 항목 영구 삭제 (Delete)" : "휴지통으로 이동 (Delete) · 영구 삭제는 Shift+Delete";
        ApplyViewMode(resetIconWidth: false);
    }
    private static void AddRecycleCommand(ItemCollection items, string title, RoutedEventHandler handler, bool selection = false, string? gesture = null)
    {
        var item = new MenuItem { Header = title, Tag = selection ? "RecycleSelection" : null, InputGestureText = gesture ?? "" };
        item.Click += handler; items.Add(item);
    }
    private void UpdateRecycleCommands(ItemCollection items)
    {
        bool selected = RecycleSelection().Length > 0;
        foreach (var item in items.OfType<MenuItem>())
            if (Equals(item.Tag, "RecycleSelection")) item.IsEnabled = selected && _actionsEnabled && !_restoringRecycle;
    }
    private void RecycleMenu_Opened(object sender, RoutedEventArgs e) => UpdateRecycleCommands(RecycleCommandMenu.Items);

    private async Task RunRecycleActionAsync(string title, Func<IntPtr, Task> action, bool changesFiles = true)
    {
        if (!IsRecycleBin || !_actionsEnabled || _restoringRecycle || (changesFiles && BeforeFileChange?.Invoke() == false)) return;
        string? location = CurrentLocation;
        _restoringRecycle = true; BlockingFileActionChanged?.Invoke(true);
        try
        {
            FooterText.Text = title + " — Windows에서 처리 중…";
            await action(RecycleOwner);
            if (changesFiles) FileActionCompleted?.Invoke();
            if (CurrentLocation == location) RefreshCurrent();
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _restoringRecycle = false; BlockingFileActionChanged?.Invoke(false); }
    }
    private void ShowRecycleRequestResult(RecycleRestoreResult result)
    {
        // Invocation is a request, not proof of completion. Refresh the actual Shell listing.
        if (result.Missing > 0 || result.Errors.Count > 0)
            MessageBox.Show(Window.GetWindow(this), $"찾지 못한 항목: {result.Missing:N0}개\n" + string.Join("\n", result.Errors),
                "휴지통 작업 결과", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private async void RestoreRecycle_Click(object sender, RoutedEventArgs e)
    {
        string[] selected = RecycleSelection(); if (selected.Length == 0) return;
        await RunRecycleActionAsync("휴지통 복원", async owner =>
            ShowRecycleRequestResult(await RecycleBinService.RestoreAsync(selected, CancellationToken.None, owner)));
    }
    private async void RestoreAllRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (!IsRecycleBin || !_actionsEnabled || _restoringRecycle) return;
        if (MessageBox.Show(Window.GetWindow(this), "휴지통의 모든 항목을 원래 위치로 복원할까요?\n현재 검색·필터·선택과 관계없이 전체 휴지통에 적용됩니다.",
            "휴지통 전체 복원", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunRecycleActionAsync("휴지통 전체 복원", async owner =>
            ShowRecycleRequestResult(await RecycleBinService.RestoreAllAsync(CancellationToken.None, owner)));
    }
    private async void PermanentDeleteRecycle_Click(object sender, RoutedEventArgs e)
    {
        string[] selected = RecycleSelection(); if (selected.Length == 0) return;
        // Windows receives one selection and displays its one aggregate confirmation.
        await RunRecycleActionAsync("휴지통 영구 삭제", async owner =>
            ShowRecycleRequestResult(await RecycleBinService.PermanentlyDeleteAsync(selected, CancellationToken.None, owner)));
    }
    private async void EmptyRecycle_Click(object sender, RoutedEventArgs e) =>
        await RunRecycleActionAsync("휴지통 비우기", async owner => { await RecycleBinService.EmptyAsync(owner, CancellationToken.None); });
    private async void RecycleProperties_Click(object sender, RoutedEventArgs e)
    {
        string[] selected = RecycleSelection(); if (selected.Length == 0) return;
        await RunRecycleActionAsync("휴지통 항목 속성", async owner =>
            ShowRecycleRequestResult(await RecycleBinService.PropertiesAsync(selected, owner)), changesFiles: false);
    }
    private async void RecycleBinProperties_Click(object sender, RoutedEventArgs e) =>
        await RunRecycleActionAsync("휴지통 속성", async owner => { await RecycleBinService.BinPropertiesAsync(owner); }, changesFiles: false);
    private void RecycleWindowsMenu_Click(object sender, RoutedEventArgs e)
    {
        string[] selected = RecycleSelection();
        if (!IsRecycleBin || !_actionsEnabled || _restoringRecycle || selected.Length == 0 || BeforeFileChange?.Invoke() == false) return;
        _restoringRecycle = true; BlockingFileActionChanged?.Invoke(true);
        try { RecycleBinService.ShowWindowsMenu(Window.GetWindow(this), selected); FileActionCompleted?.Invoke(); RefreshCurrent(); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Windows 휴지통 메뉴", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _restoringRecycle = false; BlockingFileActionChanged?.Invoke(false); }
    }
    private void OpenNativeRecycle_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Windows 휴지통", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
