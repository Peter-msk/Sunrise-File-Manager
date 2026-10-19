using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>
/// Core AI Command Panel: validated file organization planning, approval preview, and audited execution.
/// Onboard AI (Ollama) connection, local Q&A/summarization, and summary exports are modularized in
/// Features/OnboardAi/AiCommandPanel.OllamaAssistant.cs.
/// </summary>
public partial class AiCommandPanel : UserControl
{
    public Func<(string? Folder, IReadOnlyList<string> Paths)>? GetContext { get; set; }
    public Func<string?>? GetOppositePaneFolder { get; set; }
    public Func<string, IReadOnlyList<string>, bool>? ContextStillMatches { get; set; }
    public Func<bool>? CanStart { get; set; }
    public Func<bool>? BeforeFileChange { get; set; }
    public Action? FileWorkCompleted { get; set; }
    public Action<string>? RevealResultFile { get; set; }
    public Action<bool>? SetBusy { get; set; }
    public event Action? CloseRequested;
    private void CloseAiPanel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { Status.Text = "작업 중에는 닫을 수 없습니다. 작업 완료 후 다시 시도하세요."; return; }
        CloseRequested?.Invoke();
    }
    public Action<string>? OllamaStatusChanged { get; set; }
    public Action<string>? SaveSummaryToActiveMemo { get; set; }

    private AiValidatedPlan? _plan;
    private CancellationTokenSource? _cancellation;
    private string? _destination;
    private string? _selectionSignature;
    private string? _lastAiAnswer;
    private (string Folder, IReadOnlyList<string> Paths)? _planContext;
    private bool _busy;
    private bool _suppressProviderPrompt;

    public AiCommandPanel()
    {
        InitializeComponent();
        HelpText.Text = AiNaturalLanguageService.Examples + "\n\n기존 고급 명령:\n" + AiLocalPlanner.Help +
            "\n\n온보드 AI (Ollama): 상단 [🤖 AI 연결] 버튼을 누르면 기능 설명을 확인하고 cmd.exe로 사용자 PC에 Ollama(https://github.com/ollama/ollama)를 자동 설치·연결할 수 있습니다.";
        QuickAction.SelectedIndex = 0;
        Loaded += async (_, _) => await RefreshOllamaBadgeAsync();
    }

    private async void OllamaConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await PromptAndConnectOllamaAsync();
    }

    private async void Provider_Changed(object sender, SelectionChangedEventArgs e) =>
        await HandleProviderSelectionChangedAsync(e);

    public void UpdateSelectionSummary(string? folder, IReadOnlyList<string> selected)
    {
        string folderName = string.IsNullOrWhiteSpace(folder) ? "폴더 없음" :
            Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;
        string sample = selected.Count == 0 ? "선택 항목 없음" :
            string.Join(", ", selected.Take(2).Select(x => Path.GetFileName(Path.TrimEndingDirectorySeparator(x)))) +
            (selected.Count > 2 ? $" 외 {selected.Count - 2:N0}개" : "");
        ScopeLabel.Text = $"현재 폴더: {folderName} · 선택 {selected.Count:N0}개 ({sample})";
        string signature = (folder ?? "") + "\n" + string.Join("\n", selected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        if (_selectionSignature is not null && !string.Equals(_selectionSignature, signature, StringComparison.OrdinalIgnoreCase))
        {
            if (_busy) _cancellation?.Cancel();
            if (_plan is not null) Status.Text = "선택 항목이 바뀌어 이전 계획을 해제했습니다. 다시 [계획 미리보기]를 누르세요.";
            _plan = null; _planContext = null; ExecuteButton.IsEnabled = false;
        }
        _selectionSignature = signature;
    }

    private void QuickAction_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CommandBox is null || QuickDescription is null) return;
        string command = (QuickAction.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        if (command is not ("" or "__natural__")) CommandBox.Text = command;
        else if (command == "__natural__") CommandBox.Clear();
        QuickDescription.Text = command == "__natural__"
            ? "로컬 규칙 엔진 또는 내 PC 온보드 AI(Ollama)로 명령을 해석합니다. 실제 파일 변경은 모두 사전 승인 후 실행됩니다."
            : command.Length == 0
                ? "선택한 파일을 미리보기로 검토한 뒤, 승인한 경우에만 이동·복사·이름 변경을 실행합니다."
                : $"선택 항목에 '{command}' 계획을 만듭니다. 대상 폴더를 지정한 뒤 [1. 계획 미리보기]를 누르세요.";
        Input_Changed(sender, e);
    }

    public void FocusCommand() => CommandBox.Focus();
    public void Cancel() { _cancellation?.Cancel(); _plan = null; _planContext = null; ExecuteButton.IsEnabled = false; }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (_busy) _cancellation?.Cancel();
        _plan = null; _planContext = null;
        if (ExecuteButton is not null) ExecuteButton.IsEnabled = false;
    }

    private void Destination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "AI 작업 대상 폴더" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        { _destination = dialog.FolderName; DestinationLabel.Text = _destination; Input_Changed(sender, e); }
    }

    private void UseOtherPane_Click(object sender, RoutedEventArgs e) => ApplyOppositePaneDestination(e);
    private void UseCurrentFolder_Click(object sender, RoutedEventArgs e) => ApplyCurrentFolderDestination(e);

    private void Busy(bool value)
    {
        _busy = value; SetBusy?.Invoke(value);
        PlanButton.IsEnabled = DestinationButton.IsEnabled = UseOtherPaneButton.IsEnabled =
            UseCurrentFolderButton.IsEnabled = CommandBox.IsEnabled = Provider.IsEnabled =
            Collision.IsEnabled = HistoryButton.IsEnabled = InspectionButton.IsEnabled =
            ScopeMode.IsEnabled = QuickAction.IsEnabled = ContentReadApproval.IsEnabled =
            OllamaConnectButton.IsEnabled = AskOllamaButton.IsEnabled = !value;
    }

    private static bool Expected(Exception e) => e is IOException or InvalidDataException or
        System.Security.Cryptography.CryptographicException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or JsonException or HttpRequestException or
        OperationCanceledException or NotSupportedException or System.Security.SecurityException;

    private async void Plan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || CanStart?.Invoke() != true || GetContext is null) return;
        var context = GetContext();
        if (context.Folder is null) { Status.Text = "현재 폴더가 없습니다."; return; }
        string quick = (QuickAction.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        string command = string.IsNullOrWhiteSpace(CommandBox.Text) && quick != "__natural__" ? quick : CommandBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(command))
        { Status.Text = "정리 방법을 선택하거나 명령을 입력하세요."; return; }

        bool natural = QuickAction.SelectedIndex == 0;
        bool useOllama = natural && Provider.SelectedIndex == 1;
        AiNaturalLanguageService.Intent? intent = null;

        try { if (natural && !useOllama) intent = AiNaturalLanguageService.Parse(command); }
        catch (ArgumentException ex) { Status.Text = ex.Message; return; }

        bool needsDestination = natural ? intent?.NeedsDestination == true :
            command is "복사" or "이동" || command.EndsWith("복사", StringComparison.Ordinal) || command.EndsWith("이동", StringComparison.Ordinal);
        if (needsDestination && _destination is null)
        {
            var dialog = new OpenFolderDialog { Title = "이동·복사할 대상 폴더를 먼저 선택하세요" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            { Status.Text = "대상 폴더를 선택해야 계획을 만들 수 있습니다."; return; }
            _destination = dialog.FolderName;
            DestinationLabel.Text = _destination;
        }
        bool deepScope = ScopeMode.SelectedIndex == 1;
        if (deepScope && !natural)
        { Status.Text = "'현재 폴더 및 하위' 범위는 자연어 명령에서만 지원합니다."; return; }
        if (deepScope && MessageBox.Show(Window.GetWindow(this),
            $"현재 폴더와 하위 폴더(최대 깊이 12, 최대 5,000개 항목 검토)에서 조건에 맞는 파일을 찾을까요?\n폴더: {context.Folder}\n원본 파일은 계획 단계에서 변경되지 않습니다.",
            "폴더 범위 확인", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

        if (useOllama)
        {
            var cfg = AiOllamaService.LoadConfig();
            using var checkSvc = new AiOllamaService(new Uri(cfg.Endpoint), cfg.Model);
            var report = await checkSvc.EnsureServerRunningViaCmdAsync();
            await RefreshOllamaBadgeAsync();
            if (!report.IsRunning)
            {
                bool connected = await PromptAndConnectOllamaAsync();
                if (!connected) return;
            }
        }

        CollisionPolicy collision = Collision.SelectedIndex == 1 ? CollisionPolicy.Rename : CollisionPolicy.Skip;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        _plan = null; _planContext = null; ExecuteButton.IsEnabled = false; Busy(true); PlanGrid.ItemsSource = null; Progress.Value = 0;
        try
        {
            Status.Text = useOllama
                ? "내 PC 온보드 AI(Ollama)로 명령 분석 및 무결성 스냅샷 검증 중입니다. 외부 서버 전송은 없습니다."
                : "선택 범위 스냅샷과 해시를 계산하는 중입니다. 파일은 변경하지 않습니다.";
            if (natural)
            {
                AiAgentOrchestrator.Proposal prepared;
                bool contentApproved = ContentReadApproval.IsChecked == true;
                if (useOllama)
                {
                    var cfg = AiOllamaService.LoadConfig();
                    using var ollama = new AiOllamaService(new Uri(cfg.Endpoint), cfg.Model);
                    try
                    {
                        intent = await ollama.InterpretAsync(command, cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (intent.NeedsDestination && _destination is null)
                            throw new ArgumentException("온보드 AI(Ollama)가 복사·이동을 제안했습니다. 대상 폴더를 지정한 뒤 다시 계획하세요.");
                        prepared = await Task.Run(() => AiAgentOrchestrator.PrepareIntent(context.Folder, context.Paths, _destination,
                            intent, deepScope, collision, cancellation.Token, contentApproved));
                    }
                    catch when (!deepScope && context.Paths.Count > 0)
                    {
                        try
                        {
                            var smartScope = await Task.Run(() => AiScope.Capture(context.Folder, context.Paths, _destination, cancellation.Token));
                            AiPlan smartPlan = await ollama.CreateSmartPlanAsync(smartScope, command, cancellation.Token);
                            var smartValidated = await Task.Run(() => AiPlanService.Validate(smartScope, smartPlan, collision, cancellation.Token));
                            _planContext = (context.Folder, context.Paths.ToArray());
                            _plan = smartValidated;
                            PlanGrid.ItemsSource = smartValidated.Rows;
                            Status.Text = $"온보드 AI(Ollama) 스마트 작업 계획 {smartValidated.Rows.Count:N0}건 생성 · 원본 변경 0건 · 실행 전 승인 필요 · 외부 호출 0회";
                            return;
                        }
                        catch
                        {
                            intent = AiNaturalLanguageService.Parse(command);
                            prepared = await Task.Run(() => AiAgentOrchestrator.PrepareIntent(context.Folder, context.Paths, _destination,
                                intent, deepScope, collision, cancellation.Token, contentApproved));
                        }
                    }
                    catch
                    {
                        intent = AiNaturalLanguageService.Parse(command);
                        prepared = await Task.Run(() => AiAgentOrchestrator.PrepareIntent(context.Folder, context.Paths, _destination,
                            intent, deepScope, collision, cancellation.Token, contentApproved));
                    }
                }
                else
                {
                    prepared = await Task.Run(() => AiAgentOrchestrator.Prepare(context.Folder, context.Paths, _destination,
                        command, deepScope, collision, cancellation.Token, contentApproved));
                }
                cancellation.Token.ThrowIfCancellationRequested();
                PlanGrid.ItemsSource = prepared.PreviewRows;
                string documentNotice = prepared.UnreadableDocuments > 0 || prepared.UnsupportedDocuments > 0
                    ? $" · 읽기 불가 문서 {prepared.UnreadableDocuments}개 · 미지원 형식 {prepared.UnsupportedDocuments}개 제외" : "";
                if (prepared.Matches.Count == 0)
                { Status.Text = $"{prepared.Intent.Description} · 일치하는 파일이 없습니다.{documentNotice} · 원본 변경 0건"; return; }
                if (prepared.SearchOnly)
                {
                    Status.Text = $"{prepared.Intent.Description} · 검색 결과 {prepared.Matches.Count:N0}개 · 원본 변경 0건 · 실행할 작업 없음 · " +
                        (useOllama ? "온보드 AI(Ollama) 로컬 분석 · 외부 호출 0회" : "외부 호출 0회") + documentNotice;
                    return;
                }
                _planContext = (context.Folder, deepScope ? context.Paths.ToArray() : prepared.Matches.ToArray());
                _plan = prepared.ValidatedPlan;
                Status.Text = $"{prepared.Intent.Description} · {prepared.PreviewRows.Count:N0}개 작업 계획 · 원본 변경 0건 · 실행 전 승인 필요 · " +
                    (useOllama ? "온보드 AI(Ollama) 로컬 분석 · 외부 호출 0회" : "외부 호출 0회") + documentNotice;
                return;
            }
            var scope = await Task.Run(() => AiScope.Capture(context.Folder, context.Paths, _destination, cancellation.Token));
            _planContext = (context.Folder, context.Paths.ToArray());
            AiPlan plan = AiLocalPlanner.Create(scope, command);
            var validated = await Task.Run(() => AiPlanService.Validate(scope, plan, collision, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            _plan = validated; PlanGrid.ItemsSource = validated.Rows;
            Status.Text = $"{validated.Rows.Count:N0}개 계획 · 원본 변경 0건 · 변경 전후를 확인하고 승인하세요.\n외부 호출 0회 · 비용 0";
        }
        catch (Exception ex) when (Expected(ex))
        { Status.Text = ex is OperationCanceledException ? "계획 생성을 취소했습니다." : "계획 거부: " + ex.Message; }
        finally { _cancellation = null; Busy(false); ExecuteButton.IsEnabled = _plan is not null; }
    }

    private async void AskOllama_Click(object sender, RoutedEventArgs e) => await RunAskOllamaAssistantAsync();
    private void SaveSummaryMemo_Click(object sender, RoutedEventArgs e) => ExportSummaryToActiveMemo();
    private void SaveSummaryMd_Click(object sender, RoutedEventArgs e) => ExportSummaryToMarkdownFile();

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _plan is null || CanStart?.Invoke() != true) return;
        var plan = _plan; var sourceContext = _planContext;
        _plan = null; _planContext = null; ExecuteButton.IsEnabled = false;
        if (sourceContext is null || ContextStillMatches?.Invoke(sourceContext.Value.Folder, sourceContext.Value.Paths) == false)
        { Status.Text = "계획 생성 이후 폴더 또는 선택이 바뀌었습니다. 다시 계획을 생성하세요."; return; }
        if (BeforeFileChange?.Invoke() == false) { Status.Text = "저장되지 않은 메모 보호를 위해 실행을 취소했습니다."; return; }
        if (MessageBox.Show(Window.GetWindow(this), $"검증된 작업 {plan.Rows.Count}건을 실행할까요?\n계획 외 경로·덮어쓰기는 허용되지 않으며 첫 오류에서 후속 작업을 멈춥니다.",
            "AI 파일 작업 승인", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        { Status.Text = "승인을 취소했습니다. 파일은 변경하지 않았습니다."; return; }
        bool recycleApproved = true;
        if (plan.Rows.Any(x => x.Action == "recycle"))
        {
            recycleApproved = MessageBox.Show(Window.GetWindow(this),
                $"휴지통 이동 {plan.Rows.Count(x => x.Action == "recycle"):N0}건이 포함되어 있습니다.\n" +
                "영구 삭제는 아니지만, 이 앱의 자동 되돌리기 대상이 아닙니다. Windows 휴지통으로 이동할까요?",
                "휴지통 이동 2차 승인", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!recycleApproved) { Status.Text = "휴지통 2차 승인을 취소했습니다. 파일은 변경하지 않았습니다."; return; }
        }
        var rows = new ObservableCollection<AiPreviewRow>(plan.Rows); PlanGrid.ItemsSource = rows;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation; Busy(true); int done = 0;
        var stepProgress = new Progress<AiPreviewRow>(row =>
        {
            if (!ReferenceEquals(_cancellation, cancellation)) return;
            rows[done] = row; done++; Progress.Value = 100.0 * done / rows.Count;
        });
        using var activity = new UiLatestProgress<AiPreviewRow>(Dispatcher, row =>
        {
            if (ReferenceEquals(_cancellation, cancellation))
                Status.Text = $"실행 {done}/{rows.Count} · {Path.GetFileName(row.Before)} · {row.Status}";
        });
        string audit = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sunrise", "FileManager", "AiAudit");
        try
        {
            var result = await Task.Run(() => AiPlanService.ExecuteAsync(plan, true, audit, stepProgress, cancellation.Token, activity, recycleApproved: recycleApproved));
            activity.Dispose();
            PlanGrid.ItemsSource = result.Rows;
            Status.Text = $"{(result.Canceled ? "취소" : result.Failed > 0 ? "부분 실패" : "완료")} · 완료 {result.Completed} · 건너뜀 {result.Skipped} · 실패 {result.Failed} · 미실행 {plan.Rows.Count - result.Completed - result.Skipped - result.Failed}";
        }
        catch (Exception ex) when (Expected(ex))
        { Status.Text = ex is OperationCanceledException ? "실행 전 취소했습니다." : "실행 중단: " + ex.Message; }
        finally { activity.Dispose(); _cancellation = null; Busy(false); FileWorkCompleted?.Invoke(); }
    }

    public async Task CheckInterruptedAsync()
    {
        try
        {
            var interrupted = await Task.Run(() => new AiJournalStore(AiJournalStore.DefaultDirectory)
                .List(20).Where(x => x.State is "interrupted" or "unavailable").ToArray());
            if (interrupted.Length > 0)
                Status.Text = $"이전 실행에서 확인이 필요한 작업 기록 {interrupted.Length}건이 있습니다. [작업 기록 / 복구]에서 파일 변경 없이 검사하세요.";
        }
        catch (Exception ex) when (AiJournalStore.IsRecordError(ex))
        { Status.Text = "작업 기록 확인 필요: " + ex.Message; }
    }

    private void Inspection_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || CanStart?.Invoke() != true || GetContext is null) return;
        var context = GetContext();
        if (context.Folder is null || context.Paths.Count == 0)
        { Status.Text = "중복 검사·내용 분류할 항목을 탐색기에서 먼저 선택하세요."; return; }
        var window = new AiInspectionWindow(context.Folder, context.Paths) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true && window.PreparedPlan is not null)
        {
            if (ContextStillMatches?.Invoke(context.Folder, context.Paths) == false)
            {
                _plan = null; _planContext = null; ExecuteButton.IsEnabled = false;
                Status.Text = "검사 창이 열려 있는 동안 탐색기 선택이 바뀌어 계획을 가져오지 않았습니다.";
                return;
            }
            _planContext = (context.Folder, context.Paths.ToArray());
            _plan = window.PreparedPlan; PlanGrid.ItemsSource = _plan.Rows; ExecuteButton.IsEnabled = true;
            Status.Text = $"내용 분류 계획 {_plan.Rows.Count}건을 가져왔습니다. 원본 변경 0건 · [승인 후 실행]으로 진행하세요.";
        }
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || CanStart?.Invoke() != true) return;
        if (BeforeFileChange?.Invoke() == false) return;
        var window = new AiHistoryWindow { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        if (window.DidChangeFiles) FileWorkCompleted?.Invoke();
    }

    private void PlanGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PlanGrid.SelectedItem is AiPreviewRow row && File.Exists(row.Before))
            RevealResultFile?.Invoke(row.Before);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    { _cancellation?.Cancel(); _plan = null; _planContext = null; ExecuteButton.IsEnabled = false; }
}
