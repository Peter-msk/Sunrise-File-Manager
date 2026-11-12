"""v1.8.1 R01: targeted UI/operation safety contracts (not Windows runtime tests)."""
from pathlib import Path
import re
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1] / 'Sunrise.FileManager' / 'Controls'
XAML = (ROOT / 'EiaWorkspaceExecutionReviewWindow.xaml').read_text(encoding='utf-8')
CS = (ROOT / 'EiaWorkspaceExecutionReviewWindow.xaml.cs').read_text(encoding='utf-8')


def test_all_declared_xaml_handlers_exist():
    ET.fromstring(XAML)
    handlers = set(re.findall(r'(?:Click|TextChanged)="([A-Za-z_][A-Za-z_0-9]*)"', XAML))
    for name in handlers:
        assert re.search(r'\b' + re.escape(name) + r'\s*\(', CS), name


def test_cancellation_prevents_window_disposal_while_worker_runs():
    assert 'protected override void OnClosing(CancelEventArgs e)' in CS
    before = CS.split('protected override void OnClosing(CancelEventArgs e)', 1)[1]
    section = before.split('protected override void OnClosed(', 1)[0]
    assert 'e.Cancel = true;' in section
    assert '_preflightCancellation?.Cancel();' in section
    assert '_creationCancellation?.Cancel();' in section
    assert 'base.OnClosing(e);' in section


def test_preflight_locks_confirmation_while_running():
    section = CS.split('private async void RecheckBeforeExecution_Click', 1)[1].split('private void CancelPreflight_Click', 1)[0]
    assert '_creationCancellation is not null' in section
    for name in ['WorkspaceNameConfirmation', 'AcknowledgeProjectButton']:
        assert f'{name}.IsEnabled = false;' in section
        assert f'{name}.IsEnabled = true;' in section
    assert 'OperationProgress.Visibility = Visibility.Visible;' in section
    assert 'OperationProgress.Visibility = Visibility.Collapsed;' in section


def test_progress_bar_and_safe_result_open_ui():
    assert 'x:Name="OperationProgress"' in XAML
    assert 'x:Name="OpenWorkspaceFolderButton"' in XAML
    assert 'Click="OpenWorkspaceFolder_Click"' in XAML
    assert 'TextTrimming="CharacterEllipsis"' in XAML
    assert 'ToolTip="{Binding WorkspacePath}"' in XAML
    assert 'OperationProgress.IsIndeterminate = true;' in CS
    assert 'UpdateOperationProgress(' in CS
    assert 'CanOpenWorkspaceFolder()' in CS
    assert 'EiaWorkspaceCreationEngine.VerifyExistingChain(_review.WorkspacePath)' in CS
    assert 'new ProcessStartInfo("explorer.exe")' in CS


def test_creation_engine_is_still_directory_only():
    feature = (ROOT.parent / 'Features' / 'EiaWorkspace' / 'EiaWorkspaceProductionWriter.cs').read_text()
    for unsafe in ['File.Delete(', 'File.Move(', 'Directory.Delete(', 'Directory.Move(', 'Directory.CreateDirectory(']:
        assert unsafe not in feature
