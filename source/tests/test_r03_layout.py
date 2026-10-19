import pathlib
import unittest
import xml.etree.ElementTree as ET

SRC=pathlib.Path(__file__).resolve().parents[1]/'Sunrise.FileManager'
NS='{http://schemas.microsoft.com/winfx/2006/xaml/presentation}'
X='{http://schemas.microsoft.com/winfx/2006/xaml}'

class RightColumnLayoutR03(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.doc=ET.parse(SRC/'MainWindow.xaml').getroot()
        cls.nodes={n.attrib[X+'Name']: n for n in cls.doc.iter() if X+'Name' in n.attrib}
        cls.code=(SRC/'MainWindow.xaml.cs').read_text(encoding='utf-8')
        cls.ui=(pathlib.Path(__file__).parent/'UiSmokeTests/Program.cs').read_text(encoding='utf-8')

    def test_preview_is_last_sibling_of_filearea(self):
        shell=self.nodes['WorkspaceShell']
        named=[n.attrib.get(X+'Name') for n in shell if X+'Name' in n.attrib]
        self.assertEqual(['FileAreaShell','PreviewColumnSplitter','PreviewPane'],named)
        self.assertEqual('2', self.nodes['PreviewPane'].attrib['Grid.Column'])
        self.assertNotIn('AiColumn',self.nodes)

    def test_ai_only_shrinks_explorer(self):
        filearea=self.nodes['FileAreaShell']
        self.assertIn(self.nodes['AiViewport'],list(filearea))
        self.assertEqual('1',self.nodes['AiViewport'].attrib['Grid.Row'])
        self.assertEqual('0',self.nodes['AiViewport'].attrib['Grid.Column'])
        self.assertEqual('2',self.nodes['Inspector'].attrib['Grid.RowSpan'])
        self.assertEqual('2',self.nodes['InspectorSplitter'].attrib['Grid.RowSpan'])
        self.assertNotIn('Grid.SetColumn(AiViewport', self.code)
        self.assertNotIn('Grid.SetColumnSpan(AiViewport', self.code)

    def test_calculations_are_measured_not_gridlength_ratios(self):
        self.assertIn('WorkspaceViewport.ActualWidth',self.code)
        self.assertIn('WorkspaceViewport.ActualHeight',self.code)
        self.assertIn('InspectorColumn.ActualWidth',self.code)
        self.assertIn('PreviewColumn.ActualWidth',self.code)
        self.assertNotIn('InspectorColumn.Width.Value',self.code)
        self.assertNotIn('PreviewColumn.Width.Value',self.code)

    def test_windows_real_render_checks_added(self):
        for x in ['preview-info-memo-f8.png','preview-min-window-width.png',
                  'F8 must dock under Explorer','Right columns must remain usable']:
            self.assertIn(x,self.ui)

if __name__=='__main__':unittest.main()
