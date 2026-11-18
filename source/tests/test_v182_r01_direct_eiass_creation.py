"""Targeted source contracts for v1.8.2 R01 simplified EIASS UI.

Does NOT assert Windows native WPF/NTFS behaviour: it needs Windows execution.
"""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1] / 'Sunrise.FileManager'
XAML = (ROOT / 'Controls/EiaWorkspacePlanningWindow.xaml').read_text(encoding='utf-8')
CODE = (ROOT / 'Controls/EiaWorkspacePlanningWindow.xaml.cs').read_text(encoding='utf-8')
DIRECT = (ROOT / 'Controls/EiaWorkspacePlanningWindow.DirectCreation.cs').read_text(encoding='utf-8')
ALL_CODE = CODE + '\n' + DIRECT
WRITER = (ROOT / 'Features/EiaWorkspace/EiaWorkspaceProductionWriter.cs').read_text(encoding='utf-8')
CREATION_ENGINE = (ROOT / 'Features/EiaWorkspace/EiaWorkspaceCreationEngine.cs').read_text(encoding='utf-8')
NS = {'w': 'http://schemas.microsoft.com/winfx/2006/xaml/presentation',
      'x': 'http://schemas.microsoft.com/winfx/2006/xaml'}
TREE = ET.fromstring(XAML)


def find_named(name):
    nodes = [node for node in TREE.iter()
             if node.attrib.get('{%s}Name' % NS['x']) == name]
    assert len(nodes) == 1, (name, len(nodes))
    return nodes[0]


def test_only_one_visible_creation_button():
    node = find_named('CreateFoldersButton')
    assert node.attrib['Click'] == 'CreateFoldersDirect_Click'
    assert node.attrib['Content'] == '누락 폴더 생성'
    assert node.attrib.get('Visibility') != 'Collapsed'
    assert 'EiaWorkspaceExecutionReviewWindow(' not in ALL_CODE
    assert 'ExecutionReadiness_Click' not in CODE
    assert not any(x.attrib.get('Click') == 'ExecutionReadiness_Click' for x in TREE.iter())


def test_plan_input_area_compact_and_aligned():
    assert 'Width="1120" Height="780"' in XAML
    assert 'MaxHeight="335"' in XAML
    assert 'ColumnDefinition Width="105"' in XAML
    title = find_named('ProjectTitleInput')
    contract = find_named('ContractDescriptionInput')
    assert title.attrib.get('Grid.Column') == '1'
    assert contract.attrib.get('Grid.Column') == '1'
    assert '운영 쓰기 잠금' not in XAML
    assert '자동 생성 비활성' not in XAML
    assert '생성 기능 없음' not in CODE


def test_visible_destination_flow_without_mandatory_inspection():
    assert find_named('DestinationPathInput').attrib.get('Grid.Column') == '1'
    assert find_named('InspectDestinationButton').attrib.get('Visibility') == 'Collapsed'
    assert find_named('CancelDestinationButton').attrib.get('Visibility') == 'Collapsed'
    assert find_named('CancelFolderCreationButton').attrib['Click'] == 'CancelFolderCreationDirect_Click'
    assert find_named('OpenWorkspaceFolderButton').attrib['Click'] == 'OpenWorkspaceFolderDirect_Click'
    assert find_named('FolderCreationProgress').attrib.get('Visibility') == 'Collapsed'


def test_new_ui_handlers_are_resolved_and_xaml_valid():
    for node in TREE.iter():
        if 'Click' not in node.attrib:
            continue
        handler = node.attrib['Click']
        assert re.search(r'\b' + re.escape(handler) + r'\s*\(', ALL_CODE), handler


def test_automatic_path_diagnosis_and_rechecks_before_write():
    i = DIRECT.index('private async void CreateFoldersDirect_Click')
    section = DIRECT[i: DIRECT.index('private void CancelFolderCreationDirect_Click', i)]
    markers = [
        'RequireFreshVisiblePlan()',
        'EiaWorkspaceDestinationInspector.Diagnose(',
        'scan.Blocked != 0',
        'EiaWorkspaceDestinationInspector.Inspect(',
        'RequireConsistentObservation(',
        'PrepareReadOnlyReview(',
        'EiaWorkspaceProjectReviewPlanner.AcknowledgeReadOnly(',
        'EiaWorkspaceExecutionPreflight.RecheckReadOnly(',
        'EiaWorkspaceExecutionService.PrepareReadOnlyManifest(',
        'MessageBoxButton.YesNo',
        'EiaWorkspaceProductionWriter.ExecuteReviewed(',
    ]
    positions = [section.index(m) for m in markers]
    assert positions == sorted(positions)
    assert 'Directory.CreateDirectory(' not in section
    assert 'File.Delete(' not in section
    assert 'File.Move(' not in section


def test_exact_project_and_workspace_are_bound_for_final_consent():
    assert '"CREATE " + review.WorkspaceFolderName' in DIRECT
    assert 'signature != CurrentInputSignature()' in DIRECT
    assert 'basePath, DestinationPathInput.Text.Trim()' in DIRECT
    assert 'StringComparison.Ordinal' in DIRECT
    assert 'review.SourceLabel != "기본안"' in WRITER
    assert 'EiaWorkspaceCreationEngine.ProductionWritesEnabled' not in DIRECT


def test_progress_cancel_closing_and_no_stale_output():
    for marker in (
        'RunExclusiveDestinationScanAsync(',
        'FolderCreationProgress.IsIndeterminate = true;',
        'CancelFolderCreationButton.IsEnabled = true;',
        '_folderCreationCancellation.Cancel();',
        'protected override void OnClosing(CancelEventArgs e)',
        'if (_folderCreationCancellation is not null)',
        'e.Cancel = true;',
        'result.Completed',
        'result.Failed',
        'result.NotAttempted',
        'ClearDestinationDiagnostics("생성 차단',
    ):
        assert marker in ALL_CODE, marker


def test_source_readonly_engine_keeps_its_safety_boundary():
    assert 'ProductionWritesEnabled = false' in CREATION_ENGINE
    assert '#if EIA_WORKSPACE_SANDBOX_TEST' in CREATION_ENGINE
    assert 'EiaWorkspaceNativeDirectoryWriter.OpenPinnedBase(' in WRITER
    assert 'CreateOrVerify(' in WRITER
    assert 'Directory.CreateDirectory(' not in WRITER
    assert 'File.Delete(' not in WRITER


def test_existing_local_volume_only_in_native_writer():
    native = (ROOT / 'Features/EiaWorkspace/EiaWorkspaceNativeDirectoryWriter.cs').read_text()
    for marker in ('GetDriveTypeW(', 'GetVolumeInformationW(', 'NTFS', 'ReFS',
                   'OpenPinnedBase(', 'FileCreate', 'RequirePinnedPath('):
        assert marker in native
    assert 'Directory.CreateDirectory(' not in native


def test_only_result_folder_opened_after_path_check():
    assert 'CanOpenVerifiedWorkspace(' in DIRECT
    assert 'EiaWorkspaceCreationEngine.VerifyExistingChain(path)' in DIRECT
    assert 'start.ArgumentList.Add(path);' in DIRECT
    assert 'Process.Start(start);' in DIRECT


def test_browse_locked_while_disk_write_in_progress():
    assert 'x:Name="ChooseDestinationButton"' in XAML
    assert 'ChooseDestinationButton.IsEnabled = false;' in DIRECT
    assert 'ChooseDestinationButton.IsEnabled = true;' in DIRECT
    assert 'if (_folderCreationCancellation is not null) return;' in CODE


def test_stale_result_open_action_is_invalidated_on_edits():
    section = CODE[CODE.index('private void InvalidateDestinationInspection('):]
    assert '_createdWorkspacePath = null;' in section
    assert 'OpenWorkspaceFolderButton.IsEnabled = false;' in section
    assert 'DestinationReviewTabs.SelectedIndex = 0;' in CODE


def test_four_column_results_and_original_absolute_path_tooltip():
    grid = find_named('DestinationDiagnosticGrid')
    assert grid.attrib.get('CanUserResizeColumns') == 'False'
    assert grid.attrib.get('CanUserReorderColumns') == 'False'
    ns = NS['w']
    columns = [node for node in grid.iter() if node.tag == '{'+ns+'}DataGridTextColumn']
    assert len(columns) == 4
    assert any('Binding RelativePath' in node.attrib.get('Binding','') for node in columns)
    assert all('Binding AbsolutePath' not in node.attrib.get('Binding','') for node in columns)
    assert 'ToolTip" Value="{Binding AbsolutePath}"' in XAML

def test_old_wizard_window_is_not_reachable_from_primary_screen():
    assert 'EiaWorkspaceExecutionReviewWindow(' not in ALL_CODE
    assert 'ExecutionReadinessButton' not in XAML
    assert 'ExecutionReadinessButton' not in ALL_CODE
    assert 'EiaWorkspaceExecutionReadinessPlanner.PrepareReadOnlyReview(' in DIRECT
