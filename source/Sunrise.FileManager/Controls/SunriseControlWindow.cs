using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>Product notice/update surface. New releases offer update or application exit.</summary>
public sealed class SunriseControlWindow : Window
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox _details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(12), FontSize = 13, AcceptsReturn = true };
    private readonly Button _download = new() { Content = "업데이트", MinWidth = 130, IsEnabled = false, Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _refresh = new() { Content = "업데이트 확인", MinWidth = 115 };
    private readonly Button _notice = new() { Content = "공지사항 새로고침", MinWidth = 132, Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _close = new() { MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
    private readonly CancellationTokenSource _lifetime = new();
    private SunriseReleaseService.Release? _release;
    private SunriseControlService.Policy _policy;
    private bool _required;
    private bool _busy;
    private bool _closed;
    private bool _installRequested;
    public bool MustExit => _required;

    public SunriseControlWindow(SunriseControlService.Policy policy, bool required = false,
        SunriseReleaseService.Release? release = null, string? notice = null, bool startupSnapshot = false)
    {
        _policy = policy;
        _required = required;
        _release = release;
        Title = $"Sunrise File Manager {SunriseControlService.Version} · 공지사항 및 업데이트";
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
        var heading = new TextBlock { Text = $"Sunrise File Manager  ·  현재 버전 {SunriseControlService.Version}",
            FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(heading, 0); layout.Children.Add(heading);
        Grid.SetRow(_status, 1); layout.Children.Add(_status);
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        _refresh.Click += async (_, _) => await RefreshReleaseAsync();
        _notice.Click += async (_, _) => await RefreshNoticeAsync();
        _download.Click += async (_, _) => await DownloadAsync();
        buttons.Children.Add(_refresh); buttons.Children.Add(_notice); buttons.Children.Add(_download);
        Grid.SetRow(buttons, 2); layout.Children.Add(buttons);
        _details.Text = notice ?? "공지사항을 불러올 수 없습니다. 새로고침으로 다시 확인할 수 있습니다.";
        Grid.SetRow(_details, 3); layout.Children.Add(_details);
        _close.Click += (_, _) => Close();
        Grid.SetRow(_close, 4); layout.Children.Add(_close);
        Content = layout;
        RenderReleaseStatus();
        Loaded += async (_, _) =>
        {
            if (notice is not null) SunriseReleaseService.MarkNoticeRead(notice);
            if (!startupSnapshot) await Task.WhenAll(RefreshNoticeAsync(), RefreshReleaseAsync());
        };
        Closing += WindowClosing;
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            if (_required && !_installRequested && Application.Current is App app) app.ExitUpdateGate();
        };
    }

    private bool CanInstall => !_policy.BlocksEntry && _policy.Update != "block" && _release?.CanDownload == true &&
        !SunriseControlService.VersionIsOlder(_release.Version, _policy.MinimumVersion);

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_installRequested && _required && Application.Current.MainWindow is MainWindow main && !main.ConfirmUpdateExit())
            e.Cancel = true;
    }

    private void UpdateButtons()
    {
        if (_closed) return;
        _download.IsEnabled = !_busy && CanInstall;
        _refresh.IsEnabled = !_busy;
        _close.Content = _required ? "프로그램 종료" : "닫기";
    }

    private void RenderReleaseStatus()
    {
        _required |= _policy.RequiresUpdate || (_policy.Update != "block" && _release?.IsNewer == true) || _policy.BlocksEntry;
        if (_policy.BlocksEntry) _status.Text = string.IsNullOrWhiteSpace(_policy.Message)
            ? "중앙 운영 정책에 따라 실행이 제한되었습니다. 프로그램을 종료하십시오." : _policy.Message;
        else if (_policy.Update == "block") _status.Text = "업데이트가 일시 중지되어 있습니다.";
        else if (_release?.IsNewer == true)
            _status.Text = $"새 버전 {_release.Version}이 있습니다. " + (CanInstall
                ? "업데이트를 진행하거나 프로그램을 종료하십시오."
                : "설치파일 배포정보를 확인할 수 없습니다. 새로고침하거나 종료하십시오.");
        else if (_required) _status.Text = "업데이트가 필요합니다. 배포정보를 새로 확인하거나 프로그램을 종료하십시오.";
        else _status.Text = _release is null ? "업데이트 정보를 불러오지 못했습니다. 다시 확인할 수 있습니다."
            : $"최신 버전을 사용 중입니다: {SunriseControlService.Version}";
        UpdateButtons();
    }

    private async Task RefreshNoticeAsync()
    {
        if (_closed || !_notice.IsEnabled) return;
        _notice.IsEnabled = false;
        try
        {
            string body = await SunriseReleaseService.GetNoticeAsync(_lifetime.Token);
            if (_closed) return;
            _details.Text = body;
            SunriseReleaseService.MarkNoticeRead(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or DecoderFallbackException)
        { if (!_closed) _details.Text = "공지사항을 불러올 수 없습니다. 인터넷 연결을 확인한 다음 다시 시도하십시오."; }
        finally { if (!_closed) _notice.IsEnabled = true; }
    }

    private async Task RefreshReleaseAsync()
    {
        if (_busy || _closed) return;
        _busy = true;
        UpdateButtons();
        try
        {
            _policy = await SunriseControlService.CheckAsync(_lifetime.Token);
            _release = await SunriseReleaseService.CheckAsync(_lifetime.Token);
            if (!_closed) RenderReleaseStatus();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or JsonException)
        {
            _release = null;
            if (!_closed) { RenderReleaseStatus(); _status.Text += " 배포정보를 다시 확인하십시오."; }
        }
        finally { _busy = false; UpdateButtons(); }
    }

    private async Task DownloadAsync()
    {
        if (_busy || _closed || !CanInstall || _release is not { } release) return;
        if (MessageBox.Show(this, $"Sunrise File Manager {release.Version}(으)로 업데이트하겠습니까?\n" +
            "설치파일 다운로드와 검증 후 현재 프로그램을 종료하고 설치를 시작합니다.\n진행 중인 파일 작업을 먼저 완료하십시오.",
            "업데이트", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _busy = true;
        UpdateButtons();
        InstallerUpdateHandoff? unusedDownload = null;
        try
        {
            _policy = await SunriseControlService.CheckAsync(_lifetime.Token);
            if (!CanInstall) { RenderReleaseStatus(); return; }
            _status.Text = "설치파일 다운로드·검증 진행 중…";
            var progress = new Progress<long>(n =>
            {
                if (!_closed && !_installRequested) _status.Text = $"다운로드: {n / 1024d / 1024d:F1} / {release.FileSize / 1024d / 1024d:F1} MB";
            });
            string path = await SunriseReleaseService.DownloadAsync(release, progress, _lifetime.Token);
            unusedDownload = InstallerUpdateHandoff.Prepare(path, release);
            _lifetime.Token.ThrowIfCancellationRequested();
            _policy = await SunriseControlService.CheckAsync(_lifetime.Token);
            if (!CanInstall) { RenderReleaseStatus(); return; }
            _status.Text = "다운로드 완료. 프로그램 종료 후 설치를 시작합니다…";
            _installRequested = true;
            if (Application.Current is not App app || !app.RequestInstallerUpdate(unusedDownload))
            {
                _installRequested = false;
                _status.Text = "파일 작업·메모를 확인한 다음 업데이트를 다시 선택하십시오.";
            }
            else unusedDownload = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException or Win32Exception)
        {
            _installRequested = false;
            if (!_closed)
            {
                _status.Text = ex is InvalidDataException ? "설치파일 배포정보 또는 검증 결과가 일치하지 않아 중단했습니다."
                    : "업데이트를 완료하지 못했습니다. 연결 상태를 확인한 다음 다시 시도하십시오.";
                System.Diagnostics.Trace.TraceWarning("Update failed: {0}", ex.GetType().Name);
            }
        }
        finally
        {
            unusedDownload?.Cancel();
            _busy = false;
            UpdateButtons();
        }
    }
}
