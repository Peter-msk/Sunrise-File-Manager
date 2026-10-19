"""R10 static safety contracts; native WPF rendering requires Windows acceptance."""
import pathlib, unittest
from xml.etree import ElementTree as ET
ROOT=pathlib.Path(__file__).resolve().parents[1]/'Sunrise.FileManager'
X='{http://schemas.microsoft.com/winfx/2006/xaml}'
NS='{http://schemas.microsoft.com/winfx/2006/xaml/presentation}'
class R10Contracts(unittest.TestCase):
    def test_navigation_is_single_scroll_surface(self):
        doc=ET.parse(ROOT/'Controls/FolderTree.xaml').getroot()
        outer=[e for e in doc.iter(NS+'ScrollViewer') if e.get('VerticalScrollBarVisibility')=='Auto']
        self.assertEqual(1,len(outer))
        self.assertEqual(1,len(list(doc.iter(NS+'TreeView'))))
        self.assertNotIn('MaxHeight="290"',(ROOT/'Controls/FolderTree.xaml').read_text())
    def test_menu_template_uses_theme_resources(self):
        code=(ROOT/'App.xaml').read_text()
        self.assertIn('x:Name="PART_Popup"',code)
        self.assertIn('ThemeMenuPopup',code)
        self.assertIn('ThemeMenuHover',code)
        self.assertIn('ThemeMenuSeparator',code)
        self.assertIn('IsSubmenuOpen',code)
        self.assertIn('ThemeMenuText',code)
    def test_session_restores_last_folder_and_window(self):
        sess=(ROOT/'Services/SessionStore.cs').read_text()
        main=(ROOT/'MainWindow.xaml.cs').read_text()
        pane=(ROOT/'Controls/ExplorerPane.xaml.cs').read_text()
        for key in ['ActivePane','WindowWidth','WindowHeight','WindowLeft','WindowTop','IsMaximized','InspectorWidth','PreviewWidth','ColumnWidths','SearchText','SortField']:
            self.assertIn(key,sess)
        self.assertIn('TabPaths = _tabs.Select(tab => tab.CurrentPath)',pane)
        self.assertIn('if (Directory.Exists(path)) _tabs.Add(new ExplorerTab(path))',pane)
        self.assertIn('TrySaveSession(showError: true)',main)
        self.assertIn('TrySaveSession(showError: false)',main)
        self.assertIn('LayoutVersion = 20',main)
    def test_company_and_onefile_unchanged(self):
        assert 'COMPANY_SOURCE_ONLY.flag' in [p.name for p in ROOT.parents[1].iterdir()]
        assert (ROOT.parents[1]/'BUILD_RELEASE_ONEFILE.cmd').exists()
if __name__=='__main__': unittest.main()
