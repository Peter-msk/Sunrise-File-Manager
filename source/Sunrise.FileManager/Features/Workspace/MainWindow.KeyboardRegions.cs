using System.Windows;
using System.Windows.Controls;

namespace Sunrise.FileManager;

/// <summary>
/// R55: F6 / Shift+F6 moves keyboard focus among *visible* workspace regions.
/// This never toggles panels, changes file selection, commits memo text or runs a file action.
/// The existing active-pane owner is preserved, including cloud workspace slots.
/// </summary>
public partial class MainWindow
{
    private void NextWorkspaceFocus_Click(object sender, RoutedEventArgs e) => CycleWorkspaceKeyboardFocus(false);
    private void PreviousWorkspaceFocus_Click(object sender, RoutedEventArgs e) => CycleWorkspaceKeyboardFocus(true);

    private bool CycleWorkspaceKeyboardFocus(bool reverse)
    {
        if (_activePane < 0 || _activePane >= _visiblePanels) return false;
        var regions = new List<(FrameworkElement Surface, Func<bool> FocusTarget)>();
        if (_paneLocationModes[_activePane] == "Local" && _panes[_activePane] is { } local)
            regions.Add((local, () => { local.FocusFiles(); return true; }));
        else if (_cloudSlots[_activePane] is { } cloud)
            regions.Add((cloud, cloud.FocusFiles));

        if (_detailsOpen && FileDetails.Visibility == Visibility.Visible)
            regions.Add((FileDetails, FileDetails.FocusDetailsControl));
        if (_memoOpen && MemoPanel.Visibility == Visibility.Visible)
            regions.Add((MemoPanel, MemoPanel.FocusMemoEditor));
        if (_dockPreviewOpen && PreviewPane.Visibility == Visibility.Visible)
            regions.Add((PreviewPane, PreviewPane.FocusPreviewContent));
        if (regions.Count < 2) return false;

        int current = regions.FindIndex(region => region.Surface.IsKeyboardFocusWithin);
        int next = current < 0 ? 0 : (current + (reverse ? regions.Count - 1 : 1)) % regions.Count;
        // A disabled field is not a destination. Try the other visible regions.
        for (int attempt = 0; attempt < regions.Count; attempt++)
        {
            if (regions[next].FocusTarget()) return true;
            next = (next + (reverse ? regions.Count - 1 : 1)) % regions.Count;
        }
        return false;
    }
}
