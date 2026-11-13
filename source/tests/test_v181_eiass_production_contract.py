"""v1.8.1 source-contract checks. Never claim these replace .NET/Windows runtime tests."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1] / "Sunrise.FileManager"
FEATURE = ROOT / "Features" / "EiaWorkspace"


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


def test_versions_agree():
    project = read("Sunrise.FileManager.csproj")
    main = read("MainWindow.xaml")
    manifest = read("app.manifest")
    control = read("Services/SunriseControlService.cs")
    assert '<Version>1.8.1</Version>' in project
    assert '<AssemblyVersion>1.8.1.0</AssemblyVersion>' in project
    assert 'Sunrise File Manager 1.8.1' in main
    assert 'v1.8.1' in main
    assert 'version="1.8.1.0"' in manifest
    assert 'const string Version = "1.8.1"' in control


def test_core_reader_still_has_locked_preview_contract():
    s = (FEATURE / 'EiaWorkspaceCreationEngine.cs').read_text()
    assert 'ProductionWritesEnabled = false' in s
    assert '#if EIA_WORKSPACE_SANDBOX_TEST' in s
    assert 'internal static bool VerifyExistingChain(' in s
    assert 'RequireClosedProposedPathTree(' in s


def test_production_requires_two_fresh_scans_and_final_consent():
    s = (FEATURE / 'EiaWorkspaceProductionWriter.cs').read_text()
    for marker in ('"기본안"', '"CREATE " + review.WorkspaceFolderName',
                   'PrepareReadOnlyManifest(', 'RecheckReadOnly(',
                   'RequireSameReview(', 'shown.Paths[i]',
                   'OpenPinnedBase(', 'CreateOrVerify(',
                   'NOT_ATTEMPTED', 'FAILED', 'CREATED', 'ALREADY_EXISTS'):
        assert marker in s, marker
    for dangerous in ('Directory.CreateDirectory(', 'File.Delete(',
                      'File.Move(', 'Directory.Delete(', 'Directory.Move(',
                      'File.WriteAll', 'Process.Start(', 'https://'):
        assert dangerous not in s, dangerous


def test_native_no_path_based_fallback_or_cross_volume():
    s = (FEATURE / 'EiaWorkspaceNativeDirectoryWriter.cs').read_text()
    for marker in ('NtCreateFile(', 'FileCreate = 2', 'FileOpen = 1',
                   'RootDirectory = parent.DangerousGetHandle()',
                   'FileOpenReparsePoint', 'RequirePinnedPath(',
                   'GetDriveTypeW(', 'GetVolumeInformationW(', 'NTFS', 'ReFS'):
        assert marker in s, marker
    assert 'Directory.CreateDirectory(' not in s
    assert 'File.Delete(' not in s
    assert 'Directory.Delete(' not in s
    assert 'OpenPinnedSandbox' not in s


def test_wpf_controls_event_names_and_xml():
    xaml = read('Controls/EiaWorkspaceExecutionReviewWindow.xaml')
    code = read('Controls/EiaWorkspaceExecutionReviewWindow.xaml.cs')
    ET.fromstring(xaml)
    handlers = set(re.findall(r'\b(?:Click|TextChanged)="([A-Za-z_][A-Za-z_0-9]*)"', xaml))
    for handler in handlers:
        assert re.search(r'\b' + re.escape(handler) + r'\s*\(', code), handler
    for marker in ('x:Name="CreateFoldersButton"', 'x:Name="CancelFolderCreationButton"',
                   'Click="CreateFolders_Click"', 'Click="CancelFolderCreation_Click"',
                   'x:Name="AcknowledgeProjectButton"', 'x:Name="CreatedCountText"'):
        assert marker in xaml, marker
    assert 'Task.Run(() => EiaWorkspaceProductionWriter.ExecuteReviewed(' in code
    assert 'MessageBoxButton.YesNo' in code
    assert '_creationCancellation?.Cancel();' in code


def test_profile_draft_not_promoted_and_rename_not_changed():
    s = (FEATURE / 'EiaWorkspaceProductionWriter.cs').read_text()
    assert 'review.SourceLabel != "기본안"' in s
    rename = read('Services/FileActionsService.EiassRename.cs')
    assert 'BatchRenameFilesSafely' in rename
    assert 'MoveEntry(current[i], pairs[i].OldPath)' in rename
    assert 'File.Delete' not in rename


def test_smoke_project_present_without_sandbox_symbol():
    p = ROOT.parent / 'tests' / 'EiaWorkspaceProductionSmoke' / 'EiaWorkspaceProductionSmoke.csproj'
    txt = p.read_text()
    ET.fromstring(txt)
    assert 'EiaWorkspaceProductionWriter.cs' in txt
    assert 'EiaWorkspaceNativeDirectoryWriter.cs' in txt
    assert 'EIA_WORKSPACE_SANDBOX_TEST' not in txt
