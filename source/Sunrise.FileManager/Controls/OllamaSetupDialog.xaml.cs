using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

public partial class OllamaSetupDialog : Window
{
    private CancellationTokenSource? _installCts;
    private bool _installing;
    private bool _closed;
    private bool _initializingModels = true;
    private bool _modelChosenByUser;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<ModelRecommendation> _recommendations = Array.Empty<ModelRecommendation>();
    public OllamaUserConfig Config { get; private set; }
    public bool IsConnected { get; private set; }

    public OllamaSetupDialog(OllamaUserConfig? currentConfig = null)
    {
        InitializeComponent();
        Config = currentConfig ?? AiOllamaService.LoadConfig();
        DescriptionBlock.Text = AiOllamaService.FeatureDescription;

        foreach (var m in HardwareModelAdvisor.Catalog)
            ModelSelector.Items.Add(new ComboBoxItem { Tag = m.Tag, Content = $"{m.Tag} · {m.Label}" });
        string preferred = string.IsNullOrWhiteSpace(Config.PreferredSetupModel) ? Config.Model : Config.PreferredSetupModel;
        EnsureModelItem(preferred);
        SelectModelItem(preferred);
        _modelChosenByUser = !string.IsNullOrWhiteSpace(Config.PreferredSetupModel);
        _initializingModels = false;

        InstallLogBox.Text =
            "[온보드 AI 안내] 사용자는 온보드 AI(Ollama)를 설치할지 선택할 수 있습니다.\n" +
            "• [온보드 AI 설치 및 연결] 선택 시: cmd.exe를 통해 GitHub(https://github.com/ollama/ollama)에서 Ollama를 자동 설치하고 연결합니다.\n" +
            "• [설치 안 함] 선택 시: AI 설치 없이 윈도우 기본 폴더 탐색기 기능과 내장 오프라인 규칙 정리를 그대로 사용합니다.";

        Loaded += async (_, _) => await Task.WhenAll(RefreshStatusAsync(), LoadRecommendationsAsync());
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); };
        Closing += (_, closeArgs) =>
        {
            if (_installing)
            {
                _installCts?.Cancel();
            }
            try { Config.PreferredSetupModel = SelectedModelTag; AiOllamaService.SaveConfig(Config); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            { closeArgs.Cancel = true; MessageBox.Show(this, "AI 모델 선택을 저장하지 못했습니다. " + ex.Message, "설정 저장"); }
        };
    }

    private void EnsureModelItem(string tag)
    {
        if (!ModelSelector.Items.OfType<ComboBoxItem>().Any(x => string.Equals(x.Tag as string, tag, StringComparison.OrdinalIgnoreCase)))
            ModelSelector.Items.Add(new ComboBoxItem { Tag = tag, Content = tag + " · 기존 선택 모델" });
    }
    private void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializingModels) _modelChosenByUser = true;
        ShowRecommendation();
    }
    private void ShowRecommendation()
    {
        var selected = _recommendations.FirstOrDefault(x => x.Model.Tag == SelectedModelTag);
        if (RecommendationText is null) return;
        RecommendationText.Text = selected is null ? "기존 모델은 선택을 유지합니다. 목록에서 다른 모델도 선택할 수 있습니다." :
            $"약 {selected.Model.DownloadGb:0.0}GB 다운로드 · {selected.Reason}" +
            (selected.Fits ? "" : " · 현재 사양에는 권장하지 않습니다.");
    }
    private async Task LoadRecommendationsAsync()
    {
        try
        {
            var pc = await Task.Run(WindowsHardwareProbe.Read).WaitAsync(TimeSpan.FromSeconds(8), _lifetime.Token);
            if (_closed) return;
            HardwareText.Text = HardwareModelAdvisor.Describe(pc);
            _recommendations = HardwareModelAdvisor.Rank(pc);
            string previous = SelectedModelTag;
            string[] previousTags = ModelSelector.Items.OfType<ComboBoxItem>().Select(x => x.Tag as string ?? "").Where(x => x.Length > 0).ToArray();
            _initializingModels = true;
            ModelSelector.Items.Clear();
            int rank = 0;
            foreach (var recommendation in _recommendations)
            {
                string label = recommendation.Fits ? $"추천 {++rank} · " : "사양 확인 필요 · ";
                ModelSelector.Items.Add(new ComboBoxItem { Tag = recommendation.Model.Tag,
                    Content = label + recommendation.Model.Tag + " · " + recommendation.Model.Label });
            }
            foreach (string tag in previousTags) EnsureModelItem(tag);
            EnsureModelItem(previous);
            var first = _recommendations.FirstOrDefault(x => x.Fits);
            SelectModelItem(!Config.HasPromptedSetup && !Config.OllamaEnabled && !Config.UserAcceptedInstall && Config.Model == AiOllamaService.DefaultModel && !_modelChosenByUser && !_installing && first is not null ? first.Model.Tag : previous);
            _initializingModels = false;
            ShowRecommendation();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_closed) return;
            HardwareText.Text = "PC 사양 조회를 완료하지 못했습니다. 모델을 직접 선택할 수 있습니다. " + ex.Message;
            RecommendationText.Text = "사양을 확인할 수 없어 자동 선택을 적용하지 않았습니다.";
        }
    }

    private void SelectModelItem(string modelName)
    {
        for (int i = 0; i < ModelSelector.Items.Count; i++)
        {
            if (ModelSelector.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, modelName, StringComparison.OrdinalIgnoreCase))
            {
                ModelSelector.SelectedIndex = i;
                return;
            }
        }
    }

    private string SelectedModelTag =>
        (ModelSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? AiOllamaService.DefaultModel;

    private async Task RefreshStatusAsync()
    {
        if (_closed) return;
        try
        {
            StatusText.Text = "로컬 PC에서 온보드 AI(Ollama) 설치 및 실행 상태를 확인하는 중...";
            using var service = new AiOllamaService(new Uri(Config.Endpoint), SelectedModelTag);
            OllamaStatusReport report = await service.GetStatusAsync();
            if (_closed) return;

            var existingTags = ModelSelector.Items.OfType<ComboBoxItem>()
                .Select(x => x.Tag as string ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (string installedModel in report.InstalledModels)
            {
                if (!existingTags.Contains(installedModel))
                {
                    ModelSelector.Items.Insert(0, new ComboBoxItem
                    {
                        Content = $"{installedModel} (PC에 설치된 로컬 모델)",
                        Tag = installedModel
                    });
                    existingTags.Add(installedModel);
                }
            }
            // Status refresh must not replace the model explicitly selected by the user.

            StatusText.Text = report.StatusMessage;
            IsConnected = report.IsRunning;
            if (report.IsRunning)
            {
                InstallButton.Content = "선택 모델 준비 · 적용";
            }
            else if (report.IsInstalled)
            {
                InstallButton.Content = "서비스 시작 · 모델 연결";
            }
            else
            {
                InstallButton.Content = "선택 모델 설치 · 연결";
            }
        }
        catch (Exception ex)
        {
            if (_closed) return;
            StatusText.Text = "상태 확인 실패: " + ex.Message;
        }
    }

    private async void RefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        await RefreshStatusAsync();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        string model = SelectedModelTag;
        if (MessageBox.Show(this,
            "사용자 PC에 오픈소스 온보드 AI 'Ollama'를 설치하고 연결하시겠습니까?\n\n" +
            "• 출처: GitHub 공식 저장소 (https://github.com/ollama/ollama)\n" +
            "• 실행 도구: Windows 기본 명령 프롬프트 (cmd.exe 전용)\n" +
            "• 선택 모델: " + model + "\n\n" +
            "[예]를 누르면 자동으로 설치 및 로컬 AI 서버 연결을 시작합니다.",
            "온보드 AI (Ollama) 설치 확인",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _installing = true;
        InstallButton.IsEnabled = false;
        DeclineButton.IsEnabled = false;
        RefreshStatusButton.IsEnabled = false;
        ModelSelector.IsEnabled = false;
        InstallProgress.Visibility = Visibility.Visible;
        InstallProgress.IsIndeterminate = true;
        InstallLogBox.Clear();

        using var cts = new CancellationTokenSource();
        _installCts = cts;
        var progress = new Progress<string>(line =>
        {
            if (_closed) return;
            if (InstallLogBox.Text.Length > 32000) InstallLogBox.Text = InstallLogBox.Text[^16000..];
            InstallLogBox.AppendText(line + Environment.NewLine);
            InstallLogBox.ScrollToEnd();
            StatusText.Text = line;
        });

        try
        {
            using var service = new AiOllamaService(new Uri(Config.Endpoint), model);
            bool ok = await service.InstallAndConnectViaCmdAsync(model, progress, cts.Token);
            Config.HasPromptedSetup = true;
            Config.UserAcceptedInstall = true;
            Config.OllamaEnabled = ok;
            Config.Model = model;
            AiOllamaService.SaveConfig(Config);
            await RefreshStatusAsync();

            if (ok && !_closed)
            {
                MessageBox.Show(this,
                    $"온보드 AI (Ollama · {model}) 설치 및 로컬 연결이 완료되었습니다.\n이제 외부 서버 연결 없이 내 PC에서 AI 파일 정리와 문서 요약을 사용할 수 있습니다.",
                    "온보드 AI 연결 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
        }
        catch (OperationCanceledException)
        {
            if (_closed) return;
            InstallLogBox.AppendText("[취소됨] 사용자가 온보드 AI 설치를 취소했습니다." + Environment.NewLine);
            StatusText.Text = "설치가 취소되었습니다.";
        }
        catch (Exception ex)
        {
            if (_closed) return;
            InstallLogBox.AppendText("[오류] " + ex.Message + Environment.NewLine);
            StatusText.Text = "설치 중 오류 발생: " + ex.Message;
            MessageBox.Show(this, "온보드 AI 설치 중 오류가 발생했습니다.\n" + ex.Message,
                "설치 안내", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _installCts = null;
            _installing = false;
            InstallProgress.IsIndeterminate = false;
            InstallProgress.Visibility = Visibility.Collapsed;
            InstallButton.IsEnabled = true;
            DeclineButton.IsEnabled = true;
            RefreshStatusButton.IsEnabled = true;
            ModelSelector.IsEnabled = true;
        }
    }

    private void CloseSetup_Click(object sender, RoutedEventArgs e)
    {
        _installCts?.Cancel();
        Close();
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        if (_installing)
        {
            _installCts?.Cancel();
            return;
        }
        Config.HasPromptedSetup = true;
        Config.UserAcceptedInstall = false;
        Config.OllamaEnabled = false;
        Config.Model = SelectedModelTag;
        AiOllamaService.SaveConfig(Config);
        DialogResult = false;
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AiOllamaService.GitHubRepoUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "GitHub 열기 실패", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
