"""Phase 1/2 source wiring and permission-boundary checks.
These tests do not compile/execute C# and are not Windows GUI evidence.
"""
from pathlib import Path
from xml.etree import ElementTree as ET
import unittest

ROOT = Path(__file__).resolve().parents[1]
P = ROOT / 'Sunrise.FileManager'

class AiPhase12Contracts(unittest.TestCase):
    def test_xaml_contains_explicit_scope_and_separate_confirmation(self):
        root = ET.parse(P / 'Controls/AiCommandPanel.xaml').getroot()
        x = '{http://schemas.microsoft.com/winfx/2006/xaml}'
        names = {el.get(x+'Name') for el in root.iter()}
        self.assertTrue({'ScopeMode', 'CommandBox', 'PlanButton', 'ExecuteButton', 'PlanGrid'} <= names)
        text = (P/'Controls/AiCommandPanel.xaml').read_text()
        self.assertIn('MouseDoubleClick="PlanGrid_MouseDoubleClick"', text)
        self.assertIn('자연어 명령', text)

    def test_natural_agent_read_only_before_approval(self):
        interpreter = (P/'Services/AiNaturalLanguageService.cs').read_text()
        orchestrator = (P/'Services/AiAgentOrchestrator.cs').read_text()
        panel = (P/'Controls/AiCommandPanel.xaml.cs').read_text()
        self.assertIn('AiPlanService.Validate(scope, plan, collision, token)', orchestrator)
        self.assertNotIn('ExecuteAsync(', orchestrator)
        self.assertNotIn('Process.Start(', interpreter + orchestrator)
        self.assertNotIn('File.Delete(', interpreter + orchestrator)
        self.assertNotIn('File.Move(', interpreter + orchestrator)
        self.assertIn('AiAgentOrchestrator.Prepare(', panel)
        self.assertIn('AiPlanService.ExecuteAsync(plan, true', panel)
        self.assertIn('MessageBox.Show(Window.GetWindow(this)', panel)
        self.assertIn('sourceContext.Value.Paths', panel)

    def test_search_is_bounded_and_protects_memo(self):
        search = (P/'Services/AiNaturalLanguageService.cs').read_text()
        scope = (P/'Services/AiPlanService.cs').read_text()
        for token in ('5000', '1000', 'depth == 12', '#memo', '.sunmemo', 'ReparsePoint'):
            self.assertIn(token, search)
        self.assertIn('allowDescendantEntries = false', scope)
        self.assertIn('full.EndsWith(".sunmemo"', scope)

    def test_file_owner_and_existing_panels_preserved(self):
        main = (P/'MainWindow.xaml.cs').read_text()
        pane = (P/'Controls/ExplorerPane.xaml.cs').read_text()
        transfer = (P/'Services/TransferService.cs').read_text()
        owner = (P/'Services/FileActionsService.cs').read_text()
        self.assertIn('RevealResultFile', main)
        self.assertIn('RevealFileAsync(', pane)
        self.assertIn('FileActionsService.MoveEntry(effectivePlan.Source, effectivePlan.Destination)', transfer)
        self.assertIn('FileMemoStore.MoveFileWithMemos(source, destination)', owner)
        self.assertIn('SyncDockPreview()', main)
        self.assertIn('SyncInspector()', main)

if __name__ == '__main__':
    unittest.main()
