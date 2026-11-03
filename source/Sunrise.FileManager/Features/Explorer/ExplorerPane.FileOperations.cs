using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>
/// Feature module for ExplorerPane: folder/file creation, single & batch rename (F2),
/// ZIP compression/extraction, duplication (Ctrl+D), Undo (Ctrl+Z), recycle & permanent deletion,
/// and Windows shell integration (Open, Properties, CMD, Explorer).
/// </summary>
public partial class ExplorerPane
{
    private static void OpenOriginal(FileEntry entry)
    {
        if (entry.IsVirtual) return;
        try
        {
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            MessageBox.Show($"파일을 열지 못했습니다.\n{exception.Message}", "열기 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private CancellationTokenSource? _localFileJob;
    internal void CancelLocalFileJob() => _localFileJob?.Cancel();

    private async Task<T> RunLocalFileJobAsync<T>(Func<CancellationToken, T> operation, string status)
    {
        if (_localFileJob is not null) throw new IOException("이 탐색기에서 파일 작업이 진행 중입니다.");
        using var cancellation = new CancellationTokenSource();
        _localFileJob = cancellation;
        bool enabled = IsEnabled;
        IsEnabled = false;
        BlockingFileActionChanged?.Invoke(true);
        PaneStatus.Text = status;
        try { return await Task.Run(() => operation(cancellation.Token), cancellation.Token); }
        finally
        {
            _localFileJob = null;
            IsEnabled = enabled;
            BlockingFileActionChanged?.Invoke(false);
        }
    }

    private async Task DuplicatePathsAsync(IReadOnlyList<string> paths)
    {
        try
        {
            string[] snapshot = paths.ToArray();
            var created = await RunLocalFileJobAsync(token => FileActionsService.DuplicateEntries(snapshot, token), "사본 생성 중…");
            if (created.Count > 0)
            {
                _undoService.RecordDuplicates(created);
                UpdateUndoButtonState();
                _restoreSelectionPath = created[^1];
            }
            if (FileActionCompleted is null) RefreshCurrent();
            else FileActionCompleted.Invoke();
        }
        catch (OperationCanceledException) { PaneStatus.Text = "파일 작업을 취소했습니다."; RefreshCurrent(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            RefreshCurrent();
            MessageBox.Show(Window.GetWindow(this), ex.Message, "사본 만들기 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExecuteCreateFolder()
    {
        if (!_actionsEnabled || ActiveFolder is not string folder) return;
        string suggested = FileActionsService.SuggestNewFolderName(folder);
        var dialog = new NameInputDialog("새 폴더", "새 폴더 이름", suggested) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try
        {
            string created = FileActionsService.CreateFolder(folder, dialog.ResultName);
            _undoService.RecordCreated(created, "새 폴더");
            UpdateUndoButtonState();
            _restoreSelectionPath = created;
            if (FileActionCompleted is null) RefreshCurrent();
            else FileActionCompleted.Invoke();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "새 폴더 생성 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExecuteCreateFile()
    {
        if (!_actionsEnabled || ActiveFolder is not string folder) return;
        string suggested = FileActionsService.SuggestNewFileName(folder);
        var dialog = new NameInputDialog("새 파일 만들기", "파일 이름 (확장자 포함: .txt, .md, .csv, .cmd 등)", suggested) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try
        {
            string created = FileActionsService.CreateFile(folder, dialog.ResultName);
            _undoService.RecordCreated(created, "새 파일");
            UpdateUndoButtonState();
            _restoreSelectionPath = created;
            if (FileActionCompleted is null) RefreshCurrent();
            else FileActionCompleted.Invoke();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "새 파일 생성 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ExecuteZipCompressAsync()
    {
        if (!_actionsEnabled || ActiveFolder is not string folder) return;
        string[] selection = SelectedPaths.ToArray();
        if (selection.Length == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "압축할 파일이나 폴더를 먼저 선택하세요.", "ZIP 압축", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string defaultZipName = selection.Length == 1
            ? Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(selection[0])) + ".zip"
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) + "_압축.zip";
        var dialog = new NameInputDialog("ZIP 압축 파일 생성", "생성할 ZIP 파일 이름", defaultZipName) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try
        {
            string zipPath = Path.Combine(folder, dialog.ResultName);
            string created = await RunLocalFileJobAsync(token => FileActionsService.CompressToZip(selection, zipPath, token), "ZIP 압축 중…");
            _undoService.RecordCreated(created, "ZIP 압축");
            UpdateUndoButtonState();
            _restoreSelectionPath = created;
            if (FileActionCompleted is null) RefreshCurrent();
            else FileActionCompleted.Invoke();
        }
        catch (OperationCanceledException) { PaneStatus.Text = "파일 작업을 취소했습니다."; RefreshCurrent(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "ZIP 압축 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ExecuteZipExtractAsync(bool askForPassword = false)
    {
        if (!_actionsEnabled || ActiveFolder is not string folder) return;
        if (FilesGrid.SelectedItem is not FileEntry entry || entry.IsDirectory ||
            !ArchiveService.CanExtract(entry.FullPath))
        {
            MessageBox.Show(Window.GetWindow(this), "압축을 풀 ZIP·7z·RAR·TAR 파일을 하나 선택하세요.", "압축 풀기", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string defaultFolder = Path.GetFileNameWithoutExtension(entry.Name);
        var dialog = new NameInputDialog("압축 풀기", "압축을 풀 하위 폴더 이름", defaultFolder) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        string? password = null;
        bool askPassword = askForPassword;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (askPassword)
            {
                var prompt = new ArchivePasswordWindow { Owner = Window.GetWindow(this) };
                if (prompt.ShowDialog() != true) return;
                password = prompt.Password;
            }
            try
            {
                string destFolder = Path.Combine(folder, FileActionsService.ValidateName(dialog.ResultName));
                string sourceZip = entry.FullPath;
                string extracted = await RunLocalFileJobAsync(token => ArchiveService.Extract(sourceZip, destFolder, token, password), "압축 해제 중…");
                _undoService.RecordCreated(extracted, "압축 풀기"); UpdateUndoButtonState();
                _restoreSelectionPath = extracted;
                if (FileActionCompleted is null) RefreshCurrent(); else FileActionCompleted.Invoke();
                return;
            }
            catch (ArchiveService.PasswordRequiredException) { askPassword = true; }
            catch (OperationCanceledException) { PaneStatus.Text = "파일 작업을 취소했습니다."; RefreshCurrent(); return; }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                MessageBox.Show(Window.GetWindow(this), "압축 해제에 실패했습니다. 암호·파일 손상·권한을 확인하세요.\n" + ex.GetType().Name, "압축 풀기 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
                if (!askPassword) return;
            }
        }
        PaneStatus.Text = "암호 재시도 횟수를 초과했습니다. 원본 압축 파일은 유지됩니다.";
    }

    private void ExecutePermanentDelete()
    {
        if (!_actionsEnabled || _recycling) return;
        string[] selection = SelectedPaths.ToArray();
        if (selection.Length == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "영구 삭제할 항목을 선택하세요.", "영구 삭제", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (BeforeFileChange?.Invoke() == false) return;
        if (MessageBox.Show(Window.GetWindow(this),
            $"선택한 항목 {selection.Length:N0}개를 휴지통을 거치지 않고 영구적으로 삭제하시겠습니까?\n\n이 작업은 되돌릴 수 없습니다.",
            "영구 삭제 확인 (Shift+Delete)", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var result = FileActionsService.DeletePermanently(selection);
        PaneStatus.Text = $"영구 삭제 {result.Completed:N0}개 · 실패 {result.Failed:N0}개";
        if (result.Failed > 0)
        {
            MessageBox.Show(Window.GetWindow(this),
                $"영구 삭제 완료 {result.Completed}개 · 실패 {result.Failed}개\n\n" + string.Join("\n", result.Errors.Take(8)),
                "영구 삭제 결과", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (FileActionCompleted is null) RefreshCurrent();
        else FileActionCompleted.Invoke();
    }

    private async Task ExecuteRecycleAsync()
    {
        if (!_actionsEnabled || _recycling) return;
        string[] selection = SelectedPaths.ToArray();
        if (selection.Length == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "휴지통으로 보낼 항목을 선택하세요.", "휴지통 이동", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (BeforeFileChange?.Invoke() == false) return;
        string sample = string.Join("\n", selection.Take(5).Select(p => Path.GetFileName(Path.TrimEndingDirectorySeparator(p))));
        if (selection.Length > 5) sample += $"\n외 {selection.Length - 5:N0}개";
        if (MessageBox.Show(Window.GetWindow(this),
                $"선택한 항목 {selection.Length:N0}개를 휴지통으로 이동할까요?\n영구 삭제는 수행하지 않습니다.\n\n{sample}",
                "휴지통 이동 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _recycling = true;
        RecycleButton.IsEnabled = RenameButton.IsEnabled = NewFolderButton.IsEnabled = false;
        BlockingFileActionChanged?.Invoke(true);
        var progress = new UiLatestProgress<int>(Dispatcher, done =>
            PaneStatus.Text = $"휴지통 이동 중 ({done}/{selection.Length})...");
        try
        {
            PaneStatus.Text = $"휴지통 이동 중 (0/{selection.Length})...";
            var result = await FileActionsService.RecycleManyAsync(selection, progress);
            progress.Dispose();
            PaneStatus.Text = $"휴지통 이동 {result.Completed:N0}개 · 실패 {result.Failed:N0}개";
            if (result.Failed > 0)
            {
                MessageBox.Show(Window.GetWindow(this),
                    $"완료 {result.Completed:N0}개 · 실패 {result.Failed:N0}개\n\n" + string.Join("\n", result.Errors.Take(8)),
                    "휴지통 이동 결과", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or Win32Exception or
            OperationCanceledException or System.Security.SecurityException)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "휴지통 이동 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            progress.Dispose();
            _recycling = false;
            BlockingFileActionChanged?.Invoke(false);
            SetFileActionsEnabled(_actionsEnabled);
            if (FileActionCompleted is null) RefreshCurrent();
            else FileActionCompleted.Invoke();
        }
    }

    private void ExecuteExportDirectoryListing()
    {
        if (!_actionsEnabled || ActiveFolder is not string folder) return;
        var visibleEntries = FilesGrid.Items.OfType<FileEntry>().ToArray();
        if (visibleEntries.Length == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "내보낼 항목이 없습니다.", "목록 내보내기", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            string csv = FileActionsService.FormatDirectoryListingCsv(folder, visibleEntries);
            try { Clipboard.SetText(csv); } catch { }
            string defaultName = FileActionsService.SuggestNewFileName(folder, "폴더목록_내보내기.csv");
            var dialog = new NameInputDialog("폴더 목록 내보내기 (Q-Dir)", "저장할 CSV 파일 이름 (클립보드에도 복사됨)", defaultName)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() != true)
            {
                FooterText.Text = $"현재 폴더 목록({visibleEntries.Length:N0}개 항목)을 클립보드에 복사했습니다.";
                return;
            }
            string savedPath = FileActionsService.ExportDirectoryListingToFile(folder, visibleEntries, dialog.ResultName);
            _undoService.RecordCreated(savedPath, "목록 내보내기");
            UpdateUndoButtonState();
            _restoreSelectionPath = savedPath;
            FooterText.Text = $"목록 내보내기 완료: {Path.GetFileName(savedPath)}";
            if (FileActionCompleted is null) RefreshCurrent();
            else FileActionCompleted.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "목록 내보내기 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
