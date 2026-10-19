"""Source-level regression contract for the exact CS0165/CS8602 repairs.

Does not replace a Windows WPF compilation.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]

class WpfHotfixIntegrationTests(unittest.TestCase):
    def test_file_inspector_entry_is_assigned_before_edit_guard(self):
        code = (ROOT / 'source/Sunrise.FileManager/Controls/FileInspector.xaml.cs').read_text(encoding='utf-8-sig')
        self.assertIn('FileEntry? entry = _selected;', code)
        self.assertIn('bool canEdit = entry is { IsDirectory: false } && File.Exists(entry.FullPath)', code)
        self.assertNotIn('bool canEdit = _selected is { IsDirectory: false } entry &&', code)

    def test_panel_button_state_is_updated_only_once(self):
        code = (ROOT / 'source/Sunrise.FileManager/MainWindow.xaml.cs').read_text(encoding='utf-8-sig')
        start = code.index('private void ArrangePanels(int count)')
        end = code.index('private void AddPane(', start)
        body = code[start:end]
        self.assertIn('var choices = new[] { OneButton, TwoButton, ThreeButton, FourButton };', body)
        self.assertIn('choices[i].Background', body)
        for name in ('OneButton','TwoButton','ThreeButton','FourButton'):
            self.assertNotIn(name + '.Background =', body)
        self.assertIn('UpdateWorkspaceLayout();', body)
        self.assertIn('SyncInspector();', body)
        self.assertIn('SyncDockPreview();', body)

    def test_cmd_build_and_ollama_are_preserved(self):
        build = (ROOT / 'BUILD_DEV_FULL_SOURCE.cmd').read_text(encoding='utf-8-sig')
        self.assertIn('Running Logic', build)
        self.assertIn('Building Sunrise.FileManager WPF Application', build)
        self.assertIn('Publishing Windows x64 Executable', build)
        self.assertTrue((ROOT / 'source/Sunrise.FileManager/Services/AiOllamaService.cs').is_file())
        self.assertTrue((ROOT / 'INSTALL_OLLAMA.cmd').is_file())

if __name__ == '__main__':
    unittest.main()
