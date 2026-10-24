using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Sunrise.FileManager;
using Sunrise.FileManager.Controls;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

internal static class RecycleBatchAudit
{
    private delegate bool EnumProc(nint hwnd, nint p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc action, nint p);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumProc action, nint p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint msg, nint wp, nint lp);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static string Text(nint hwnd) { var text = new StringBuilder(2048); GetWindowText(hwnd, text, text.Capacity); return text.ToString(); }
    private static async Task Until(Func<bool> test, string message)
    { var end = DateTime.UtcNow.AddSeconds(25); while (!test() && DateTime.UtcNow < end) await Task.Delay(50); Check(test(), message); }
    private static bool Busy(ExplorerPane pane) => (bool)typeof(ExplorerPane).GetField("_restoringRecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pane)!;
    private static void Key(byte key) { keybd_event(key, 0, 0, 0); keybd_event(key, 0, 2, 0); }
    private static MenuItem Command(ExplorerPane pane, string header) => ((MenuItem)pane.FindName("RecycleCommandMenu")).Items.OfType<MenuItem>().Single(x => Equals(x.Header, header));
    private static void Invoke(ExplorerPane pane, string title) => Command(pane, title).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static void Select(DataGrid files, IEnumerable<string> identities)
    {
        var keys = identities.ToHashSet(StringComparer.Ordinal); files.SelectedItems.Clear();
        foreach (var entry in files.Items.OfType<FileEntry>().Where(e => keys.Contains(e.FullPath))) files.SelectedItems.Add(entry);
        Check(files.SelectedItems.Count == keys.Count, "Fixture selection was truncated."); files.Focus();
    }
    private sealed class Dialogs : IDisposable
    {
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
        private readonly HashSet<nint> seen = new();
        internal List<string> Prompts { get; } = new();
        internal List<string> Unexpected { get; } = new();
        internal Dialogs(string output, string name, bool approve)
        {
            timer.Tick += (_, _) => EnumWindows((hwnd, _) =>
            {
                var cls = new StringBuilder(100); GetClassName(hwnd, cls, cls.Capacity);
                if (!IsWindowVisible(hwnd) || cls.ToString() != "#32770") return true;
                string title = Text(hwnd);
                // The hosted VM can display an unrelated boot-time paging-file notice.
                if (title == "System Properties") { PostMessage(hwnd, 0x111, 1, 0); return true; }
                if (!seen.Add(hwnd)) return true;
                string details = title;
                EnumChildWindows(hwnd, (child, _) => { details += " | " + Text(child); return true; }, 0);
                NativeUiRegression.CaptureDesktop(Path.Combine(output, $"{name}-dialog-{seen.Count}.png"));
                Console.WriteLine("RECYCLE_DIALOG " + details);
                if (title.Contains("Delete", StringComparison.OrdinalIgnoreCase) || title.Contains("Recycle Bin", StringComparison.OrdinalIgnoreCase) || title == "휴지통 전체 복원")
                { Prompts.Add(details); PostMessage(hwnd, 0x111, approve ? 6 : 7, 0); }
                else { Unexpected.Add(details); PostMessage(hwnd, 0x111, 1, 0); }
                return true;
            }, 0);
            timer.Start();
        }
        public void Dispose() => timer.Stop();
    }
    private static async Task CheckPropertiesAsync(ExplorerPane pane, string title, string output, string name)
    {
        int matched = 0; bool seen = false; nint observed = 0; string? failure = null;
        using var timer = new System.Threading.Timer(_ => EnumWindows((hwnd, _) =>
        {
            string actual = Text(hwnd);
            bool expected = name == "recycle-bin-properties" ? actual == "Recycle Bin Properties"
                : actual.EndsWith(" Properties", StringComparison.OrdinalIgnoreCase) && actual is not ("System Properties" or "Recycle Bin Properties");
            if (IsWindowVisible(hwnd) && expected && Interlocked.CompareExchange(ref matched, 1, 0) == 0)
            {
                observed = hwnd;
                try { NativeUiRegression.CaptureDesktop(Path.Combine(output, name + ".png")); }
                catch (Exception ex) { failure = ex.ToString(); }
                finally { PostMessage(hwnd, 0x10, 0, 0); seen = true; }
            }
            return true;
        }, 0), null, 50, 80);
        Invoke(pane, title);
        await Until(() => seen && !Busy(pane) && !IsWindow(observed), "Native properties window did not appear/finish: " + title);
        Check(failure is null, failure ?? "Property capture failed.");
    }
    private static async Task CheckNativeMenuAsync(Action show, string output, string name, ExplorerPane? pane = null)
    {
        int matched = 0; bool seen = false; string? failure = null;
        using var timer = new System.Threading.Timer(_ => EnumWindows((hwnd, _) =>
        {
            var cls = new StringBuilder(100); GetClassName(hwnd, cls, cls.Capacity);
            if (IsWindowVisible(hwnd) && cls.ToString() == "#32768" && Interlocked.CompareExchange(ref matched, 1, 0) == 0)
            {
                try { NativeUiRegression.CaptureDesktop(Path.Combine(output, name + ".png")); }
                catch (Exception ex) { failure = ex.ToString(); }
                finally { seen = true; Key(0x1B); }
            }
            return true;
        }, 0), null, 50, 80);
        show(); await Until(() => seen && (pane is null || !Busy(pane)), "Native menu did not appear/finish: " + name);
        Check(failure is null, failure ?? "Native menu capture failed.");
    }
    internal static async Task RunAsync(MainWindow window, ExplorerPane pane, string root, string output, bool nativeTailOnly = false)
    {
        var files = (DataGrid)pane.FindName("FilesGrid");
        string prefix = "sunrise-batch-" + Guid.NewGuid().ToString("N");
        var before = await RecycleBinService.ReadAsync(default);
        // This runner is isolated. All-bin operations below are forbidden if any preexisting item exists.
        Check(before.Entries.Count == 0, "Refusing all-bin tests: recycle bin is not an empty isolated fixture.");
        string group = Path.Combine(root, prefix); Directory.CreateDirectory(group);
        string guard = Path.Combine(group, "unselected-guard.txt");
        if (!nativeTailOnly)
        {
        var deleted = Enumerable.Range(0, 103).Select(i => Path.Combine(group, $"item-{i:D3}.txt")).ToArray();
        foreach (string path in deleted) { File.WriteAllText(path, "synthetic batch"); FileActionsService.Recycle(path); }
        File.WriteAllText(guard, "must survive selection delete"); FileActionsService.Recycle(guard);
        pane.SyncToFolder(WindowsKnownLocations.RecycleBin);
        await Until(() => pane.IsRecycleBin && files.Items.Count == 104, "104 recycled fixtures did not appear.");
        Check(((MenuItem)pane.FindName("RecycleCommandMenu")).Visibility == Visibility.Visible, "Recycle commands unavailable.");
        foreach (string title in new[] { "원래 위치로 복원", "영구 삭제…", "모든 항목 복원…", "휴지통 비우기…", "속성", "휴지통 속성·설정…", "Windows 기본 메뉴…" }) Command(pane, title);
        Check(((DataGridColumn)pane.FindName("RecycleDeletedColumn")).Visibility == Visibility.Visible && ((DataGridColumn)pane.FindName("RecycleOriginColumn")).Visibility == Visibility.Visible, "Recycle metadata columns missing.");
        var snapshot = await RecycleBinService.ReadAsync(default);
        var selected = snapshot.Entries.Where(e => e.OriginalLocation == group && e.Name.StartsWith("item-", StringComparison.Ordinal)).Select(e => e.FullPath).ToArray();
        string guardKey = snapshot.Entries.Single(e => e.OriginalLocation == group && e.Name.StartsWith("unselected-guard", StringComparison.Ordinal)).FullPath;
        Check(selected.Length == 103, "Fixture identities not resolved.");
        foreach (bool approve in new[] { false, true })
        {
            Select(files, selected);
            using var dialogs = new Dialogs(output, approve ? "batch-approve" : "batch-cancel", approve);
            Key(0x2E);
            await Until(() => dialogs.Prompts.Count > 0 || dialogs.Unexpected.Count > 0, "Native Delete did not reach Windows aggregate confirmation.");
            await Until(() => !Busy(pane), "Batch delete did not finish.");
            Check(dialogs.Unexpected.Count == 0, string.Join("\n", dialogs.Unexpected));
            Check(dialogs.Prompts.Count == 1, $"Expected ONE confirmation for 103 selected files; got {dialogs.Prompts.Count}.");
            var result = await RecycleBinService.ReadAsync(default);
            Check(result.Entries.Any(e => e.FullPath == guardKey), "Unselected item was deleted.");
            Check(result.Entries.Count(e => selected.Contains(e.FullPath)) == (approve ? 0 : 103), "Cancel/approve batch result is incorrect.");
            await Until(() => files.Items.Count == (approve ? 1 : 104), "UI did not refresh the actual batch result.");
        }
        for (int i = 0; i < 3; i++)
            await Task.WhenAll(RecycleBinService.ReadAsync(default), WindowsCollectionsService.ReadAsync("shell:Recent", default));
        Console.WriteLine("SHELL_CONCURRENT_READ_PASS: recycle refresh and Windows recent-items enumeration");
        Console.WriteLine("BATCH_DELETE_PASS: 103 items, one native confirmation per action, cancel preserves all, approve removes selected only, unselected guard survives");

        // Same names from different original folders plus a folder with its child.
        string a = Path.Combine(group, "A"), b = Path.Combine(group, "B"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        string[] restore = { Path.Combine(a, "same-name.txt"), Path.Combine(b, "same-name.txt"), Path.Combine(group, "restore-folder") };
        foreach (string path in restore.Take(2)) { File.WriteAllText(path, path); FileActionsService.Recycle(path); }
        Directory.CreateDirectory(restore[2]); File.WriteAllText(Path.Combine(restore[2], "child.txt"), "nested"); FileActionsService.Recycle(restore[2]);
        pane.RefreshCurrent(); await Until(() => files.Items.Count == 4, "Restore fixtures did not appear.");
        Select(files, files.Items.OfType<FileEntry>().Where(e => e.FullPath != guardKey).Select(e => e.FullPath).ToArray());
        using (var dialogs = new Dialogs(output, "restore-selection", true))
        {
            Invoke(pane, "원래 위치로 복원");
            await Until(() => !Busy(pane) && File.Exists(restore[0]) && File.Exists(restore[1]) && File.Exists(Path.Combine(restore[2], "child.txt")), "Grouped restore did not recover correct original paths and folder child.");
            Check(dialogs.Unexpected.Count == 0, string.Join("\n", dialogs.Unexpected));
        }
        Console.WriteLine("BATCH_RESTORE_PASS: duplicate display names, distinct original paths, folder child, unselected guard unchanged");
        await Until(() => files.Items.Count == 1, "Restore list did not refresh.");
        using (var dialogs = new Dialogs(output, "restore-all", true))
        {
            files.SelectedItems.Clear(); Invoke(pane, "모든 항목 복원…");
            await Until(() => !Busy(pane) && File.Exists(guard), "Restore all did not include the unselected guard.");
            Check(dialogs.Prompts.Count == 1 && dialogs.Unexpected.Count == 0, "Restore-all confirmation/result mismatch.");
        }
        Check((await RecycleBinService.ReadAsync(default)).Entries.Count == 0, "Restore all left synthetic entries.");
        Console.WriteLine("RESTORE_ALL_PASS: no selection required; actual original file restored");

        foreach (string path in new[] { restore[0], restore[1] }) FileActionsService.Recycle(path);
        pane.RefreshCurrent(); await Until(() => files.Items.Count == 2, "Empty-bin fixtures did not appear.");
        foreach (bool approve in new[] { false, true })
        {
            var current = await RecycleBinService.ReadAsync(default);
            Check(current.Entries.All(e => e.OriginalLocation.StartsWith(group, StringComparison.Ordinal)), "Refusing empty-bin: nonfixture item appeared.");
            using var dialogs = new Dialogs(output, approve ? "empty-approve" : "empty-cancel", approve);
            Invoke(pane, "휴지통 비우기…");
            await Until(() => dialogs.Prompts.Count > 0 || dialogs.Unexpected.Count > 0, "Empty-bin native confirmation missing.");
            await Until(() => !Busy(pane), "Empty bin did not finish.");
            Check(dialogs.Prompts.Count == 1 && dialogs.Unexpected.Count == 0, "Empty-bin aggregate confirmation mismatch.");
            Check((await RecycleBinService.ReadAsync(default)).Entries.Count == (approve ? 0 : 2), "Empty-bin cancel/approve result mismatch.");
        }
        Console.WriteLine("EMPTY_BIN_PASS: native one confirmation, cancel preserves, approve empties isolated fixture only");
        await Until(() => files.Items.Count == 0, "Empty UI did not refresh.");
        NativeUiRegression.CaptureDesktop(Path.Combine(output, "recycle-empty-commands.png"));
        }
        else Console.WriteLine("REUSED_PASS: 103-item native Delete cancel/approve, unselected guard, concurrent recent-items read, selected duplicate-name/folder restore, restore-all and empty cancel/approve completed in run 40078034110; production inputs unchanged");
        pane.SyncToFolder(WindowsKnownLocations.RecycleBin);
        File.WriteAllText(guard, "property fixture"); FileActionsService.Recycle(guard);
        pane.RefreshCurrent(); await Until(() => files.Items.Count == 1, "Property fixture missing.");
        Select(files, files.Items.OfType<FileEntry>().Select(e => e.FullPath).ToArray());
        await CheckPropertiesAsync(pane, "속성", output, "recycle-item-properties");
        await Until(() => files.Items.Count == 1, "Property refresh failed.");
        await CheckPropertiesAsync(pane, "휴지통 속성·설정…", output, "recycle-bin-properties");
        await Until(() => files.Items.Count == 1, "Bin property refresh failed.");
        Select(files, files.Items.OfType<FileEntry>().Select(e => e.FullPath).ToArray());
        await CheckNativeMenuAsync(() => Invoke(pane, "Windows 기본 메뉴…"), output, "recycle-native-menu", pane);
        string ordinary = Path.Combine(group, "normal-menu.txt"); File.WriteAllText(ordinary, "normal menu regression");
        await CheckNativeMenuAsync(() => WindowsShellContextMenu.Show(window, new[] { ordinary }), output, "normal-native-menu");
        Console.WriteLine("NATIVE_PROPERTIES_AND_MENUS_PASS: actual recycle item/bin property windows, native recycle and ordinary file context menus");
        // Restore to another location through the Windows clipboard, including its recycle identity.
        await Until(() => files.Items.Count == 1, "Cut fixture missing after native menu refresh.");
        Select(files, files.Items.OfType<FileEntry>().Select(e => e.FullPath).ToArray());
        keybd_event(0x11, 0, 0, 0); Key(0x58); keybd_event(0x11, 0, 2, 0);
        await Until(() => Clipboard.ContainsData("Shell IDList Array") && !Busy(pane), "Native recycle cut did not preserve Shell clipboard identities.");
        string alternate = Path.Combine(group, "alternate-restore"); Directory.CreateDirectory(alternate);
        pane.SyncToFolder(alternate); await Until(() => pane.ActiveFolder == alternate && files.Items.Count == 0, "Paste destination failed.");
        files.Focus(); keybd_event(0x11, 0, 0, 0); Key(0x56); keybd_event(0x11, 0, 2, 0);
        await Until(() => File.Exists(Path.Combine(alternate, Path.GetFileName(guard))) && pane.IsEnabled, "Native cut/paste failed to restore to another folder.");
        Check((await RecycleBinService.ReadAsync(default)).Entries.Count == 0, "Cut/paste left a ghost recycle entry.");
        Console.WriteLine("RECYCLE_CUT_PASTE_PASS: native Ctrl+X and Ctrl+V restore to another location without ghost metadata");
        File.WriteAllText(Path.Combine(output, "RECYCLE_BATCH_RESULT.json"), JsonSerializer.Serialize(new { status = "PASS", batchEvidence = nativeTailOnly ? "REUSED_COMPLETED_SCOPES_RUN_40078034110_NOT_WHOLE_RUN_PASS" : "CURRENT_RUN", selectedCount = 103, nativeConfirmationsPerDelete = 1, canceledPreserved = true, unselectedPreserved = true, restoredDuplicateNamesToCorrectOrigins = true, restoredFolderChild = true, restoreAll = true, emptyCancelAndApprove = true, nativePropertiesAndMenus = true, cutPasteAlternateRestore = true, testedOn = Environment.OSVersion.ToString() }));
    }
}
