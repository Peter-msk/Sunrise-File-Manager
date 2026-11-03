using System;
using System.Runtime.InteropServices;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Sunrise.FileManager.Features.EiaWorkspace;

namespace Sunrise.FileManager.Controls;

/// <summary>DEV23 review-only window. No creation/approval/updater operations.</summary>
public partial class EiaWorkspaceExecutionReviewWindow : Window
{
    private readonly EiaWorkspaceExecutionReadiness _review;
    private EiaWorkspaceProjectReviewAcknowledgement? _projectReviewAcknowledgement;
    private EiaWorkspaceExecutionPreflightResult? _lastPreflight;
    private EiaWorkspaceExecutionManifest? _lastExecutionManifest;
    private CancellationTokenSource? _preflightCancellation;

    public EiaWorkspaceExecutionReviewWindow(EiaWorkspaceExecutionReadiness review)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (review.CanExecute || review.ProductionWritesEnabled || review.Blocked != 0)
            throw new InvalidOperationException("승인되지 않은 폴더 생성 검토 상태입니다.");
        _review = review;
        InitializeComponent();
        DataContext = review;
    }

    private void CopyReadiness_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_review.BuildReviewText(maxPaths: 5001));
        }
        catch (ExternalException ex)
        {
            MessageBox.Show(this, "클립보드 복사 실패: " + ex.Message,
                "생성 준비 검토", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void WorkspaceNameConfirmation_TextChanged(object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        // Confirmation is only valid for the exact text the user submitted.
        _projectReviewAcknowledgement = null;
        _lastPreflight = null;
        _lastExecutionManifest = null;
        if (ReadinessPathsGrid is not null) ReadinessPathsGrid.ItemsSource = Array.Empty<EiaFolderPathObservation>();
        if (PreflightStatus is not null) PreflightStatus.Text = "사업 확인이 변경되어 재점검 결과가 폐기됐습니다.";
        if (CopyPreflightButton is not null) CopyPreflightButton.IsEnabled = false;
        if (CopyExecutionManifestButton is not null) CopyExecutionManifestButton.IsEnabled = false;
        if (ProjectReviewStatus is not null)
            ProjectReviewStatus.Text = "폴더명이 변경됐습니다. 이 사업의 검토를 다시 확인하세요.";
    }

    private void AcknowledgeProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _lastPreflight = null;
            _lastExecutionManifest = null;
            ReadinessPathsGrid.ItemsSource = Array.Empty<EiaFolderPathObservation>();
            CopyPreflightButton.IsEnabled = false;
            CopyExecutionManifestButton.IsEnabled = false;
            _projectReviewAcknowledgement = EiaWorkspaceProjectReviewPlanner.AcknowledgeReadOnly(
                _review, WorkspaceNameConfirmation.Text, DateTimeOffset.UtcNow);
            EiaWorkspaceProjectReviewPlanner.RequireSameReview(_projectReviewAcknowledgement, _review);
            ProjectReviewStatus.Text = "이 사업의 읽기 전용 검토를 확인했습니다. 폴더는 생성되지 않았습니다.";
        }
        catch (System.IO.InvalidDataException ex)
        {
            _projectReviewAcknowledgement = null;
            _lastPreflight = null;
            _lastExecutionManifest = null;
            CopyPreflightButton.IsEnabled = false;
            CopyExecutionManifestButton.IsEnabled = false;
            ProjectReviewStatus.Text = ex.Message;
        }
    }

    private async void RecheckBeforeExecution_Click(object sender, RoutedEventArgs e)
    {
        if (_preflightCancellation is not null) return;
        _lastPreflight = null;
        _lastExecutionManifest = null;
        CopyPreflightButton.IsEnabled = false;
        CopyExecutionManifestButton.IsEnabled = false;
        // Prior observations are not a current path view. Clear them before rechecking.
        ReadinessPathsGrid.ItemsSource = Array.Empty<EiaFolderPathObservation>();
        if (_projectReviewAcknowledgement is null)
        {
            PreflightStatus.Text = "먼저 해당 사업 폴더명을 재입력하고 검토 확인을 완료하세요.";
            return;
        }
        var acknowledgement = _projectReviewAcknowledgement;
        var cancellation = new CancellationTokenSource();
        _preflightCancellation = cancellation;
        RecheckBeforeExecutionButton.IsEnabled = false;
        CancelPreflightButton.IsEnabled = true;
        PreflightStatus.Text = "저장 위치를 새로 확인하는 중입니다. 생성·삭제·수정은 수행하지 않습니다.";
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (ReferenceEquals(_preflightCancellation, cancellation))
                PreflightStatus.Text = $"재점검 {p.Done:N0} / {p.Total:N0}개 (읽기 전용)";
        });
        try
        {
            var result = await Task.Run(() => EiaWorkspaceExecutionPreflight.RecheckReadOnly(
                _review, acknowledgement, cancellation.Token,
                (done, total) => ((IProgress<(int Done, int Total)>)progress).Report((done, total))),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            // The user may have edited the confirmation while the scan was running.
            if (!ReferenceEquals(acknowledgement, _projectReviewAcknowledgement))
                throw new InvalidDataException("재점검 중 사업 확인이 변경됐습니다. 다시 검토하세요.");
            var manifest = EiaWorkspaceExecutionService.PrepareReadOnlyManifest(
                _review, acknowledgement, result, cancellation.Token);
            _lastExecutionManifest = manifest;
            _lastPreflight = result;
            ReadinessPathsGrid.ItemsSource = result.CurrentPaths;
            CopyPreflightButton.IsEnabled = true;
            CopyExecutionManifestButton.IsEnabled = true;
            PreflightStatus.Text = $"재점검 일치: 기존 {result.Existing:N0} / 생성 예상 {result.WouldCreate:N0} / 차단 0 (실제 생성 0)";
        }
        catch (OperationCanceledException)
        {
            _lastPreflight = null;
            _lastExecutionManifest = null;
            CopyExecutionManifestButton.IsEnabled = false;
            CopyPreflightButton.IsEnabled = false;
            _projectReviewAcknowledgement = null;
            PreflightStatus.Text = "재점검이 취소됐습니다. 이전 사업 확인은 무효입니다.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or
            UnauthorizedAccessException or System.Security.SecurityException or
            ArgumentException or NotSupportedException)
        {
            _lastPreflight = null;
            _lastExecutionManifest = null;
            CopyExecutionManifestButton.IsEnabled = false;
            CopyPreflightButton.IsEnabled = false;
            _projectReviewAcknowledgement = null;
            PreflightStatus.Text = "재점검 실패 · 기존 확인 폐기: " + ex.Message;
        }
        catch (Exception ex)
        {
            // Unexpected failures are fail-closed; no stale result is retained.
            _lastPreflight = null;
            _lastExecutionManifest = null;
            CopyExecutionManifestButton.IsEnabled = false;
            CopyPreflightButton.IsEnabled = false;
            _projectReviewAcknowledgement = null;
            PreflightStatus.Text = "재점검 처리 오류 · 이전 확인 폐기: " + ex.GetType().Name;
        }
        finally
        {
            if (ReferenceEquals(_preflightCancellation, cancellation))
                _preflightCancellation = null;
            cancellation.Dispose();
            RecheckBeforeExecutionButton.IsEnabled = true;
            CancelPreflightButton.IsEnabled = false;
        }
    }

    private void CancelPreflight_Click(object sender, RoutedEventArgs e) =>
        _preflightCancellation?.Cancel();

    private void CopyPreflight_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPreflight is null) return;
        try { Clipboard.SetText(_lastPreflight.BuildReadOnlyReport()); }
        catch (ExternalException ex)
        {
            MessageBox.Show(this, "재점검표 복사 실패: " + ex.Message,
                "실행 직전 재점검", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyExecutionManifest_Click(object sender, RoutedEventArgs e)
    {
        if (_lastExecutionManifest is null || _lastPreflight is null ||
            _projectReviewAcknowledgement is null) return;
        try
        {
            // Only hand out a read-only, site-bound manifest; never a write token.
            Clipboard.SetText(_lastExecutionManifest.BuildReadOnlyReport());
        }
        catch (ExternalException ex)
        {
            MessageBox.Show(this, "생성 계획표 복사 실패: " + ex.Message,
                "실행 계획(읽기 전용)", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _preflightCancellation?.Cancel();
        base.OnClosed(e);
    }

    private void CloseReview_Click(object sender, RoutedEventArgs e) => Close();
}
