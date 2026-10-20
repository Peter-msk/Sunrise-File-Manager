using System.IO;
using System.Windows;
using Sunrise.FileManager.Controls;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
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
        if (policy.RequiresUpdate)
        {
            // Only update or exit are offered. The product owns its download/hash/installer contract.
            new SunriseControlWindow(policy, required: true).ShowDialog();
            Shutdown();
            return;
        }
        MainWindow main = new();
        MainWindow = main;
        main.Show();
        main.ApplyControlPolicy(policy);
    }

    private static void FitWindowToWorkArea(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || !ReferenceEquals(e.OriginalSource, window)) return;
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
        }
        finally
        {
            base.OnExit(e);
        }
    }
}
