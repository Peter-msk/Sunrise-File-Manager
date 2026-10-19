using System.Windows.Threading;

namespace Sunrise.FileManager.Services;

/// <summary>Latest activity only, at most ten UI refreshes/second and no per-buffer dispatcher queue.</summary>
public sealed class UiLatestProgress<T> : IProgress<T>, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Action<T> _render;
    private readonly object _gate = new();
    private T? _latest;
    private bool _pending;
    private bool _disposed;
    public UiLatestProgress(Dispatcher dispatcher, Action<T> render)
    {
        _render = render;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, Tick, dispatcher);
        _timer.Start();
    }
    public void Report(T value)
    {
        lock (_gate) { if (_disposed) return; _latest = value; _pending = true; }
    }
    private void Tick(object? sender, EventArgs e)
    {
        T value;
        lock (_gate)
        {
            if (_disposed || !_pending) return;
            value = _latest!; _latest = default; _pending = false;
        }
        _render(value);
    }
    public void Dispose()
    {
        _timer.Stop(); _timer.Tick -= Tick;
        lock (_gate) { _disposed = true; _pending = false; _latest = default; }
    }
}
