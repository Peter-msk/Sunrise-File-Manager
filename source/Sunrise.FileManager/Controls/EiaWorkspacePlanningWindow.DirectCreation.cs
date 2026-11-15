// v1.8.2 R01: single-screen folder-only execution in the EIASS planner.
// The planning engine never writes; this partial class invokes only the
// pinned local NTFS/ReFS directory writer after read-only verification and
// explicit final Yes/No approval. No files are copied, moved or deleted.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Sunrise.FileManager.Features.EiaWorkspace;

namespace Sunrise.FileManager.Controls;

public partial class EiaWorkspacePlanningWindow
{
    private CancellationTokenSource? _folderCreationCancellation;
    private string? _createdWorkspacePath;

    // The visible workflow is now: preview -> choose an existing base -> create.
    // All former explicit read-only recheck/acknowledgement steps are performed
    // inside this one command, followed by exactly one final Yes/No consent.
    // The locked preview engine remains read-only: the isolated pinned NTFS
    // production writer is the ONLY filesystem-mutating path.
    private void RefreshDirectCreationAvailability()
    {
        if (CreateFoldersButton is null || DestinationPathInput is null) return;
        CreateFoldersButton.IsEnabled = !_windowClosed && _folderCreationCancellation is null &&
            !_destinationInspectionRunning && !_profileDestinationInspectionRunning &&
            _lastPreview is not null && _lastInputSignature == CurrentInputSignature() &&
            !string.IsNullOrWhiteSpace(DestinationPathInput.Text);
    }

    private async void CreateFoldersDirect_Click(object sender, RoutedEventArgs e)
    {
        if (_windowClosed || _folderCreationCancellation is not null ||
            _destinationInspectionRunning || _profileDestinationInspectionRunning)
            return;

        EiaWorkspaceUiPreview preview;
        EiaCaseWorkspaceRequest request;
        EiaScenarioSelection[] selections;
        string signature;
        string basePath;
        try
        {
            CommitDetailedEdits();
            preview = RequireFreshVisiblePlan();
            string mode = (CoreModeSelector.SelectedItem as ComboBoxItem)?.Tag as string
                ?? throw new InvalidDataException("폴더 구성 유형을 선택하세요.");
            if (DetailExpander.IsExpanded)
                (request, selections) = EiaWorkspaceUiComposer.CompileDetailedInputs(
                    ReadDetailedInput(mode), ReadScenarioInputs());
            else
            {
                request = EiaWorkspaceUiComposer.CompileSingleContractRequest(
                    ProjectCodeInput.Text, ProjectTitleInput.Text,
                    ContractCodeInput.Text, ContractDescriptionInput.Text,
                    _types.Where(x => x.Selected).Select(x => x.Id).ToArray(), mode);
                selections = Array.Empty<EiaScenarioSelection>();
            }
            basePath = DestinationPathInput.Text.Trim();
            if (basePath.Length == 0)
                throw new InvalidDataException("생성할 위치를 먼저 선택하세요.");
            signature = CurrentInputSignature();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            DestinationInspectionStatus.Text = "생성 준비 오류: " + ex.Message;
            RefreshDirectCreationAvailability();
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _folderCreationCancellation = cancellation;
        _createdWorkspacePath = null;
        OpenWorkspaceFolderButton.IsEnabled = false;
        CreateFoldersButton.IsEnabled = false;
        CancelFolderCreationButton.IsEnabled = true;
        WorkspaceInputsScroll.IsEnabled = false;
        DestinationPathInput.IsEnabled = false;
        ChooseDestinationButton.IsEnabled = false;
        PreviewButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        InspectDestinationButton.IsEnabled = false;
        FolderCreationProgress.Visibility = Visibility.Visible;
        FolderCreationProgress.IsIndeterminate = true;
        ClearDestinationDiagnostics("생성 전 경로 안전 점검 중 · 이전 결과를 사용하지 않습니다.");
        DestinationInspectionStatus.Text = "기존 폴더·충돌·연결 지점을 확인하고 있습니다…";
        var token = cancellation.Token;
        try
        {
            // Do not retain the prior inspection as execution permission.
            // Fresh observation, strict plan comparison and per-project read-only
            // acknowledgement are produced together, off the UI thread.
            var (observed, review, acknowledgement, preflight) =
                await RunExclusiveDestinationScanAsync(() =>
                {
                    var scan = EiaWorkspaceDestinationInspector.Diagnose(
                        basePath, request, selections, preview, token);
                    if (scan.Blocked != 0)
                        throw new InvalidDataException($"충돌 또는 접근 불가 경로 {scan.Blocked}개. 생성하지 않았습니다.");
                    var inspection = EiaWorkspaceDestinationInspector.Inspect(
                        basePath, request, selections, preview, token);
                    EiaWorkspaceDestinationInspector.RequireConsistentObservation(
                        scan, preview.FingerprintSha256, inspection.PlanFingerprint);
                    var prepared = EiaWorkspaceExecutionReadinessPlanner.PrepareReadOnlyReview(
                        scan, preview.FingerprintSha256, inspection.PlanFingerprint, "기본안");
                    // This is NOT a write token. The final Yes/No question below
                    // explicitly authorizes the exact project in this UI session.
                    var checkedProject = EiaWorkspaceProjectReviewPlanner.AcknowledgeReadOnly(
                        prepared, prepared.WorkspaceFolderName, DateTimeOffset.UtcNow);
                    var fresh = EiaWorkspaceExecutionPreflight.RecheckReadOnly(
                        prepared, checkedProject, token);
                    EiaWorkspaceExecutionService.PrepareReadOnlyManifest(
                        prepared, checkedProject, fresh, token);
                    return (scan, prepared, checkedProject, fresh);
                }, token);
            token.ThrowIfCancellationRequested();
            if (_windowClosed || signature != CurrentInputSignature() ||
                !string.Equals(basePath, DestinationPathInput.Text.Trim(), StringComparison.Ordinal))
                throw new InvalidDataException("작업 중 사업 입력 또는 저장 위치가 변경됐습니다. 다시 확인하세요.");

            FolderCreationProgress.IsIndeterminate = false;
            FolderCreationProgress.Maximum = Math.Max(1, observed.Rows.Count);
            FolderCreationProgress.Value = 0;
            DiagnosticStatusFilter.SelectedIndex = 0;
            ShowDestinationDiagnostics(observed, "기본안 생성 전 점검");
            DestinationInspectionStatus.Text = $"기존 {preflight.Existing}개 · 생성 대상 {preflight.WouldCreate}개 · 충돌 없음";
            DestinationInspectionStatus.ToolTip = review.WorkspacePath;
            if (preflight.WouldCreate == 0)
            {
                _createdWorkspacePath = review.WorkspacePath;
                OpenWorkspaceFolderButton.IsEnabled = CanOpenVerifiedWorkspace(review.WorkspacePath);
                StatusText.Text = "모든 계획 폴더가 이미 존재합니다. 신규 생성 0개.";
                return;
            }
            if (MessageBox.Show(this,
                    $"다음 사업의 누락 폴더 {preflight.WouldCreate:N0}개를 생성합니다.\n\n" +
                    $"사업명: {review.WorkspaceFolderName}\n저장 위치: {review.BaseDirectory}\n\n" +
                    "기존 파일과 폴더는 삭제·이동·덮어쓰지 않습니다.\n계속하시겠습니까?",
                    "누락 폴더 생성 확인", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                DestinationInspectionStatus.Text = "사용자가 취소했습니다. 폴더는 생성되지 않았습니다.";
                return;
            }
            token.ThrowIfCancellationRequested();
            DestinationInspectionStatus.Text = "누락 폴더 생성 중… 기존 파일과 폴더는 유지됩니다.";
            var progress = new Progress<(int Done, int Total, string Path)>(value =>
            {
                if (!ReferenceEquals(_folderCreationCancellation, cancellation)) return;
                FolderCreationProgress.Maximum = Math.Max(1, value.Total);
                FolderCreationProgress.Value = Math.Clamp(value.Done, 0, FolderCreationProgress.Maximum);
                DestinationInspectionStatus.Text = $"폴더 {value.Done:N0}/{value.Total:N0} · {Path.GetFileName(value.Path.TrimEnd('/', '\\'))}";
                DestinationInspectionStatus.ToolTip = value.Path;
            });
            var result = await Task.Run(() => EiaWorkspaceProductionWriter.ExecuteReviewed(
                review, acknowledgement, preflight, "CREATE " + review.WorkspaceFolderName,
                token, (done, total, path) => ((IProgress<(int Done, int Total, string Path)>)progress)
                    .Report((done, total, path))), token);

            // The operation is partial when ANY path failed or was not attempted.
            // Show actual per-path outcomes rather than the stale pre-create snapshot.
            var actualRows = preflight.CurrentPaths.Zip(result.Outcomes, (old, created) =>
                new EiaFolderPathObservation(old.ScopeLabel, old.RelativePath,
                    old.AbsolutePath, created.Outcome, created.Detail)).ToArray();
            _diagnosticRows = actualRows;
            DiagnosticStatusFilter.SelectedIndex = 0;
            _diagnosticView = CollectionViewSource.GetDefaultView(_diagnosticRows);
            _diagnosticView.Filter = item => item is EiaFolderPathObservation row &&
                EiaWorkspaceDiagnosticFilter.Matches(row, CurrentDiagnosticStatus(),
                    DiagnosticSearchInput.Text);
            DestinationDiagnosticGrid.ItemsSource = _diagnosticView;
            DestinationReviewTabs.SelectedIndex = 1;
            DestinationReviewSummary.Text = result.Completed
                ? $"생성 완료 · 신규 {result.Created:N0}개 · 기존 {result.AlreadyExists:N0}개 · 파일 변경 0개"
                : $"부분 완료 · 신규 {result.Created:N0}개 · 실패 {result.Failed:N0}개 · 미시도 {result.NotAttempted:N0}개";
            DestinationInspectionStatus.Text = result.Completed
                ? $"완료: 신규 {result.Created:N0}개 · 기존 {result.AlreadyExists:N0}개"
                : $"중단/실패: 신규 {result.Created:N0}개 · 실패 {result.Failed:N0}개 · 미시도 {result.NotAttempted:N0}개";
            StatusText.Text = result.Completed
                ? "사업별 폴더 생성이 완료됐습니다. 결과 폴더를 열 수 있습니다."
                : "부분 완료 상태입니다. 진단표를 확인한 후 목록과 경로를 다시 점검하세요.";
            _createdWorkspacePath = review.WorkspacePath;
            OpenWorkspaceFolderButton.IsEnabled = CanOpenVerifiedWorkspace(review.WorkspacePath);
        }
        catch (OperationCanceledException)
        {
            DestinationInspectionStatus.Text = "생성이 중지됐습니다. 이미 생성된 폴더는 보존됩니다. 결과를 다시 점검하세요.";
            ClearDestinationDiagnostics("취소 후 현재 폴더 상태 재점검 필요");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
            UnauthorizedAccessException or System.Security.SecurityException or
            ArgumentException or NotSupportedException or InvalidOperationException)
        {
            DestinationInspectionStatus.Text = "폴더 생성 차단: " + ex.Message;
            ClearDestinationDiagnostics("생성 차단 · 이전 점검 결과는 무효입니다.");
        }
        catch (Exception ex)
        {
            DestinationInspectionStatus.Text = "생성 중 예기치 못한 오류: " + ex.GetType().Name;
            ClearDestinationDiagnostics("오류 후 경로 상태 재점검 필요");
        }
        finally
        {
            if (ReferenceEquals(_folderCreationCancellation, cancellation))
                _folderCreationCancellation = null;
            FolderCreationProgress.IsIndeterminate = false;
            FolderCreationProgress.Visibility = Visibility.Collapsed;
            CancelFolderCreationButton.IsEnabled = false;
            WorkspaceInputsScroll.IsEnabled = true;
            DestinationPathInput.IsEnabled = true;
            ChooseDestinationButton.IsEnabled = true;
            PreviewButton.IsEnabled = true;
            CopyButton.IsEnabled = _lastPreview is not null;
            InspectDestinationButton.IsEnabled = _lastPreview is not null;
            RefreshDirectCreationAvailability();
        }
    }

    private void CancelFolderCreationDirect_Click(object sender, RoutedEventArgs e)
    {
        if (_folderCreationCancellation is null) return;
        CancelFolderCreationButton.IsEnabled = false;
        DestinationInspectionStatus.Text = "중지 요청 중… 이미 생성된 폴더는 그대로 보존됩니다.";
        _folderCreationCancellation.Cancel();
    }

    private static bool CanOpenVerifiedWorkspace(string path)
    {
        try { return Directory.Exists(path) && EiaWorkspaceCreationEngine.VerifyExistingChain(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
            UnauthorizedAccessException or System.Security.SecurityException or
            ArgumentException or NotSupportedException) { return false; }
    }

    private void OpenWorkspaceFolderDirect_Click(object sender, RoutedEventArgs e)
    {
        string? path = _createdWorkspacePath;
        if (path is null || !CanOpenVerifiedWorkspace(path))
        {
            OpenWorkspaceFolderButton.IsEnabled = false;
            DestinationInspectionStatus.Text = "폴더 경로를 다시 확인한 후 열 수 있습니다.";
            return;
        }
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(path);
            Process.Start(start);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            DestinationInspectionStatus.Text = "폴더를 열 수 없습니다: " + ex.Message;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_folderCreationCancellation is not null)
        {
            e.Cancel = true;
            _folderCreationCancellation.Cancel();
            DestinationInspectionStatus.Text = "작업 종료 요청 중입니다. 생성 결과 확인 후 닫을 수 있습니다.";
            return;
        }
        base.OnClosing(e);
    }

}
