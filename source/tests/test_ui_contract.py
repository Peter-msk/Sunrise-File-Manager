"""Source/theme contracts. These checks do not claim native WPF visual acceptance."""
from pathlib import Path
from xml.etree import ElementTree as ET
import unittest

ROOT = Path(__file__).resolve().parents[1] / 'Sunrise.FileManager'
NS = '{http://schemas.microsoft.com/winfx/2006/xaml/presentation}'
X = '{http://schemas.microsoft.com/winfx/2006/xaml}'

class UiContractTests(unittest.TestCase):
    def setUp(self):
        self.app = ET.parse(ROOT / 'App.xaml').getroot()
        self.pane = (ROOT / 'Controls/ExplorerPane.xaml').read_text()
        self.main = (ROOT / 'MainWindow.xaml.cs').read_text()

    def test_every_derived_window_has_explicit_theme(self):
        windows = [p for p in ROOT.rglob('*.xaml') if ET.parse(p).getroot().tag == NS+'Window']
        self.assertGreaterEqual(len(windows), 5)
        for path in windows:
            with self.subTest(path=path.name): self.assertEqual(ET.parse(path).getroot().get('Style'), '{StaticResource AppWindow}')

    def test_text_contrast_on_real_theme_surfaces(self):
        colors = {n.get(X+'Key'): n.get('Color') for n in self.app.iter(NS+'SolidColorBrush')}
        def luminance(value):
            rgb = [int(value[i:i+2], 16)/255 for i in (1,3,5)]
            linear = [v/12.92 if v <= .04045 else ((v+.055)/1.055)**2.4 for v in rgb]
            return sum(v*w for v,w in zip(linear, (.2126,.7152,.0722)))
        for text in ('TextPrimary', 'TextMuted'):
            for surface in ('PanelBackground', 'AppBackground', 'Selected'):
                a,b = sorted([luminance(colors[text]), luminance(colors[surface])])
                with self.subTest(text=text, surface=surface): self.assertGreaterEqual((b+.05)/(a+.05), 4.5)

    def test_sidebar_no_white_foreground_override(self):
        self.assertNotIn('Brushes.White', (ROOT/'Controls/FolderTree.xaml.cs').read_text())
        self.assertIn('KnownFolder_Click', (ROOT/'Controls/FolderTree.xaml').read_text())

    def test_commands_overflow_instead_of_fixed_880px_toolbar(self):
        self.assertIn('<ToolBar ', self.pane)
        self.assertNotIn('MinWidth="880"', self.pane)

    def test_search_placeholder_moves_with_search_box(self):
        code = (ROOT/'Controls/ExplorerPane.xaml.cs').read_text()
        self.assertIn('x:Name="SearchHost"', self.pane)
        self.assertIn('Grid.SetRow(SearchHost', code)
        self.assertIn('ElementName=SearchBox', self.pane)

    def test_clipboard_uses_shared_transfer_approval(self):
        self.assertIn('pane.PasteRequested += Pane_PasteRequested', self.main)
        paste = self.main.split('private async void Pane_PasteRequested', 1)[1].split('private void RefreshAllPanes', 1)[0]
        self.assertIn('RunTransferAsync(request.Paths', paste)
        transfer = self.main.split('private async Task RunTransferAsync', 1)[1]
        self.assertLess(transfer.index('MessageBox.Show'), transfer.index('TransferService.RunAsync'))
        self.assertNotIn('sourceIndex == targetIndex', transfer)

    def test_open_and_preview_are_distinct_commands(self):
        self.assertIn('연결 앱에서 원본 열기', self.pane)
        self.assertIn('Click="PreviewToggle_Click"', self.pane)
        self.assertIn('OpenOriginal_Click', (ROOT/'Controls/PreviewWindow.xaml').read_text())

    def test_hangul_preview_is_bounded_and_read_only(self):
        code = (ROOT/'Services/HangulPreviewService.cs').read_text()
        self.assertIn('MaxArchiveBytes', code); self.assertIn('MaxPreviewImageBytes', code)
        self.assertIn('width * height <= 64_000_000', code)
        self.assertNotIn('ExtractTo', code); self.assertNotIn('File.Write', code)
        self.assertNotIn('Process.Start', code)

    def test_large_document_limit_disclosure_is_present(self):
        code = (ROOT/'Services/FilePreviewService.cs').read_text()
        self.assertLess(code.index('HangulPreviewService.ReadStoredPreview'), code.index('if (extension is ".docx" or ".hwpx")'))
        self.assertIn('저장된 미리보기가 없습니다', code)

    def test_preview_zoom_changes_scroll_extent(self):
        self.assertIn('PageSurface.Width = 700 * factor + 68', (ROOT/'Controls/PreviewWindow.xaml.cs').read_text())

    def test_native_ui_test_isolated_from_user_session(self):
        self.assertIn('_persistSession = initialSession is null', self.main)
        self.assertIn('if (_persistSession) TrySaveSession(showError: true)', self.main)
        self.assertIn('SessionStore.Save(new AppSession', self.main)
        self.assertIn('if (_persistSession) Loaded +=', self.main)

    def test_both_image_consumers_share_two_dimensional_decode(self):
        image = (ROOT/'Services/ImageThumbnailService.cs').read_text()
        self.assertIn('bitmap.DecodePixelHeight', image)
        self.assertIn('return DecodeBounded(file, 100, 100, token)', image)
        self.assertIn('ImageThumbnailService.DecodeBounded(stream, 1600, 2100', (ROOT/'Controls/PreviewWindow.xaml.cs').read_text())

if __name__ == '__main__': unittest.main()

# The following tests exercise wiring/structure, not a Windows graphical session.
class InspectorContracts(unittest.TestCase):
    def test_new_dock_is_inside_main_window(self):
        xaml = (ROOT/'MainWindow.xaml').read_text()
        self.assertIn('x:Name="InspectorColumn"', xaml)
        self.assertIn('x:Name="Inspector"', xaml)
        self.assertIn('Click="MainPreview_Click"', xaml)
        self.assertIn('Click="InspectorToggle_Click"', xaml)

    def test_preview_does_not_open_an_external_window(self):
        pane = (ROOT/'Controls/ExplorerPane.xaml.cs').read_text()
        method = pane.split('private void PreviewToggle_Click', 1)[1].split('private void FilesGrid_SelectionChanged', 1)[0]
        self.assertIn('PreviewRequested?.Invoke(this)', method)
        self.assertNotIn('new PreviewWindow', method)

    def test_memo_panel_is_separate_from_full_height_preview(self):
        inspector = (ROOT/'Controls/FileInspector.xaml').read_text()
        preview = (ROOT/'Controls/FilePreviewPanel.xaml').read_text()
        main = ET.parse(ROOT/'MainWindow.xaml').getroot()
        find = lambda name: next(el for el in main.iter() if el.get(X+'Name') == name)
        self.assertNotIn('x:Name="PreviewRegion"', inspector)
        self.assertIn('x:Name="PreviewRegion"', preview)
        self.assertIn('x:Name="DetailName"', inspector)
        self.assertIn('x:Name="MemoEditor"', inspector)
        self.assertIn('x:Name="MemoHistory"', inspector)
        self.assertIn('Click="SaveButton_Click"', inspector)
        self.assertIs(find('PreviewPane'), next(e for e in find('WorkspaceShell') if e.get(X+'Name') == 'PreviewPane'))
        self.assertIs(find('Inspector'), next(e for e in find('FileAreaShell') if e.get(X+'Name') == 'Inspector'))

    def test_preview_and_info_independent_toggles(self):
        main = (ROOT/'MainWindow.xaml.cs').read_text()
        self.assertIn('PreviewPane.SetPreviewVisible(visible)', main)
        self.assertIn('Inspector.Visibility = InspectorSplitter.Visibility = visible', main)
        self.assertIn('PreviewPane.Visibility = PreviewColumnSplitter.Visibility = visible', main)
        self.assertNotIn('if (visible && !_inspectorOpen) SetInspectorVisible(true)', main)
        self.assertIn('PreviewPane.SelectFile(_panes[_activePane].SelectedFile)', main)
        self.assertIn('PreviewPane.CloseRequested += () => SetDockPreview(false)', main)

    def test_memo_is_bounded_atomic_and_file_local(self):
        code = (ROOT/'Services/FileMemoStore.cs').read_text()
        self.assertIn('"#memo"', code)
        self.assertIn('".sunmemo"', code)
        self.assertIn('_memo_', code)
        self.assertIn('Environment.UserName', code)
        self.assertIn('File.Move(temporary, path, overwrite: true)', code)
        self.assertIn('MaxTextLength', code)
        self.assertIn('File.Exists(filePath)', code)
        self.assertIn('FileName, string UserName', code)

    def test_unsaved_memos_are_not_silently_dropped(self):
        code = (ROOT/'Controls/FileInspector.xaml.cs').read_text()
        self.assertIn('MessageBoxButton.YesNoCancel', code)
        self.assertIn('if (choice == MessageBoxResult.Cancel) return false;', code)
        self.assertIn('if (choice == MessageBoxResult.Yes) return SaveMemo();', code)
        self.assertIn('LoadMemoHistory(); // Discard', code)

    def test_session_saves_global_inspector_flags(self):
        code = (ROOT/'Services/SessionStore.cs').read_text()
        self.assertIn('public bool InspectorVisible', code)
        self.assertIn('public bool DockPreviewVisible', code)
        main = (ROOT/'MainWindow.xaml.cs').read_text()
        self.assertIn('LayoutVersion = 20', main)
        self.assertIn('InspectorVisible = _inspectorOpen', main)
        self.assertIn('DockPreviewVisible = _dockPreviewOpen', main)
