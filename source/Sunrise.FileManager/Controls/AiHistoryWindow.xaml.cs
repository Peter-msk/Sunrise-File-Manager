using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

public partial class AiHistoryWindow : Window
{
    private readonly AiJournalStore _store = new(AiJournalStore.DefaultDirectory);
    private AiUndoPlan? _undo;
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    public bool DidChangeFiles { get; private set; }
    public AiHistoryWindow() { InitializeComponent(); Loaded += async (_, _) => await ReloadAsync(); }
    private void Busy(bool busy)
    {
        _busy = busy;
        Jobs.IsEnabled = ReloadButton.IsEnabled = InspectButton.IsEnabled = PreviewButton.IsEnabled = !busy;
        UndoButton.IsEnabled = !busy && _undo?.EligibleCount > 0;
    }
    private async Task ReloadAsync()
    {
        _undo = null; Busy(true);
        try
        {
            var jobs = await Task.Run(() => _store.List());
            Jobs.ItemsSource = jobs;
            if (jobs.Count > 0) Jobs.SelectedIndex = 0;
            Status.Text = jobs.Count == 0 ? "저장된 작업 기록이 없습니다. 이전 P13의 개수 요약만으로는 되돌릴 수 없습니다."
                : $"최근 {jobs.Count}개 기록 · 선택 후 중단 상태 검사 또는 되돌리기 미리보기를 진행하세요.";
        }
        catch (Exception ex) when (AiJournalStore.IsRecordError(ex)) { Status.Text = ex.Message; }
        finally { Busy(false); }
    }
    private void Jobs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { _undo = null; if (UndoButton is not null) UndoButton.IsEnabled = false; if (Rows is not null) Rows.ItemsSource = null; }
    private async void Reload_Click(object sender, RoutedEventArgs e) { if (!_busy) await ReloadAsync(); }
    private async void Inspect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Jobs.SelectedItem is not AiJournalSummary job) return;
        _undo = null; using var cancel = new CancellationTokenSource(); _cancellation = cancel; Busy(true);
        try
        {
            var snapshot = await Task.Run(() => AiRecoveryService.InspectInterrupted(_store, job.Id, cancel.Token));
            Rows.ItemsSource = snapshot.Items.Select(x => new AiPreviewRow(x.Item.Operation, x.Item.Source ?? "새 폴더",
                x.Item.Operation == "recycle" ? "Windows 휴지통" : x.Item.Destination, AiJournalSummary.Describe(x.State))).ToArray();
            Status.Text = "파일 변경 없이 현재 상태를 검사했습니다. '확인 필요' 항목은 자동 실행·복구하지 않습니다.";
        }
        catch (OperationCanceledException) { Status.Text = "상태 검사를 취소했습니다."; }
        catch (Exception ex) when (AiJournalStore.IsRecordError(ex)) { Status.Text = "기록 확인 중단: " + ex.Message; }
        finally { _cancellation = null; Busy(false); }
    }
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Jobs.SelectedItem is not AiJournalSummary job) return;
        _undo = null; using var cancel = new CancellationTokenSource(); _cancellation = cancel; Busy(true);
        try
        {
            _undo = await Task.Run(() => AiRecoveryService.PrepareUndo(_store, job.Id, cancel.Token));
            Rows.ItemsSource = _undo.Rows;
            Status.Text = $"되돌리기 가능 {_undo.EligibleCount}개 · 검사된 결과 {_undo.Rows.Count}개 · 파일 변경 0건";
        }
        catch (OperationCanceledException) { Status.Text = "되돌리기 검증을 취소했습니다."; }
        catch (Exception ex) when (AiJournalStore.IsRecordError(ex)) { Status.Text = ex.Message; }
        finally { _cancellation = null; Busy(false); }
    }
    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _undo is null || _undo.EligibleCount == 0) return;
        var plan = _undo; _undo = null; UndoButton.IsEnabled = false;
        if (MessageBox.Show(this, $"검증된 {plan.EligibleCount}개 항목을 표시된 원래 경로로 되돌릴까요?\n기존 파일을 덮어쓰지 않으며 실패 시 후속 작업을 멈춥니다.",
            "되돌리기 실행 승인", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        using var cancel = new CancellationTokenSource(); _cancellation = cancel; Busy(true); int completed = 0;
        var progress = new Progress<AiPreviewRow>(row =>
        {
            if (!ReferenceEquals(_cancellation, cancel)) return;
            completed++; Progress.Value = 100.0 * completed / plan.EligibleCount;
            Status.Text = $"처리 {completed}/{plan.EligibleCount} · {row.Before} · {row.Status}";
        });
        try
        {
            // A post-write journal error is ambiguous; refresh the explorer even if the call throws.
            DidChangeFiles = true;
            var result = await Task.Run(() => AiRecoveryService.UndoAsync(_store, plan, true, progress, cancel.Token));
            Rows.ItemsSource = result.Rows;
            Status.Text = $"{(result.Canceled ? "취소" : result.Failed > 0 ? "부분 실패" : "되돌리기 완료")} · 완료 {result.Completed} · 실패 {result.Failed} · 미실행 {plan.EligibleCount-result.Completed-result.Failed}";
        }
        catch (OperationCanceledException) { Status.Text = "되돌리기를 취소했습니다. 기록을 다시 조회하세요."; }
        catch (Exception ex) when (AiJournalStore.IsRecordError(ex) || ex is InvalidOperationException) { Status.Text = "되돌리기 중단: " + ex.Message; }
        finally { _cancellation = null; Busy(false); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    { _cancellation?.Cancel(); _undo = null; UndoButton.IsEnabled = false; }
    private void Window_Closing(object? sender, CancelEventArgs e)
    { if (_busy) { e.Cancel = true; _cancellation?.Cancel(); Status.Text = "취소 처리 완료 후 창을 닫으세요."; } }
}
