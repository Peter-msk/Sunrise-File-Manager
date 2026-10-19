import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "source" / "Sunrise.FileManager"

class OllamaAndExplorerContractTests(unittest.TestCase):
    def test_ollama_service_and_dialog_cmd_only(self):
        svc = (APP / "Services" / "AiOllamaService.cs").read_text(encoding="utf-8")
        installer = (APP / "Services" / "AiOllamaCmdInstaller.cs").read_text(encoding="utf-8")
        dlg = (APP / "Controls" / "OllamaSetupDialog.xaml.cs").read_text(encoding="utf-8")
        self.assertIn("https://github.com/ollama/ollama", svc)
        self.assertIn('FileName = "cmd.exe"', svc)
        self.assertNotIn("powershell.exe", (svc + installer).lower())
        self.assertIn("InstallAndConnectViaCmdAsync", dlg)
        self.assertNotIn("powershell.exe", dlg.lower())

    def test_explorer_core_and_undo_and_batch_rename(self):
        pane = (APP / "Controls" / "ExplorerPane.xaml.cs").read_text(encoding="utf-8")
        nav_mod = (APP / "Features" / "Explorer" / "ExplorerPane.NavigationAndWatcher.cs").read_text(encoding="utf-8")
        undo = (APP / "Services" / "ExplorerUndoService.cs").read_text(encoding="utf-8")
        batch = (APP / "Controls" / "BatchRenameDialog.xaml.cs").read_text(encoding="utf-8")
        self.assertIn("_undoService.UndoLast()", pane)
        self.assertIn("BatchRenameDialog", pane)
        self.assertIn("FileSystemWatcher", pane + nav_mod)
        self.assertIn("RecordBatchRename", undo)
        self.assertIn("ApprovedRenames", batch)

    def test_feature_grouped_modular_architecture(self):
        expected_modules = [
            APP / "Features/Explorer/ExplorerCommandRouter.cs",
            APP / "Features/Explorer/ExplorerPane.NavigationAndWatcher.cs",
            APP / "Features/Explorer/ExplorerPane.ViewAndFiltering.cs",
            APP / "Features/Explorer/ExplorerPane.FileOperations.cs",
            APP / "Features/Explorer/ExplorerPane.ClipboardAndDragDrop.cs",
            APP / "Features/Workspace/WorkspaceSplitLayoutEngine.cs",
            APP / "Features/Workspace/MainWindow.PanelCoordination.cs",
            APP / "Features/Navigation/FolderTree.QuickAccessAndDrives.cs",
            APP / "Features/Inspector/FileInspector.AttributesAndAiSummary.cs",
            APP / "Features/OnboardAi/AiCommandPanel.OllamaAssistant.cs",
            APP / "Features/FileSystem/FileActionsService.ArchiveAndShell.cs",
            APP / "Services/AiOllamaModelsAndConfig.cs",
            APP / "Services/AiOllamaCmdInstaller.cs",
            APP / "Services/AiModelIntentSafety.cs",
        ]
        for mod in expected_modules:
            self.assertTrue(mod.is_file(), f"Missing modular file: {mod}")

    def test_logic_tests_csproj_includes_all_required_services(self):
        csproj = (ROOT / "source" / "tests" / "LogicTests" / "LogicTests.csproj").read_text(encoding="utf-8")
        self.assertIn("ExplorerUndoService.cs", csproj)
        self.assertIn("FileActionsService.ArchiveAndShell.cs", csproj)
        self.assertIn("ExplorerCommandRouter.cs", csproj)
        self.assertIn("WorkspaceSplitLayoutEngine.cs", csproj)
        self.assertIn("Ai*.cs", csproj)
        self.assertIn("<RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>", csproj)

if __name__ == "__main__":
    unittest.main()
