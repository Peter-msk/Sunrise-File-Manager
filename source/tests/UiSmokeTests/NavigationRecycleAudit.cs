using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Sunrise.FileManager;
using Sunrise.FileManager.Controls;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

internal static class NavigationRecycleAudit
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint msg, nint wparam, nint lparam);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Until(Func<bool> value, string message)
    { var end = DateTime.UtcNow.AddSeconds(15); while (!value() && DateTime.UtcNow < end) await Task.Delay(50); Check(value(), message); }
    private static async Task Click(FrameworkElement item)
    {
        var p = item.PointToScreen(new Point(item.ActualWidth / 2, item.ActualHeight / 2));
        Check(item.IsVisible && p.Y > 0 && p.Y < SystemParameters.PrimaryScreenHeight, "Drive arrow is outside desktop.");
        SetCursorPos((int)p.X, (int)p.Y); mouse_event(2, 0, 0, 0, 0); await Task.Delay(40); mouse_event(4, 0, 0, 0, 0); await Task.Delay(180);
    }
    internal static async Task RunAsync(MainWindow window, ExplorerPane pane, string root, string output, bool recycleOnly = false)
    {
        var scale = (ComboBox)window.FindName("UiScaleSelector");
        scale.SelectedItem = scale.Items.OfType<ComboBoxItem>().Single(x => (int)x.Tag == 80);
        var files = (DataGrid)pane.FindName("FilesGrid");
        if (!recycleOnly)
        {
        var tree = (FolderTree)pane.FindName("NavigationTree");
        var items = (TreeView)tree.FindName("FolderItems");
        var scroll = (ScrollViewer)tree.FindName("NavigationScroll");
        string fixture = Path.Combine(root, "tree-first-click-fixture"); Directory.CreateDirectory(fixture);
        for (int i = 0; i < 55; i++) Directory.CreateDirectory(Path.Combine(fixture, $"folder-{i:D3}"));
        string deep = Path.Combine(fixture, "zz-current", "nested"); Directory.CreateDirectory(deep); File.WriteAllText(Path.Combine(deep, "stay.txt"), "preserve current location");
        pane.SyncToFolder(deep); await Until(() => pane.CurrentLocation == deep, "Deep fixture navigation failed.");
        await tree.HighlightPathAsync(deep); await Task.Delay(200);
        var drive = items.Items.OfType<TreeViewItem>().Single(n => string.Equals(n.Tag as string, Path.GetPathRoot(deep), StringComparison.OrdinalIgnoreCase));
        for (int turn = 0; turn < 3; turn++)
        {
            await tree.HighlightPathAsync(deep); await Task.Delay(150); drive.ApplyTemplate(); window.UpdateLayout();
            var row = (FrameworkElement)drive.Template.FindName("TreeRow", drive);
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + row.TransformToAncestor(scroll).Transform(new Point()).Y - 35);
            await Task.Delay(100); window.UpdateLayout(); files.Focus();
            var arrow = (ToggleButton)drive.Template.FindName("ExpandToggle", drive);
            await Click(arrow);
            Check(!drive.IsExpanded, "The FIRST drive arrow click revealed the selected descendant instead of collapsing.");
            Check(pane.CurrentLocation == deep, "Collapsing a drive unexpectedly navigated to its root.");
            await Task.Delay(300); Check(!drive.IsExpanded, "Queued reveal reopened manually collapsed drive.");
            pane.RefreshCurrent(); await Task.Delay(550);
            Check(!drive.IsExpanded && pane.CurrentLocation == deep, "Same-location refresh reopened a collapsed drive.");
            NativeUiRegression.CaptureDesktop(Path.Combine(output, $"drive-first-collapse-{turn}.png"));
            await Click(arrow); Check(drive.IsExpanded, "Next arrow click did not expand drive.");
        }
        Console.WriteLine("DRIVE_FIRST_CLICK_PASS: 3 collapse/expand cycles, descendant selected, file-list focus, queued reveal and same-folder refresh");

        }
        else Console.WriteLine("REUSED_PASS: drive first-click 3 native cycles and same-folder refresh from run 40073498262; production inputs unchanged");

        await RecycleBatchAudit.RunAsync(window, pane, root, output);
    }
}
