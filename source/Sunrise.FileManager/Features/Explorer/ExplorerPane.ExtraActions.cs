using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

public partial class ExplorerPane
{
    private void AdvancedFilter_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NameInputDialog("현재 폴더 고급 필터", "예: ext:pdf,docx size:>=1MiB modified:>=2026-01-01\n이름: name:\"보고서*.pdf\" / 폴더: kind:folder", SearchBox.Text,
            value => { ExplorerQuery.Parse(value); return value.Trim(); }) { Owner = Window.GetWindow(this), Width = 680 };
        if (dialog.ShowDialog() != true) return;
        try { ExplorerQuery.Parse(dialog.ResultName); _searchScope = ExplorerSearchScope.Current; SearchModeLabel.Text = "현재 폴더에서 검색"; SearchBox.Text = dialog.ResultName; _ = ApplyFilterAndSortAsync(); SettingsChanged?.Invoke(); }
        catch (ArgumentException ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "검색 조건"); }
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _pagedSelection.Clear();
        FilesGrid.UnselectAll();
        foreach (var entry in FilesGrid.Items.OfType<FileEntry>()) entry.IsChecked = false;
        UpdateFooter(); SelectionContextChanged?.Invoke(this);
    }
    private async void CreateShortcut_Click(object sender, RoutedEventArgs e) => await CreateShortcutsAsync(SelectedPaths.ToArray());
    private async void PasteShortcut_Click(object sender, RoutedEventArgs e)
    {
        try { if (Clipboard.ContainsFileDropList()) await CreateShortcutsAsync(Clipboard.GetFileDropList().Cast<string>().ToArray()); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "바로가기 붙여넣기"); }
    }
    private async Task CreateShortcutsAsync(IReadOnlyList<string> paths)
    {
        if (!_actionsEnabled || ActiveFolder is not string folder || paths.Count == 0 || BeforeFileChange?.Invoke() == false) return;
        if (paths.Count > 1000) { MessageBox.Show("바로가기는 한 번에 1,000개까지 만들 수 있습니다."); return; }
        try
        {
            var result = await RunLocalFileJobAsync(token => ExplorerExtraActions.CreateShortcutsAsync(paths, folder, token).GetAwaiter().GetResult(), "바로가기 만드는 중…");
            _undoService.RecordDuplicates(result.Created);
            PaneStatus.Text = $"바로가기 생성 {result.Created.Count}개 · 실패 {result.Errors.Count}개" + (result.Canceled ? " · 취소됨" : "");
            if (result.Errors.Count > 0) MessageBox.Show(Window.GetWindow(this), string.Join("\n", result.Errors.Take(5)), "바로가기 만들기");
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "바로가기 만들기"); }
        finally { UpdateUndoButtonState(); RefreshCurrent(); }
    }
    private void SetFileAttribute_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled || sender is not MenuItem { Tag: string mode } || SelectedPaths.Count == 0 || BeforeFileChange?.Invoke() == false) return;
        var paths = SelectedPaths.ToArray();
        bool value = mode.EndsWith("on", StringComparison.Ordinal);
        FileAttributes flag = mode.StartsWith("hidden", StringComparison.Ordinal) ? FileAttributes.Hidden : FileAttributes.ReadOnly;
        int completed = 0; var errors = new List<string>();
        foreach (string path in paths)
        {
            try { ExplorerExtraActions.SetAttribute(path, flag, value); completed++; }
            catch (Exception ex) { errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
        }
        RefreshCurrent();
        MessageBox.Show(Window.GetWindow(this), $"속성 변경 {completed}개 · 실패 {errors.Count}개\n" + string.Join("\n", errors.Take(5)), "파일 속성");
    }
    private async void PasswordExtract_Click(object sender, RoutedEventArgs e) => await ExecuteZipExtractAsync(true);
    private async void ArchiveContents_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled || ActiveFolder is not string folder || SelectedPaths.Count != 1) return;
        string path = SelectedPaths[0];
        if (!ArchiveService.CanExtract(path)) return;
        var browser = new ArchiveContentsWindow(path) { Owner = Window.GetWindow(this) };
        bool? extracting = browser.ShowDialog();
        if (browser.WasModified) RefreshCurrent();
        if (extracting != true) return;
        var name = new NameInputDialog("압축 항목 풀기", "새 하위 폴더 이름", System.IO.Path.GetFileNameWithoutExtension(path)) { Owner = Window.GetWindow(this) };
        if (name.ShowDialog() != true) return;
        try
        {
            string result = await RunLocalFileJobAsync(token => ArchiveService.Extract(path, Path.Combine(folder, name.ResultName), token, browser.Password, browser.SelectedKeys), "선택 압축 항목 해제 중…");
            _undoService.RecordCreated(result, "선택 압축 항목 풀기"); UpdateUndoButtonState(); RefreshCurrent();
        }
        catch (OperationCanceledException) { PaneStatus.Text = "압축 해제 취소됨"; }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "압축 항목 풀기 실패"); }
    }

    private async void OcrRead_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled || SelectedPaths.Count != 1) return;
        string path = SelectedPaths[0];
        if (Directory.Exists(path)) return;
        int first = 1, last = 1;
        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var pages = new NameInputDialog("PDF OCR", "읽을 쪽 범위 (예: 1 또는 1-5, 한 번에 최대 20쪽)", "1") { Owner = Window.GetWindow(this) };
            if (pages.ShowDialog() != true) return;
            string[] range = pages.ResultName.Split('-');
            if (range.Length is < 1 or > 2 || !int.TryParse(range[0], out first) || !int.TryParse(range[^1], out last) || first < 1 || last < first || last - first >= 20)
            { MessageBox.Show("1부터 시작하는 쪽 범위를 최대 20쪽까지 지정하세요."); return; }
        }
        try
        {
            string text = await RunLocalFileJobAsync(token => WindowsOcrService.ReadAsync(path, first, last, token).GetAwaiter().GetResult(), "Windows OCR 처리 중…");
            var panel = new DockPanel { Margin = new Thickness(12) };
            var copy = new Button { Content = "텍스트 복사", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 8) };
            copy.Click += (_, _) => Clipboard.SetText(text.Length > 0 ? text : "인식된 텍스트가 없습니다."); DockPanel.SetDock(copy, Dock.Top); panel.Children.Add(copy);
            panel.Children.Add(new TextBox { Text = text.Length == 0 ? "인식된 텍스트가 없습니다." : text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var result = new Window { Title = "Windows OCR · " + Path.GetFileName(path), Owner = Window.GetWindow(this), Content = panel, Width = 720, Height = 540, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            result.SetResourceReference(BackgroundProperty, "AppBackground"); result.SetResourceReference(ForegroundProperty, "TextPrimary"); result.ShowDialog();
        }
        catch (OperationCanceledException) { PaneStatus.Text = "OCR 취소 요청됨"; }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Windows OCR"); }
    }

    private async void ExportAllResults_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled || ActiveFolder is not string folder) return;
        var dialog = new NameInputDialog("전체 결과 목록 내보내기", "현재 정렬·필터에 맞는 전체 결과를 저장할 CSV 이름", FileActionsService.SuggestNewFileName(folder, "전체목록.csv")) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        string query = SearchBox.Text.Trim(), field = _sortField, category = _categoryFilter;
        bool descending = _sortDescending, hidden = _showHidden;
        var scope = _searchScope;
        try
        {
            string created = await RunLocalFileJobAsync(token =>
            {
                IReadOnlyList<FileEntry> entries;
                if (scope == ExplorerSearchScope.Current || query.Length == 0)
                {
                    var listing = DirectoryScanner.ReadAsync(folder, token, hidden, ExplorerSearchService.MaxResults).GetAwaiter().GetResult();
                    if (listing.Warning is not null) throw new IOException("전체 목록을 수집하지 못했습니다: " + listing.Warning);
                    entries = listing.Entries;
                }
                else
                {
                    var result = ExplorerSearchService.SearchAsync(folder, query, scope, hidden, token).GetAwaiter().GetResult();
                    if (result.Entries.Count == ExplorerSearchService.MaxResults) throw new IOException("검색 결과 상한에 도달했습니다. 범위를 좁힌 뒤 내보내세요.");
                    entries = result.Entries;
                }
                var rows = FileListView.Apply(entries, scope == ExplorerSearchScope.Current ? query : "", field, descending, token, category, hidden);
                token.ThrowIfCancellationRequested();
                return FileActionsService.ExportDirectoryListingToFile(folder, rows, dialog.ResultName);
            }, "전체 결과 목록 내보내기 중…");
            _undoService.RecordCreated(created, "전체 목록 내보내기"); UpdateUndoButtonState(); RefreshCurrent();
        }
        catch (OperationCanceledException) { PaneStatus.Text = "내보내기를 취소했습니다."; }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "전체 목록 내보내기"); }
    }
}
