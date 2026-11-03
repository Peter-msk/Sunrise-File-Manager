using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Sunrise.FileManager.Controls;

/// <summary>
/// DEV29: The classic per-pane File/Edit/View/Favorites/Tools/Info menu stays
/// visible in single, two-, three- and four-pane layouts. Compact mode changes
/// labels and row density only: it never reparents commands or loses handlers.
/// </summary>
public partial class ExplorerPane
{
    private void ApplyCompactMenuMode(bool compact)
    {
        if (PaneClassicMenu is null || PaneMoreButton is null || PaneMoreContextMenu is null)
            return;

        // Compatibility: if a previous layout placed items inside the flyout,
        // restore the ORIGINAL command objects exactly once, with all handlers.
        if (PaneMoreContextMenu.Items.Count > 0)
        {
            PaneMoreContextMenu.IsOpen = false;
            while (PaneMoreContextMenu.Items.Count > 0)
            {
                var command = PaneMoreContextMenu.Items[0];
                PaneMoreContextMenu.Items.RemoveAt(0);
                PaneClassicMenu.Items.Add(command);
            }
        }

        PaneMenuRow.Height = new GridLength(compact ? 34 : 36);
        PaneClassicMenu.Visibility = Visibility.Visible;
        PaneMoreButton.Visibility = Visibility.Collapsed;

        // Short headers preserve the menu in even narrow split panes (>=330 DIP),
        // while their submenus and existing per-pane command ownership do not change.
        FileCommandMenu.Header = compact ? "파일" : "파일(_F)";
        EditCommandMenu.Header = compact ? "편집" : "편집(_E)";
        ViewCommandMenu.Header = compact ? "보기" : "보기(_V)";
        FavoritesCommandMenu.Header = compact ? "즐겨" : "즐겨찾기(_A)";
        ToolsCommandMenu.Header = compact ? "도구" : "추가 기능(_X)";
        InfoCommandMenu.Header = compact ? "정보" : "정보(_I)";
    }

    // Retained for compatibility with older XAML snapshots. The split layout
    // no longer hides the classic menu behind this button.
    private void PaneMore_Click(object sender, RoutedEventArgs e)
    {
    }
}
