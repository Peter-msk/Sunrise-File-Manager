using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>
/// Feature module for ExplorerPane: clipboard Copy/Cut/Paste, internal & external drag-and-drop
/// with Windows File Explorer semantics (same-volume drag defaults to Move, Ctrl forces Copy),
/// and unified keyboard shortcut dispatching via ExplorerCommandRouter.
/// </summary>
public partial class ExplorerPane
{
    public sealed record ClipboardPasteRequest(ExplorerPane Pane, IReadOnlyList<string> Paths, string TargetFolder, TransferKind Kind);
    private sealed record InternalDragPayload(ExplorerPane Source, string[] Paths);
    public sealed record ExplorerDropRequest(ExplorerPane Source, ExplorerPane Destination,
        IReadOnlyList<string> Paths, string TargetFolder, TransferKind Kind);

    public void RecordCompletedMoves(IReadOnlyList<(string OldPath, string NewPath)> movedPairs)
    {
        if (movedPairs.Count == 0) return;
        _undoService.RecordMovedItems(movedPairs);
        UpdateUndoButtonState();
    }

    private void PutSelectionOnClipboard(bool cut)
    {
        string[] paths = SelectedPaths.ToArray();
        if (paths.Length == 0) return;
        try
        {
            var data = new DataObject();
            var files = new System.Collections.Specialized.StringCollection();
            files.AddRange(paths);
            data.SetFileDropList(files);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
            Clipboard.SetDataObject(data, true);
            var selectedSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (FileEntry entry in _allEntries)
                entry.IsCutPending = cut && selectedSet.Contains(entry.FullPath);
            FooterText.Text = $"{paths.Length:N0}개 항목을 클립보드에 {(cut ? "잘라내기" : "복사")} 준비했습니다.";
        }
        catch (ExternalException ex)
        { MessageBox.Show(ex.Message, "클립보드 사용 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task ExecuteClipboardPasteAsync()
    {
        if (!_actionsEnabled || ActiveFolder is not string folder) return;
        try
        {
            if (!Clipboard.ContainsFileDropList()) { FooterText.Text = "붙여넣을 파일이 없습니다."; return; }
            string[] paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
            if (paths.Length == 0) return;
            TransferKind kind = TransferKind.Copy;
            if (Clipboard.GetData("Preferred DropEffect") is MemoryStream effect && effect.Length >= 4)
            {
                byte[] bytes = new byte[4];
                if (effect.Read(bytes, 0, 4) == 4 && (BitConverter.ToInt32(bytes, 0) & 2) != 0)
                    kind = TransferKind.Move;
            }
            string normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (kind == TransferKind.Copy)
            {
                string[] sameFolderPaths = paths
                    .Where(p => string.Equals(
                        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))),
                        normalizedTarget,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                string[] crossFolderPaths = paths.Except(sameFolderPaths, StringComparer.OrdinalIgnoreCase).ToArray();
                if (sameFolderPaths.Length > 0)
                {
                    await DuplicatePathsAsync(sameFolderPaths);
                }
                if (crossFolderPaths.Length > 0)
                {
                    PasteRequested?.Invoke(new(this, crossFolderPaths, folder, kind));
                }
                return;
            }

            string[] movablePaths = paths
                .Where(p => !string.Equals(
                    Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))),
                    normalizedTarget,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (movablePaths.Length == 0)
            {
                FooterText.Text = "선택한 항목이 이미 현재 폴더에 있습니다.";
                return;
            }
            PasteRequested?.Invoke(new(this, movablePaths, folder, kind));
        }
        catch (ExternalException ex)
        { MessageBox.Show(ex.Message, "클립보드 사용 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private bool DispatchPaneShortcut(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool isTextBox = e.OriginalSource is TextBox || AddressBox.IsKeyboardFocusWithin || SearchBox.IsKeyboardFocusWithin;

        ExplorerShortcutAction action = ExplorerCommandRouter.ResolvePaneShortcut(
            ctrl, alt, shift, e.Key.ToString(), e.SystemKey.ToString(), isTextBox);

        return ExecuteShortcutAction(action, e);
    }

    private bool DispatchGridShortcut(KeyEventArgs e)
    {
        if (AddressBox.IsKeyboardFocusWithin || SearchBox.IsKeyboardFocusWithin || e.OriginalSource is TextBox)
            return false;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        ExplorerShortcutAction action = ExplorerCommandRouter.ResolveGridShortcut(
            ctrl, alt, shift, e.Key.ToString());

        return ExecuteShortcutAction(action, e);
    }

    private bool ExecuteShortcutAction(ExplorerShortcutAction action, RoutedEventArgs e)
    {
        if (IsRecycleBin && action is ExplorerShortcutAction.Undo or ExplorerShortcutAction.Redo or
            ExplorerShortcutAction.Copy or ExplorerShortcutAction.Cut or ExplorerShortcutAction.Paste or
            ExplorerShortcutAction.CopyPath or ExplorerShortcutAction.Duplicate or ExplorerShortcutAction.Rename or
            ExplorerShortcutAction.RecycleDelete or ExplorerShortcutAction.PermanentDelete or
            ExplorerShortcutAction.NewFolder or ExplorerShortcutAction.NewFile)
        {
            FooterText.Text = "휴지통에서는 우클릭 메뉴로 원래 위치에 복원할 수 있습니다.";
            e.Handled = true;
            return true;
        }
        switch (action)
        {
            case ExplorerShortcutAction.Undo:
                Undo_Click(this, e);
                return true;
            case ExplorerShortcutAction.Redo:
                Redo_Click(this, e);
                return true;
            case ExplorerShortcutAction.FocusAddress:
                AddressBox.Focus();
                AddressBox.SelectAll();
                return true;
            case ExplorerShortcutAction.FocusSearch:
                SearchBox.Focus();
                SearchBox.SelectAll();
                return true;
            case ExplorerShortcutAction.NewTab:
                NewTab_Click(this, e);
                return true;
            case ExplorerShortcutAction.CloseTab:
                if (_activeTab is not null && _tabs.Count > 1) CloseTab(_activeTab);
                return true;
            case ExplorerShortcutAction.NextTab:
                if (_tabs.Count > 1 && _activeTab is not null)
                {
                    int next = (_tabs.IndexOf(_activeTab) + 1) % _tabs.Count;
                    SelectTab(_tabs[next]);
                }
                return true;
            case ExplorerShortcutAction.PreviousTab:
                if (_tabs.Count > 1 && _activeTab is not null)
                {
                    int prev = (_tabs.IndexOf(_activeTab) - 1 + _tabs.Count) % _tabs.Count;
                    SelectTab(_tabs[prev]);
                }
                return true;
            case ExplorerShortcutAction.NavigateBack:
                Back_Click(this, e);
                return true;
            case ExplorerShortcutAction.NavigateForward:
                Forward_Click(this, e);
                return true;
            case ExplorerShortcutAction.NavigateUp:
                Up_Click(this, e);
                return true;
            case ExplorerShortcutAction.ShowProperties:
                ContextProperties_Click(this, e);
                return true;
            case ExplorerShortcutAction.OpenSelected:
                ContextOpen_Click(this, e);
                return true;
            case ExplorerShortcutAction.TogglePreview:
                PreviewToggle_Click(this, e);
                return true;
            case ExplorerShortcutAction.ToggleDetails:
                DetailsToggle_Click(this, e);
                return true;
            case ExplorerShortcutAction.ClearSelection:
                FilesGrid.SelectedItems.Clear();
                return true;
            case ExplorerShortcutAction.SelectAll:
                SelectAll_Click(this, e);
                return true;
            case ExplorerShortcutAction.CopyPath:
                ContextCopyPath_Click(this, e);
                return true;
            case ExplorerShortcutAction.Copy:
                ClipboardCopy_Click(this, e);
                return true;
            case ExplorerShortcutAction.Cut:
                ClipboardCut_Click(this, e);
                return true;
            case ExplorerShortcutAction.Paste:
                ClipboardPaste_Click(this, e);
                return true;
            case ExplorerShortcutAction.Duplicate:
                Duplicate_Click(this, e);
                return true;
            case ExplorerShortcutAction.Rename:
                if (!_actionsEnabled) return false;
                Rename_Click(this, e);
                return true;
            case ExplorerShortcutAction.Refresh:
                Refresh_Click(this, e);
                return true;
            case ExplorerShortcutAction.PermanentDelete:
                if (!_actionsEnabled) return false;
                PermanentDelete_Click(this, e);
                return true;
            case ExplorerShortcutAction.RecycleDelete:
                if (!_actionsEnabled) return false;
                Recycle_Click(this, e);
                return true;
            case ExplorerShortcutAction.NewFolder:
                if (!_actionsEnabled) return false;
                NewFolder_Click(this, e);
                return true;
            case ExplorerShortcutAction.NewFile:
                if (!_actionsEnabled) return false;
                NewFile_Click(this, e);
                return true;
            default:
                return false;
        }
    }

    private string ResolveDropTargetFolder(DragEventArgs e)
    {
        string target = ActiveFolder ?? string.Empty;
        if (e.OriginalSource is DependencyObject element &&
            ItemsControl.ContainerFromElement(FilesGrid, element) is DataGridRow { Item: FileEntry entry } &&
            entry.IsDirectory)
        {
            target = entry.FullPath;
        }
        return target;
    }

    private TransferKind ResolveDropTransferKind(DragEventArgs e, IReadOnlyList<string> sourcePaths, string targetFolder)
    {
        bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 || (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 || (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        return ExplorerCommandRouter.ResolveDragTransferKind(sourcePaths, targetFolder, ctrl, shift);
    }

    private InternalDragPayload? GetDropInfo(DragEventArgs e)
    {
        if (!_actionsEnabled || ActiveFolder is null) return null;
        if (e.Data.GetDataPresent(typeof(InternalDragPayload)) &&
            e.Data.GetData(typeof(InternalDragPayload)) is InternalDragPayload internalPayload &&
            internalPayload.Paths.Length > 0)
        {
            if (ReferenceEquals(internalPayload.Source, this))
            {
                if (e.OriginalSource is DependencyObject element &&
                    ItemsControl.ContainerFromElement(FilesGrid, element) is DataGridRow { Item: FileEntry entry } &&
                    entry.IsDirectory && !internalPayload.Paths.Contains(entry.FullPath, StringComparer.OrdinalIgnoreCase))
                {
                    return internalPayload;
                }
                bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 || (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                if (ctrl)
                {
                    return internalPayload;
                }
                return null;
            }
            return internalPayload;
        }
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] externalFiles &&
            externalFiles.Length > 0)
        {
            return new InternalDragPayload(this, externalFiles);
        }
        return null;
    }

    private bool TryTypeToSelectInGrid(KeyEventArgs e)
    {
        if (AddressBox.IsKeyboardFocusWithin || SearchBox.IsKeyboardFocusWithin || e.OriginalSource is TextBox)
            return false;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
            return false;

        string keyStr = e.Key.ToString();
        char? targetChar = null;
        if (keyStr.Length == 1 && char.IsLetterOrDigit(keyStr[0]))
            targetChar = keyStr[0];
        else if (keyStr.Length == 2 && keyStr[0] == 'D' && char.IsDigit(keyStr[1]))
            targetChar = keyStr[1];
        else if (keyStr.StartsWith("NumPad", StringComparison.Ordinal) && keyStr.Length == 7 && char.IsDigit(keyStr[6]))
            targetChar = keyStr[6];

        if (targetChar is not char prefixChar) return false;
        string prefix = prefixChar.ToString();
        var items = FilesGrid.Items.OfType<FileEntry>().ToList();
        if (items.Count == 0) return false;

        int currentIdx = FilesGrid.SelectedItem is FileEntry current ? items.IndexOf(current) : -1;
        for (int offset = 1; offset <= items.Count; offset++)
        {
            var candidate = items[(currentIdx + offset) % items.Count];
            if (candidate.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                FilesGrid.SelectedItems.Clear();
                FilesGrid.SelectedItem = candidate;
                FilesGrid.ScrollIntoView(candidate);
                return true;
            }
        }
        return false;
    }
}
