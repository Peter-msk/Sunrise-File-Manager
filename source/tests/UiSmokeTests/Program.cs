using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sunrise.FileManager;
using Sunrise.FileManager.Controls;
using Sunrise.FileManager.Services;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Actual WPF UI checks require Windows.");
        if (args.FirstOrDefault() == "--packaged-smoke") { PackagedSmoke.Run(args[1], args[2]); return; }
        if (args.FirstOrDefault() == "--native-files-only") { WindowsSmokeTests.RunAsync().GetAwaiter().GetResult(); return; }
        bool fileScrollOnly = args.Contains("--file-scroll-only", StringComparer.Ordinal);
        bool splitInteractionOnly = args.Contains("--split-interaction-only", StringComparer.Ordinal);
        bool eiassOnly = args.Contains("--eiass-rename-only", StringComparer.Ordinal);
        bool recycleBatchOnly = args.Contains("--recycle-batch-only", StringComparer.Ordinal);
        bool navigationRecycleOnly = args.Contains("--navigation-recycle-only", StringComparer.Ordinal) || args.Contains("--recycle-delete-only", StringComparer.Ordinal);
        bool contextMenuOnly = args.Contains("--context-menu-only", StringComparer.Ordinal);
        bool currentAudit = args.Contains("--current-ui-audit", StringComparer.Ordinal);
        string output = Path.GetFullPath(args.FirstOrDefault(arg => arg is not ("--file-scroll-only" or "--split-interaction-only" or "--eiass-rename-only" or "--current-ui-audit" or "--context-menu-only" or "--navigation-recycle-only" or "--recycle-delete-only" or "--recycle-batch-only")) ?? "ui-check-results"); Directory.CreateDirectory(output);
        using var timeout = new Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "TIMEOUT.txt"), "WPF test exceeded 180 seconds.");
            try { NativeUiRegression.CaptureDesktop(Path.Combine(output, "timeout-desktop.png")); }
            catch (Exception ex) { File.AppendAllText(Path.Combine(output, "TIMEOUT.txt"), ex.ToString()); }
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(180), Timeout.InfiniteTimeSpan);
        string root = Path.Combine(Path.GetTempPath(), "SunriseUiFixture_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "검토 자료"));
        File.WriteAllText(Path.Combine(root, "보고서.txt"), "UI smoke fixture");
        File.WriteAllText(Path.Combine(root, "검토 메모.md"), "UI smoke fixture");
        // Use the exact product resource definitions with an isolated Application host.
        // Loading App itself would also run the network/update startup gate.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var productXaml = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ProductApp.xaml")).Root!;
        System.Xml.Linq.XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new System.Xml.Linq.XElement(xaml + "ResourceDictionary",
            productXaml.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
            productXaml.Element(xaml + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
        var session = new AppSession { LayoutVersion = 19, VisiblePanels = 1, Panels = Enumerable.Range(0, 4).Select(_ => new PanelSession { TabPaths = [root], DetailsVisible = false }).ToList() };
        if (splitInteractionOnly) session.UiScalePercent = 90;
        UiScaleService.Set(session.UiScalePercent);
        var window = new MainWindow(session);
        window.Loaded += async (_, _) =>
        {
            var checks = new List<object>(); string? error = null;
            try
            {
                if (recycleBatchOnly)
                {
                    var batchWorkspace = (Grid)window.FindName("Workspace");
                    var batchPane = (ExplorerPane)((Grid)batchWorkspace.Children[0]).Children[0];
                    await RecycleBatchAudit.RunAsync(window, batchPane, root, output);
                    checks.Add(new { feature = "Recycle grouped delete, restore, restore all, empty", result = "PASS" });
                    return;
                }
                if (navigationRecycleOnly)
                {
                    var grid = (Grid)window.FindName("Workspace");
                    var targetPane = grid.Children.OfType<Grid>().SelectMany(h => h.Children.OfType<ExplorerPane>()).First();
                    await NavigationRecycleAudit.RunAsync(window, targetPane, root, output, args.Contains("--recycle-delete-only", StringComparer.Ordinal));
                    checks.Add(new { feature = "First-click drive collapse and recycle Delete native keyboard", result = "PASS" });
                    return;
                }
                if (contextMenuOnly)
                {
                    await ContextMenuAudit.RunAsync(window, root, output);
                    checks.Add(new { feature = "Grouped context menu: native input, submenus, EIASS, keyboard and upper-menu isolation", result = "PASS" });
                    return;
                }
                if (currentAudit)
                {
                    await CurrentUiAudit.RunAsync(window, root, output);
                    checks.Add(new { feature = "Current UI inspector-preview-memo-filter-inline-rename-theme-scale-AI-popup", result = "PASS", assertions = CurrentUiAudit.Assertions });
                    return;
                }
                if (eiassOnly)
                {
                    await EiassRenameRegression.RunAsync(window, root, output);
                    checks.Add(new { feature = "V153-EIASS-rename-logic-readers-native-dialog-apply-undo-redo", result = "PASS", assertions = EiassRenameRegression.Assertions });
                    return;
                }
                if (splitInteractionOnly)
                {
                    FileScrollRegression.EvidenceDirectory = output;
                    Console.WriteLine($"WINDOWS_UI os={Environment.OSVersion} interactive={Environment.UserInteractive} screen={SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight} dpi={VisualTreeHelper.GetDpi(window).DpiScaleX}");
                    await FileScrollRegression.RunAllSplitsAsync(window, root);
                    checks.Add(new { feature = "V151-R02-all-14-split-presets-interaction", result = "PASS", layouts = 14, scale = 90 });
                    await NativeUiRegression.RunAsync(window, root, output);
                    checks.Add(new { feature = "V151-R03-native-mouse-split-and-file-tree-scroll", result = "PASS" });
                    return;
                }
                if (fileScrollOnly)
                {
                    await FileScrollRegression.RunAsync(window, root);
                    checks.Add(new { feature = "V151-R01-file-scroll-single-split-reparent", result = "PASS" });
                    return;
                }
                byte[] pixels = new byte[32 * 1024 * 4];
                var tall = BitmapSource.Create(32, 1024, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
                using (var imageStream = new MemoryStream())
                {
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(tall)); png.Save(imageStream); imageStream.Position = 0;
                    var bounded = ImageThumbnailService.DecodeBounded(imageStream, 100, 100, CancellationToken.None);
                    if (bounded.PixelWidth > 100 || bounded.PixelHeight > 100) throw new Exception("Image decode exceeded its two-dimensional budget.");
                }
                var workspace = (Grid)window.FindName("Workspace");
                var pane = (ExplorerPane)((Grid)workspace.Children[0]).Children[0];
                var files = (DataGrid)pane.FindName("FilesGrid");
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (files.Items.Count != 3 && DateTime.UtcNow < deadline) await Task.Delay(30);
                if (files.Items.Count != 3) throw new Exception("Fixture folder was not loaded.");
                if (window.Background is not SolidColorBrush bg || bg.Color != Color.FromRgb(243, 245, 248)) throw new Exception("AppWindow background was not applied.");
                if (window.Foreground is not SolidColorBrush fg || fg.Color != Color.FromRgb(23, 43, 67)) throw new Exception("AppWindow foreground was not applied.");
                bool aiOpen = false;
                foreach (var sample in new[] { (1440,900,1,false), (1024,768,1,false), (1010,650,1,true), (1440,900,2,false), (1440,900,4,false), (1440,900,1,true) })
                {
                    window.Width = sample.Item1; window.Height = sample.Item2;
                    ((Button)window.FindName(sample.Item3 switch { 1 => "OneButton", 2 => "TwoButton", _ => "FourButton" })).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (aiOpen != sample.Item4)
                    {
                        var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, System.Windows.Input.Key.F8) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                        window.RaiseEvent(key); aiOpen = sample.Item4;
                    }
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout();
                    if (files.ActualHeight < 100 || files.ActualWidth < 250) throw new Exception("File list lost its usable viewport.");
                    if (sample.Item3 == 1 && ((Border)pane.FindName("DetailsPanel")).Visibility != Visibility.Collapsed) throw new Exception("Details should start closed.");
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    string name = $"{sample.Item1}x{sample.Item2}-{sample.Item3}pane-ai{sample.Item4}.png";
                    using (var file = File.Create(Path.Combine(output, name))) encoder.Save(file);
                    checks.Add(new { width = window.ActualWidth, height = window.ActualHeight, panels = sample.Item3, ai = sample.Item4, fileRows = files.Items.Count, viewportWidth = files.ActualWidth, viewportHeight = files.ActualHeight, dpiScale = VisualTreeHelper.GetDpi(window).DpiScaleX, screenshot = name });
                }
                if (aiOpen) ((Button)window.FindName("AiToggleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Width = 1500; window.Height = 900;
                ((Button)window.FindName("OneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var inspector = (FileInspector)window.FindName("Inspector");
                var previewPane = (FilePreviewPanel)window.FindName("PreviewPane");
                var fileArea = (Grid)window.FindName("FileAreaShell");
                var outerShell = (Grid)window.FindName("WorkspaceShell");
                if (!ReferenceEquals(((Grid)VisualTreeHelper.GetParent(inspector)), fileArea) ||
                    !ReferenceEquals(((Grid)VisualTreeHelper.GetParent(previewPane)), outerShell) ||
                    Grid.GetColumn(previewPane) != 2 || Grid.GetColumn(fileArea) != 0)
                    throw new Exception("Preview must be a sibling of the file area, in the far-right column.");
                if (inspector.FindName("PreviewRegion") is not null)
                    throw new Exception("Preview must not be nested inside the info/memo inspector.");
                var sampleFile = Path.Combine(root, "보고서.txt");
                files.SelectedItem = files.Items.OfType<Sunrise.FileManager.Models.FileEntry>()
                    .First(item => item.Name == "보고서.txt");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var detail = (TextBlock)inspector.FindName("DetailName");
                var editor = (TextBox)inspector.FindName("MemoEditor");
                if (detail.Text != "보고서.txt" || !editor.IsEnabled) throw new Exception("Inspector did not follow selection.");
                editor.Text = "선택한 파일의 테스트 메모";
                ((Button)inspector.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (FileMemoStore.ListForFile(sampleFile).Count != 1 ||
                    FileMemoStore.ListForFile(sampleFile)[0].Text != editor.Text)
                    throw new Exception("Inspector failed memo roundtrip.");
                ((Button)window.FindName("MainPreviewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                if (previewPane.Visibility != Visibility.Visible || inspector.Visibility != Visibility.Visible ||
                    previewPane.ActualWidth < 260 || inspector.ActualWidth < 295 ||
                    previewPane.ActualHeight < inspector.ActualHeight * 0.8)
                    throw new Exception("Preview must occupy a separate full-height column.");
                if (!string.Equals(previewPane.CurrentFilePath, sampleFile, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Preview did not follow active file selection.");
                var shot = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                shot.Render(window); var shotEncoder = new PngBitmapEncoder(); shotEncoder.Frames.Add(BitmapFrame.Create(shot));
                using (var image = File.Create(Path.Combine(output, "separate-preview-rightmost.png"))) shotEncoder.Save(image);
                ((Button)window.FindName("InspectorToggleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                if (inspector.Visibility != Visibility.Collapsed || previewPane.Visibility != Visibility.Visible || previewPane.ActualWidth < 260)
                    throw new Exception("Closing info/memo must not hide preview.");
                ((Button)window.FindName("InspectorToggleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (inspector.Visibility != Visibility.Visible || previewPane.Visibility != Visibility.Visible)
                    throw new Exception("Restoring info/memo must preserve preview.");
                ((Button)previewPane.FindName("ClosePreviewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (previewPane.Visibility != Visibility.Collapsed || inspector.Visibility != Visibility.Visible)
                    throw new Exception("Preview close button must not close info/memo.");
                ((Button)window.FindName("MainPreviewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (previewPane.Visibility != Visibility.Visible)
                    throw new Exception("Preview cannot reopen independently.");
                checks.Add(new { feature = "P182-standalone-full-height-right-preview", result = "PASS", screenshot = "separate-preview-rightmost.png" });
                ((Button)window.FindName("AiToggleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                var aiViewport = (ScrollViewer)window.FindName("AiViewport");
                var explorerViewport = (ScrollViewer)window.FindName("WorkspaceViewport");
                if (aiViewport.Visibility != Visibility.Visible ||
                    !ReferenceEquals(VisualTreeHelper.GetParent(aiViewport), fileArea) ||
                    Grid.GetColumn(aiViewport) != 0 || Grid.GetRow(aiViewport) != 1 ||
                    previewPane.Visibility != Visibility.Visible ||
                    previewPane.ActualHeight < fileArea.ActualHeight - 3 ||
                    inspector.ActualHeight < fileArea.ActualHeight - 3 ||
                    explorerViewport.ActualHeight >= fileArea.ActualHeight - 40)
                    throw new Exception("F8 must dock under Explorer while preview and inspector retain full height.");
                var f8shot = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
                    (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                f8shot.Render(window); var f8encoder = new PngBitmapEncoder();
                f8encoder.Frames.Add(BitmapFrame.Create(f8shot));
                using (var image = File.Create(Path.Combine(output, "preview-info-memo-f8.png"))) f8encoder.Save(image);
                checks.Add(new { feature = "R03-f8-below-explorer-preview-full-height", result = "PASS", screenshot = "preview-info-memo-f8.png" });

                var aiPanel = (AiCommandPanel)window.FindName("AiPanel");
                var provider = (ComboBox)aiPanel.FindName("Provider");
                var command = (TextBox)aiPanel.FindName("CommandBox");
                if (provider.Items.Count != 2 ||
                    !((ComboBoxItem)provider.Items[1]).Content.ToString()!.Contains("Ollama") ||
                    Grid.GetColumn(aiViewport) != 0 || Grid.GetRow(aiViewport) != 1)
                    throw new Exception("Ollama must be selectable inside the Explorer's F8 dock.");
                command.Text = "PDF 파일 찾아줘";
                var aiShot = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
                    (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                aiShot.Render(window); var aiEncoder = new PngBitmapEncoder();
                aiEncoder.Frames.Add(BitmapFrame.Create(aiShot));
                using (var image = File.Create(Path.Combine(output, "ollama-ai-dock.png"))) aiEncoder.Save(image);
                checks.Add(new { feature = "ollama-within-existing-ai-dock", result = "PASS", screenshot = "ollama-ai-dock.png" });
                ((Button)window.FindName("AiToggleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                window.Width = window.MinWidth; window.Height = 768;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                if (previewPane.Visibility != Visibility.Visible || inspector.Visibility != Visibility.Visible ||
                    Grid.GetColumn(previewPane) != 2 ||
                    previewPane.ActualHeight < inspector.ActualHeight - 3 ||
                    previewPane.ActualWidth < 260 || inspector.ActualWidth < 295)
                    throw new Exception("Right columns must remain usable at the minimum window width.");
                var smallShot = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
                    (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                smallShot.Render(window); var smallEncoder = new PngBitmapEncoder();
                smallEncoder.Frames.Add(BitmapFrame.Create(smallShot));
                using (var image = File.Create(Path.Combine(output, "preview-min-window-width.png"))) smallEncoder.Save(image);
                checks.Add(new { feature = "R03-min-window-width", result = "PASS", screenshot = "preview-min-window-width.png" });

                // V1.3.5 navigation regression: opening a folder and going back must not
                // pan the outer multi-pane viewport, and each new folder starts at x=0.
                ((Button)window.FindName("FourButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                var workspaceViewport = (ScrollViewer)window.FindName("WorkspaceViewport");
                workspaceViewport.ScrollToHorizontalOffset(0);
                pane.BringIntoView(); // Focus/selection requests from a descendant are not user horizontal scrolling.
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                if (workspaceViewport.HorizontalOffset > 0.5)
                    throw new Exception("Nested navigation panned the workspace horizontally.");

                string nestedFolder = Path.Combine(root, "검토 자료");
                pane.SyncToFolder(nestedFolder); // Same navigation owner as a folder double-click.
                var navigationDeadline = DateTime.UtcNow.AddSeconds(10);
                while ((pane.CurrentLocation != nestedFolder || files.Items.Count != 0) && DateTime.UtcNow < navigationDeadline)
                    await Task.Delay(30);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var fileScroll = FindScrollViewer(files);
                if (fileScroll is null) throw new Exception("File-list ScrollViewer was not found.");
                if (workspaceViewport.HorizontalOffset > 0.5 || fileScroll.HorizontalOffset > 0.5)
                    throw new Exception("Folder entry failed to reset horizontal viewport.");
                ((Button)pane.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                navigationDeadline = DateTime.UtcNow.AddSeconds(10);
                while ((pane.CurrentLocation != root || files.Items.Count != 3) && DateTime.UtcNow < navigationDeadline)
                    await Task.Delay(30);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                if (pane.CurrentLocation != root || files.Items.Count != 3 ||
                    workspaceViewport.HorizontalOffset > 0.5 || fileScroll.HorizontalOffset > 0.5)
                    throw new Exception("Back navigation shifted the file list or outer workspace right.");
                checks.Add(new { feature = "V135-folder-back-horizontal-scroll-stability", result = "PASS" });

            }
            catch (Exception ex) { error = ex.ToString(); Console.WriteLine(error); NativeUiRegression.CaptureDesktop(Path.Combine(output, "failure-desktop.png")); }
            finally
            {
                File.WriteAllText(Path.Combine(output, "UI_RESULT.json"), JsonSerializer.Serialize(new { status = error is null ? "PASS" : "FAIL", checks, error }, new JsonSerializerOptions { WriteIndented = true }));
                if (contextMenuOnly || navigationRecycleOnly || recycleBatchOnly)
                {
                    // Focused source-UI assertions can finish inside a modal Windows
                    // message loop. Do not reenter the product close guard from that loop.
                    // Normal app shutdown is checked separately on the installed EXE.
                    timeout.Dispose();
                    try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    Environment.Exit(error is null ? 0 : 1);
                }
                window.Close(); app.Shutdown(error is null ? 0 : 1);
            }
        };
        try { app.Run(window); }
        finally { Directory.Delete(root, true); }
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

}
