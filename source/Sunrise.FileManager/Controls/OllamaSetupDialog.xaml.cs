using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

public partial class OllamaSetupDialog : Window
{
    private CancellationTokenSource? _installCts;
    private bool _installing;
    public OllamaUserConfig Config { get; private set; }
    public bool IsConnected { get; private set; }

    public OllamaSetupDialog(OllamaUserConfig? currentConfig = null)
    {
        InitializeComponent();
        Config = currentConfig ?? AiOllamaService.LoadConfig();
        DescriptionBlock.Text = AiOllamaService.FeatureDescription;

        SelectModelItem(Config.Model);

        InstallLogBox.Text =
            "[온보드 AI 안내] 사용자는 온보드 AI(Ollama)를 설치할지 선택할 수 있습니다.\n" +
            "• [온보드 AI 설치 및 연결] 선택 시: cmd.exe를 통해 GitHub(https://github.com/ollama/ollama)에서 Ollama를 자동 설치하고 연결합니다.\n" +
            "• [설치 안 함] 선택 시: AI 설치 없이 윈도우 기본 폴더 탐색기 기능과 내장 오프라인 규칙 정리를 그대로 사용합니다.";

        Loaded += async (_, _) => await RefreshStatusAsync();
        Closing += (_, _) =>
        {
            if (_installing)
            {
                _installCts?.Cancel();
            }
        };
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
        try
        {
            StatusText.Text = "로컬 PC에서 온보드 AI(Ollama) 설치 및 실행 상태를 확인하는 중...";
            using var service = new AiOllamaService(new Uri(Config.Endpoint), SelectedModelTag);
            OllamaStatusReport report = await service.GetStatusAsync();

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
            SelectModelItem(report.ActiveModel);

            StatusText.Text = report.StatusMessage;
            IsConnected = report.IsRunning;
            if (report.IsRunning)
            {
                InstallButton.Content = "✔ 온보드 AI 연결 완료 (선택 모델 준비 및 적용)";
            }
            else if (report.IsInstalled)
            {
                InstallButton.Content = "▶ 설치된 Ollama 서비스 시작 및 모델 연결 (cmd)";
            }
            else
            {
                InstallButton.Content = "✔ 온보드 AI (Ollama) 자동 설치 및 연결 (cmd)";
            }
        }
        catch (Exception ex)
        {
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

            if (ok)
            {
                MessageBox.Show(this,
                    $"온보드 AI (Ollama · {model}) 설치 및 로컬 연결이 완료되었습니다.\n이제 외부 서버 연결 없이 내 PC에서 AI 파일 정리와 문서 요약을 사용할 수 있습니다.",
                    "온보드 AI 연결 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
        }
        catch (OperationCanceledException)
        {
            InstallLogBox.AppendText("[취소됨] 사용자가 온보드 AI 설치를 취소했습니다." + Environment.NewLine);
            StatusText.Text = "설치가 취소되었습니다.";
        }
        catch (Exception ex)
        {
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
