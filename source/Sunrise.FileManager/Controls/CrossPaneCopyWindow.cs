using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using Sunrise.FileManager.Services.CloudDrive;

namespace Sunrise.FileManager.Controls;

/// <summary>Modal owner-bound copy task: explicit destination, visible progress, user cancellation.</summary>
public sealed class CrossPaneCopyWindow : Window
{
    public sealed record DestinationOption(int PaneIndex, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly ComboBox _targets = new() { MinHeight = 31 };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 12, Margin = new Thickness(0, 10, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _results = new() { Height = 94, Margin = new Thickness(0, 7, 0, 0),
        ToolTip = "파일별 복사 결과" };
    private readonly Button _start = new() { Content = "복사 시작", MinWidth = 100, Padding = new Thickness(12, 7, 12, 7) };
    private readonly Button _cancel = new() { Content = "닫기", MinWidth = 90, Padding = new Thickness(12, 7, 12, 7) };
    private readonly Func<int, IProgress<BatchCopyProgress>, CancellationToken, Task<BatchCopyReport>> _copy;
    private readonly int _sourceCount;
    public BatchCopyReport? LastReport { get; private set; }
    // Shared with every parent entry point; null if no transfer was started.
    public string? CompletionMessage { get; private set; }
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private bool _cancelRequested;
    private readonly bool _move;

    public CrossPaneCopyWindow(string sourceLabel, int sourceCount, IReadOnlyList<DestinationOption> targets,
        Func<int, IProgress<BatchCopyProgress>, CancellationToken, Task<BatchCopyReport>> copy, bool move = false)
    {
        _move = move;
        Title = move ? "Sunrise 분할창 간 검증 후 안전 이동" : "Sunrise 분할창 간 파일·폴더 복사";
        Width = 540; MinWidth = 430; Height = 390; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        _results.SetResourceReference(FrameworkElement.StyleProperty, "PopupResultList");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _copy = copy; _sourceCount = sourceCount;
        if (_move) _start.Content = "이동 시작";
        var panel = new StackPanel { Margin = new Thickness(16) };
        AutomationProperties.SetName(_targets, "복사할 대상 분할창");
        AutomationProperties.SetName(_results, "파일별 전송 결과");
        AutomationProperties.SetName(_bar, "선택 항목 처리 진행률 (성공률이 아님)");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        panel.Children.Add(new TextBlock { Text = "원본", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = sourceLabel, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "복사할 대상 분할창", FontWeight = FontWeights.SemiBold });
        foreach (var option in targets) _targets.Items.Add(option);
        _targets.SelectedIndex = targets.Count > 0 ? 0 : -1;
        panel.Children.Add(_targets);
        panel.Children.Add(_bar);
        _status.Text = _move
            ? $"파일 {sourceCount}개를 복사 후 SHA-256 재검증하여 원본을 정리합니다. 폴더 이동·자동 덮어쓰기는 금지됩니다. 이동 후 되돌리기는 지원하지 않습니다."
            : $"선택 파일·폴더 {sourceCount}개를 순서대로 복사합니다. 기존 대상 파일은 덮어쓰지 않으며 원본은 삭제하지 않습니다.";
        panel.Children.Add(_status);
        panel.Children.Add(_results);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_start); buttons.Children.Add(_cancel);
        panel.Children.Add(buttons);
        Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Loaded += (_, _) => _targets.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (_busy) RequestCancel(); else Close();
        };
        _start.IsEnabled = targets.Count > 0 && sourceCount > 0;
        _start.Click += Start_Click;
        _cancel.Click += (_, _) => { if (_busy) RequestCancel(); else Close(); };
        Closing += OnClosing;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _targets.SelectedItem is not DestinationOption option) return;
        _busy = true; _cancelRequested = false; CompletionMessage = null; LastReport = null;
        _results.Items.Clear(); _bar.Value = 0;
        _start.IsEnabled = _targets.IsEnabled = false; _cancel.Content = "전송 취소";
        _cancel.IsEnabled = true;
        using var cts = new CancellationTokenSource(); _cancellation = cts;
        var progress = new Progress<BatchCopyProgress>(p =>
        {
            if (Dispatcher.HasShutdownStarted || !_busy || LastReport is not null) return;
            double next = p.Total > 0 ? Math.Clamp((p.Completed + p.FilePercent / 100.0) / p.Total * 100, 0, 100) : 0;
            _bar.Value = Math.Max(_bar.Value, next);
            if (!_cancelRequested)
                _status.Text = $"진행 중 · {Math.Min(p.Completed + 1, _sourceCount)}/{_sourceCount} · {p.CurrentFile}";
        });
        try
        {
            LastReport = await _copy(option.PaneIndex, progress, cts.Token);
            foreach (var item in LastReport.Items)
            {
                string outcome = item.Outcome switch
                {
                    BatchCopyItemOutcome.Succeeded => "[성공] ",
                    BatchCopyItemOutcome.Skipped => "[건너뜀] ",
                    BatchCopyItemOutcome.Canceled => "[취소] ",
                    _ => "[실패] "
                };
                _results.Items.Add(outcome + item.FileName + " · " + item.Status);
            }
            // 100% means all selected items received an outcome, not all succeeded.
            // A canceled batch can have unstarted entries: never call it 100% complete.
            if (LastReport.NotStarted == 0 && !LastReport.Canceled) _bar.Value = 100;
            CompletionMessage = BatchCopyUiSummary.Format(LastReport, _move);
            _status.Text = CompletionMessage;
            if (BatchCopyUiSummary.NeedsReview(LastReport))
                _status.Text += _move
                    ? "\n일부 항목은 이동이 완료되지 않았거나 원본 정리가 불확실할 수 있습니다. 양쪽 위치를 확인하세요."
                    : "\n일부 대상 파일은 이미 생성되었을 수 있습니다. 원본을 보존하고 대상 상태를 확인하세요.";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            CompletionMessage = BatchCopyUiSummary.Incomplete(_move, userCanceled: true);
            _status.Text = CompletionMessage;
        }
        catch (Exception)
        {
            // Provider exception text may contain private paths or signed URLs. Never show the raw exception.
            CompletionMessage = BatchCopyUiSummary.Incomplete(_move, userCanceled: cts.IsCancellationRequested);
            _status.Text = CompletionMessage;
        }
        finally
        {
            _busy = false; _cancellation = null; _cancel.Content = "닫기";
            _cancel.IsEnabled = true;
        }
    }

    private void RequestCancel()
    {
        if (!_busy || _cancelRequested) return;
        _cancelRequested = true;
        _cancel.IsEnabled = false;
        _cancellation?.Cancel();
        _status.Text = _move
            ? "이동 취소 처리 중 · 이미 원본이 정리된 항목이 있을 수 있습니다."
            : "복사 취소 처리 중 · 원본은 보존하며 부분 대상이 남을 수 있습니다.";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        RequestCancel();
    }
}
