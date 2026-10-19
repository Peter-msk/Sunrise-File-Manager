using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>
/// Feature module for AiCommandPanel: Onboard AI (Ollama) status badge probing,
/// first-run setup dialog workflow, opposite-pane destination folder helpers,
/// local document Q&A/summarization, and summary export to #memo or Markdown (.md).
/// </summary>
public partial class AiCommandPanel
{
    public async Task RefreshOllamaBadgeAsync()
    {
        try
        {
            var cfg = AiOllamaService.LoadConfig();
            using var service = new AiOllamaService(new Uri(cfg.Endpoint), cfg.Model);
            var report = await service.GetStatusAsync();
            string badge = report.IsRunning
                ? $"🟢 Ollama 연결됨 ({report.ActiveModel})"
                : report.IsInstalled
                    ? $"🟡 Ollama 설치됨 ({report.ActiveModel} · 자동 구동 대기)"
                    : "⚪ 온보드 AI 미설치";
            if (OllamaBadgeText is not null) OllamaBadgeText.Text = badge;
            OllamaStatusChanged?.Invoke(badge);
        }
        catch
        {
            if (OllamaBadgeText is not null) OllamaBadgeText.Text = "⚪ 온보드 AI 미연결";
        }
    }

    public async Task<bool> PromptAndConnectOllamaAsync()
    {
        var cfg = AiOllamaService.LoadConfig();
        var dialog = new OllamaSetupDialog(cfg) { Owner = Window.GetWindow(this) };
        bool? result = dialog.ShowDialog();
        await RefreshOllamaBadgeAsync();
        if (result == true || dialog.Config.OllamaEnabled)
        {
            _suppressProviderPrompt = true;
            try { Provider.SelectedIndex = 1; }
            finally { _suppressProviderPrompt = false; }
            Status.Text = $"온보드 AI (Ollama · {dialog.Config.Model}) 연결이 활성화되었습니다. 외부 전송 없이 내 PC에서 실행됩니다.";
            return true;
        }
        Status.Text = "온보드 AI 설치를 건너뛰었습니다. 기본 로컬 규칙 모드로 모든 탐색기·정리 기능을 사용할 수 있습니다.";
        return false;
    }

    private async Task HandleProviderSelectionChangedAsync(RoutedEventArgs e)
    {
        Input_Changed(this, e);
        if (_suppressProviderPrompt || Provider is null || Provider.SelectedIndex != 1 || !IsLoaded) return;
        var cfg = AiOllamaService.LoadConfig();
        using var service = new AiOllamaService(new Uri(cfg.Endpoint), cfg.Model);
        var status = await service.EnsureServerRunningViaCmdAsync();
        await RefreshOllamaBadgeAsync();
        if (!cfg.HasPromptedSetup || !status.IsRunning)
        {
            bool connected = await PromptAndConnectOllamaAsync();
            if (!connected)
            {
                _suppressProviderPrompt = true;
                try { Provider.SelectedIndex = 0; }
                finally { _suppressProviderPrompt = false; }
            }
        }
    }

    private void ApplyOppositePaneDestination(RoutedEventArgs e)
    {
        string? otherFolder = GetOppositePaneFolder?.Invoke();
        if (string.IsNullOrWhiteSpace(otherFolder) || !Directory.Exists(otherFolder))
        {
            Status.Text = "2~4분할 화면에서 다른 탐색기 패널이 열려 있어야 반대쪽 폴더를 가져올 수 있습니다.";
            return;
        }
        _destination = otherFolder;
        DestinationLabel.Text = _destination;
        Input_Changed(this, e);
        Status.Text = $"반대쪽 분할 탐색기의 폴더를 대상 폴더로 지정했습니다: {_destination}";
    }

    private void ApplyCurrentFolderDestination(RoutedEventArgs e)
    {
        var context = GetContext?.Invoke();
        if (context?.Folder is not string current || !Directory.Exists(current))
        {
            Status.Text = "현재 탐색기 폴더를 먼저 여세요.";
            return;
        }
        _destination = current;
        DestinationLabel.Text = _destination;
        Input_Changed(this, e);
        Status.Text = $"현재 폴더를 대상 폴더로 지정했습니다: {_destination}";
    }

    private async Task RunAskOllamaAssistantAsync()
    {
        if (_busy || CanStart?.Invoke() != true || GetContext is null) return;
        var context = GetContext();
        if (context.Folder is null) { Status.Text = "현재 폴더를 먼저 선택하세요."; return; }

        var cfg = AiOllamaService.LoadConfig();
        using var checkSvc = new AiOllamaService(new Uri(cfg.Endpoint), cfg.Model);
        var status = await checkSvc.EnsureServerRunningViaCmdAsync();
        await RefreshOllamaBadgeAsync();
        if (!status.IsRunning)
        {
            bool connected = await PromptAndConnectOllamaAsync();
            if (!connected) return;
            cfg = AiOllamaService.LoadConfig();
        }

        string question = string.IsNullOrWhiteSpace(CommandBox.Text)
            ? "선택한 파일 및 문서의 핵심 내용을 한국어로 알기 쉽게 요약해 줘."
            : CommandBox.Text.Trim();

        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        Busy(true);
        SaveSummaryMemoButton.Visibility = SaveSummaryMdButton.Visibility = Visibility.Collapsed;
        try
        {
            Status.Text = $"온보드 AI (Ollama · {status.ActiveModel})가 내 PC 내부에서 로컬 데이터를 분석하고 있습니다...";
            var sb = new StringBuilder();
            sb.AppendLine($"현재 폴더: {context.Folder}");
            sb.AppendLine($"선택 항목 수: {context.Paths.Count}");
            foreach (string path in context.Paths.Take(15))
            {
                sb.AppendLine($"- {Path.GetFileName(path)}");
                if (ContentReadApproval.IsChecked == true && File.Exists(path))
                {
                    string? docText = FilePreviewService.ReadSearchableDocumentText(path, cancellation.Token);
                    if (docText is null)
                    {
                        var preview = FilePreviewService.Read(path, false, cancellation.Token);
                        if (preview.Kind == PreviewKind.Text) docText = preview.Body;
                    }
                    if (!string.IsNullOrWhiteSpace(docText))
                    {
                        sb.AppendLine("  [본문 발췌]: " + (docText.Length > 3000 ? docText[..3000] : docText));
                    }
                }
            }

            using var ollama = new AiOllamaService(new Uri(cfg.Endpoint), status.ActiveModel);
            string answer = await ollama.AskLocalAssistantAsync(question, sb.ToString(), cancellation.Token);
            _lastAiAnswer = answer.Trim();
            Status.Text = $"[온보드 AI ({status.ActiveModel}) 로컬 응답 · 외부 전송 0건]\n{_lastAiAnswer}";
            SaveSummaryMemoButton.Visibility = SaveSummaryMdButton.Visibility =
                string.IsNullOrWhiteSpace(_lastAiAnswer) ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex) when (Expected(ex))
        {
            Status.Text = "온보드 AI 응답 오류: " + ex.Message;
        }
        finally
        {
            _cancellation = null;
            Busy(false);
        }
    }

    private void ExportSummaryToActiveMemo()
    {
        if (string.IsNullOrWhiteSpace(_lastAiAnswer)) return;
        SaveSummaryToActiveMemo?.Invoke($"[온보드 AI 요약 ({DateTime.Now:yyyy-MM-dd HH:mm})]\n{_lastAiAnswer}");
        Status.Text = "온보드 AI 요약 결과를 선택한 파일의 스마트 #memo에 저장했습니다.";
    }

    private void ExportSummaryToMarkdownFile()
    {
        if (string.IsNullOrWhiteSpace(_lastAiAnswer) || GetContext is null) return;
        var context = GetContext();
        if (context.Folder is null || !Directory.Exists(context.Folder)) return;
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"AI_요약보고서_{stamp}.md";
            string mdContent = $"# 온보드 AI (Ollama) 로컬 요약 보고서\n\n- 생성 시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n- 대상 폴더: `{context.Folder}`\n\n## 요약 내용\n\n{_lastAiAnswer}\n";
            string savedPath = FileActionsService.CreateFile(context.Folder, fileName, mdContent);
            FileWorkCompleted?.Invoke();
            RevealResultFile?.Invoke(savedPath);
            Status.Text = $"요약 보고서를 저장했습니다: {savedPath}";
        }
        catch (Exception ex)
        {
            Status.Text = "요약 파일 저장 실패: " + ex.Message;
        }
    }
}
