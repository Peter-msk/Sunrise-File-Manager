using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

public partial class AiInspectionWindow : Window
{
    private readonly string _folder;
    private readonly string[] _paths;
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private string? _destination;
    public AiValidatedPlan? PreparedPlan { get; private set; }
    public AiInspectionWindow(string folder, IReadOnlyList<string> paths)
    {
        InitializeComponent(); _folder = folder; _paths = paths.ToArray();
        SelectionLabel.Text = $"{folder} · 명시적 선택 {paths.Count:N0}개 · 중복 결과는 검사 시점 기준이며 자동 삭제하지 않습니다.";
        var limits = AiInspectionSettings.Load(out string? warning);
        MaxFilesBox.Text = limits.MaxFiles.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MaxMiBBox.Text = (limits.MaxBytes / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        MaxCallsBox.Text = limits.MaxRemoteCallsPerDay.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (warning is not null) Status.Text = warning;
    }
    private AiWorkLimits Limits()
    {
        if (!int.TryParse(MaxFilesBox.Text, out int count) || !int.TryParse(MaxMiBBox.Text, out int mib) || mib is < 1 or > 8192 || !int.TryParse(MaxCallsBox.Text, out int calls))
            throw new ArgumentException("파일 수와 읽기 MB를 정수로 입력하세요. MB 범위는 1~8192입니다.");
        var limits = new AiWorkLimits(count, (long)mib * 1024 * 1024, calls); limits.Validate();
        AiInspectionSettings.Save(limits); return limits;
    }
    private void Busy(bool busy)
    {
        _busy = busy;
        ScanButton.IsEnabled = ClassifyButton.IsEnabled = DestinationButton.IsEnabled = MaxFilesBox.IsEnabled = MaxMiBBox.IsEnabled = MaxCallsBox.IsEnabled =
            RulesBox.IsEnabled = ContentConsent.IsEnabled = ActionSelector.IsEnabled = Recursive.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy && PreparedPlan is not null;
    }
    private void Columns(params (string Title, string Binding, double Width)[] columns)
    {
        Results.Columns.Clear();
        foreach (var column in columns) Results.Columns.Add(new DataGridTextColumn { Header = column.Title,
            Binding = new Binding(column.Binding), Width = column.Width < 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(column.Width) });
    }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        PreparedPlan = null; Results.ItemsSource = null; Errors.Text = "";
        using var cancel = new CancellationTokenSource(); _cancellation = cancel;
        try
        {
            AiWorkLimits limits = Limits(); bool recursive = Recursive.IsChecked == true; Busy(true); Progress.Value = 0;
            using var progress = new UiLatestProgress<AiInspectionProgress>(Dispatcher, p =>
            {
                if (!ReferenceEquals(_cancellation, cancel)) return;
                Progress.IsIndeterminate = p.Phase == "목록 수집";
                if (p.Candidates > 0) Progress.Value = Math.Clamp(100.0 * p.Processed / p.Candidates, 0, 100);
                Status.Text = $"{p.Phase} · 발견 {p.Discovered:N0}개 · 해시 {p.Processed:N0}/{p.Candidates:N0} · 성공 {p.Hashed:N0} · 남음 {Math.Max(0, p.Candidates - p.Processed):N0} · 읽음 {p.BytesRead:N0}바이트\n{p.Current}";
            });
            var report = await AiInspectionService.FindDuplicatesAsync(_paths, recursive, limits, progress, cancel.Token);
            progress.Dispose();
            Progress.IsIndeterminate = false;
            Columns(("그룹", "Group", 55), ("파일 경로", "Path", -1), ("크기(바이트)", "Bytes", 110), ("SHA-256", "Sha256", 180), ("동일 내용 경로 수", "Copies", 120));
            Results.ItemsSource = report.Rows; Errors.Text = string.Join("\n", report.Errors);
            Progress.Value = report.Canceled || report.Limited ? Progress.Value : 100;
            Status.Text = $"{(report.Canceled ? "취소 · 부분 결과" : report.Limited ? "상한 도달 · 부분 결과" : "검사 완료")} · 중복 {report.Groups}그룹/{report.Rows.Count}경로 · 발견 {report.Discovered} · 해시 {report.Hashed} · 링크 제외 {report.LinksSkipped} · 오류 {report.ErrorCount}\nSHA-256 결과는 검사 시점 기준입니다. 하드 링크는 실제 저장공간 회수량과 다를 수 있습니다.";
        }
        catch (Exception ex) when (Expected(ex)) { Status.Text = ex.Message; }
        finally { _cancellation = null; Progress.IsIndeterminate = false; Busy(false); }
    }
    private async void Classify_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        PreparedPlan = null; Results.ItemsSource = null; Errors.Text = "";
        using var cancel = new CancellationTokenSource(); _cancellation = cancel;
        try
        {
            if (ContentConsent.IsChecked != true) throw new InvalidOperationException("내용 읽기에 동의한 뒤 진행하세요.");
            if (_destination is null) throw new ArgumentException("분류 대상 폴더를 선택하세요.");
            AiWorkLimits limits = Limits();
            if (_paths.Length > Math.Min(1000, limits.MaxFiles) || _paths.Any(Directory.Exists))
                throw new ArgumentException("내용 분류는 현재 폴더에서 직접 선택한 파일만 지원합니다.");
            long total = 0;
            foreach (string path in _paths) { total = checked(total + new FileInfo(path).Length); if (total > limits.MaxBytes) throw new IOException("선택 파일 크기가 설정된 읽기 상한을 초과했습니다."); }
            string rules = RulesBox.Text, target = _destination, action = ActionSelector.SelectedIndex == 0 ? "copy" : "move";
            Busy(true); Status.Text = "로컬 내용 분류·원본 검증 중입니다. 외부 전송은 없습니다.";
            var result = await Task.Run(() =>
            {
                var scope = AiScope.Capture(_folder, _paths, target, cancel.Token);
                var classification = AiContentClassifier.Create(scope, rules, action, true, limits, cancel.Token);
                var validated = classification.Plan is null ? null : AiPlanService.Validate(scope, classification.Plan, CollisionPolicy.Skip, cancel.Token);
                return (classification, validated);
            });
            cancel.Token.ThrowIfCancellationRequested(); PreparedPlan = result.validated;
            Columns(("작업", "Action", 125), ("원본", "Before", -1), ("분류 / 대상 경로", "After", -1), ("판정", "Status", 230));
            Results.ItemsSource = result.classification.Rows.Concat(result.validated?.Rows ?? Array.Empty<AiPreviewRow>()).ToArray();
            Status.Text = $"분류 제안 {result.classification.Rows.Count}개 · 실행 계획 {result.validated?.Rows.Count ?? 0}개 · 파일 변경 0건\n확인 필요·미분류는 실행 대상에서 제외했습니다. 계획을 가져온 뒤 별도로 실행 승인하세요.";
        }
        catch (OperationCanceledException) { Status.Text = "분류 계획을 취소했습니다. 파일은 변경하지 않았습니다."; }
        catch (Exception ex) when (Expected(ex)) { Status.Text = ex.Message; }
        finally { _cancellation = null; Progress.IsIndeterminate = false; Busy(false); }
    }
    private static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException or System.Security.SecurityException or System.Xml.XmlException;
    private void Rules_Changed(object sender, RoutedEventArgs e) { PreparedPlan = null; if (ImportButton is not null) ImportButton.IsEnabled = false; }
    private void Destination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "분류 대상 폴더" };
        if (dialog.ShowDialog(this) == true) { _destination = dialog.FolderName; DestinationLabel.Text = _destination; Rules_Changed(sender, e); }
    }
    private void Import_Click(object sender, RoutedEventArgs e) { if (!_busy && PreparedPlan is not null) DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _cancellation?.Cancel(); PreparedPlan = null; ImportButton.IsEnabled = false; }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (_busy) { e.Cancel = true; _cancellation?.Cancel(); Status.Text = "취소 처리 완료 후 닫으세요."; } }
}
