"""Static contracts; no documents, fixtures, filesystem user content or Windows controls opened."""
from pathlib import Path
from xml.etree import ElementTree as ET
import unittest

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / 'source' / 'Sunrise.FileManager'

class R08UiContract(unittest.TestCase):
    def test_dark_colors_respect_model_binding(self):
        model = (SRC/'Models/FileEntry.cs').read_text('utf-8')
        manager = (SRC/'Services/ThemeManager.cs').read_text('utf-8')
        main = (SRC/'MainWindow.xaml.cs').read_text('utf-8')
        for term in ['ThemeManager.IsDark', 'RefreshThemeColors', '#EDF4FF']:
            self.assertIn(term, model)
        self.assertIn('public static bool IsDark', manager)
        self.assertIn('pane?.RefreshThemeColors()', main)
        self.assertIn('ThemeButtonOnAccent', manager)
    def test_tree_each_pane(self):
        policy=(SRC/'Services/UiLayoutPolicy.cs').read_text('utf-8')
        pane=(SRC/'Controls/ExplorerPane.xaml.cs').read_text('utf-8')
        view=(SRC/'Features/Explorer/ExplorerPane.ViewAndFiltering.cs').read_text('utf-8')
        self.assertIn('bool tree = treePreference;', policy)
        self.assertIn('_treePreference = !_treePreference;',pane)
        self.assertIn('new GridLength(_compactMode ? 150 : 215)',view)
        self.assertIn('TreeVisible = _treePreference', pane)
    def test_ai_close_and_compact_title(self):
        xaml=(SRC/'MainWindow.xaml').read_text('utf-8')
        panel=(SRC/'Controls/AiCommandPanel.xaml').read_text('utf-8')
        code=(SRC/'MainWindow.xaml.cs').read_text('utf-8')
        self.assertIn('Header="설정 · 기능 ▾"',xaml)
        self.assertNotIn('HorizontalScrollBarVisibility="Auto"', xaml.split('<Border x:Name="TransferToolbar"')[0])
        self.assertIn('Click="CloseAiPanel_Click"', panel)
        self.assertIn('AiPanel.CloseRequested +=', code)
        self.assertIn('Click="CloseSetup_Click"',(SRC/'Controls/OllamaSetupDialog.xaml').read_text('utf-8'))
        self.assertIn('CloseSetup_Click',(SRC/'Controls/OllamaSetupDialog.xaml.cs').read_text('utf-8'))
    def test_grid_resize_and_contrast(self):
        view=(SRC/'Controls/ExplorerPane.xaml').read_text('utf-8')
        app=(SRC/'App.xaml').read_text('utf-8')
        self.assertIn('CanUserResizeColumns="True"',view)
        self.assertIn('ThemeColumnDivider',app)
        self.assertIn('SeparatorVisibility',app)
        self.assertIn('BorderThickness" Value="0,0,2,1"',app)
    def test_all_xaml_valid(self):
        for xaml in SRC.rglob('*.xaml'):
            with self.subTest(path=xaml.relative_to(ROOT)):
                ET.parse(xaml)
    def test_company_mode_still_skips_docs(self):
        self.assertTrue((ROOT/'COMPANY_SOURCE_ONLY.flag').exists())
        script=(ROOT/'BUILD_ON_WINDOWS_COMPANY.cmd').read_text('ascii')
        self.assertIn('--company',script)
        self.assertFalse((ROOT/'source/tests/LogicTests/Fixtures/river_report.pdf').exists())

if __name__ == '__main__': unittest.main()
