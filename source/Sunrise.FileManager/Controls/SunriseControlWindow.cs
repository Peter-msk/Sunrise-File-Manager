using System.Diagnostics;
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

    public SunriseControlWindow(SunriseControlService.Policy policy, bool required = false)
    {
        _policy = policy;
        _required = required;
        Title = "Sunrise File Manager 1.0.0 · 공지사항 및 업데이트";
        Width = 780; Height = 590; MinWidth = 590; MinHeight = 390;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        Foreground = new SolidColorBrush(Color.FromRgb(25, 46, 68));
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = "Sunrise File Manager  ·  공식 버전 1.0.0", FontSize = 19, FontWeight = FontWeights.SemiBold };
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

    private async Task DownloadAsync()
    {
        if (_release is null || !_release.CanDownload || _policy.Update == "block" ||
            (_required && SunriseControlService.VersionIsOlder(_release.Version, _policy.MinimumVersion))) return;
        if (MessageBox.Show(this, $"Sunrise File Manager {_release.Version} 설치파일을 다운로드하겠습니까?\n다운로드 후 SHA-256 무결성을 검증합니다.",
            "업데이트 다운로드", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _download.IsEnabled = false;
        _refresh.IsEnabled = false;
        try
        {
            _status.Text = "설치파일 다운로드·무결성 검사 진행 중…";
            var progress = new Progress<long>(n => _status.Text = $"다운로드: {n / 1024d / 1024d:F1} / {_release.FileSize / 1024d / 1024d:F1} MB");
            string path = await SunriseReleaseService.DownloadAsync(_release, progress);
            _status.Text = "SHA-256 검증 완료. 설치 실행 여부를 선택하십시오.";
            if (MessageBox.Show(this, "다운로드와 SHA-256 검증이 완료되었습니다.\n설치 프로그램을 실행하고 File Manager를 종료하시겠습니까?",
                "업데이트 설치", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            _status.Text = "업데이트 실패: " + ex.Message;
            MessageBox.Show(this, "업데이트가 완료되지 않았습니다. 기존 프로그램과 사용자 파일은 변경되지 않았습니다.\n" + ex.Message,
                "업데이트 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _refresh.IsEnabled = true; _download.IsEnabled = _release?.CanDownload == true &&
            _policy.Update != "block" && (!_required || !SunriseControlService.VersionIsOlder(_release.Version, _policy.MinimumVersion)); }
    }
}
