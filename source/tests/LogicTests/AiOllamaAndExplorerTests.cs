using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

public static class AiOllamaAndExplorerTests
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED Ollama/Explorer: " + name);
        _passed++;
        Console.WriteLine("PASS Ollama/Explorer: " + name);
    }

    public static async Task RunAsync()
    {
        Check(AiOllamaService.GitHubRepoUrl == "https://github.com/ollama/ollama", "Official GitHub URL is ollama/ollama");
        Check(AiOllamaService.GitHubLatestInstallerUrl == "https://github.com/ollama/ollama/releases/latest/download/OllamaSetup.exe", "GitHub installer URL points to OllamaSetup.exe");
        Check(AiOllamaService.IsAllowedLoopback(new Uri("http://127.0.0.1:11434")), "Loopback 127.0.0.1 allowed");
        Check(!AiOllamaService.IsAllowedLoopback(new Uri("https://example.com")), "External remote host blocked");

        var steps = AiOllamaService.BuildCmdInstallSteps("qwen2.5:1.5b");
        Check(steps.Count == 3 && steps.All(s => !s.CmdArguments.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase)),
            "CMD install steps use pure cmd.exe and curl.exe without powershell.exe");

        var parsedIntent = AiOllamaService.ParseIntentResponse(
            "{\"schema_version\":1,\"intent\":{\"action\":\"search\",\"extension\":\".pdf\",\"category\":null,\"name_contains\":\"안성천\",\"modified_within_days\":null,\"grouping\":\"\",\"content_contains\":null,\"rename_prefix\":null,\"rename_suffix\":null}}");
        AiModelIntentSafety.Validate("파일명에 '안성천' 포함된 PDF 파일 찾아줘", parsedIntent);
        Check(parsedIntent.SearchOnly && parsedIntent.Extension == ".pdf" && parsedIntent.NameContains == "안성천",
            "Ollama JSON intent parsed and safety-validated");

        // 1. Test ExplorerCommandRouter keyboard shortcuts & WPF Key.System handling
        Check(ExplorerCommandRouter.ResolvePaneShortcut(true, false, false, "Z", "None", false) == ExplorerShortcutAction.Undo,
            "Ctrl+Z routes to Undo when TextBox is not focused");
        Check(ExplorerCommandRouter.ResolvePaneShortcut(true, false, false, "Y", "None", false) == ExplorerShortcutAction.Redo,
            "Ctrl+Y routes to Redo when TextBox is not focused");
        Check(ExplorerCommandRouter.ResolvePaneShortcut(true, false, false, "Z", "None", true) == ExplorerShortcutAction.None,
            "Ctrl+Z inside TextBox does not hijack text undo");
        Check(ExplorerCommandRouter.ResolvePaneShortcut(false, true, false, "System", "Left", false) == ExplorerShortcutAction.NavigateBack,
            "Alt+Left via WPF Key.System routes to NavigateBack");
        Check(ExplorerCommandRouter.ResolvePaneShortcut(false, true, false, "System", "Right", false) == ExplorerShortcutAction.NavigateForward,
            "Alt+Right via WPF Key.System routes to NavigateForward");
        Check(ExplorerCommandRouter.ResolvePaneShortcut(false, true, false, "System", "Up", false) == ExplorerShortcutAction.NavigateUp,
            "Alt+Up via WPF Key.System routes to NavigateUp");
        Check(ExplorerCommandRouter.ResolvePaneShortcut(false, true, false, "System", "D", false) == ExplorerShortcutAction.FocusAddress,
            "Alt+D via WPF Key.System routes to FocusAddress");
        Check(ExplorerCommandRouter.ResolvePaneShortcut(false, true, false, "System", "P", false) == ExplorerShortcutAction.TogglePreview,
            "Alt+P via WPF Key.System routes to TogglePreview");
        Check(ExplorerCommandRouter.ResolvePaneShortcut(false, true, true, "System", "P", false) == ExplorerShortcutAction.ToggleDetails,
            "Alt+Shift+P via WPF Key.System routes to ToggleDetails");
        Check(ExplorerCommandRouter.ResolvePaneShortcut(true, false, false, "E", "None", false) == ExplorerShortcutAction.FocusSearch,
            "Ctrl+E routes to FocusSearch like Windows Explorer");
        Check(ExplorerCommandRouter.ResolvePaneShortcut(false, false, false, "Delete", "None", false) == ExplorerShortcutAction.RecycleDelete,
            "Pane-level Delete routes to RecycleDelete when TextBox is not focused");
        Check(ExplorerCommandRouter.ResolvePaneShortcut(false, false, false, "Delete", "None", true) == ExplorerShortcutAction.None,
            "Pane-level Delete inside TextBox does not hijack text deletion");
        Check(ExplorerCommandRouter.ResolveGridShortcut(false, false, false, "F2") == ExplorerShortcutAction.Rename,
            "F2 routes to Rename");
        Check(ExplorerCommandRouter.ResolveGridShortcut(false, false, true, "Delete") == ExplorerShortcutAction.PermanentDelete,
            "Shift+Delete routes to PermanentDelete");
        Check(ExplorerCommandRouter.ResolveGridShortcut(false, false, false, "Delete") == ExplorerShortcutAction.RecycleDelete,
            "Delete routes to RecycleDelete");
        Check(ExplorerCommandRouter.GetViewSpec("Tiles").ShowTileColumns && !ExplorerCommandRouter.GetViewSpec("Tiles").ShowAllDetailColumns,
            "Tiles view spec branches column visibility");

        // 2. Test all 14 Predefined Row/Column Split Presets & State Preservation in WorkspaceSplitLayoutEngine
        Check(WorkspaceSplitLayoutEngine.GetPresetsForCount(1).Count == 1, "1-split has 1 preset (Single)");
        Check(WorkspaceSplitLayoutEngine.GetPresetsForCount(2).Count == 2, "2-split has 2 presets (TwoColumns, TwoRows)");
        Check(WorkspaceSplitLayoutEngine.GetPresetsForCount(3).Count == 6, "3-split has 6 row/column presets");
        Check(WorkspaceSplitLayoutEngine.GetPresetsForCount(4).Count == 5, "4-split has 5 row/column presets");

        foreach (SplitLayoutPreset preset in Enum.GetValues<SplitLayoutPreset>())
        {
            var bp = WorkspaceSplitLayoutEngine.BuildBlueprint(preset);
            int expectedCount = WorkspaceSplitLayoutEngine.GetPanelCountForPreset(preset);
            Check(bp.PanelCount == expectedCount && bp.Panes.Count == expectedCount,
                $"Blueprint for {preset} places exactly {expectedCount} panes");
        }
        Check(WorkspaceSplitLayoutEngine.CycleNextPreset(2, SplitLayoutPreset.TwoColumns) == SplitLayoutPreset.TwoRows &&
              WorkspaceSplitLayoutEngine.CycleNextPreset(2, SplitLayoutPreset.TwoRows) == SplitLayoutPreset.TwoColumns,
            "2-split preset cycling alternates between TwoColumns and TwoRows");

        var shrinkSwap = WorkspaceSplitLayoutEngine.ResolveShrinkPreservation(2, 1, activePaneIndex: 1, activePaneHasUserNavigation: true);
        Check(shrinkSwap.ShouldSwap && shrinkSwap.SurvivingSlotIndex == 0,
            "Shrinking 2->1 while focused on user-navigated Pane 2 swaps active working folder into surviving Pane 1");

        var shrinkNoSwap = WorkspaceSplitLayoutEngine.ResolveShrinkPreservation(2, 1, activePaneIndex: 0, activePaneHasUserNavigation: true);
        Check(!shrinkNoSwap.ShouldSwap && shrinkNoSwap.SurvivingSlotIndex == 0,
            "Shrinking 2->1 while focused on Pane 1 keeps Pane 1 as-is without swap");

        Check(WorkspaceSplitLayoutEngine.ResolveOppositePaneIndex(2, 0, 0) == 1,
            "Opposite pane index resolves to pane 1 when pane 0 is active");
        Check(WorkspaceSplitLayoutEngine.CalculateAiDockHeight(false, 800) == 0 &&
              WorkspaceSplitLayoutEngine.CalculateAiDockHeight(true, 800) >= 235,
            "AI dock height clamps cleanly");

        // 3. Test Real Filesystem Operations, Q-Dir Features, and Undo/Redo
        string root = Path.Combine(Path.GetTempPath(), "SunriseOllamaExp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string subA = FileActionsService.CreateFolder(root, "폴더A");
            string subB = FileActionsService.CreateFolder(root, "폴더B");
            string dragSample = FileActionsService.CreateFile(subA, "드래그파일.txt", "drag content");
            Check(ExplorerCommandRouter.ResolveDragTransferKind(new[] { dragSample }, subB, ctrlPressed: false, shiftPressed: false) == TransferKind.Move,
                "Same-volume drag defaults to Move like Windows Explorer");
            Check(ExplorerCommandRouter.ResolveDragTransferKind(new[] { dragSample }, subB, ctrlPressed: true, shiftPressed: false) == TransferKind.Copy,
                "Ctrl+drag forces Copy on same volume");

            // Relative & environment variable path resolution
            Check(FileActionsService.ExpandAndResolveFolderPath("폴더A", root) == subA,
                "ExpandAndResolveFolderPath resolves relative child folder");
            Check(FileActionsService.ExpandAndResolveFolderPath("..", subA) == root,
                "ExpandAndResolveFolderPath resolves parent '..' path");

            // Auto-numbered folder and file suggestions
            Check(FileActionsService.SuggestNewFolderName(root) == "새 폴더", "Initial folder suggestion is 새 폴더");
            string nf1 = FileActionsService.CreateFolder(root, FileActionsService.SuggestNewFolderName(root));
            Check(Path.GetFileName(nf1) == "새 폴더" && FileActionsService.SuggestNewFolderName(root) == "새 폴더 (2)",
                "Second folder suggestion auto-increments to 새 폴더 (2)");
            string nf2 = FileActionsService.CreateFolder(root, FileActionsService.SuggestNewFolderName(root));
            Check(Path.GetFileName(nf2) == "새 폴더 (2)" && FileActionsService.SuggestNewFolderName(root) == "새 폴더 (3)",
                "Third folder suggestion auto-increments to 새 폴더 (3)");

            var undo = new ExplorerUndoService();
            string createdFolder = FileActionsService.CreateFolder(root, "새 작업 폴더");
            undo.RecordCreated(createdFolder, "새 폴더");
            Check(Directory.Exists(createdFolder) && undo.CanUndo, "Folder created and recorded in undo stack");

            var (undoneFolder, _) = undo.UndoLast();
            Check(undoneFolder && !Directory.Exists(createdFolder) && undo.CanRedo, "Ctrl+Z Undo removes newly created empty folder and enables Redo");
            var (redoneFolder, _) = undo.RedoLast();
            Check(redoneFolder && Directory.Exists(createdFolder), "Ctrl+Y Redo recreates undone folder");

            string f1 = FileActionsService.CreateFile(root, "alpha.txt", "Alpha body");
            string f2 = FileActionsService.CreateFile(root, "beta.txt", "Beta body");
            FileMemoStore.Save(f1, "첫 번째 파일 메모");

            var batchPairs = FileActionsService.BatchRename(new[] { (f1, "문서_001.txt"), (f2, "문서_002.txt") });
            Check(batchPairs.Count == 2 && File.Exists(batchPairs[0].NewPath) && FileMemoStore.ListForFile(batchPairs[0].NewPath).Count == 1,
                "BatchRename renames multiple files and preserves #memo sidecars");

            undo.RecordBatchRename(batchPairs);
            var (undoneBatch, _) = undo.UndoLast();
            Check(undoneBatch && File.Exists(f1) && File.Exists(f2) && FileMemoStore.ListForFile(f1).Count == 1,
                "Ctrl+Z Undo reverses BatchRename and restores #memo sidecar");
            var (redoneBatch, _) = undo.RedoLast();
            Check(redoneBatch && File.Exists(batchPairs[0].NewPath) && File.Exists(batchPairs[1].NewPath) && FileMemoStore.ListForFile(batchPairs[0].NewPath).Count == 1,
                "Ctrl+Y Redo reapplies BatchRename and preserves #memo sidecar");
            undo.UndoLast();

            // Move + Undo + Redo with #memo preservation
            var moveRes = await TransferService.RunAsync(new[] { f1 }, subB, TransferKind.Move, null, CancellationToken.None);
            string movedF1 = Path.Combine(subB, "alpha.txt");
            Check(moveRes.Completed == 1 && File.Exists(movedF1) && FileMemoStore.ListForFile(movedF1).Count == 1,
                "Same-drive drag/transfer Move relocates file and #memo sidecar");
            undo.RecordMovedItems(new[] { (f1, movedF1) });
            var (undoneMove, _) = undo.UndoLast();
            Check(undoneMove && File.Exists(f1) && !File.Exists(movedF1) && FileMemoStore.ListForFile(f1).Count == 1,
                "Ctrl+Z Undo restores moved file and #memo to original folder");
            var (redoneMove, _) = undo.RedoLast();
            Check(redoneMove && File.Exists(movedF1) && !File.Exists(f1) && FileMemoStore.ListForFile(movedF1).Count == 1,
                "Ctrl+Y Redo moves file and #memo back to target folder");
            undo.UndoLast();

            var dups = FileActionsService.DuplicateEntries(new[] { f1 });
            Check(dups.Count == 1 && File.Exists(dups[0]) && Path.GetFileName(dups[0]) == "alpha - 복사본.txt",
                "DuplicateEntries creates ' - 복사본' copy");
            var dups2 = FileActionsService.DuplicateEntries(new[] { f1 });
            Check(dups2.Count == 1 && File.Exists(dups2[0]) && Path.GetFileName(dups2[0]) == "alpha - 복사본 (2).txt",
                "Second DuplicateEntries creates ' - 복사본 (2)' copy");

            // Q-Dir Color Filter & Cut Dimming & CSV Directory Export
            var entryExe = FileEntry.From(new FileInfo(f1));
            entryExe.IsCutPending = true;
            Check(Math.Abs(entryExe.RowOpacity - 0.52) < 0.01, "IsCutPending dims row opacity to 0.52 like Windows Explorer");
            entryExe.IsCutPending = false;
            Check(Math.Abs(entryExe.RowOpacity - 1.0) < 0.01, "Clearing IsCutPending restores row opacity to 1.0");

            var listing = await DirectoryScanner.ReadAsync(root, CancellationToken.None);
            string exportedCsv = FileActionsService.ExportDirectoryListingToFile(root, listing.Entries, "목록테스트.csv");
            Check(File.Exists(exportedCsv) && File.ReadAllText(exportedCsv).Contains("alpha.txt"),
                "Q-Dir ExportDirectoryListingToFile exports CSV with file metadata");

            // Regression: CSV quote escaping must remain valid C# and RFC 4180-compatible
            // when the input contains commas or embedded double quotes.
            var specialCsvEntry = new FileEntry
            {
                Name = "보고서,\"검토\".txt",
                FullPath = @"C:\자료\보고서,""검토"".txt",
                IsDirectory = false,
                TypeName = "텍스트 문서",
                SizeBytes = 42,
                ModifiedAt = new DateTime(2026, 10, 19)
            };
            string specialCsv = FileActionsService.FormatDirectoryListingCsv(root, new[] { specialCsvEntry });
            Check(specialCsv.Contains("\"보고서,\"\"검토\"\".txt\"")
                  && specialCsv.Contains("\"C:\\자료\\보고서,\"\"검토\"\".txt\""),
                "CSV export escapes commas and embedded quotes in names and paths");

            string zipPath = Path.Combine(root, "archive.zip");
            FileActionsService.CompressToZip(new[] { f1, f2 }, zipPath);
            Check(File.Exists(zipPath), "CompressToZip creates archive");

            string extractDir = Path.Combine(root, "extracted");
            FileActionsService.ExtractZip(zipPath, extractDir);
            Check(File.Exists(Path.Combine(extractDir, "alpha.txt")), "ExtractZip extracts archive contents");

            var sizeSummary = FileActionsService.CalculateFolderSize(root, CancellationToken.None);
            Check(sizeSummary.FileCount >= 4 && sizeSummary.TotalBytes > 0, "CalculateFolderSize computes total bytes and counts");

            await Task.CompletedTask;
            Console.WriteLine($"OLLAMA & EXPLORER TESTS PASS: {_passed}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
