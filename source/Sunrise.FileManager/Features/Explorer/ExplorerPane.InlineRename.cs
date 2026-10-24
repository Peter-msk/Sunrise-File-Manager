using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>Single-entry rename lifecycle; batch rename retains its dedicated dialog.</summary>
public partial class ExplorerPane
{
    private void BeginInlineRename(FileEntry entry)
    {
        if (!_actionsEnabled || IsRecycleBin || IsThisPc || entry.IsVirtual || entry.IsNavigationLocation)
            return;
        if (_renamingEntry is not null) FinishInlineRename(false);
        _renameShowsExtension = _showExtensions;
        _renamingEntry = entry;
        entry.RenameDraft = FileRenameNamePolicy.EditorText(entry.Name, entry.IsDirectory, _renameShowsExtension);
        FilesGrid.ScrollIntoView(entry);
        entry.IsRenaming = true;
    }

    private void InlineRenameEditor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!Equals(e.NewValue, true) || sender is not TextBox editor ||
            !ReferenceEquals(editor.DataContext, _renamingEntry)) return;
        _ = editor.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!ReferenceEquals(editor.DataContext, _renamingEntry) || !editor.IsVisible) return;
            editor.Focus();
            editor.Select(0, Math.Min(editor.Text.Length, FileRenameNamePolicy.InitialSelectionLength(
                _renamingEntry!.Name, _renamingEntry.IsDirectory, _renameShowsExtension)));
        }));
    }

    private void InlineRenameEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            FinishInlineRename(true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            FinishInlineRename(false);
        }
    }

    private void InlineRenameEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox editor && ReferenceEquals(editor.DataContext, _renamingEntry))
            FinishInlineRename(true);
    }

    private void FinishInlineRename(bool accept)
    {
        FileEntry? entry = _renamingEntry;
        if (entry is null) return;
        string draft = entry.RenameDraft;
        bool showingExtension = _renameShowsExtension;
        _renamingEntry = null; // Before dialogs, change notifications, or async refresh.
        entry.IsRenaming = false;
        if (!accept || !_actionsEnabled || IsRecycleBin || IsThisPc) return;
        try
        {
            string targetName = FileRenameNamePolicy.ResolveFullName(entry.Name, entry.IsDirectory, showingExtension, draft);
            if (string.Equals(entry.Name, targetName, StringComparison.Ordinal)) return;
            if (!entry.IsDirectory && showingExtension &&
                !string.Equals(Path.GetExtension(entry.Name), Path.GetExtension(targetName), StringComparison.OrdinalIgnoreCase) &&
                MessageBox.Show(Window.GetWindow(this), "파일 확장자가 변경됩니다. 계속하시겠습니까?", "확장자 변경 확인",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (BeforeFileChange?.Invoke() == false) return;
            string renamed = FileActionsService.Rename(entry.FullPath, targetName);
            if (!string.Equals(renamed, entry.FullPath, StringComparison.Ordinal))
            {
                _undoService.RecordRename(entry.FullPath, renamed);
                UpdateUndoButtonState();
            }
            _restoreSelectionPath = renamed;
            if (FileActionCompleted is null) RefreshCurrent();
            else FileActionCompleted.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "이름 변경 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
