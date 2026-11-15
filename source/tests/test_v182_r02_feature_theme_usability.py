"""v1.8.2 R02 source-only regression gates (not a Windows native PASS).

The disabled-state inventory separates valid permission/selection gates from
hardcoded feature gaps; no safety locks are forced open by these tests.
"""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1] / 'Sunrise.FileManager'
X = '{http://schemas.microsoft.com/winfx/2006/xaml/presentation}'


def src(p): return (ROOT / p).read_text(encoding='utf-8')


def lum(s):
    v = [int(s[i:i+2], 16) / 255 for i in (1, 3, 5)]
    def c(x): return x / 12.92 if x <= 0.04045 else ((x+.055)/1.055)**2.4
    return sum(a*c(x) for a,x in zip((.2126,.7152,.0722),v))


def contrast(a, b):
    hi,lo=sorted((lum(a),lum(b)),reverse=True)
    return (hi+.05)/(lo+.05)


def test_default_light_dark_palette_readable_for_real_text_surfaces():
    text = src('Services/ThemeManager.cs')
    colors = {name:(light,dark) for name,light,dark in re.findall(
        r'\["([^"]+)"\] = \("(#[0-9A-Fa-f]{6})", "(#[0-9A-Fa-f]{6})"\)', text)}
    pairs = [('TextPrimary','ThemeSurface'),('TextMuted','ThemeSurface'),
             ('ThemeMenuText','ThemeMenuBar'),('TextPrimary','ThemeComboPopup'),
             ('ThemeNavigationRailText','ThemeNavigationRail'),
             ('ThemeUtilityRailText','ThemeUtilityRail'),
             ('ThemeButtonOnAccent','Accent'),('ThemeSplitText','ThemeSplitInactive'),
             ('ThemeSplitActiveText','ThemeSplitActive'),('ThemeNoticeText','ThemeNotice')]
    assert len(colors)>=55
    for index in (0,1):
        for fg,bg in pairs:
            assert contrast(colors[fg][index],colors[bg][index]) >= 4.5,(index,fg,bg)


def test_theme_focus_and_readonly_state_and_tooltips():
    app = src('App.xaml')
    assert 'ToolTipService.ShowOnDisabled' in app
    assert '<Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>' in app
    assert '<Trigger Property="IsReadOnly" Value="True"><Setter Property="Background" Value="{DynamicResource ThemeSoft}"/></Trigger>' in app
    assert 'Opacity" Value="0.45"' not in app
    assert 'Opacity" Value="0.68"' in app
    assert '<sys:Double x:Key="UiScaleFactor">1.0</sys:Double>' in app
    assert 'VerticalScrollBarWidthKey}">9' in app
    assert 'HorizontalScrollBarHeightKey}">9' in app


def test_tabs_have_theme_owned_foreground_focus_and_selection():
    for p in ('Controls/FilePropertiesWindow.xaml','Controls/EiaWorkspacePlanningWindow.xaml'):
        x=src(p)
        ET.fromstring(x)
        assert '<Window.Resources>' in x and '<Style TargetType="TabItem">' in x
        assert '{DynamicResource TextPrimary}' in x
        assert '<Trigger Property="IsSelected" Value="True">' in x
        assert '{DynamicResource Accent}' in x


def test_eiass_preview_is_optional_not_an_execution_gate():
    code=src('Controls/EiaWorkspacePlanningWindow.DirectCreation.cs')
    xaml=src('Controls/EiaWorkspacePlanningWindow.xaml')
    assert 'Preview_Click(sender, e);' in code
    assert 'preview = RequireFreshVisiblePlan();' in code
    avail=code[code.index('private void RefreshDirectCreationAvailability'):code.index('private async void CreateFoldersDirect_Click')]
    assert '_lastPreview is not null' not in avail
    assert 'ProjectTitleInput.Text' in avail and 'ContractDescriptionInput.Text' in avail
    assert 'DestinationPathInput.Text' in avail
    assert '폴더 미리보기 (선택)' in xaml
    assert 'Style="{StaticResource PrimaryButton}" IsDefault="True"' in xaml


def test_eiass_direct_writer_safety_is_not_bypassed():
    code=src('Controls/EiaWorkspacePlanningWindow.DirectCreation.cs')
    core=code[code.index('private async void CreateFoldersDirect_Click'):code.index('private void CancelFolderCreationDirect_Click')]
    tokens=['Preview_Click(sender, e);','RequireFreshVisiblePlan();',
            'EiaWorkspaceDestinationInspector.Diagnose(',
            'scan.Blocked != 0','RequireConsistentObservation(',
            'EiaWorkspaceExecutionPreflight.RecheckReadOnly(',
            'MessageBoxButton.YesNo','EiaWorkspaceProductionWriter.ExecuteReviewed(']
    positions=[core.index(s) for s in tokens]
    assert positions==sorted(positions)
    assert 'Directory.CreateDirectory(' not in core and 'File.Delete(' not in core
    native=src('Features/EiaWorkspace/EiaWorkspaceNativeDirectoryWriter.cs')
    assert 'NTFS' in native and 'ReFS' in native


def test_local_folders_can_be_selected_for_verified_cross_pane_move():
    ui=src('Features/Workspace/MainWindow.CrossPaneMove.cs')
    service=src('Services/CloudDrive/CrossPaneVerifiedMoveService.cs')
    assert 'hasLocalFolder = paths.Any(Directory.Exists);' in ui
    assert '_paneLocationModes[i] == "Local"' in ui
    assert 'paths.Any(path => !File.Exists(path) && !Directory.Exists(path))' in ui
    assert '폴더 이동은 아직 지원하지 않습니다' not in ui[ui.index('if (originalMode == \"Local\")'):ui.index('        else', ui.index('if (originalMode == \"Local\")'))]
    assert 'Directory.Exists(local)' in service
    assert 'TransferService.RunAsync(' in service
    assert 'forceVerifiedMove: true' in service
    assert 'CollisionPolicy.Skip' in service
    assert 'moved.Completed == 1' in service


def test_remote_provider_protection_and_race_guards_retained():
    svc=src('Services/CloudDrive/CrossPaneVerifiedMoveService.cs')
    assert 'source.RemoteApi is not IConditionalCloudRetirement' in svc
    assert 'string.IsNullOrWhiteSpace(s.RemoteFile?.VersionTag)' in src('Features/Workspace/MainWindow.CrossPaneMove.cs')
    assert 'selectedFolders.Contains(parent)' in svc
    assert 'target.LocalFolder is null' in svc
    assert '클라우드 폴더 이동은 원본 버전 보호를 보장할 수 없습니다' in svc
    assert 'File.Delete(Path.GetFullPath(path));' in svc  # file-only, after both hashes


def test_crosspane_dialog_reports_folder_capability_and_completion():
    dlg=src('Controls/CrossPaneCopyWindow.cs')
    assert 'bool localFolders = false' in dlg
    assert '_localFolders' in dlg
    assert '로컬 파일·폴더' in dlg
    assert '자동으로 되돌리지 않습니다' in dlg
    assert '원본·대상' in src('Services/CloudDrive/CrossPaneVerifiedMoveService.cs')
    assert '_cancel.IsEnabled = false' in dlg and 'RequestCancel()' in dlg
    assert 'CompletionMessage = BatchCopyUiSummary.Format' in dlg


def test_ai_ui_has_single_primary_execute_action():
    xaml=src('Controls/AiCommandPanel.xaml')
    assert 'x:Name="PlanButton" Style="{StaticResource ExplorerCommand}" Content="작업 미리보기"' in xaml
    assert 'x:Name="ExecuteButton" Style="{StaticResource PrimaryButton}" Content="검토한 작업 실행"' in xaml
    assert 'Text="탐색 범위"' in xaml
    assert 'Text="정리 방법"' in xaml
    assert 'Text="대상 폴더"' in xaml
    assert '① 탐색 범위' not in xaml and '② AI 명령' not in xaml
    assert 'IsEnabled="False"' in xaml  # execute waits for a VALIDATED plan


def test_user_safety_checks_still_guard_destructive_or_remote_operations():
    ai=src('Services/AiNaturalLanguageService.cs')
    assert '영구 삭제는 지원하지 않습니다' in ai
    actions=src('Services/FileActionsService.cs')
    assert '네트워크·이동식 위치' in actions
    transfer=src('Services/TransferService.cs')
    assert 'CollisionPolicy.Skip' in transfer
    assert 'ReparsePointPolicy.IsBlocked' in transfer


def test_xaml_all_22_parses():
    files=list(ROOT.rglob('*.xaml'))
    assert len(files)==22
    for p in files: ET.parse(p)
