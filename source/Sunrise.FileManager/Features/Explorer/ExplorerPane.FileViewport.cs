using System.Windows.Threading;

namespace Sunrise.FileManager.Controls;

public partial class ExplorerPane
{
    private bool _fileViewportRefreshQueued;
    private bool _fileViewportResetHorizontalPending;

    // A reused pane is measured again after split reparenting. Refresh only on
    // load/size/layout-mode changes, never on ScrollChanged or a periodic timer.
    private void QueueFileViewportLayoutRefresh(bool resetHorizontal = false)
    {
        _fileViewportResetHorizontalPending |= resetHorizontal;
        if (_fileViewportRefreshQueued) return;
        _fileViewportRefreshQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                if (!FilesGrid.IsLoaded || FilesGrid.ActualWidth <= 0 || FilesGrid.ActualHeight <= 0) return;
                FilesGrid.ApplyTemplate();
                FilesGrid.InvalidateMeasure();
                FilesGrid.InvalidateArrange();
                FilesGrid.UpdateLayout();
                if (GetFilesScrollViewer() is not { } scroll) return;
                scroll.InvalidateScrollInfo();
                scroll.UpdateLayout();

                bool resetHorizontalNow = _fileViewportResetHorizontalPending;
                _fileViewportResetHorizontalPending = false;
                // Initial session restoration remains authoritative at startup.
                if (_restoreViewportPending) TryRestoreFileViewport();
                else if (resetHorizontalNow) scroll.ScrollToHorizontalOffset(0);
                else if (scroll.HorizontalOffset > scroll.ScrollableWidth)
                    scroll.ScrollToHorizontalOffset(scroll.ScrollableWidth);
            }
            finally { _fileViewportRefreshQueued = false; }
        }));
    }
}
