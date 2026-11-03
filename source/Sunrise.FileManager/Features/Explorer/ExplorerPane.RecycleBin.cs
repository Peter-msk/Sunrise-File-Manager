using System.Diagnostics;
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
        "휴지통 항목은 '원래 위치로 복원'한 뒤 열 수 있습니다. Windows 휴지통에서도 직접 관리할 수 있습니다.",
        "휴지통", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ConfigureRecycleBinView()
    {
        _normalFilesMenu ??= FilesGrid.ContextMenu;
        if (IsRecycleBin)
        {
            var menu = new ContextMenu();
            menu.Resources[typeof(MenuItem)] = FindResource("CompactContextMenuItem");
            var restore = new MenuItem { Header = "원래 위치로 복원" };
            restore.Click += RestoreRecycle_Click;
            var restoreAll = new MenuItem { Header = "모든 항목 복원" };
            restoreAll.Click += RestoreAllRecycle_Click;
            var delete = new MenuItem { Header = "선택 항목 영구 삭제…", InputGestureText = "Delete / Shift+Delete" };
            delete.Click += PermanentDeleteRecycle_Click;
            var empty = new MenuItem { Header = "휴지통 비우기…" };
            empty.Click += EmptyRecycle_Click;
            var properties = new MenuItem { Header = "속성" };
            properties.Click += RecycleProperties_Click;
            var windows = new MenuItem { Header = "Windows 휴지통에서 열기" };
            windows.Click += OpenWindowsRecycle_Click;
            var selectAll = new MenuItem { Header = "모두 선택", InputGestureText = "Ctrl+A" };
            selectAll.Click += SelectAll_Click;
            var refresh = new MenuItem { Header = "새로 고침", InputGestureText = "F5" };
            refresh.Click += (_, _) => RefreshCurrent();
            menu.Items.Add(restore);
            menu.Items.Add(restoreAll);
            menu.Items.Add(new Separator());
            menu.Items.Add(delete);
            menu.Items.Add(empty);
            menu.Items.Add(new Separator());
            menu.Items.Add(properties);
            menu.Items.Add(selectAll);
            menu.Items.Add(refresh);
            menu.Items.Add(windows);
            menu.Opened += (_, _) =>
            {
                int selected = FilesGrid.SelectedItems.OfType<FileEntry>().Count(x => x.IsVirtual);
                bool ready = _actionsEnabled && !_restoringRecycle;
                restore.IsEnabled = delete.IsEnabled = properties.IsEnabled = ready && selected > 0;
                restoreAll.IsEnabled = empty.IsEnabled = ready && _allEntries.Count > 0;
            };
            FilesGrid.ContextMenu = menu;
        }
        else if (IsThisPc)
        {
            var menu = new ContextMenu();
            menu.Resources[typeof(MenuItem)] = FindResource("CompactContextMenuItem");
            var open = new MenuItem { Header = "드라이브 열기" }; open.Click += ContextOpen_Click;
            var refresh = new MenuItem { Header = "새로 고침", InputGestureText = "F5" }; refresh.Click += (_, _) => RefreshCurrent();
            var shell = new MenuItem { Header = "Windows 확장 메뉴…" }; shell.Click += WindowsMenu_Click;
            var properties = new MenuItem { Header = "속성", InputGestureText = "Alt+Enter" }; properties.Click += ContextProperties_Click;
            menu.Items.Add(open);
            menu.Items.Add(new Separator());
            menu.Items.Add(shell);
            menu.Items.Add(properties);
            menu.Items.Add(refresh);
            menu.Opened += (_, _) =>
            {
                bool selected = FilesGrid.SelectedItems.OfType<FileEntry>().Any();
                open.IsEnabled = selected;
                shell.IsEnabled = selected && _actionsEnabled;
                properties.IsEnabled = selected;
            };
            FilesGrid.ContextMenu = menu;
        }
        else FilesGrid.ContextMenu = _normalFilesMenu;
        FilesGrid.AllowDrop = !IsRecycleBin && !IsThisPc;
        FileCommandMenu.IsEnabled = true;
        EditCommandMenu.IsEnabled = !IsRecycleBin && !IsThisPc;
        ApplyViewMode(resetIconWidth: false);
    }

    private void RestoreRecycle_Click(object sender, RoutedEventArgs e)
        => _ = RestoreRecycleEntriesAsync(FilesGrid.SelectedItems.OfType<FileEntry>().Where(x => x.IsVirtual).ToArray(), false);

    private void RestoreAllRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (!IsRecycleBin || _restoringRecycle || !_actionsEnabled) return;
        if (_listingWarning is not null)
        {
            MessageBox.Show(Window.GetWindow(this), "목록 일부가 표시되지 않았습니다. 안전한 전체 복원을 위해 Windows 휴지통에서 작업하세요.",
                "휴지통", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _ = RestoreRecycleEntriesAsync(_allEntries.Where(x => x.IsVirtual).ToArray(), true);
    }

    private async Task RestoreRecycleEntriesAsync(FileEntry[] selected, bool all)
    {
        if (!IsRecycleBin || !_actionsEnabled || _restoringRecycle || selected.Length == 0 ||
            BeforeFileChange?.Invoke() == false) return;
        if (MessageBox.Show(Window.GetWindow(this),
                all ? $"휴지통의 {selected.Length:N0}개 항목을 모두 원래 위치로 복원할까요?" :
                $"선택한 {selected.Length:N0}개 항목을 원래 위치로 복원할까요?",
                "휴지통 복원", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _restoringRecycle = true;
        BlockingFileActionChanged?.Invoke(true);
        try
        {
            FooterText.Text = $"Windows에 {selected.Length:N0}개 복원 요청 중…";
            var result = await RecycleBinService.RestoreAsync(selected.Select(x => x.FullPath).ToArray(), CancellationToken.None);
            // Shell verbs may be asynchronous. Always refresh instead of assuming a request completed.
            FooterText.Text = $"복원 요청 {result.Requested:N0}개 · 찾지 못함 {result.Missing:N0}개";
            if (result.Errors.Count > 0 || result.Missing > 0)
                MessageBox.Show(Window.GetWindow(this), FooterText.Text + "\n" + string.Join("\n", result.Errors.Take(8)),
                    "휴지통 복원 결과", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        if (selected.Length == 0 || BeforeFileChange?.Invoke() == false) return;
        string preview = string.Join("\n", selected.Take(5).Select(x => x.Name));
        if (selected.Length > 5) preview += $"\n외 {selected.Length - 5:N0}개";
        if (MessageBox.Show(Window.GetWindow(this),
                $"휴지통에서 선택한 {selected.Length:N0}개 항목을 영구 삭제할까요?\n\n{preview}\n\n" +
                "이 작업은 되돌릴 수 없습니다. 전체 선택 항목을 Windows에서 한 번에 삭제합니다.",
                "휴지통 영구 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _restoringRecycle = true;
        BlockingFileActionChanged?.Invoke(true);
        try
        {
            FooterText.Text = $"Windows Shell 일괄 삭제 중… ({selected.Length:N0}개)";
            var result = await RecycleBinService.PermanentlyDeleteAsync(selected.Select(x => x.FullPath).ToArray(), CancellationToken.None);
            int failed = selected.Length - result.Completed - result.Missing;
            FooterText.Text = $"영구 삭제 {result.Completed:N0}개 · 실패 {Math.Max(0, failed):N0}개 · 이미 없음 {result.Missing:N0}개";
            if (failed > 0 || result.Missing > 0 || result.Errors.Count > 0 || result.Aborted)
                MessageBox.Show(Window.GetWindow(this), FooterText.Text + "\n" + string.Join("\n", result.Errors.Take(8)),
                    "휴지통 영구 삭제 결과", MessageBoxButton.OK, MessageBoxImage.Warning);
            FileActionCompleted?.Invoke();
            RefreshCurrent();
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "휴지통 영구 삭제", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _restoringRecycle = false; BlockingFileActionChanged?.Invoke(false); }
    }

    private async void EmptyRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (!IsRecycleBin || !_actionsEnabled || _restoringRecycle || BeforeFileChange?.Invoke() == false) return;
        if (MessageBox.Show(Window.GetWindow(this),
                "휴지통을 완전히 비울까요?\n\n모든 드라이브에 있는 휴지통 항목이 영구 삭제됩니다.\n" +
                "현재 화면의 검색 및 선택 항목과 관계없이 적용되며 되돌릴 수 없습니다.",
                "휴지통 비우기", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _restoringRecycle = true;
        BlockingFileActionChanged?.Invoke(true);
        try
        {
            FooterText.Text = "Windows 휴지통 비우는 중…";
            var result = await RecycleBinService.EmptyAsync(CancellationToken.None);
            FooterText.Text = result.Aborted ? "휴지통 비우기가 취소되었습니다." :
                result.Remaining == 0 ? "휴지통을 비웠습니다." : $"휴지통에 {result.Remaining:N0}개 항목이 남아 있습니다.";
            if (result.Remaining > 0 || result.Aborted)
                MessageBox.Show(Window.GetWindow(this), FooterText.Text, "휴지통 비우기", MessageBoxButton.OK, MessageBoxImage.Warning);
            FileActionCompleted?.Invoke();
            RefreshCurrent();
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "휴지통 비우기", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _restoringRecycle = false; BlockingFileActionChanged?.Invoke(false); }
    }

    private void RecycleProperties_Click(object sender, RoutedEventArgs e)
    {
        if (!IsRecycleBin) return;
        var selected = FilesGrid.SelectedItems.OfType<FileEntry>().Where(x => x.IsVirtual).ToArray();
        if (selected.Length == 0) return;
        if (selected.Length > 1)
        {
            MessageBox.Show(Window.GetWindow(this),
                $"선택한 항목: {selected.Length:N0}개\n총 크기: {selected.Sum(x => x.SizeBytes):N0}바이트\n" +
                "개별 항목의 원래 위치 및 삭제 날짜는 목록의 열에서 확인할 수 있습니다.",
                "휴지통 선택 항목 속성", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var item = selected[0];
        MessageBox.Show(Window.GetWindow(this),
            $"이름: {item.Name}\n형식: {item.TypeName}\n크기: {item.SizeBytes:N0}바이트\n" +
            $"원래 위치: {item.OriginalLocation}\n삭제한 날짜: {item.DeletedDisplay}\n" +
            "\nWindows 속성 창과 고급 작업은 'Windows 휴지통에서 열기'를 사용하세요.",
            "휴지통 항목 속성", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenWindowsRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (!IsRecycleBin) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", WindowsKnownLocations.RecycleBin)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Windows 휴지통 열기", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
