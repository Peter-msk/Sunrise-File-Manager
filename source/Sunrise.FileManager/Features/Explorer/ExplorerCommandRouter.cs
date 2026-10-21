using System.IO;

namespace Sunrise.FileManager.Services;

public enum ExplorerShortcutAction
{
    None,
    Undo,
    Redo,
    FocusAddress,
    FocusSearch,
    NewTab,
    CloseTab,
    NextTab,
    PreviousTab,
    NavigateBack,
    NavigateForward,
    NavigateUp,
    ShowProperties,
    OpenSelected,
    TogglePreview,
    ToggleDetails,
    ClearSelection,
    SelectAll,
    CopyPath,
    Copy,
    Cut,
    Paste,
    Duplicate,
    Rename,
    Refresh,
    PermanentDelete,
    RecycleDelete,
    NewFolder,
    NewFile
}

public sealed record ExplorerViewSpec(
    string Mode,
    double RowHeight,
    double IconColumnWidth,
    bool ShowAllDetailColumns,
    bool ShowTileColumns);

/// <summary>
/// Pure, deterministic branching router for Windows Explorer keyboard shortcuts,
/// drag-and-drop transfer kind (Move on same volume, Copy across volumes or with Ctrl),
/// and view-mode specifications.
/// </summary>
public static class ExplorerCommandRouter
{
    public static ExplorerShortcutAction ResolvePaneShortcut(
        bool ctrl, bool alt, bool shift, string key, string systemKey, bool isTextBoxFocused)
    {
        string effectiveKey = (key == "System" && !string.IsNullOrEmpty(systemKey) && systemKey != "None")
            ? systemKey
            : key;

        if (ctrl && !alt && !shift && effectiveKey == "Z" && !isTextBoxFocused)
            return ExplorerShortcutAction.Undo;
        if (ctrl && !alt && !shift && effectiveKey == "Y" && !isTextBoxFocused)
            return ExplorerShortcutAction.Redo;
        if ((ctrl && !alt && !shift && effectiveKey == "L") ||
            (alt && !ctrl && !shift && effectiveKey == "D") ||
            (!ctrl && !alt && !shift && effectiveKey == "F4" && !isTextBoxFocused))
            return ExplorerShortcutAction.FocusAddress;
        if ((ctrl && !alt && !shift && effectiveKey is "F" or "E") ||
            (!ctrl && !alt && !shift && effectiveKey == "F3"))
            return ExplorerShortcutAction.FocusSearch;
        if (ctrl && !alt && !shift && effectiveKey == "T")
            return ExplorerShortcutAction.NewTab;
        if (ctrl && !alt && !shift && effectiveKey is "W" or "F4")
            return ExplorerShortcutAction.CloseTab;
        if (ctrl && !alt && shift && effectiveKey == "Tab")
            return ExplorerShortcutAction.PreviousTab;
        if (ctrl && !alt && !shift && effectiveKey == "Tab")
            return ExplorerShortcutAction.NextTab;
        if (alt && !ctrl && !shift && effectiveKey == "Left")
            return ExplorerShortcutAction.NavigateBack;
        if (alt && !ctrl && !shift && effectiveKey == "Right")
            return ExplorerShortcutAction.NavigateForward;
        if (alt && !ctrl && !shift && effectiveKey == "Up")
            return ExplorerShortcutAction.NavigateUp;
        if (alt && !ctrl && !shift && effectiveKey is "Return" or "Enter")
            return ExplorerShortcutAction.ShowProperties;
        if (alt && !ctrl && !shift && effectiveKey == "P")
            return ExplorerShortcutAction.TogglePreview;
        if (alt && !ctrl && shift && effectiveKey == "P")
            return ExplorerShortcutAction.ToggleDetails;
        if ((!ctrl && !alt && !shift && effectiveKey == "F5") ||
            (ctrl && !alt && !shift && effectiveKey == "R" && !isTextBoxFocused))
            return ExplorerShortcutAction.Refresh;

        // If user is not inside a TextBox (AddressBox/SearchBox), also allow standard file shortcuts at pane level
        if (!isTextBoxFocused)
        {
            return ResolveGridShortcut(ctrl, alt, shift, effectiveKey);
        }

        return ExplorerShortcutAction.None;
    }

    public static ExplorerShortcutAction ResolveGridShortcut(
        bool ctrl, bool alt, bool shift, string key)
    {
        if (!ctrl && !alt && !shift && key is "Return" or "Enter")
            return ExplorerShortcutAction.OpenSelected;
        if (!ctrl && !alt && !shift && key == "Back")
            return ExplorerShortcutAction.NavigateBack;
        if (!ctrl && !alt && !shift && key == "Escape")
            return ExplorerShortcutAction.ClearSelection;
        if (!ctrl && !alt && !shift && key == "Space")
            return ExplorerShortcutAction.TogglePreview;
        if (ctrl && !alt && !shift && key == "Z")
            return ExplorerShortcutAction.Undo;
        if (ctrl && !alt && !shift && key == "Y")
            return ExplorerShortcutAction.Redo;
        if (ctrl && !alt && !shift && key == "A")
            return ExplorerShortcutAction.SelectAll;
        if (ctrl && shift && !alt && key == "C")
            return ExplorerShortcutAction.CopyPath;
        if (ctrl && !alt && !shift && key == "C")
            return ExplorerShortcutAction.Copy;
        if (ctrl && !alt && !shift && key == "X")
            return ExplorerShortcutAction.Cut;
        if (ctrl && !alt && !shift && key == "V")
            return ExplorerShortcutAction.Paste;
        if (ctrl && !alt && !shift && key == "D")
            return ExplorerShortcutAction.Duplicate;
        if (!ctrl && !alt && !shift && key == "F2")
            return ExplorerShortcutAction.Rename;
        if (!ctrl && !alt && !shift && key == "F5")
            return ExplorerShortcutAction.Refresh;
        if (!ctrl && !alt && shift && key == "Delete")
            return ExplorerShortcutAction.PermanentDelete;
        if (!ctrl && !alt && !shift && key == "Delete")
            return ExplorerShortcutAction.RecycleDelete;
        if (ctrl && shift && !alt && key == "N")
            return ExplorerShortcutAction.NewFolder;
        if (ctrl && !shift && !alt && key == "N")
            return ExplorerShortcutAction.NewFile;
        return ExplorerShortcutAction.None;
    }

    /// <summary>
    /// Matches Windows File Explorer drag-and-drop semantics:
    /// - Ctrl held: always Copy
    /// - Shift held: Move (when on the same volume; Copy across volumes where atomic rename is unsupported)
    /// - No modifier: Move when source and target are on the same drive volume (e.g. C:\ -> C:\), Copy when across drives.
    /// </summary>
    public static TransferKind ResolveDragTransferKind(
        IReadOnlyList<string> sourcePaths,
        string targetFolder,
        bool ctrlPressed,
        bool shiftPressed)
    {
        if (ctrlPressed && !shiftPressed)
            return TransferKind.Copy;

        bool sameVolume = AreOnSameVolume(sourcePaths, targetFolder);
        if (shiftPressed && !ctrlPressed)
            return sameVolume ? TransferKind.Move : TransferKind.Copy;

        return sameVolume ? TransferKind.Move : TransferKind.Copy;
    }

    public static bool AreOnSameVolume(IReadOnlyList<string> sourcePaths, string targetFolder)
    {
        if (sourcePaths is null || sourcePaths.Count == 0 || string.IsNullOrWhiteSpace(targetFolder))
            return false;
        try
        {
            string? targetRoot = Path.GetPathRoot(Path.GetFullPath(targetFolder));
            if (string.IsNullOrEmpty(targetRoot)) return false;
            return sourcePaths.All(src =>
                !string.IsNullOrWhiteSpace(src) &&
                string.Equals(Path.GetPathRoot(Path.GetFullPath(src)), targetRoot, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public static string NormalizeViewMode(string? candidate) =>
        candidate is "List" or "Thumbnails" or "SmallIcons" or "Tiles" ? candidate : "Details";

    public static int ViewModeToSelectorIndex(string viewMode) => viewMode switch
    {
        "List" => 1,
        "Thumbnails" => 2,
        "SmallIcons" => 3,
        "Tiles" => 4,
        _ => 0
    };

    public static string SelectorIndexToViewMode(int index) => index switch
    {
        1 => "List",
        2 => "Thumbnails",
        3 => "SmallIcons",
        4 => "Tiles",
        _ => "Details"
    };

    public static ExplorerViewSpec GetViewSpec(string viewMode)
    {
        string normalized = NormalizeViewMode(viewMode);
        return normalized switch
        {
            "Thumbnails" => new ExplorerViewSpec("Thumbnails", 104, 116, false, false),
            "Tiles" => new ExplorerViewSpec("Tiles", 52, 64, false, true),
            "SmallIcons" => new ExplorerViewSpec("SmallIcons", 26, 48, false, false),
            "List" => new ExplorerViewSpec("List", 32, 48, false, false),
            _ => new ExplorerViewSpec("Details", 30, 48, true, false)
        };
    }
}
