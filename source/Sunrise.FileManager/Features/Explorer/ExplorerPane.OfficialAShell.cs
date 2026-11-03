using System.Windows;

namespace Sunrise.FileManager.Controls;

/// <summary>Use the same validated commands as the pane's existing menus and hotkeys.</summary>
public partial class ExplorerPane
{
    public void ExecuteOfficialAShellCommand(string command)
    {
        if (!_actionsEnabled) return;
        var args = new RoutedEventArgs();
        switch (command)
        {
            case "NewFolder": NewFolder_Click(this, args); break;
            case "Rename": Rename_Click(this, args); break;
            case "Copy": ClipboardCopy_Click(this, args); break;
            case "Cut": ClipboardCut_Click(this, args); break;
            case "Paste": ClipboardPaste_Click(this, args); break;
            case "Delete": Recycle_Click(this, args); break;
            case "Undo": Undo_Click(this, args); break;
            case "Redo": Redo_Click(this, args); break;
            case "Filter": AdvancedFilter_Click(this, args); break;
            case "Eiass": EiassRename_Click(this, args); break;
            case "Zip": ZipCompress_Click(this, args); break;
            case "Extract": ZipExtract_Click(this, args); break;
        }
    }
}
