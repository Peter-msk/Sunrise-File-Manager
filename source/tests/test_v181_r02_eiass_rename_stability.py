"""v1.8.1 R02 source contract checks. Windows C# runtime acceptance is separate."""
from pathlib import Path
from xml.etree import ElementTree as ET
import random
import re

BASE = Path(__file__).resolve().parents[1] / 'Sunrise.FileManager'
UI = (BASE / 'Controls/ExplorerPane.xaml.cs').read_text(encoding='utf-8')
RENAME_UI = (BASE / 'Controls/EiassRenameDialog.xaml.cs').read_text(encoding='utf-8')
XAML = (BASE / 'Controls/EiassRenameDialog.xaml').read_text(encoding='utf-8')
MATCH = (BASE / 'Services/EiassFileNameService.cs').read_text(encoding='utf-8')
SERVICE = (BASE / 'Services/FileActionsService.EiassRename.cs').read_text(encoding='utf-8')
SMOKE = (Path(__file__).resolve().parent / 'UiSmokeTests/EiassRenameRegression.cs').read_text(encoding='utf-8')


def test_toc_match_has_unique_key_indices_not_quadratic_rescans():
    section = MATCH.split('public static int?[] MatchContents(', 1)[1]
    assert 'titleCounts' in section and 'contentPositions' in section
    assert 'TryGetValue' in section and 'TryAdd' in section
    assert 'titleKeys.Count(' not in section and 'contentsKeys.Count(' not in section
    assert 'Array.IndexOf(contentsKeys' not in section
    assert 'Normalize(NormalizationForm.FormKC)' in MATCH


def test_eiass_preview_uses_linear_case_aware_collision_counts():
    section = RENAME_UI.split('private void Rebuild()', 1)[1].split('private void Up_Click', 1)[0]
    assert 'Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)' in section
    assert 'Dictionary<string, int>(StringComparer.Ordinal)' in section
    assert 'pathCounts[pair.Value] > 1' in section
    assert '_rows.Count(r => r.Code' not in section and 'names.Values.Count(' not in section
    assert 'File.Exists(row.SourcePath)' in section
    assert 'activeSources.Contains(pair.Value)' in section
    assert 'ApplyButton.IsEnabled =' in section


def test_workflow_reuses_protected_local_file_job_and_async_undo():
    section = UI.split('public async void ExecuteEiassRename()', 1)[1].split('private void EiassRename_Click', 1)[0]
    assert 'await RunLocalFileJobAsync(' in section
    assert 'BlockingFileActionChanged' not in section  # already owned by local job
    assert '_undoService.RecordBatchRename' in section
    assert 'catch (OperationCanceledException)' in section
    assert 'finally { reportsActive = false; }' in section
    assert 'approved = dialog.ApprovedRenames.ToArray();' in section


def test_cancellation_occurs_only_between_individual_moves_and_rolls_back():
    assert 'CancellationToken cancellationToken = default' in SERVICE
    assert SERVICE.count('cancellationToken.ThrowIfCancellationRequested();') >= 3
    assert 'current[i] = staged[i];' in SERVICE
    assert 'current[i] = pairs[i].NewPath;' in SERVICE
    assert 'for (int i = 0; i < published; i++)' in SERVICE
    assert 'recoveryErrors.Count > 0' in SERVICE
    assert 'failure is OperationCanceledException && cancellationToken.IsCancellationRequested' in SERVICE
    assert 'File.Delete(' not in SERVICE and 'File.Copy(' not in SERVICE
    assert 'MoveEntry(' in SERVICE


def test_large_dialog_virtualization_and_full_name_tooltip():
    ET.fromstring(XAML)
    assert 'EnableRowVirtualization="True"' in XAML
    assert 'EnableColumnVirtualization="True"' in XAML
    assert 'VirtualizingPanel.VirtualizationMode="Recycling"' in XAML
    assert 'ToolTip" Value="{Binding NewName}' in XAML
    assert 'TextTrimming" Value="CharacterEllipsis' in XAML


def test_native_smoke_has_content_memo_undo_and_cancel_checkpoints():
    assert '1,000-title / 2,000-TOC matching lost order' in SMOKE
    assert 'foreach (int cancelAt in new[] { 1, 3 })' in SMOKE
    assert 'Canceled rename detached file memos' in SMOKE
    assert 'Canceled rename left an incomplete stage file' in SMOKE
    assert 'protected async file-job ownership' in SMOKE
    assert 'pane.FindName("UndoButton")' in SMOKE


def test_no_release_bumped_to_unverified_version():
    root = Path(__file__).resolve().parents[2]
    assert not (root / 'latest.json').exists() or '"1.8.1"' not in (root / 'latest.json').read_text(encoding='utf-8')
    assert (root / 'installer/RELEASE_VERSION.txt').read_text(encoding='utf-8').strip() == '1.8.1'


def test_name_matching_equivalence_property_model():
    # Logical model of the old and new duplicate/index rules; not an execution
    # of the C# implementation. The native C# smoke above owns runtime proof.
    for seed in range(60):
        rng = random.Random(seed)
        titles = [rng.choice(['', 'a', 'b', 'c', 'd', 'e']) for _ in range(100)]
        contents = [rng.choice(['', 'a', 'b', 'c', 'd', 'e']) for _ in range(130)]
        old = [contents.index(x) if x and titles.count(x) == 1 and contents.count(x) == 1 else None for x in titles]
        tc = {x: titles.count(x) for x in set(titles)}
        ci = {x: (contents.index(x) if contents.count(x) == 1 else None) for x in set(contents)}
        new = [ci.get(x) if x and tc[x] == 1 else None for x in titles]
        assert old == new
