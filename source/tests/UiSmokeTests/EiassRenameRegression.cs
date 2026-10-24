using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Sunrise.FileManager;
using Sunrise.FileManager.Controls;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

internal static class EiassRenameRegression
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    internal static int Assertions { get; private set; }
    private static void Check(bool condition, string message) { Assertions++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message) { try { action(); } catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException) { Assertions++; return; } throw new Exception(message); }

    internal static async Task RunAsync(MainWindow window, string root, string output)
    {
        LogicAndFiles(Path.Combine(root, "isolated-regressions"));
        string first = Path.Combine(root, "표지 및 목차.pdf"), second = Path.Combine(root, "동·식물상.pdf");
        File.WriteAllText(first, "first document content"); File.WriteAllText(second, "second document content");
        FileMemoStore.Save(first, "EIASS rename memo", userName: "Regression");
        var workspace = (Grid)window.FindName("Workspace");
        var pane = (ExplorerPane)((Grid)workspace.Children[0]).Children[0];
        var files = (DataGrid)pane.FindName("FilesGrid");
        int busyStarts = 0, busyEnds = 0;
        pane.BlockingFileActionChanged += busy => { if (busy) busyStarts++; else busyEnds++; };
        await WaitUntil(() => files.Items.OfType<FileEntry>().Count(r => r.FullPath == first || r.FullPath == second) == 2);
        foreach (var entry in files.Items.OfType<FileEntry>().Where(r => r.FullPath == first || r.FullPath == second)) files.SelectedItems.Add(entry);
        Exception? driverError = null; bool drove = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += async (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<EiassRenameDialog>().FirstOrDefault();
            if (dialog is null || !dialog.IsLoaded) return;
            timer.Stop();
            try
            {
                drove = true; UiScaleService.Apply(dialog); await Task.Delay(180);
                var grid = (DataGrid)dialog.FindName("PreviewGrid");
                Check(grid.Items.Count == 2, "Dialog lost selected PDF rows.");
                var rows = grid.Items.OfType<EiassRenameDialog.Row>().ToArray();
                Check(rows.Select(r => r.Code).SequenceEqual(new[] { "0000", "0001" }), "Default numbering failed.");
                rows[1].Code = "0000";
                Check(!((Button)dialog.FindName("ApplyButton")).IsEnabled, "Duplicate code accepted.");
                rows[1].Code = "0001";
                grid.SelectedIndex = 1;
                string moved = rows[1].SourcePath;
                await Click((FrameworkElement)dialog.FindName("UpButton"));
                Check(((EiassRenameDialog.Row)grid.Items[0]).SourcePath == moved, "Native Up click failed.");
                await Click((FrameworkElement)dialog.FindName("DownButton"));
                Check(((EiassRenameDialog.Row)grid.Items[1]).SourcePath == moved, "Native Down click failed.");
                ((TextBox)dialog.FindName("StepBox")).Text = "100";
                await Click((FrameworkElement)dialog.FindName("NumberButton"));
                Check(((EiassRenameDialog.Row)grid.Items[1]).Code == "0100", "Step 100 was not applied.");
                var assessment = (ComboBox)dialog.FindName("AssessmentBox");
                assessment.SelectedIndex = 3;
                Check(!((EiassRenameDialog.Row)grid.Items[0]).NewName.StartsWith('('), "Post assessment still has a prefix.");
                assessment.SelectedIndex = 4;
                Check(!((EiassRenameDialog.Row)grid.Items[0]).NewName.StartsWith('('), "Abbreviated final still has a prefix.");
                assessment.SelectedIndex = 5;
                Check(!((Button)dialog.FindName("ApplyButton")).IsEnabled, "Climate names must be preserved.");
                assessment.SelectedIndex = 2;
                var stageBox = (ComboBox)dialog.FindName("StageBox");
                stageBox.ApplyTemplate();
                var stageEditor = (TextBox)stageBox.Template.FindName("PART_EditableTextBox", stageBox);
                Check(stageEditor is not null && stageEditor.IsVisible && stageEditor.Text == "초안", "Stage editor is blank or inaccessible.");
                await Click(Descendants<System.Windows.Controls.Primitives.ToggleButton>(stageBox).Single());
                Check(stageBox.IsDropDownOpen, "Stage dropdown cannot be opened with the mouse.");
                stageBox.IsDropDownOpen = false;
                stageEditor!.Text = "보완 3차";
                ((ComboBox)dialog.FindName("DigitsBox")).SelectedIndex = 1;
                Check(((EiassRenameDialog.Row)grid.Items[0]).NewName.StartsWith("(보완 3차) 00000 "), "Editable stage or five digits failed.");
                ((ComboBox)dialog.FindName("StageBox")).Text = "초안";
                ((ComboBox)dialog.FindName("DigitsBox")).SelectedIndex = 0;
                ((TextBox)dialog.FindName("StepBox")).Text = "1";
                await Click((FrameworkElement)dialog.FindName("NumberButton"));
                var firstRow = (EiassRenameDialog.Row)grid.Items[0];
                string savedTitle = firstRow.Title;
                firstRow.Title = "★검토 & 제목";
                Check(!((Button)dialog.FindName("ApplyButton")).IsEnabled, "Forbidden title did not block apply.");
                await Click((FrameworkElement)dialog.FindName("CleanCheck"));
                Check(((Button)dialog.FindName("ApplyButton")).IsEnabled && !firstRow.NewName.Contains('★'), "Cleaning did not update preview.");
                firstRow.Title = savedTitle;
                // Open the real expander with a native pointer before operating the contents controls.
                var expander = Descendants<Expander>(dialog).Single();
                expander.IsExpanded = true; await Settle(dialog);
                ((TextBox)dialog.FindName("ContentsBox")).Text = "목차\n표지 및 목차\n동·식물상";
                await Click((FrameworkElement)dialog.FindName("MatchContentsButton"));
                Check(((EiassRenameDialog.Row)grid.Items[0]).Title == "표지 및 목차", "Unnumbered TOC order failed.");
                Check(!((Button)dialog.FindName("ApplyButton")).IsEnabled, "TOC requires order review.");
                await Click((FrameworkElement)dialog.FindName("OrderConfirmedCheck"));
                Check(((Button)dialog.FindName("ApplyButton")).IsEnabled, "Reviewed TOC cannot apply.");
                dialog.Width = 900; dialog.Height = 640; await Settle(dialog);
                Check(grid.ActualHeight > 100, "Minimum dialog size lost the editable table.");
                var apply = (Button)dialog.FindName("ApplyButton");
                var corner = apply.TranslatePoint(new Point(apply.ActualWidth, apply.ActualHeight), dialog);
                Check(corner.X <= dialog.ActualWidth && corner.Y <= dialog.ActualHeight, "Minimum dialog size clipped Apply.");
                NativeUiRegression.CaptureDesktop(Path.Combine(output, "eiass-minimum-native.png"));
                dialog.Width = 1120; dialog.Height = 760; await Settle(dialog);
                NativeUiRegression.CaptureDesktop(Path.Combine(output, "eiass-preview-native.png"));
                await Click((FrameworkElement)dialog.FindName("ApplyButton"));
            }
            catch (Exception ex) { driverError = ex; NativeUiRegression.CaptureDesktop(Path.Combine(output, "eiass-failure.png")); dialog.Close(); }
        };
        timer.Start();
        ((MenuItem)pane.FindName("EiassCommandMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        timer.Stop();
        if (driverError is not null) throw driverError;
        Check(drove, "Real pane command did not open the EIASS dialog.");
        string renamedFirst = Path.Combine(root, "(초안) 0000 표지 및 목차.pdf"), renamedSecond = Path.Combine(root, "(초안) 0001 동·식물상.pdf");
        Check(File.ReadAllText(renamedFirst) == "first document content" && File.ReadAllText(renamedSecond) == "second document content", "Actual apply lost content or names.");
        Check(FileMemoStore.ListForFile(renamedFirst).Single().Text == "EIASS rename memo", "Memo did not follow apply.");
        ((Button)pane.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(busyStarts == 1 && busyEnds == 0, "Undo did not announce its in-flight file work.");
        // Closing a real WPF window during Undo must show the guard and preserve the window.
        var dismissGuard = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            Thread.Sleep(500);
            NativeUiRegression.CaptureDesktop(Path.Combine(output, "undo-close-guard-native.png"));
            keybd_event(0x0D, 0, 0, 0); keybd_event(0x0D, 0, 2, 0);
            dismissGuard.TrySetResult(true);
        }) { IsBackground = true }.Start();
        window.Close();
        Check(await dismissGuard.Task && window.IsVisible, "Main window closed during Undo or no close guard was shown.");
        await WaitUntil(() => File.Exists(first) && File.Exists(second) && ((Button)pane.FindName("RedoButton")).IsEnabled);
        Check(busyEnds == 1, "Undo left its close guard active.");
        Check(File.Exists(first) && File.Exists(second) && !File.Exists(renamedFirst), "Pane Undo did not restore filenames.");
        Check(FileMemoStore.ListForFile(first).Single().Text == "EIASS rename memo", "Memo did not follow Undo.");
        ((Button)pane.FindName("RedoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(busyStarts == 2 && busyEnds == 1, "Redo did not announce its in-flight file work.");
        await WaitUntil(() => File.Exists(renamedFirst) && File.Exists(renamedSecond) && ((Button)pane.FindName("UndoButton")).IsEnabled);
        Check(busyEnds == 2, "Redo left its close guard active.");
        Check(File.Exists(renamedFirst) && File.Exists(renamedSecond), "Pane Redo failed.");
        ((Button)pane.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => File.Exists(first) && File.Exists(second) && ((Button)pane.FindName("RedoButton")).IsEnabled);
        await Task.Delay(200); NativeUiRegression.CaptureDesktop(Path.Combine(output, "eiass-restored-native.png"));
        Console.WriteLine($"EIASS_ASSERTIONS={Assertions} native dialog reorder/clean/number/match/review/apply PASS; pane undo/redo PASS");
    }

    private static void LogicAndFiles(string root)
    {
        Directory.CreateDirectory(root);
        string paging = Path.Combine(root, "paging-fixture"); Directory.CreateDirectory(paging);
        for (int i = 0; i < 2005; i++) File.WriteAllText(Path.Combine(paging, $"f{i:D4}.txt"), "fixture");
        var firstPage = DirectoryScanner.ReadPageAsync(paging, null, "", "Name", false, "All", false, default, Path.Combine(paging, "f0005.txt")).GetAwaiter().GetResult();
        Check(firstPage.Entries.Count == 2000 && firstPage.Entries[0].Name == "f0000.txt" && !firstPage.IsRevealPage, "In-page reveal dropped earlier entries.");
        var boundary = DirectoryScanner.ReadPageAsync(paging, null, "", "Name", false, "All", false, default, Path.Combine(paging, "f2000.txt")).GetAwaiter().GetResult();
        Check(boundary.IsRevealPage && boundary.Entries[0].Name == "f2000.txt" && boundary.RevealBefore?.Name == "f1999.txt", "Reveal failed at the 2000-row boundary.");
        var farPage = DirectoryScanner.ReadPageAsync(paging, null, "", "Name", false, "All", false, default, Path.Combine(paging, "f2003.txt")).GetAwaiter().GetResult();
        Check(farPage.IsRevealPage && farPage.Entries.Count == 2 && farPage.TotalEntries == 2005 && farPage.RevealBefore?.Name == "f2002.txt", "Off-page reveal lost its predecessor cursor.");
        foreach (string stage in EiassFileNameService.Stages)
            Check(EiassFileNameService.MakeName("a.pdf", "0000", "표지 및 목차", 4, stage, false) == $"({stage}) 0000 표지 및 목차.pdf", "Stage formatting failed.");
        Check(EiassFileNameService.MakeName("a.PDF", "00000", "수리·수문 (현황)", 5, null, false) == "00000 수리·수문 (현황).PDF", "Allowed punctuation / extension damaged.");
        Reject(() => EiassFileNameService.MakeName("a.hwp", "0000", "목차", 4, "초안", false), "Non-PDF accepted.");
        foreach (string title in new[] { "★제목", "●제목", "제목+", "제목&", "①제목", "제목<", "제목*", "제목\"", "제목?", "제목/", "제목‘" })
            Reject(() => EiassFileNameService.MakeName("a.pdf", "0000", title, 4, "초안", false), "Forbidden title accepted.");
        Reject(() => EiassFileNameService.MakeName("a.pdf", "１２３４", "제목", 4, "초안", false), "Non-ASCII number accepted.");
        Reject(() => EiassFileNameService.MakeName("a.pdf", "0000", "★", 4, "초안", true), "Empty cleaned title accepted.");
        Check(EiassFileNameService.Number(3, 4, "0", "100").SequenceEqual(new[] { "0000", "0100", "0200" }), "Spacing numbers failed.");
        Check(EiassFileNameService.Number(1, 5, "99999", "1").Single() == "99999", "Max valid code rejected.");
        Reject(() => EiassFileNameService.Number(2, 4, "9999", "1"), "Overflow accepted.");
        Reject(() => EiassFileNameService.Number(2, 4, "0", "0"), "Zero step accepted.");
        var contents = EiassFileNameService.ParseContents("목차\n제 1 장 표지 및 목차 .... 1\n2. 동·식물상\n수리·수문  123\n기준연도 2026");
        Check(contents.SequenceEqual(new[] { "표지 및 목차", "동·식물상", "수리·수문", "기준연도 2026" }), "TOC cleanup removed a title number or left page numbers.");
        Check(EiassFileNameService.MatchContents(new[] { "동·식물상", "표지및목차", "미일치" }, contents).SequenceEqual(new int?[] { 1, 0, null }), "TOC exact order matching failed.");
        Check(EiassFileNameService.MatchContents(new[] { "동·식물상", "동·식물상" }, contents).All(x => x is null), "Duplicate filenames were guessed.");
        Check(EiassFileNameService.MatchContents(new[] { "제목" }, new[] { "제목", "제목" }).Single() is null, "Duplicate contents were guessed.");
        string txt = Path.Combine(root, "contents.txt"); File.WriteAllText(txt, "표지 및 목차\n동·식물상", new UTF8Encoding(true));
        Check(EiassTableOfContentsReader.Read(txt, default).Contains("동·식물상"), "TXT reader failed.");
        foreach (string ext in new[] { ".docx", ".hwpx" })
        {
            string path = Path.Combine(root, "contents" + ext);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry(ext == ".docx" ? "word/document.xml" : "Contents/section0.xml").Open());
                writer.Write("<doc><p><t>표지 및 목차</t></p><p><t>동·식물상</t></p></doc>");
            }
            Check(EiassFileNameService.ParseContents(EiassTableOfContentsReader.Read(path, default)).Count == 2, ext + " reader lost paragraph boundaries.");
        }
        string pdf = Path.Combine(root, "contents.pdf"); WritePdf(pdf, "Cover and contents");
        Check(EiassTableOfContentsReader.Read(pdf, default).Contains("Cover and contents"), "PDF text reader failed.");
        string blank = Path.Combine(root, "blank.pdf"); WritePdf(blank, "");
        Reject(() => EiassTableOfContentsReader.Read(blank, default), "Textless PDF silently accepted.");
        string a = Path.Combine(root, "A.pdf"), b = Path.Combine(root, "B.pdf"), outside = Path.Combine(root, "Occupied.pdf");
        File.WriteAllText(a, "AAA"); File.WriteAllText(b, "BBB"); File.WriteAllText(outside, "DO NOT OVERWRITE");
        FileMemoStore.Save(a, "memo A", userName: "Regression"); FileMemoStore.Save(b, "memo B", userName: "Regression");
        var undo = new ExplorerUndoService();
        var swapped = FileActionsService.BatchRenameFilesSafely(new[] { (a, "B.pdf"), (b, "A.pdf") });
        undo.RecordBatchRename(swapped, true);
        Check(File.ReadAllText(a) == "BBB" && File.ReadAllText(b) == "AAA", "Name swap failed.");
        Check(FileMemoStore.ListForFile(b).Single().Text == "memo A", "Swap memo mismatch.");
        Check(undo.UndoLast().Success && File.ReadAllText(a) == "AAA", "Swap Undo failed.");
        Check(undo.RedoLast().Success && File.ReadAllText(a) == "BBB", "Swap Redo failed.");
        Check(undo.UndoLast().Success, "Second swap Undo failed.");
        Reject(() => FileActionsService.BatchRenameFilesSafely(new[] { (a, "Occupied.pdf"), (b, "Other.pdf") }), "Existing target overwritten.");
        Check(File.ReadAllText(outside) == "DO NOT OVERWRITE" && File.Exists(a) && File.Exists(b), "Collision modified files.");
        Reject(() => FileActionsService.BatchRenameFilesSafely(new[] { (a, "Same.pdf"), (b, "Same.pdf") }), "Duplicate targets accepted.");
        // Failure after staging A must restore A and its memo, with no temporary files left.
        using (var locked = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None))
            Reject(() => FileActionsService.BatchRenameFilesSafely(new[] { (a, "NewA.pdf"), (b, "NewB.pdf") }), "Locked file did not stop batch.");
        Check(File.Exists(a) && File.Exists(b) && FileMemoStore.ListForFile(a).Single().Text == "memo A", "Staging failure rollback failed.");
        Check(!Directory.EnumerateFiles(root).Any(p => Path.GetFileName(p).StartsWith(".sunrise-eiass-")), "Staging files leaked.");
        using (var locked = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None))
            Reject(() => FileActionsService.BatchRename(new[] { (a, "GenericA.pdf"), (b, "GenericB.pdf") }), "Generic rename did not stop on a locked file.");
        Check(File.ReadAllText(a) == "AAA" && File.ReadAllText(b) == "BBB" && !File.Exists(Path.Combine(root, "GenericA.pdf")), "Generic rename partially applied before failure.");
        Check(FileMemoStore.ListForFile(a).Single().Text == "memo A", "Generic rollback lost its memo.");
        string folder = Path.Combine(root, "Folder"); Directory.CreateDirectory(folder);
        Reject(() => FileActionsService.BatchRename(new[] { (folder, "RenamedFolder"), (a, "Occupied.pdf") }), "Mixed rename failed to preflight an occupied target.");
        Check(Directory.Exists(folder) && !Directory.Exists(Path.Combine(root, "RenamedFolder")), "Mixed rename changed a folder before preflight finished.");
        string movedFolder = FileActionsService.Rename(folder, "RenamedFolder");
        var folderUndo = new ExplorerUndoService(); folderUndo.RecordRename(folder, movedFolder);
        Directory.CreateDirectory(folder);
        Check(!folderUndo.UndoLast().Success && folderUndo.CanUndo, "Folder Undo collision discarded its history.");
        Directory.Delete(folder);
        Check(folderUndo.UndoLast().Success && Directory.Exists(folder), "Folder Undo retry failed.");
        Directory.CreateDirectory(movedFolder);
        Check(!folderUndo.RedoLast().Success && folderUndo.CanRedo, "Folder Redo collision discarded its history.");
        Directory.Delete(movedFolder);
        Check(folderUndo.RedoLast().Success && Directory.Exists(movedFolder), "Folder Redo retry failed.");
        string blockedTarget = Path.Combine(root, "BlockedByMemo.pdf");
        File.WriteAllText(blockedTarget, "temporary fixture");
        var orphan = FileMemoStore.Save(blockedTarget, "KEEP EXISTING MEMO", userName: "Regression");
        File.Delete(blockedTarget);
        Reject(() => FileActionsService.BatchRenameFilesSafely(new[] { (a, "PublishedA.pdf"), (b, "BlockedByMemo.pdf") }), "Destination memo collision accepted.");
        Check(File.ReadAllText(a) == "AAA" && File.ReadAllText(b) == "BBB" && !File.Exists(Path.Combine(root, "PublishedA.pdf")), "Publish-phase failure did not restore both files.");
        Check(FileMemoStore.ListForFile(a).Single().Text == "memo A" && File.Exists(orphan.Path), "Publish rollback lost a source or existing target memo.");
        Check(!Directory.EnumerateFiles(root).Any(p => Path.GetFileName(p).StartsWith(".sunrise-eiass-")), "Publish rollback left staging files.");
        var renamed = FileActionsService.BatchRenameFilesSafely(new[] { (a, "NewA.pdf") }); undo.RecordBatchRename(renamed, true);
        File.WriteAllText(a, "EXTERNAL");
        Check(!undo.UndoLast().Success && undo.CanUndo && File.ReadAllText(a) == "EXTERNAL", "Undo collision lost history or overwrote external file.");
        File.Delete(a); Check(undo.UndoLast().Success && File.ReadAllText(a) == "AAA", "Retry Undo failed.");
    }

    private static void WritePdf(string path, string text)
    {
        string stream = $"BT /F1 12 Tf 30 800 Td ({text}) Tj ET";
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream" };
        var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length; pdf.Append($"xref\n0 6\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); if (child is T t) yield return t; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private static async Task Settle(Window window) { await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout(); await Task.Delay(100); }
    private static async Task Click(FrameworkElement element)
    {
        var owner = Window.GetWindow(element); await Settle(owner);
        Check(element.IsVisible && element.IsEnabled && element.ActualWidth > 0, "Cannot click " + element.Name);
        Point point = element.PointToScreen(new Point(Math.Min(element.ActualWidth / 2, 25), element.ActualHeight / 2));
        Check(SetCursorPos((int)point.X, (int)point.Y), "Native cursor failed.");
        mouse_event(0x0002, 0, 0, 0, 0); await Task.Delay(50); mouse_event(0x0004, 0, 0, 0, 0);
        await Task.Delay(150);
    }
    private static async Task WaitUntil(Func<bool> condition)
    { var deadline = DateTime.UtcNow.AddSeconds(10); while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(40); Check(condition(), "File view fixture timeout."); }
}
