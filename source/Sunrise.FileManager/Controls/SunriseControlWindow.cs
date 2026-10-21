using System.Diagnostics;
using System.Text;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>Central status, product-specific notices and user-approved verified installer update.</summary>
public sealed class SunriseControlWindow : Window
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox _details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(12), FontSize = 13, AcceptsReturn = true };
    private readonly Button _download = new() { Content = "업데이트 다운로드", MinWidth = 130, IsEnabled = false, Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _refresh = new() { Content = "업데이트 확인", MinWidth = 115 };
    private readonly Button _notice = new() { Content = "공지사항 새로고침", MinWidth = 132, Margin = new Thickness(6, 0, 0, 0) };
    private SunriseReleaseService.Release? _release;
    private readonly SunriseControlService.Policy _policy;
    private readonly bool _required;
    private bool _installHandoffStarted;

    public SunriseControlWindow(SunriseControlService.Policy policy, bool required = false)
    {
        _policy = policy;
        _required = required;
        Title = "Sunrise File Manager 1.2.0 · 공지사항 및 업데이트";
        Width = 780; Height = 590; MinWidth = 590; MinHeight = 390;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = "Sunrise File Manager  ·  공식 버전 1.2.0", FontSize = 19, FontWeight = FontWeights.SemiBold };
        Grid.SetRow(heading, 0); layout.Children.Add(heading);
        _status.Text = _required ? "Sunrise Control 정책에 따라 업데이트가 필요합니다. 업데이트를 완료하기 전에는 실행할 수 없습니다."
            : $"Sunrise Control: {policy.Operation} / 업데이트: {policy.Update} (출처: {policy.Source})";
        Grid.SetRow(_status, 1); layout.Children.Add(_status);
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        _refresh.Click += async (_, _) => await RefreshReleaseAsync();
        _notice.Click += async (_, _) => await RefreshNoticeAsync();
        _download.Click += async (_, _) => await DownloadAsync();
        buttons.Children.Add(_refresh); buttons.Children.Add(_notice); buttons.Children.Add(_download);
        Grid.SetRow(buttons, 2); layout.Children.Add(buttons);
        _details.Text = "공지사항을 불러오는 중입니다…";
        Grid.SetRow(_details, 3); layout.Children.Add(_details);
        var close = new Button { Content = _required ? "프로그램 종료" : "닫기", MinWidth = 110,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 4); layout.Children.Add(close);
        Content = layout;
        Loaded += async (_, _) => { await RefreshNoticeAsync(); await RefreshReleaseAsync(); };
    }

    private async Task RefreshNoticeAsync()
    {
        _notice.IsEnabled = false;
        try { _details.Text = await SunriseReleaseService.GetNoticeAsync(); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        { _details.Text = "공지사항을 불러올 수 없습니다. 인터넷 연결을 확인하십시오.\n" + ex.Message; }
        finally { _notice.IsEnabled = true; }
    }

    private async Task RefreshReleaseAsync()
    {
        _refresh.IsEnabled = false;
        _download.IsEnabled = false;
        try
        {
            _release = await SunriseReleaseService.CheckAsync();
            if (_policy.Update == "block") _status.Text = "중앙 정책에서 업데이트를 일시 중지했습니다.";
            else if (_release.IsNewer)
                _status.Text = $"새 버전 {_release.Version} 확인됨. " +
                    (_release.CanDownload ? "검증된 설치파일을 다운로드할 수 있습니다." : "설치파일 검증 정보가 아직 게시되지 않았습니다.");
            else _status.Text = $"최신 버전 사용 중: {SunriseControlService.Version} (서버: {_release.Version})";
            _download.IsEnabled = _policy.Update != "block" && _release.CanDownload &&
                (!_required || !SunriseControlService.VersionIsOlder(_release.Version, _policy.MinimumVersion));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
        { _status.Text = "업데이트 서버에 연결하지 못했습니다: " + ex.Message; }
        finally { _refresh.IsEnabled = true; }
    }

    /// <summary>Stage a local launch request while this process is alive; wait for its exit before Setup starts.</summary>
    private static void LaunchSetupAfterExit(string installerPath)
    {
        string fullPath = Path.GetFullPath(installerPath);
        if (!File.Exists(fullPath) || !Path.IsPathFullyQualified(fullPath) ||
            !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("설치파일 경로를 확인할 수 없습니다.");

        // This path has already been verified by DownloadAsync (HTTPS + exact SHA-256).
        // PowerShell runs a FIXED local command, never code or commands from the server.
        string quotedPath = fullPath.Replace("'", "''", StringComparison.Ordinal);
        int currentPid = Environment.ProcessId;
        string command =
            "$ErrorActionPreference='Stop'; " +
            "try { $old=[System.Diagnostics.Process]::GetProcessById(" + currentPid + "); " +
            "if (-not $old.WaitForExit(60000)) { exit 12 } " +
            "} catch [System.ArgumentException] { } " +
            "Start-Process -FilePath '" + quotedPath + "'";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string host = Path.Combine(systemDir, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(host)) throw new FileNotFoundException("Windows 업데이트 실행 호스트를 찾을 수 없습니다.", host);
        var psi = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encoded);
        using var helper = Process.Start(psi) ?? throw new IOException("설치 대기 프로세스를 시작하지 못했습니다.");
    }

    private async Task DownloadAsync()
    {
        if (_release is null || !_release.CanDownload || _policy.Update == "block" ||
            (_required && SunriseControlService.VersionIsOlder(_release.Version, _policy.MinimumVersion))) return;
        if (MessageBox.Show(this, $"Sunrise File Manager {_release.Version} 설치파일을 다운로드하겠습니까?\n다운로드 후 SHA-256 무결성을 검증합니다.",
            "업데이트 다운로드", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_installHandoffStarted) return;
        _download.IsEnabled = false;
        _refresh.IsEnabled = false;
        try
        {
            // Recheck the central decision at the action boundary; the dialog may
            // have stayed open after an administrator changed the group policy.
            var currentPolicy = await SunriseControlService.CheckAsync();
            if (currentPolicy.BlocksEntry || currentPolicy.Update == "block" ||
                (currentPolicy.Update == "required" &&
                 SunriseControlService.VersionIsOlder(_release.Version, currentPolicy.MinimumVersion)))
            {
                _status.Text = "Sunrise Control 정책에 따라 업데이트를 진행할 수 없습니다.";
                return;
            }
            _status.Text = "설치파일 다운로드·무결성 검사 진행 중…";
            var progress = new Progress<long>(n => _status.Text = $"다운로드: {n / 1024d / 1024d:F1} / {_release.FileSize / 1024d / 1024d:F1} MB");
            string path = await SunriseReleaseService.DownloadAsync(_release, progress);
            _status.Text = "SHA-256 검증 완료. 설치 실행 여부를 선택하십시오.";
            if (MessageBox.Show(this, "다운로드와 SHA-256 검증이 완료되었습니다.\n설치 프로그램을 실행하고 File Manager를 종료하시겠습니까?",
                "업데이트 설치", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                // The installer MUST start only after the WPF process releases its AppMutex
                // and all program files. The local OS helper has no network access and does
                // not modify files; Inno Setup owns the actual installation/repair.
                currentPolicy = await SunriseControlService.CheckAsync();
                if (currentPolicy.BlocksEntry || currentPolicy.Update == "block" ||
                    (currentPolicy.Update == "required" &&
                     SunriseControlService.VersionIsOlder(_release.Version, currentPolicy.MinimumVersion)))
                {
                    _status.Text = "Sunrise Control 정책이 변경되어 설치를 중단했습니다.";
                    return;
                }
                if (Application.Current.MainWindow is MainWindow main && !main.CanExitForUpdate())
                {
                    _status.Text = "진행 중인 파일 작업을 마친 뒤 업데이트를 다시 실행하십시오.";
                    MessageBox.Show(this, "파일 작업이 진행 중이거나 메모 변경사항이 저장되지 않았습니다.\n작업을 마치고 다시 시도하십시오.",
                        "업데이트 대기", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                LaunchSetupAfterExit(path);
                _installHandoffStarted = true;
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            _status.Text = "업데이트 실패: " + ex.Message;
            MessageBox.Show(this, "업데이트가 완료되지 않았습니다. 기존 프로그램과 사용자 파일은 변경되지 않았습니다.\n" + ex.Message,
                "업데이트 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _refresh.IsEnabled = !_installHandoffStarted; _download.IsEnabled = !_installHandoffStarted && _release?.CanDownload == true &&
            _policy.Update != "block" && (!_required || !SunriseControlService.VersionIsOlder(_release.Version, _policy.MinimumVersion)); }
    }
}
