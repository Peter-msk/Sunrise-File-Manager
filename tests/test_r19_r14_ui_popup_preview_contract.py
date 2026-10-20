"""Focused R14 UI source contracts; static only, no company document or user data access."""
import re
import unittest
from pathlib import Path
from xml.etree import ElementTree as ET

SRC = Path(__file__).resolve().parents[1] / 'source' / 'Sunrise.FileManager'
NS = '{http://schemas.microsoft.com/winfx/2006/xaml/presentation}'

class R14PopupPreviewUiContract(unittest.TestCase):
    def read(self, rel):
        return (SRC / rel).read_text(encoding='utf8')

    def test_no_visual_quick_command_row_and_commands_preserved(self):
        xml = ET.parse(SRC / 'Controls/ExplorerPane.xaml').getroot()
        grid = xml.find(f'{NS}Border/{NS}Grid')
        rows = grid.find(f'{NS}Grid.RowDefinitions')
        self.assertEqual(len(rows), 6)  # formerly 7: no file command row
        toolbars = list(grid.findall(f'{NS}ToolBar'))
        self.assertEqual(len(toolbars), 1)
        self.assertEqual(toolbars[0].attrib['Visibility'], 'Collapsed')
        self.assertIn('Ctrl+Shift+N', self.read('Controls/ExplorerPane.xaml'))
        self.assertIn('Content="붙여넣기"', self.read('Controls/ExplorerPane.xaml'))
        self.assertIn('Grid.Row="4"', self.read('Controls/ExplorerPane.xaml'))

    def test_ai_is_independent_popup_and_context_safety_retained(self):
        main = self.read('MainWindow.xaml.cs')
        layout = self.read('MainWindow.xaml')
        self.assertNotIn('AiViewport', layout)
        self.assertNotIn('AiRow', layout)
        self.assertIn('Content = AiPanel', main)
        self.assertIn('private void OpenAiWindow()', main)
        self.assertIn('private void HideAiWindow()', main)
        self.assertIn('if (_aiBusy) return;', main)
        self.assertIn('AiPanel.ContextStillMatches', main)
        self.assertIn('AiPanel.BeforeFileChange', main)
        self.assertIn('AiPanel.FileWorkCompleted', main)
        self.assertIn('AiPanel.CloseRequested += HideAiWindow', main)

    def test_inspector_labels_have_their_own_rows(self):
        x = self.read('Controls/FileInspector.xaml')
        self.assertIn('Grid.ColumnSpan="3" Text="상세정보 · 스마트 파일 메모"', x)
        self.assertIn('Grid.ColumnSpan="3" Text="스마트 파일 메모 (#memo)"', x)
        self.assertIn('Grid.Row="1" Grid.Column="1" x:Name="AiMemoButton"', x)
        self.assertIn('Grid.Row="1" Grid.Column="2" x:Name="SaveButton"', x)

    def test_preview_controls_and_keyboard_wheel(self):
        for control in ('PreviewWindow', 'FilePreviewPanel'):
            x = self.read(f'Controls/{control}.xaml')
            cs = self.read(f'Controls/{control}.xaml.cs')
            for name in ['ZoomIn_Click', 'ZoomOut_Click', 'FitWidth_Click']:
                self.assertIn(f'Click="{name}"', x)
                self.assertRegex(cs, rf'void {name}\(')
            self.assertIn('PreviewMouseWheel=', x)
            self.assertIn('ModifierKeys.Control', cs)
            self.assertIn('Key.PageDown', cs)
            self.assertIn('Key.PageUp', cs)
            self.assertIn('Source.Width', cs)
        self.assertIn('FitPage_Click', self.read('Controls/PreviewWindow.xaml'))
        self.assertIn('OpenLargePreview_Click', self.read('Controls/FilePreviewPanel.xaml'))

    def test_action_plan_columns_elastic_and_text_accessible(self):
        x = self.read('Controls/AiCommandPanel.xaml')
        self.assertIn('Header="변경 전" Binding="{Binding Before}" Width="2*"', x)
        self.assertIn('Header="변경 후" Binding="{Binding After}" Width="2*"', x)
        self.assertIn('TextTrimming', x)
        self.assertIn('ToolTip', x)

    def test_all_xaml_event_handlers_have_source_methods(self):
        for file in SRC.rglob('*.xaml'):
            root = ET.parse(file).getroot()
            cls = root.attrib.get('{http://schemas.microsoft.com/winfx/2006/xaml}Class', '').split('.')[-1]
            if not cls: continue
            sources = '\n'.join(x.read_text('utf8') for x in SRC.rglob('*.cs') if cls in x.name or (cls=='MainWindow' and x.name.startswith('MainWindow')))
            # Explicitly check only routed event handler attributes modified by R14.
            for elem in root.iter():
                for attr in ('Click', 'PreviewMouseWheel', 'SizeChanged', 'PreviewKeyDown'):
                    if attr in elem.attrib:
                        func = elem.attrib[attr]
                        self.assertRegex(sources, rf'\b{re.escape(func)}\s*\(', f'{file.name}: {func} missing')

if __name__ == '__main__':
    unittest.main()
