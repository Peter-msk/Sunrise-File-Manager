using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;
using System.Windows.Threading;
using System.IO;
using System.Windows.Media.Imaging;
using Sunrise.FileManager;
using Sunrise.FileManager.Controls;

// Opt-in, isolated Windows WPF scenario. Not invoked by the product builder.
internal static class FileScrollRegression
{
    internal static string? EvidenceDirectory { get; set; }
    private static void Evidence(Window window, string stage)
    {
        Console.WriteLine($"UI_STAGE {stage}");
        if (EvidenceDirectory is not { } output) return;
        File.AppendAllText(Path.Combine(output, "stages.log"), $"{DateTime.UtcNow:O} {stage}\n");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, stage + ".png")); png.Save(file);
    }
    internal static async Task RunAsync(MainWindow window, string fixtureRoot)
    {
        window.Width = 1400;
        window.Height = 850;
        var workspace = (Grid)window.FindName("Workspace");
        // Reuse the original first pane through repeated single -> split transitions.
        ExplorerPane first = Panes(workspace).First();
        foreach (string preset in new[] { "OneButton", "TwoButton", "OneButton", "TwoButton" })
        {
            ((Button)window.FindName(preset)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync(window);
            ExplorerPane[] panes = Panes(workspace);
            if (!panes.Contains(first)) throw new Exception("Original first pane was replaced.");
            if (panes.Length != (preset == "OneButton" ? 1 : 2))
                throw new Exception("Unexpected visible pane count.");
            foreach (ExplorerPane pane in panes)
                await CheckPaneAsync(window, pane, fixtureRoot);

        }
    }

    private static async Task CheckPaneAsync(MainWindow window, ExplorerPane pane, string fixtureRoot)
    {
        pane.SyncToFolder(fixtureRoot);
        var grid = (DataGrid)pane.FindName("FilesGrid");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (grid.Items.Count != 3 && DateTime.UtcNow < deadline) await Task.Delay(25);
        if (grid.Items.Count != 3) throw new Exception("Synthetic listing did not load.");
        // Force overflow independently of the machine font/DPI/previous settings.
        grid.Columns[1].Width = new DataGridLength(1200);
        await SettleAsync(window);
        ScrollViewer scroll = FindViewer(grid) ?? throw new Exception("Current file viewer missing.");
        if (scroll.ViewportWidth <= 0 || scroll.ViewportWidth > grid.ActualWidth + 1 || scroll.ScrollableWidth < 100)
            throw new Exception("Split file viewport/extent did not recompute.");
        if (scroll.ComputedHorizontalScrollBarVisibility != Visibility.Visible)
            throw new Exception("File horizontal scrollbar disappeared.");
        if (!grid.EnableRowVirtualization || grid.EnableColumnVirtualization)
            throw new Exception("File virtualization scope changed.");
        scroll.ScrollToHorizontalOffset(80);
        await SettleAsync(window);
        if (Math.Abs(scroll.HorizontalOffset - 80) > 1)
            throw new Exception("Manual right scrolling failed.");
        double stableOffset = scroll.HorizontalOffset;
        // Several idle/layout turns must not keep panning the cells right.
        await SettleAsync(window);
        if (Math.Abs(scroll.HorizontalOffset - stableOffset) > 1)
            throw new Exception("File content drifted without user scrolling.");
        scroll.ScrollToHorizontalOffset(0);
        await SettleAsync(window);
        if (scroll.HorizontalOffset > 0.5) throw new Exception("Cannot scroll back to file names.");
        var row = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow
            ?? throw new Exception("First file row is not realized.");
        var label = FindNamed<TextBlock>(row, "InlineNameLabel")
            ?? throw new Exception("File-name content disappeared.");
        Point labelPosition = label.TransformToAncestor(grid).Transform(new Point());
        if (!label.IsVisible || label.ActualWidth <= 0 || labelPosition.X < -1 || labelPosition.X >= grid.ActualWidth)
            throw new Exception("File name remains outside the viewport at x=0.");
    
    }

    internal static async Task RunAllSplitsAsync(MainWindow window, string fixtureRoot)
    {
        window.Width = 1400;
        window.Height = 850;
        var workspace = (Grid)window.FindName("Workspace");
        foreach (int count in Enumerable.Range(1, 4))
            foreach (var option in WorkspaceSplitLayoutEngine.GetPresetsForCount(count))
            {
                await ChooseLayoutAsync(window, count, option.Preset);
                Evidence(window, $"layout-{option.Preset}-start");
                AssertPaneBounds(workspace, option.Preset);
                foreach (ExplorerPane pane in Panes(workspace))
                {
                    await CheckPaneAsync(window, pane, fixtureRoot);
                    await ExerciseFolderAndTabAsync(window, pane, fixtureRoot);
                }
                // Native GridSplitter keyboard handling, including safe clamping at limits.
                if (workspace.Children.OfType<GridSplitter>().FirstOrDefault() is { } splitter)
                {
                    splitter.Focus();
                    var source = PresentationSource.FromVisual(splitter) ?? throw new Exception("Splitter not presented.");
                    Key forward = splitter.ResizeDirection == GridResizeDirection.Columns ? Key.Right : Key.Down;
                    splitter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, forward)
                        { RoutedEvent = Keyboard.KeyDownEvent });
                    await SettleAsync(window);
                    AssertPaneBounds(workspace, option.Preset);
                }
                window.Width = 1010; window.Height = 650;
                await SettleAsync(window);
                Evidence(window, $"layout-{option.Preset}-pass");
                AssertPaneBounds(workspace, option.Preset);
                var outer = (ScrollViewer)window.FindName("WorkspaceViewport");
                outer.ScrollToRightEnd(); outer.ScrollToBottom();
                await SettleAsync(window);
                if (!double.IsFinite(outer.HorizontalOffset) || !double.IsFinite(outer.VerticalOffset))
                    throw new Exception("Invalid workspace scroll offset.");
                outer.ScrollToLeftEnd(); outer.ScrollToTop();
                window.Width = 1400; window.Height = 850;
                await SettleAsync(window);
            }
        foreach (int count in new[] { 1, 2, 3, 4, 3, 2, 1, 4, 1 })
        {
            var preset = WorkspaceSplitLayoutEngine.GetDefaultPreset(count);
            Console.WriteLine($"UI_TRANSITION {count}");
            await ChooseLayoutAsync(window, count, preset);
            AssertPaneBounds(workspace, preset);
            foreach (ExplorerPane pane in Panes(workspace)) await CheckPaneAsync(window, pane, fixtureRoot);
        }
        // Give each pane a distinct directory using its actual address/Go controls.
        await ChooseLayoutAsync(window, 4, SplitLayoutPreset.FourGrid2x2);
        ExplorerPane[] four = Panes(workspace);
        for (int index = 0; index < four.Length; index++)
        {
            string folder = System.IO.Path.Combine(fixtureRoot, "검토 자료", $"pane-state-{index}");
            System.IO.Directory.CreateDirectory(folder);
            ((TextBox)four[index].FindName("AddressBox")).Text = folder;
            var go = Find<Button>(four[index], button => Equals(button.Content, "이동")) ?? throw new Exception("Go button missing.");
            go.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitListingAsync(window, four[index], folder, 0);
        }
        string activeDirectory = four[3].CurrentLocation!;
        four[3].FocusFiles();
        await SettleAsync(window);
        await ChooseLayoutAsync(window, 1, SplitLayoutPreset.Single);
        if (Panes(workspace).Single().CurrentLocation != activeDirectory)
            throw new Exception("Shrinking hid the actively navigated fourth pane.");
        var expected = four.Select(pane => pane.CurrentLocation).ToArray();
        await ChooseLayoutAsync(window, 4, SplitLayoutPreset.FourGrid2x2);
        if (!four.Select(pane => pane.CurrentLocation).SequenceEqual(expected))
            throw new Exception("Expanding overwrote independently navigated pane directories.");
    }

    private static async Task ChooseLayoutAsync(MainWindow window, int count, SplitLayoutPreset preset)
    {
        string button = count switch { 1 => "OneButton", 2 => "TwoButton", 3 => "ThreeButton", _ => "FourButton" };
        ((Button)window.FindName(button)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(window);
        if (count > 1)
        {
            var selector = (ComboBox)window.FindName("SplitPresetSelector");
            selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, preset));
            await SettleAsync(window);
        }
    }

    private static void AssertPaneBounds(Grid workspace, SplitLayoutPreset preset)
    {
        var minimum = WorkspaceSplitLayoutEngine.GetMinimumWorkspaceSize(preset);
        if (workspace.ActualWidth + 1 < minimum.Width || workspace.ActualHeight + 1 < minimum.Height)
            throw new Exception($"Workspace clips layout {preset}.");
        ExplorerPane[] panes = Panes(workspace);
        if (panes.Length != WorkspaceSplitLayoutEngine.GetPanelCountForPreset(preset))
            throw new Exception($"Missing pane in {preset}.");
        foreach (ExplorerPane pane in panes)
        {
            var grid = (DataGrid)pane.FindName("FilesGrid");
            if (pane.ActualWidth + 1 < WorkspaceSplitLayoutEngine.MinimumPaneWidth ||
                pane.ActualHeight + 1 < WorkspaceSplitLayoutEngine.MinimumPaneHeight ||
                grid.ActualWidth < 90 || grid.ActualHeight < 90)
                throw new Exception($"Pane/file viewport squeezed out in {preset}.");
        }
    }

    private static async Task ExerciseFolderAndTabAsync(MainWindow window, ExplorerPane pane, string fixtureRoot)
    {
        var grid = (DataGrid)pane.FindName("FilesGrid");
        var folder = grid.Items.OfType<FileEntry>().Single(entry => entry.IsDirectory && entry.Name == "검토 자료");
        pane.RestoreSelection(folder.FullPath);
        await SettleAsync(window);
        var row = grid.ItemContainerGenerator.ContainerFromItem(folder) as DataGridRow ?? throw new Exception("Folder row not realized.");
        grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Control.MouseDoubleClickEvent, Source = row });
        await WaitListingAsync(window, pane, folder.FullPath, 0);
        ((Button)pane.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitListingAsync(window, pane, fixtureRoot, 3);
        ((Button)pane.FindName("ForwardButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitListingAsync(window, pane, folder.FullPath, 0);
        ((Button)pane.FindName("UpButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitListingAsync(window, pane, fixtureRoot, 3);
        int originalTabs = pane.GetSession().TabPaths.Count;
        var menu = (MenuItem)pane.FindName("FileCommandMenu");
        menu.Items.OfType<MenuItem>().First(item => item.InputGestureText == "Ctrl+T")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await SettleAsync(window);
        if (pane.GetSession().TabPaths.Count != originalTabs + 1) throw new Exception("New tab failed.");
        var tabs = (ItemsControl)pane.FindName("TabStrip");
        var close = Find<Button>(tabs, button => Equals(button.Content, "×") && button.Tag is ExplorerTab { IsSelected: true })
            ?? throw new Exception("Selected-tab close button missing.");
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitListingAsync(window, pane, fixtureRoot, 3);
        if (pane.GetSession().TabPaths.Count != originalTabs) throw new Exception("Close tab failed.");
    }

    private static async Task WaitListingAsync(MainWindow window, ExplorerPane pane, string location, int count)
    {
        var grid = (DataGrid)pane.FindName("FilesGrid");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((pane.CurrentLocation != location || grid.Items.Count != count) && DateTime.UtcNow < deadline) await Task.Delay(25);
        await SettleAsync(window);
        if (pane.CurrentLocation != location || grid.Items.Count != count) throw new Exception("Folder navigation/listing failed.");
        var scroll = FindViewer(grid) ?? throw new Exception("File viewer lost after navigation.");
        if (scroll.HorizontalOffset > 0.5) throw new Exception("Navigation did not return to file names.");
    }

    private static ExplorerPane[] Panes(Grid workspace) => workspace.Children.OfType<Grid>()
        .SelectMany(host => host.Children.OfType<ExplorerPane>()).ToArray();

    private static async Task SettleAsync(Window window)
    {
        for (int turn = 0; turn < 3; turn++)
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
    }

    private static ScrollViewer? FindViewer(DependencyObject node) => Find<ScrollViewer>(node, _ => true);
    private static T? FindNamed<T>(DependencyObject node, string name) where T : FrameworkElement =>
        Find<T>(node, element => element.Name == name);
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
