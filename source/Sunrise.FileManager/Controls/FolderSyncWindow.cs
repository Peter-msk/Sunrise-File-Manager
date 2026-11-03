using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Automation;
using Sunrise.FileManager.Services.CloudDrive;

namespace Sunrise.FileManager.Controls;

/// <summary>Explicit preview + acknowledgement before an additive synchronization.</summary>
public sealed class FolderSyncWindow : Window
{
    private sealed record PreviewRow(string Path, string Action, string Reason);
    private readonly ComboBox _targets = new() { MinHeight = 32, Margin = new Thickness(0, 4, 0, 8) };
    private readonly DataGrid _diffs = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
        GridLinesVisibility = DataGridGridLinesVisibility.None,
        EnableRowVirtualization = true, EnableColumnVirtualization = true
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 12,
        Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _analyze = new() { Content = "_차이 분석", MinWidth = 105, Padding = new Thickness(13, 7, 13, 7) };
    private readonly Button _apply = new() { Content = "확인 후 누락 항목만 _생성", MinWidth = 184,
        Padding = new Thickness(13, 7, 13, 7), IsEnabled = false };
    private readonly Button _close = new() { Content = "_닫기", MinWidth = 80, Padding = new Thickness(13, 7, 13, 7) };
    private readonly Func<int, CancellationToken, Task<FolderSyncPlan>> _preview;
    private readonly Func<int, FolderSyncPlan, IProgress<FolderSyncProgress>, CancellationToken,
        Task<FolderSyncResult>> _execute;
    private FolderSyncPlan? _plan;
    private CancellationTokenSource? _running;
    private bool _busy;
    public FolderSyncResult? LastReport { get; private set; }

    public FolderSyncWindow(IReadOnlyList<CrossPaneCopyWindow.DestinationOption> destinations,
        Func<int, CancellationToken, Task<FolderSyncPlan>> preview,
        Func<int, FolderSyncPlan, IProgress<FolderSyncProgress>, CancellationToken, Task<FolderSyncResult>> execute)
    {
        _preview = preview; _execute = execute;
        Title = "Sunrise 폴더 비교 · 안전 단방향 동기화";
        Width = 790; MinWidth = 490; Height = 580; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        _diffs.SetResourceReference(FrameworkElement.StyleProperty, "PopupResultDataGrid");
        _diffs.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "PopupCenteredColumnHeader");
        var centeredCell = (Style)Application.Current.FindResource("PopupCenteredCellText");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        var layout = new Grid { Margin = new Thickness(17) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var instructions = new TextBlock
        {
            Text = "현재 활성 분할창 → 선택한 대상 창 (단방향). 기존 파일의 덮어쓰기·삭제·이동은 하지 않습니다. " +
                   "같은 경로의 원격 파일은 크기가 같아도 내용 검증 전까지 충돌로 분류합니다.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(instructions, 0); layout.Children.Add(instructions);
        foreach (var option in destinations) _targets.Items.Add(option);
        _targets.SelectedIndex = destinations.Count == 0 ? -1 : 0;
        _targets.SelectionChanged += (_, _) => ResetPreview();
        AutomationProperties.SetName(_targets, "동기화할 대상 분할창");
        AutomationProperties.SetName(_bar, "동기화 진행률");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_analyze, "폴더 차이 읽기 전용 분석");
        AutomationProperties.SetName(_apply, "대상에 없는 파일과 폴더만 생성");
        AutomationProperties.SetName(_close, "동기화 창 닫기 또는 작업 취소 요청");
        Grid.SetRow(_targets, 1); layout.Children.Add(_targets);
        _diffs.Columns.Add(new DataGridTextColumn { Header = "경로", ElementStyle = centeredCell, Binding = new Binding("Path"),
            Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _diffs.Columns.Add(new DataGridTextColumn { Header = "분류", ElementStyle = centeredCell, Binding = new Binding("Action"),
            Width = new DataGridLength(110) });
        _diffs.Columns.Add(new DataGridTextColumn { Header = "판단 근거", ElementStyle = centeredCell, Binding = new Binding("Reason"),
            Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        System.Windows.Automation.AutomationProperties.SetName(_diffs, "폴더 차이 및 동기화 예정 내역");
        Grid.SetRow(_diffs, 2); layout.Children.Add(_diffs);
        var summary = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        summary.Children.Add(_bar);
        _status.Text = "대상 분할창을 선택하고 차이 분석을 실행하세요. 분석 단계에서는 파일을 변경하지 않습니다.";
        summary.Children.Add(_status);
        Grid.SetRow(summary, 3); layout.Children.Add(summary);
        // Wrap instead of clipping important destructive-approval commands at high DPI.
        var commands = new WrapPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        commands.Children.Add(_analyze); commands.Children.Add(_apply); commands.Children.Add(_close);
        Grid.SetRow(commands, 4); layout.Children.Add(commands);
        Content = layout;
        Loaded += (_, _) => _targets.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (_busy) _running?.Cancel(); else Close();
        };
        _analyze.Click += Analyze_Click;
        _apply.Click += Apply_Click;
        _close.Click += (_, _) => { if (_busy) _running?.Cancel(); else Close(); };
        Closing += ClosingGuard;
    }

    private void ResetPreview()
    {
        _plan = null; _apply.IsEnabled = false; _diffs.ItemsSource = null; _bar.Value = 0;
        if (!_busy) _status.Text = "폴더 위치가 바뀌었습니다. 다시 차이 분석을 실행하세요.";
    }
    private void Busy(bool active)
    {
        _busy = active;
        _targets.IsEnabled = _analyze.IsEnabled = !active;
        _apply.IsEnabled = !active && _plan?.ToCreate > 0;
        _close.Content = active ? "취소 요청" : "닫기";
    }
    private static string Decision(FolderDifferenceKind kind) => kind switch
    {
        FolderDifferenceKind.AddFile => "새 파일",
        FolderDifferenceKind.AddFolder => "새 폴더",
        FolderDifferenceKind.Unchanged => "동일/공통",
        FolderDifferenceKind.TargetOnly => "대상 전용",
        FolderDifferenceKind.Conflict => "충돌 · 보존",
        _ => "검토"
    };
    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _targets.SelectedItem is not CrossPaneCopyWindow.DestinationOption option) return;
        ResetPreview(); Busy(true);
        using var cts = new CancellationTokenSource(); _running = cts;
        try
        {
            _status.Text = "두 폴더의 하위 파일 목록을 읽고 차이를 분석하고 있습니다…";
            var result = await _preview(option.PaneIndex, cts.Token);
            _plan = result;
            _diffs.ItemsSource = result.Differences.Select(d => new PreviewRow(d.Relative,
                Decision(d.Kind), d.Note)).ToArray();
            _status.Text = $"분석 완료 · 새로 생성 {result.ToCreate}개 / 충돌 {result.Conflicts}개 / 대상 전용 {result.TargetOnly}개. " +
                           "충돌과 대상 전용 항목은 변경하지 않습니다.";
        }
        catch (OperationCanceledException) { _status.Text = "분석을 취소했습니다. 변경된 파일은 없습니다."; }
        catch (Exception) { _status.Text = "분석 실패 · 폴더 권한, 클라우드 연결 및 링크·중복 경로를 확인하세요. 변경된 파일은 없습니다."; }
        finally { _running = null; Busy(false); }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _plan is not { ToCreate: > 0 } plan ||
            _targets.SelectedItem is not CrossPaneCopyWindow.DestinationOption option) return;
        var answer = MessageBox.Show(this,
            $"대상에 없는 항목 {plan.ToCreate}개를 생성할까요?\n" +
            $"충돌 {plan.Conflicts}개와 대상 전용 {plan.TargetOnly}개는 그대로 유지합니다.\n" +
            "일부 생성 후 오류가 발생하면 부분 결과가 남을 수 있습니다. 원본 파일은 삭제하지 않습니다.",
            "Sunrise · 단방향 동기화 승인", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        Busy(true);
        using var cts = new CancellationTokenSource(); _running = cts;
        var progress = new Progress<FolderSyncProgress>(p =>
        {
            if (Dispatcher.HasShutdownStarted || !_busy) return;
            _bar.Value = p.Total == 0 ? 100 : Math.Clamp((double)p.Completed / p.Total * 100, 0, 100);
            _status.Text = $"생성 중 · {p.Completed}/{p.Total} · {p.Current}";
        });
        try
        {
            LastReport = await _execute(option.PaneIndex, plan, progress, cts.Token);
            _status.Text = $"동기화 결과 · 생성 {LastReport.Created}, 실패 {LastReport.Failed}" +
                (LastReport.Canceled ? " · 취소됨" : "") +
                ". 원본·충돌 파일은 변경하지 않았습니다. 부분 생성 결과를 확인하세요.";
            _diffs.ItemsSource = LastReport.Details.Select(s => new PreviewRow(s, "작업 결과", "")).ToArray();
            _plan = null; _apply.IsEnabled = false;
        }
        catch (OperationCanceledException) { _status.Text = "동기화를 취소했습니다. 이미 생성한 항목은 남아 있을 수 있습니다."; }
        catch (Exception) { _status.Text = "동기화를 중단했습니다. 대상 위치가 변경됐거나 권한이 없습니다. 재분석 후 시도하세요."; }
        finally { _running = null; Busy(false); }
    }
    private void ClosingGuard(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true; _running?.Cancel();
        _status.Text = "안전한 취소를 요청했습니다. 원본은 삭제하지 않습니다.";
    }
}
