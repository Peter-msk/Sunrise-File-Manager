using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sunrise.FileManager;
using Sunrise.FileManager.Controls;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

internal static class CurrentUiAudit
{
    internal static int Assertions { get; private set; }
    private static void Check(bool value, string name) { Assertions++; if (!value) throw new Exception(name); }
    private static void Click(FrameworkElement owner, string name) => ((Button)owner.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition, string name)
    { var end = DateTime.UtcNow.AddSeconds(15); while (!condition() && DateTime.UtcNow < end) await Task.Delay(40); Check(condition(), name); }
    internal static async Task RunAsync(MainWindow window, string root, string output)
    {
        var workspace = (Grid)window.FindName("Workspace");
        var pane = workspace.Children.OfType<Grid>().SelectMany(h => h.Children.OfType<ExplorerPane>()).First();
        var files = (DataGrid)pane.FindName("FilesGrid");
        await Until(() => files.Items.Count == 3, "Initial folder list failed.");
        var inspector = (FileInspector)window.FindName("Inspector");
        var preview = (FilePreviewPanel)window.FindName("PreviewPane");
        string report = Path.Combine(root, "보고서.txt");
        await pane.RevealFileAsync(report);
        Check(((TextBlock)inspector.FindName("DetailName")).Text == "보고서.txt", "Inspector did not follow file selection.");
        var memo = (TextBox)inspector.FindName("MemoEditor"); memo.Text = "Current UI audit memo";
        Click(inspector, "SaveButton");
        Check(FileMemoStore.ListForFile(report).Single().Text == memo.Text, "Memo UI save failed.");
        await Until(() => pane.SelectedFile?.FullPath == report, "Memo refresh lost the selected file.");
        await Task.Delay(650);
        Check(pane.SelectedFile?.FullPath == report, "Watcher refresh lost the selected file.");
        Click(window, "MainPreviewButton");
        await Until(() => preview.IsVisible && preview.CurrentFilePath == report, "Independent preview did not follow selection.");
        Click(window, "InspectorToggleButton");
        Check(!inspector.IsVisible && preview.IsVisible, "Inspector toggle unexpectedly hid preview.");
        Click(window, "InspectorToggleButton");
        Check(inspector.IsVisible && preview.IsVisible, "Inspector cannot reopen with preview.");
        var search = (TextBox)pane.FindName("SearchBox"); search.Text = "보고서";
        await Until(() => files.Items.Count == 1, "Current folder search failed.");
        search.Text = ""; await Until(() => files.Items.OfType<FileEntry>().Any(f => f.Name == "검토 메모.md"), "Clearing search failed.");
        await pane.RevealFileAsync(report);
        Click(pane, "RenameButton");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout();
        var entry = files.SelectedItem as FileEntry;
        Check(entry?.IsRenaming == true, "Inline rename did not open.");
        entry!.RenameDraft = "보고서_검증";
        var editor = Descendants<TextBox>(files).First(t => t.Name == "InlineRenameEditor" && t.IsVisible);
        editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        string renamed = Path.Combine(root, "보고서_검증.txt");
        await Until(() => File.Exists(renamed), "Inline rename failed or extension was lost.");
        Check(FileMemoStore.ListForFile(renamed).Single().Text == "Current UI audit memo", "Inline rename lost memo.");
        Click(pane, "UndoButton");
        await Until(() => File.Exists(report) && ((Button)pane.FindName("RedoButton")).IsEnabled, "Inline rename Undo failed.");
        // Exercise the actual WIC preview decode through file selection.
        string image = Path.Combine(root, "preview.png");
        var pixels = new byte[64 * 1024 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 64; pixels[i + 1] = (byte)((i / 256) % 255); pixels[i + 2] = 192; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(64, 1024, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        using (var stream = File.Create(image)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); }
        await pane.RevealFileAsync(image);
        await Until(() => ((Image)preview.FindName("PreviewImage")).Source is BitmapSource, "WIC image preview failed.");
        var decoded = (BitmapSource)((Image)preview.FindName("PreviewImage")).Source;
        Check(decoded.PixelWidth <= 1000 && decoded.PixelHeight <= 1500, "Preview exceeded decode budget.");
        NativeUiRegression.CaptureDesktop(Path.Combine(output, "current-preview-light.png"));
        ((ComboBox)window.FindName("ThemeSelector")).SelectedIndex = 2;
        await Task.Delay(150); Check(ThemeManager.IsDark, "Dark theme menu failed.");
        NativeUiRegression.CaptureDesktop(Path.Combine(output, "current-preview-dark.png"));
        ((ComboBox)window.FindName("ThemeSelector")).SelectedIndex = 1;
        var scale = (ComboBox)window.FindName("UiScaleSelector");
        scale.SelectedItem = scale.Items.OfType<ComboBoxItem>().Single(x => (int)x.Tag == 125);
        await Task.Delay(150); Check(UiScaleService.Percent == 125, "125% application scale failed.");
        window.UpdateLayout(); Check(files.ActualHeight > 100, "125% application scale lost file viewport.");
        NativeUiRegression.CaptureDesktop(Path.Combine(output, "current-scale-125.png"));
        scale.SelectedItem = scale.Items.OfType<ComboBoxItem>().Single(x => (int)x.Tag == 80);
        Click(window, "AiToggleButton");
        await Until(() => Application.Current.Windows.OfType<Window>().Any(w => w != window && w.IsVisible && w.Content is AiCommandPanel), "Current AI popup did not open.");
        var popup = Application.Current.Windows.OfType<Window>().Single(w => w != window && w.IsVisible && w.Content is AiCommandPanel);
        NativeUiRegression.CaptureDesktop(Path.Combine(output, "current-ai-popup.png")); popup.Close();
        Check(window.IsVisible && !popup.IsVisible, "Closing AI popup affected main workspace.");
        Console.WriteLine($"CURRENT_UI_AUDIT_PASS assertions={Assertions}");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is T t) yield return t; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
}
