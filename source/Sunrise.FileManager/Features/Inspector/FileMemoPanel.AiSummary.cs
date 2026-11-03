using System.IO;
using System.Windows;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>Memo-only local AI summarization and append operations.</summary>
public partial class FileMemoPanel
{
    private async Task RunAiMemoSummaryAsync()
    {
        if (_selected is not { IsDirectory: false } entry || !File.Exists(entry.FullPath)) return;
        string path = entry.FullPath;
        var cfg = AiOllamaService.LoadConfig();
        using var ollama = new AiOllamaService(new Uri(cfg.Endpoint), cfg.Model);
        var status = await ollama.EnsureServerRunningViaCmdAsync();
        if (!status.IsRunning)
        {
            if (RequestOllamaSetup is not null)
            {
                bool connected = await RequestOllamaSetup();
                if (!connected) return;
                cfg = AiOllamaService.LoadConfig();
            }
            else
            {
                MessageBox.Show(Window.GetWindow(this), "온보드 AI(Ollama)가 실행 중이지 않습니다. 상단 [🤖 AI 연결] 버튼을 눌러 연결하세요.",
                    "온보드 AI 연결 필요", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        _memoCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _memoCts = cts;
        try
        {
            AiMemoButton.IsEnabled = false;
            MemoStatus.Text = "온보드 AI(Ollama)로 문서 내용을 요약하고 있습니다...";
            string? docText = await Task.Run(() =>
            {
                string? extracted = FilePreviewService.ReadSearchableDocumentText(path, cts.Token);
                if (extracted is not null) return extracted;
                var preview = FilePreviewService.Read(path, false, cts.Token);
                return preview.Kind == PreviewKind.Text ? preview.Body : null;
            }, cts.Token);

            string context = string.IsNullOrWhiteSpace(docText)
                ? $"파일명: {entry.Name}, 유형: {entry.TypeName}, 크기: {entry.SizeDisplay}, 수정일: {entry.ModifiedDisplay}"
                : $"파일명: {entry.Name}\n본문 내용:\n{docText}";

            using var activeOllama = new AiOllamaService(new Uri(cfg.Endpoint), cfg.Model);
            string summary = await activeOllama.AskLocalAssistantAsync("이 파일의 핵심 내용을 3~5줄로 간결하게 요약해 줘.", context, cts.Token);
            if (!string.Equals(_selected?.FullPath, path, StringComparison.OrdinalIgnoreCase)) return;

            string prefix = string.IsNullOrWhiteSpace(MemoEditor.Text) ? "" : MemoEditor.Text.TrimEnd() + "\n\n";
            MemoEditor.Text = prefix + $"[온보드 AI 요약 ({DateTime.Now:yyyy-MM-dd HH:mm})]\n{summary.Trim()}";
            MemoStatus.Text = "AI 요약이 메모 편집기에 추가되었습니다. [메모 저장]을 눌러 보존하세요.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MemoStatus.Text = "AI 요약 실패: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_memoCts, cts)) _memoCts = null;
            AiMemoButton.IsEnabled = _selected is { IsDirectory: false };
        }
    }

    public void AppendMemoAndSave(string textToAppend)
    {
        if (_selected is not { IsDirectory: false } entry || !File.Exists(entry.FullPath)) return;
        string prefix = string.IsNullOrWhiteSpace(MemoEditor.Text) ? "" : MemoEditor.Text.TrimEnd() + "\n\n";
        MemoEditor.Text = prefix + textToAppend;
        SaveMemo();
    }
}
