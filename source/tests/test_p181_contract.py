"""v1.8.2 static wiring checks; real Windows compile and memo execution require .NET."""
from pathlib import Path
from xml.etree import ElementTree as ET
import unittest

ROOT = Path(__file__).resolve().parents[1] / 'Sunrise.FileManager'


class ExplorerMemoIntegrationContracts(unittest.TestCase):
    def test_all_xaml_is_well_formed(self):
        for file in ROOT.rglob('*.xaml'):
            with self.subTest(file=file.name):
                ET.parse(file)

    def test_explorer_command_groups_and_columns(self):
        code = (ROOT/'Controls/ExplorerPane.xaml').read_text()
        positions = [code.index(f'Header="{name}"') for name in ('이름','수정한 날짜','유형','크기')]
        self.assertEqual(positions, sorted(positions))
        self.assertIn('x:Name="AddressBox" Height="34"', code)
        self.assertIn('x:Name="SearchBox" Height="34"', code)
        self.assertIn('Style="{StaticResource ExplorerNavButton}"', code)

    def test_preview_is_standalone_rightmost_not_dialog(self):
        main = (ROOT/'MainWindow.xaml').read_text()
        self.assertIn('x:Name="InspectorColumn"', main)
        self.assertIn('x:Name="PreviewColumn"', main)
        self.assertIn('x:Name="PreviewColumnSplitter"', main)
        self.assertIn('x:Name="PreviewPane" Grid.Column="2"', main)
        self.assertIn('x:Name="MainPreviewButton"', main)
        inspector = (ROOT/'Controls/FileInspector.xaml').read_text()
        preview = (ROOT/'Controls/FilePreviewPanel.xaml').read_text()
        self.assertNotIn('x:Name="PreviewRegion"', inspector)
        self.assertIn('x:Name="PreviewRegion"', preview)
        self.assertIn('x:Name="MemoEditor"', inspector)
        code = (ROOT/'MainWindow.xaml.cs').read_text()
        self.assertIn('PreviewPane.SetPreviewVisible(visible)',code)
        self.assertIn('SyncDockPreview()',code)
        self.assertNotIn('new PreviewWindow',code)

    def test_organizer_has_guided_local_actions_and_approval(self):
        xaml = (ROOT/'Controls/AiCommandPanel.xaml').read_text()
        code = (ROOT/'Controls/AiCommandPanel.xaml.cs').read_text()
        for term in ('유형별 이동','확장자별 이동','날짜별 이동','연도별 이동','유형별 복사'):
            self.assertIn(f'Tag="{term}"',xaml)
        self.assertIn('Click="Plan_Click"',xaml)
        self.assertIn('Click="Execute_Click"',xaml)
        self.assertIn('IsEnabled="False"',xaml)
        self.assertIn('BeforeFileChange?.Invoke()', code)
        self.assertIn('UpdateSelectionSummary(', code)

    def test_rename_and_move_route_through_one_owner(self):
        actions = (ROOT/'Services/FileActionsService.cs').read_text()
        transfer = (ROOT/'Services/TransferService.cs').read_text()
        self.assertIn('MoveEntry(source, target)', actions)
        self.assertIn('FileMemoStore.MoveFileWithMemos(source, destination)', actions)
        self.assertIn('FileActionsService.MoveEntry(effectivePlan.Source, effectivePlan.Destination)',transfer)
        self.assertIn('TransferService.RunAsync([step.Source!.Path]', (ROOT/'Services/AiPlanService.cs').read_text())

    def test_relocation_rewrites_memo_payload_before_file_move(self):
        store=(ROOT/'Services/FileMemoStore.cs').read_text()
        section=store.split('public static void MoveFileWithMemos',1)[1].split('public static IReadOnlyList<MemoRecord> ListForFile',1)[0]
        self.assertLess(section.index('new MemoPayload(Path.GetFileName(destination)'),section.index('File.Move(source, destination);\n            movedFile = true;'))
        self.assertLess(section.index('File.Move(memo.Staged, memo.Destination)'),section.index('File.Delete(memo.Original)'))
        self.assertIn('if (unreadable > 0)',section)
        self.assertIn('File.Exists(targetMemo)',section)
        self.assertIn('File.Move(destination, source)', section)

    def test_user_draft_protected_before_mutations(self):
        main=(ROOT/'MainWindow.xaml.cs').read_text()
        pane=(ROOT/'Controls/ExplorerPane.xaml.cs').read_text()
        self.assertIn('pane.BeforeFileChange', main)
        self.assertIn('Inspector.ConfirmLeavingMemo()', main)
        self.assertIn('BeforeFileChange?.Invoke() == false', pane)


if __name__=='__main__':unittest.main()
