using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>
/// Windows Explorer-like primary commands, with selection/blank-area context owned by
/// one menu. The Recycle Bin and This PC virtual namespaces retain their own menus.
/// OS-registered verbs remain delegated to WindowsShellContextMenu.
/// </summary>
public partial class ExplorerPane
{
    private void FilesContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || IsRecycleBin || IsThisPc) return;
        var paths = SelectedPaths;
        bool selected = paths.Count > 0;
        bool single = paths.Count == 1;
        bool folder = single && Directory.Exists(paths[0]);
        bool file = single && File.Exists(paths[0]);
        bool ready = _actionsEnabled;
        bool writableFolder = ready && ActiveFolder is string current && Directory.Exists(current);
        bool containsFiles = false;
        try { containsFiles = Clipboard.ContainsFileDropList(); }
        catch (ExternalException) { /* Clipboard may be locked by another process. */ }
        bool canPaste = writableFolder && containsFiles && (!selected || folder);
        bool hasCommonParent = selected && paths.Count <= 1000 &&
            paths.All(path => string.Equals(Path.GetDirectoryName(path),
                Path.GetDirectoryName(paths[0]), StringComparison.OrdinalIgnoreCase));

        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            switch (item.Tag as string)
            {
                case "selected": item.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = ready && selected; break;
                case "single": item.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = ready && single; break;
                case "single-folder": item.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = ready && folder; break;
                case "single-file": item.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = ready && file; break;
                case "blank": item.Visibility = !selected ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = writableFolder; break;
                case "paste": item.Visibility = (!selected || folder) ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = canPaste; break;
                case "paste-shortcut": item.Visibility = !selected ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = writableFolder && containsFiles; break;
                case "undo": item.Visibility = !selected ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = ready && _undoService.CanUndo; break;
                case "redo": item.Visibility = !selected ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = ready && _undoService.CanRedo; break;
                case "properties": item.Visibility = Visibility.Visible;
                    item.IsEnabled = (!selected && ActiveFolder is string path && Directory.Exists(path)) ||
                        (selected && (single || hasCommonParent)); break;
            }
        }

        // Direct Sunrise commands existed in R03 and must stay immediately accessible.
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
            if (item.Header?.ToString() == "EIASS 문서명 설정…")
                item.IsEnabled = ready && paths.Any(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

        // Native shell verbs require selected items from one physical directory.
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
            if (item.Header?.ToString() == "Windows 확장 메뉴…")
            {
                item.IsEnabled = ready && hasCommonParent;
                item.ToolTip = hasCommonParent ? "Windows에 등록된 기존 탐색기 메뉴·확장 기능" :
                    "Windows 확장 메뉴는 같은 폴더의 실제 항목을 선택했을 때 사용할 수 있습니다.";
            }

        foreach (MenuItem item in menu.Items.OfType<MenuItem>().Where(i => i.Header?.ToString() == "파일 작업"))
        {
            foreach (MenuItem child in item.Items.OfType<MenuItem>())
            {
                string label = child.Header?.ToString() ?? "";
                child.IsEnabled = ready && (label switch
                {
                    "즐겨찾기에 고정" => folder,
                    "사본 만들기" or "바로가기 만들기" or "영구 삭제…" => selected,
                    _ => selected
                });
            }
        }
        foreach (MenuItem item in menu.Items.OfType<MenuItem>().Where(i => i.Header?.ToString() == "Sunrise 문서·도구"))
        {
            foreach (MenuItem child in item.Items.OfType<MenuItem>())
            {
                string label = child.Header?.ToString() ?? "";
                child.IsEnabled = ready && (label switch
                {
                    "EIASS 문서명 설정…" => paths.Any(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)),
                    "암호 입력 후 풀기…" or "압축 풀기…" or "내용 보기·선택 항목 풀기…" =>
                        file && ArchiveService.CanExtract(paths[0]),
                    "태그·작성자·평점 편집…" => file,
                    _ => selected
                });
            }
        }
        foreach (MenuItem item in menu.Items.OfType<MenuItem>().Where(i => i.Header?.ToString() == "클라우드 파일"))
            item.IsEnabled = ready && file;
        foreach (MenuItem item in menu.Items.OfType<MenuItem>().Where(i => i.Header?.ToString() == "정렬 기준"))
        {
            foreach (MenuItem child in item.Items.OfType<MenuItem>())
            {
                string? key = child.Tag as string;
                if (key is null) continue;
                child.IsCheckable = true;
                child.IsChecked = key == "sort:" + _sortField ||
                    key == (_sortDescending ? "order:desc" : "order:asc");
            }
        }
        CompactContextMenuSeparators(menu);
    }

    private static void CompactContextMenuSeparators(ContextMenu menu)
    {
        // Keep only one separator between groups that actually remain visible.
        Separator? pending = null;
        bool seenCommand = false;
        foreach (object raw in menu.Items)
        {
            if (raw is Separator separator)
            {
                separator.Visibility = Visibility.Collapsed;
                pending ??= separator;
            }
            else if (raw is MenuItem item && item.Visibility == Visibility.Visible)
            {
                if (seenCommand && pending is not null) pending.Visibility = Visibility.Visible;
                pending = null;
                seenCommand = true;
            }
        }
    }

    private async void ContextPaste_Click(object sender, RoutedEventArgs e)
    {
        // Right-clicking a directory pastes into that directory; right-clicking the
        // background pastes into the active folder. No clipboard operation is invented.
        string? folder = SelectedPaths.Count == 1 && Directory.Exists(SelectedPaths[0])
            ? SelectedPaths[0] : ActiveFolder;
        await ExecuteClipboardPasteAsync(folder);
    }

    private void ContextSort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key }) return;
        if (key.StartsWith("sort:", StringComparison.Ordinal))
        {
            _sortField = key[5..];
            _sortDescending = false;
        }
        else if (key == "order:asc") _sortDescending = false;
        else if (key == "order:desc") _sortDescending = true;
        else return;
        foreach (DataGridColumn column in FilesGrid.Columns)
            column.SortDirection = column.SortMemberPath == _sortField
                ? (_sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending) : null;
        _ = ApplyFilterAndSortAsync();
        SettingsChanged?.Invoke();
    }
}
