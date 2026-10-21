using System.IO;
using System.Windows;
using Sunrise.FileManager.Controls;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private InstallerUpdateHandoff? _pendingInstaller;

    protected override async void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            // Preserve the legacy mutex name to prevent concurrent P11/P13 writers.
            _instanceMutex = new Mutex(true, @"Local\SunriseFileManagerCEF", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("Sunrise File Manager가 이미 실행 중입니다.",
                    "Sunrise File Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            _ownsMutex = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("중복 실행 보호를 초기화할 수 없습니다.\n" + ex.Message,
                "Sunrise File Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(FitWindowToWorkArea));
        UiScaleService.Set(SessionStore.Load(out _).UiScalePercent);
        base.OnStartup(e);
        // Control v1 gate runs before opening any file pane or restoring user documents.
        // Network failure follows the documented valid-cache -> fail-open fallback order.
        SunriseControlService.Policy policy;
        try { policy = await SunriseControlService.CheckAsync(); }
        catch (Exception) { policy = new SunriseControlService.Policy("allow", "allow", "", "", "fallback"); }
        if (policy.BlocksEntry)
        {
            MessageBox.Show(string.IsNullOrWhiteSpace(policy.Message)
                    ? (policy.Operation == "maintenance" ? "Sunrise File Manager는 현재 점검 중입니다." : "Sunrise Control에서 실행이 제한되었습니다.")
                    : policy.Message,
                "Sunrise Control", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        // Product checks are independent: one failed endpoint cannot suppress the other.
        var releaseTask = TryReleaseAsync();
        var noticeTask = TryNoticeAsync();
        await Task.WhenAll(releaseTask, noticeTask);
        var release = await releaseTask;
        var notice = await noticeTask;
        string? previousFailure = InstallerUpdateHandoff.ConsumeFailure();
        if (previousFailure is not null)
            MessageBox.Show(previousFailure, "업데이트 안내", MessageBoxButton.OK, MessageBoxImage.Information);
        bool required = policy.RequiresUpdate || (policy.Update != "block" && release?.IsNewer == true);
        if (required || SunriseReleaseService.IsUnreadNotice(notice))
        {
            var gate = new SunriseControlWindow(policy, required, release, notice, startupSnapshot: true);
            gate.ShowDialog();
            if (required || gate.MustExit || Dispatcher.HasShutdownStarted)
            {
                if (!Dispatcher.HasShutdownStarted) Shutdown();
                return;
            }
        }
        MainWindow main = new();
        MainWindow = main;
        main.Closed += (_, _) => Shutdown();
        main.Show();
        main.ApplyControlPolicy(policy);
    }

    private static async Task<SunriseReleaseService.Release?> TryReleaseAsync()
    {
        try { return await SunriseReleaseService.CheckAsync(); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
        { System.Diagnostics.Trace.TraceWarning("Release lookup: {0}", ex.GetType().Name); return null; }
    }

    private static async Task<string?> TryNoticeAsync()
    {
        try { return await SunriseReleaseService.GetNoticeAsync(); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidDataException or OperationCanceledException or System.Text.DecoderFallbackException)
        { System.Diagnostics.Trace.TraceWarning("Notice lookup: {0}", ex.GetType().Name); return null; }
    }

    internal bool RequestInstallerUpdate(InstallerUpdateHandoff prepared)
    {
        if (_pendingInstaller is not null) return false;
        if (MainWindow is MainWindow main && !main.ConfirmUpdateExit()) return false;
        _pendingInstaller = prepared;
        if (MainWindow is MainWindow workspace)
        {
            workspace.Close(); // Preserves normal memo/session/worker shutdown and Closing cancellation.
            if (!workspace.UpdateShutdownAccepted)
            {
                _pendingInstaller = null;
                return false;
            }
            return true; // main.Closed has already requested application shutdown.
        }
        Shutdown();
        return true;
    }

    internal void ExitUpdateGate()
    {
        if (MainWindow is MainWindow workspace) workspace.Close();
        else Shutdown();
    }

    private static void FitWindowToWorkArea(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || !ReferenceEquals(e.OriginalSource, window)) return;
        UiScaleService.Apply(window);
        Rect area = SystemParameters.WorkArea;
        double maxWidth = Math.Max(320, area.Width - 16), maxHeight = Math.Max(280, area.Height - 16);
        window.MinWidth = Math.Min(window.MinWidth, maxWidth);
        window.MinHeight = Math.Min(window.MinHeight, maxHeight);
        if (double.IsFinite(window.Width)) window.Width = Math.Min(window.Width, maxWidth);
        if (double.IsFinite(window.Height)) window.Height = Math.Min(window.Height, maxHeight);
        window.Left = Math.Clamp(window.Left, area.Left, Math.Max(area.Left, area.Right - window.ActualWidth));
        window.Top = Math.Clamp(window.Top, area.Top, Math.Max(area.Top, area.Bottom - window.ActualHeight));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_ownsMutex) _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            if (_pendingInstaller is not null)
            {
                try { _pendingInstaller.Launch(); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    MessageBox.Show("설치 대기 작업을 시작할 수 없습니다. 공식 설치파일로 다시 설치하십시오.\n" + ex.Message,
                        "업데이트 안내", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _pendingInstaller.Cancel();
                }
            }
        }
        finally
        {
            base.OnExit(e);
        }
    }
}
