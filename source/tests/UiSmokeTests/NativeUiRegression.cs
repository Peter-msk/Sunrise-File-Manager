using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sunrise.FileManager;
using Sunrise.FileManager.Controls;

// Runs on an unlocked Windows desktop. No routed events are synthesized here.
internal static class NativeUiRegression
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    private static int _clicks, _drags;

    internal static async Task RunAsync(MainWindow window, string fixtureRoot, string output)
    {
        window.Left = 0; window.Top = 0;
        window.Width = Math.Min(1800, SystemParameters.WorkArea.Width);
        window.Height = Math.Min(1000, SystemParameters.WorkArea.Height);
        window.Activate(); SetForegroundWindow(new WindowInteropHelper(window).Handle);
        await SettleAsync(window);
        var workspace = (Grid)window.FindName("Workspace");
        string longPath = Path.Combine(fixtureRoot, "검토 자료", "긴_하위_폴더명을_가로로_끝까지_읽기_위한_마우스_스크롤_실행_검증_자료_ABCDEFGHIJKLMN");
        Directory.CreateDirectory(longPath);
        foreach (int count in new[] { 1, 2, 3, 4, 1, 2, 4, 1 })
        {
            var button = (Button)window.FindName(count switch { 1 => "OneButton", 2 => "TwoButton", 3 => "ThreeButton", _ => "FourButton" });
            await ClickAsync(window, button);
            var panes = workspace.Children.OfType<Grid>().SelectMany(host => host.Children.OfType<ExplorerPane>()).ToArray();
            if (panes.Length != count) throw new Exception($"Native split button did not select {count} panes.");
            for (int index = 0; index < panes.Length; index++)
            {
                ExplorerPane pane = panes[index];
                pane.SyncToFolder(fixtureRoot);
                var files = (DataGrid)pane.FindName("FilesGrid");
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (files.Items.Count != 3 && DateTime.UtcNow < deadline) await Task.Delay(30);
                if (files.Items.Count != 3) throw new Exception("Native fixture listing timed out.");
                files.Columns[1].Width = 1200;
                await SettleAsync(window);
                var fileScroll = Find<ScrollViewer>(files, _ => true) ?? throw new Exception("File viewer missing.");
                await ExerciseHorizontalBarAsync(window, fileScroll, $"files-{count}-{index}");
                var tree = (FolderTree)pane.FindName("NavigationTree");
                if (!tree.IsVisible) await ClickAsync(window, (Button)pane.FindName("NavigationToggleButton"));
                await tree.HighlightPathAsync(longPath);
                await SettleAsync(window);
                var navigation = (ScrollViewer)tree.FindName("NavigationScroll");
                await ExerciseHorizontalBarAsync(window, navigation, $"tree-{count}-{index}");
                CaptureDesktop(Path.Combine(output, $"native-{count}pane-{index}.png"));
                Console.WriteLine($"NATIVE_PANE_PASS count={count} index={index} fileExtent={fileScroll.ScrollableWidth:F1} treeExtent={navigation.ScrollableWidth:F1}");
            }
        }
        File.WriteAllText(Path.Combine(output, "NATIVE_INPUT_RESULT.json"), System.Text.Json.JsonSerializer.Serialize(new { status = "PASS", clicks = _clicks, drags = _drags, transitions = 8, paneChecks = 18 }));
    }

    private static async Task ExerciseHorizontalBarAsync(MainWindow window, ScrollViewer scroll, string name)
    {
        scroll.ScrollToHorizontalOffset(0); await SettleAsync(window);
        if (scroll.ScrollableWidth < 20) throw new Exception($"Native horizontal overflow missing: {name}, extent={scroll.ExtentWidth}, viewport={scroll.ViewportWidth}");
        var bar = Find<ScrollBar>(scroll, b => b.Orientation == Orientation.Horizontal && b.IsVisible)
            ?? throw new Exception($"Native horizontal bar missing: {name}");
        bar.ApplyTemplate();
        var thumb = Find<Thumb>(bar, _ => true) ?? throw new Exception($"Native thumb missing: {name}");
        await DragAsync(window, thumb, 65);
        if (scroll.HorizontalOffset < 1) throw new Exception($"Native drag did not scroll right: {name}");
        double offset = scroll.HorizontalOffset;
        await Task.Delay(250); await SettleAsync(window);
        if (Math.Abs(scroll.HorizontalOffset - offset) > 1) throw new Exception($"Native scroll drift: {name}");
        await DragAsync(window, thumb, -150);
        if (scroll.HorizontalOffset > 1) throw new Exception($"Native drag did not return left: {name} offset={scroll.HorizontalOffset}");
    }

    private static async Task ClickAsync(MainWindow window, FrameworkElement element)
    {
        Point point = await VisiblePointAsync(window, element);
        if (!SetCursorPos((int)point.X, (int)point.Y)) throw new Exception("Cannot position native mouse.");
        mouse_event(0x0002, 0, 0, 0, 0); await Task.Delay(45);
        mouse_event(0x0004, 0, 0, 0, 0); _clicks++;
        await Task.Delay(100); await SettleAsync(window);
    }

    private static async Task DragAsync(MainWindow window, FrameworkElement element, int delta)
    {
        Point point = await VisiblePointAsync(window, element);
        SetCursorPos((int)point.X, (int)point.Y); await Task.Delay(40);
        mouse_event(0x0002, 0, 0, 0, 0);
        try
        {
            for (int step = 1; step <= 10; step++)
            {
                SetCursorPos((int)point.X + delta * step / 10, (int)point.Y);
                await Task.Delay(18);
            }
        }
        finally { mouse_event(0x0004, 0, 0, 0, 0); }
        _drags++; await SettleAsync(window);
    }

    private static async Task<Point> VisiblePointAsync(MainWindow window, FrameworkElement element)
    {
        var outer = (ScrollViewer)window.FindName("WorkspaceViewport");
        for (int turn = 0; turn < 3; turn++)
        {
            await SettleAsync(window);
            Point screen = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            Point relative = outer.PointFromScreen(screen);
            bool inWorkspace = element.IsDescendantOf(outer);
            if (inWorkspace && (relative.X < 10 || relative.X > outer.ActualWidth - 10 || relative.Y < 10 || relative.Y > outer.ActualHeight - 10))
            {
                outer.ScrollToHorizontalOffset(outer.HorizontalOffset + relative.X - outer.ActualWidth / 2);
                outer.ScrollToVerticalOffset(outer.VerticalOffset + relative.Y - outer.ActualHeight / 2);
                continue;
            }
            if (!element.IsVisible || screen.X < 0 || screen.Y < 0 || screen.X >= SystemParameters.PrimaryScreenWidth || screen.Y >= SystemParameters.PrimaryScreenHeight)
                throw new Exception($"Native input target outside desktop: {element.GetType().Name} {screen}.");
            return screen;
        }
        throw new Exception("Native input target could not be scrolled into view.");
    }

    internal static void CaptureDesktop(string path)
    {
        int width = (int)SystemParameters.PrimaryScreenWidth, height = (int)SystemParameters.PrimaryScreenHeight;
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height);
        nint old = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, 0, 0, 0x00CC0020)) throw new Exception("Desktop capture failed.");
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions())));
            using var file = File.Create(path); png.Save(file);
        }
        finally { SelectObject(memory, old); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(0, screen); }
    }

    private static async Task SettleAsync(Window window)
    {
        for (int i = 0; i < 3; i++) await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
    }
    private static T? Find<T>(DependencyObject node, Func<T, bool> match) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is T candidate && match(candidate)) return candidate;
            if (Find(child, match) is { } nested) return nested;
        }
        return null;
    }
}
