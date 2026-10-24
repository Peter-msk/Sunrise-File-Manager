using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Sunrise.FileManager.Services;

// The two Shell.Application consumers share one message-pumping STA for the process lifetime.
// Per-query STA teardown disconnects shared automation RCWs during refresh/batch operations.
internal static class WindowsShellApartment
{
    private static readonly SemaphoreSlim Serial = new(1, 1);
    private static readonly Lazy<Task<Dispatcher>> Worker = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            int hr = OleInitialize(IntPtr.Zero);
            if (hr < 0) { ready.TrySetException(Marshal.GetExceptionForHR(hr)!); return; }
            try
            {
                ready.TrySetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            finally { OleUninitialize(); }
        }) { IsBackground = true, Name = "Sunrise Windows Shell STA" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return ready.Task;
    });
    internal static async Task<T> RunAsync<T>(Func<T> action, CancellationToken token)
    {
        await Serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var dispatcher = await Worker.Value.ConfigureAwait(false);
            return await dispatcher.InvokeAsync(() => { token.ThrowIfCancellationRequested(); return action(); }).Task.ConfigureAwait(false);
        }
        finally { Serial.Release(); }
    }
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
}
