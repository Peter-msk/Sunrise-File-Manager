using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Sunrise.FileManager;
using Sunrise.FileManager.Controls;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

internal static class ContextMenuAudit
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> condition, string message)
    { var end = DateTime.UtcNow.AddSeconds(12); while (!condition() && DateTime.UtcNow < end) await Task.Delay(40); Check(condition(), message); }
    private static async Task Click(FrameworkElement target, bool right = false)
    {
        Point p = target.PointToScreen(new Point(Math.Min(70, target.ActualWidth / 2), target.ActualHeight / 2));
        Check(target.IsVisible && SetCursorPos((int)p.X, (int)p.Y), "Native menu target is not visible.");
        mouse_event(right ? 0x0008u : 0x0002u, 0, 0, 0, 0); await Task.Delay(30);
        mouse_event(right ? 0x0010u : 0x0004u, 0, 0, 0, 0); await Task.Delay(150);
    }
    internal static async Task RunAsync(MainWindow window, string root, string output)
    {
        window.Left = 20; window.Top = 20; window.Width = 1200; window.Height = 760;
        window.Activate(); SetForegroundWindow(new WindowInteropHelper(window).Handle);
        var workspace = (Grid)window.FindName("Workspace");
        var pane = workspace.Children.OfType<Grid>().SelectMany(h => h.Children.OfType<ExplorerPane>()).First();
        var files = (DataGrid)pane.FindName("FilesGrid");
        await Until(() => files.Items.Count == 3, "Initial menu fixture listing failed.");
        var menu = files.ContextMenu!;
        var groups = menu.Items.OfType<MenuItem>().Where(m => m.HasItems).ToArray();
        Check(groups.Length == 7 && menu.Items.OfType<MenuItem>().Count() == 9, "Context menu grew beyond the compact group contract.");
        Check(!AllItems(menu).Any(m => new[] { "복사", "잘라내기", "붙여넣기", "삭제", "실행 취소", "다시 실행", "영구 삭제" }.Contains(m.Header?.ToString())), "Basic context commands were not removed.");
        foreach (var mode in new[] { (1, 80), (2, 80), (1, 125), (2, 125) })
        {
            ((ComboBox)window.FindName("ThemeSelector")).SelectedIndex = mode.Item1;
            var scale = (ComboBox)window.FindName("UiScaleSelector");
            scale.SelectedItem = scale.Items.OfType<ComboBoxItem>().Single(x => (int)x.Tag == mode.Item2);
            await Task.Delay(200); window.UpdateLayout();
            var entry = files.Items.OfType<FileEntry>().Single(f => f.Name == "보고서.txt");
            files.ScrollIntoView(entry); files.UpdateLayout();
            var row = (DataGridRow)files.ItemContainerGenerator.ContainerFromItem(entry);
            await Click(row, true); await Until(() => menu.IsOpen && menu.ActualHeight > 0, "Native right-click did not open the menu.");
            Check(pane.SelectedFile?.FullPath == entry.FullPath, "Right-click changed the selected target incorrectly.");
            Point menuTop = menu.PointToScreen(new Point());
            Check(menuTop.Y >= 0 && menuTop.Y + menu.ActualHeight * VisualTreeHelper.GetDpi(menu).DpiScaleY <= SystemParameters.PrimaryScreenHeight + 2, "Root menu extends outside the screen.");
            Check(menu.ActualHeight < 500, "Grouped root menu still requires long scrolling.");
            NativeUiRegression.CaptureDesktop(Path.Combine(output, $"context-root-theme{mode.Item1}-scale{mode.Item2}.png"));
            foreach (var group in groups)
            {
                Point p = group.PointToScreen(new Point(45, group.ActualHeight / 2));
                SetCursorPos((int)p.X, (int)p.Y);
                await Until(() => group.IsSubmenuOpen, "Native hover did not open group: " + group.Header);
                var popup = (Popup)group.Template.FindName("PART_Popup", group);
                await Until(() => popup.IsOpen && popup.Child is FrameworkElement { IsVisible: true, ActualHeight: > 0 }, "Submenu popup did not render.");
                foreach (var child in group.Items.OfType<MenuItem>())
                {
                    Check(child.IsVisible && child.ActualWidth > 0 && child.ActualHeight > 0, "Submenu child is not accessible: " + child.Header);
                    Point bottom = child.PointToScreen(new Point(child.ActualWidth, child.ActualHeight));
                    Check(bottom.Y <= SystemParameters.PrimaryScreenHeight + 2 && bottom.X <= SystemParameters.PrimaryScreenWidth + 2, "Submenu command extends outside screen.");
                }
                if (group.Header.ToString() == "압축") NativeUiRegression.CaptureDesktop(Path.Combine(output, $"context-archive-theme{mode.Item1}-scale{mode.Item2}.png"));
                group.IsSubmenuOpen = false;
            }
            menu.IsOpen = false;
        }
        // Execute a moved command from the actual submenu via native mouse input.
        var textEntry = files.Items.OfType<FileEntry>().Single(f => f.Name == "보고서.txt");
        files.ScrollIntoView(textEntry); files.UpdateLayout();
        await Click((DataGridRow)files.ItemContainerGenerator.ContainerFromItem(textEntry), true);
        var view = groups.Single(m => m.Header.ToString() == "보기");
        await Click(view); await Until(() => view.IsSubmenuOpen, "View submenu did not open.");
        var preview = (FilePreviewPanel)window.FindName("PreviewPane"); bool wasVisible = preview.IsVisible;
        await Click(view.Items.OfType<MenuItem>().First());
        await Until(() => preview.IsVisible != wasVisible, "Moved preview command no longer invokes its owner.");
        // Basic actions still exist in the main Edit menu and use their unchanged handlers.
        var edit = (MenuItem)pane.FindName("EditCommandMenu");
        Check(edit.Items.OfType<MenuItem>().Any(m => m.Header.ToString() == "복사" && m.InputGestureText == "Ctrl+C"), "Main Edit copy command was removed.");
        Check(edit.Items.OfType<MenuItem>().Any(m => m.Header.ToString() == "삭제" && m.InputGestureText == "Delete"), "Main Edit delete command was removed.");
        // Native selection of the direct EIASS command must still reach its modal dialog.
        string pdf = Path.Combine(root, "검토보고서.pdf"); File.WriteAllText(pdf, "%PDF-1.4\nfixture");
        pane.RefreshCurrent(); await Until(() => files.Items.OfType<FileEntry>().Any(f => f.FullPath == pdf), "PDF menu fixture did not load.");
        await Task.Delay(850); // Wait for the fixture creation watcher refresh before native menu input.
        var pdfEntry = files.Items.OfType<FileEntry>().Single(f => f.FullPath == pdf);
        files.ScrollIntoView(pdfEntry); files.UpdateLayout();
        await Click((DataGridRow)files.ItemContainerGenerator.ContainerFromItem(pdfEntry), true);
        await Until(() => menu.IsOpen && menu.Items.OfType<MenuItem>().First().IsVisible, "PDF right-click menu did not open.");
        bool dialogSeen = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w is EiassRenameDialog && w.IsVisible);
            if (dialog is null) return;
            dialogSeen = true; timer.Stop(); NativeUiRegression.CaptureDesktop(Path.Combine(output, "context-eiass-command.png")); dialog.Close();
        };
        timer.Start();
        try { await Click(menu.Items.OfType<MenuItem>().First()); await Until(() => dialogSeen, "EIASS context command did not open its dialog."); }
        finally { timer.Stop(); menu.IsOpen = false; }
        await NavigationRecycleAudit.RunAsync(window, pane, root, output);
        Console.WriteLine("CONTEXT_MENU_PASS: native right-click, all 7 submenus, light/dark 80/125% app scale, moved preview action, EIASS entry and Edit-menu isolation");
    }
    private static IEnumerable<MenuItem> AllItems(ItemsControl owner)
    { foreach (var item in owner.Items.OfType<MenuItem>()) { yield return item; foreach (var child in AllItems(item)) yield return child; } }
}
