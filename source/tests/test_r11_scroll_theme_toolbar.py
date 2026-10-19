"""R11 structural regression checks. These are not a Windows WPF runtime test."""
from pathlib import Path
import unittest
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1] / 'Sunrise.FileManager'
NS = '{http://schemas.microsoft.com/winfx/2006/xaml/presentation}'
X = '{http://schemas.microsoft.com/winfx/2006/xaml}'

class R11Contracts(unittest.TestCase):
    def test_single_sidebar_wheel_dispatch(self):
        markup = (ROOT/'Controls/FolderTree.xaml').read_text(encoding='utf-8')
        impl = (ROOT/'Controls/FolderTree.xaml.cs').read_text(encoding='utf-8')
        doc = ET.fromstring(markup)
        self.assertEqual(1, len([n for n in doc.iter(NS+'ScrollViewer') if n.get('VerticalScrollBarVisibility') == 'Auto']))
        self.assertIn('x:Name="NavigationScroll"', markup)
        self.assertIn('PreviewMouseWheel="NavigationScroll_PreviewMouseWheel"', markup)
        self.assertIn('private void NavigationScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)',impl)
        self.assertIn('NavigationScroll.ScrollToVerticalOffset', impl)
        self.assertIn('e.Handled = true;', impl)

    def test_command_toolbar_theme_contrast_and_compact(self):
        doc = ET.parse(ROOT/'Controls/ExplorerPane.xaml').getroot()
        toolbars = [n for n in doc.iter(NS+'ToolBar')]
        self.assertEqual(1, len(toolbars))
        toolbar = toolbars[0]
        self.assertEqual('{DynamicResource TextPrimary}', toolbar.get('Foreground'))
        for button in toolbar.iter(NS+'Button'):
            self.assertEqual('{DynamicResource TextPrimary}', button.get('Foreground'),button.get(X+'Name'))
        buttons={n.get(X+'Name'):n for n in toolbar.iter(NS+'Button') if n.get(X+'Name')}
        for key in ('NewFolderButton','NewFileButton','CutSelectionButton','CopySelectionButton',
                    'PasteSelectionButton','RenameButton','RecycleButton'):
            self.assertNotEqual('Collapsed', buttons[key].get('Visibility'))
        for key in ('DuplicateButton','UndoButton','RedoButton','ZipCompressButton',
                    'ZipExtractButton','ExportListButton','OpenCmdButton','TreeToggle',
                    'DetailsToggle','PreviewToggle'):
            self.assertEqual('Collapsed',buttons[key].get('Visibility'))
        menus = [n for n in doc.iter(NS+'Menu')]
        self.assertGreaterEqual(len(menus),1)
        actions = [n.get('Click') for m in menus for n in m.iter(NS+'MenuItem')]
        for action in ('Undo_Click','Redo_Click','Duplicate_Click','ZipCompress_Click',
                       'ZipExtract_Click','ExportList_Click','OpenCmd_Click',
                       'TreeToggle_Click','PreviewToggle_Click'):
            self.assertIn(action,actions)

    def test_menu_dimensions_and_resources(self):
        app=(ROOT/'App.xaml').read_text(encoding='utf-8')
        pane=(ROOT/'Controls/ExplorerPane.xaml').read_text(encoding='utf-8')
        self.assertIn('x:Key="SunrisePaneMenuItem"',app)
        self.assertIn('ThemeMenuText',app)
        self.assertIn('ThemeMenuPopup',app)
        self.assertIn('ThemeMenuHover',app)
        self.assertIn('<RowDefinition Height="36"/><RowDefinition Height="36"/>',pane)

    def test_no_file_engine_or_company_policy_changed(self):
        base=Path(__file__).resolve().parents[2]
        self.assertTrue((base/'COMPANY_SOURCE_ONLY.flag').exists())
        self.assertIn('--company', (base/'BUILD_ON_WINDOWS_COMPANY.cmd').read_text())
        self.assertTrue((base/'BUILD_RELEASE_ONEFILE.cmd').exists())

if __name__ == '__main__': unittest.main()
