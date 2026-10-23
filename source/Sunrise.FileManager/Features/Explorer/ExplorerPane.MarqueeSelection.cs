using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;
namespace Sunrise.FileManager.Controls;
public partial class ExplorerPane
{
    private bool _marqueeUpdating;
    private Point? _marqueeOrigin;
    private bool _marqueeDragging, _marqueeControl, _marqueeShift;
    private HashSet<int> _marqueeBaseline = [];
    private DispatcherTimer? _marqueeScrollTimer;
    private SelectionRectangleAdorner? _marqueeAdorner;
    private static T? VisualAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T found) return found;
            node = node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
    private static T? VisualChild<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match) return match;
            if (VisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
    // Reparenting a pane or applying a template can replace the internal viewer.
    // Resolve the live visual tree rather than keeping a detached instance.
    private ScrollViewer? GetFilesScrollViewer() => VisualChild<ScrollViewer>(FilesGrid);
    private void TryRestoreFileViewport()
    {
        if (!_restoreViewportPending || !FilesGrid.IsLoaded || FilesGrid.ItemsSource is null || GetFilesScrollViewer() is not { } scroll) return;
        FilesGrid.UpdateLayout();
        scroll.ScrollToVerticalOffset(_savedVerticalOffset);
        scroll.ScrollToHorizontalOffset(_savedHorizontalOffset);
        _restoreViewportPending = false;
    }
    private Rect FileViewport()
    {
        if (VisualChild<ScrollContentPresenter>(FilesGrid) is { ActualWidth: > 0, ActualHeight: > 0 } presenter)
        {
            Point corner = presenter.TransformToAncestor(FilesGrid).Transform(new Point());
            return new Rect(corner, new Size(presenter.ActualWidth, presenter.ActualHeight));
        }
        double header = double.IsFinite(FilesGrid.ColumnHeaderHeight) ? FilesGrid.ColumnHeaderHeight : 36;
        return new Rect(0, header, Math.Max(0, FilesGrid.ActualWidth - 6), Math.Max(0, FilesGrid.ActualHeight - header - 6));
    }
    private double RowPixels => double.IsFinite(FilesGrid.RowHeight) && FilesGrid.RowHeight > 0 ? FilesGrid.RowHeight : 30;
    private double VerticalPixels()
    {
        var scroll = GetFilesScrollViewer();
        if (scroll is null) return 0;
        bool logical = scroll.CanContentScroll && scroll.ExtentHeight <= FilesGrid.Items.Count + 1;
        return scroll.VerticalOffset * (logical ? RowPixels : 1);
    }
    private Point FileContentPoint(Point point, Rect viewport) => new(
        point.X - viewport.Left + (GetFilesScrollViewer()?.HorizontalOffset ?? 0), point.Y - viewport.Top + VerticalPixels());
    private bool BeginMarquee(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || VisualAncestor<ScrollBar>(source) is not null ||
            VisualAncestor<ButtonBase>(source) is not null || VisualAncestor<DataGridColumnHeader>(source) is not null) return false;
        var row = VisualAncestor<DataGridRow>(source);
        var cell = VisualAncestor<DataGridCell>(source);
        // A file name/icon starts an item drag. Other cells and empty body start a selection rectangle.
        if (row is not null && cell?.Column is { } column && FilesGrid.Columns.IndexOf(column) <= 1) return false;
        Rect viewport = FileViewport();
        Point point = e.GetPosition(FilesGrid);
        if (!viewport.Contains(point)) return false;
        if (BeforeFileChange?.Invoke() == false) { e.Handled = true; return true; }
        EndMarquee();
        _marqueeBaseline = FilesGrid.SelectedItems.Cast<object>().Select(x => FilesGrid.Items.IndexOf(x)).Where(x => x >= 0).ToHashSet();
        _marqueeControl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        _marqueeShift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        _marqueeOrigin = FileContentPoint(point, viewport);
        _marqueeDragging = false;
        _dragOrigin = null; _dragSourcePath = null; _dragCandidatePaths = null;
        FilesGrid.Focus();
        Mouse.Capture(FilesGrid, CaptureMode.SubTree);
        _marqueeScrollTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        _marqueeScrollTimer.Tick -= MarqueeScrollTick;
        _marqueeScrollTimer.Tick += MarqueeScrollTick;
        _marqueeScrollTimer.Start();
        e.Handled = true;
        return true;
    }
    private bool UpdateMarquee(MouseEventArgs e)
    {
        if (_marqueeOrigin is null) return false;
        if (e.LeftButton != MouseButtonState.Pressed) { EndMarquee(); return true; }
        UpdateMarqueeAt(Mouse.GetPosition(FilesGrid));
        e.Handled = true;
        return true;
    }
    private void UpdateMarqueeAt(Point pointer)
    {
        if (_marqueeOrigin is not Point origin) return;
        Rect viewport = FileViewport();
        Point clamped = new(Math.Clamp(pointer.X, viewport.Left, viewport.Right), Math.Clamp(pointer.Y, viewport.Top, viewport.Bottom));
        Point current = FileContentPoint(clamped, viewport);
        if (!_marqueeDragging && Math.Abs(current.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _marqueeDragging = true;
        _lastBlankClickTick = 0;
        Rect area = new(origin, current);
        var hits = area.Right > 0 && area.Left < FilesGrid.Columns.Where(x => x.Visibility == Visibility.Visible).Sum(x => x.ActualWidth)
            ? MarqueeSelectionPolicy.HitRows(area.Top, area.Bottom, RowPixels, FilesGrid.Items.Count) : [];
        SetMarqueeSelection(MarqueeSelectionPolicy.Select(_marqueeBaseline, hits, _marqueeControl, _marqueeShift));
        if (_marqueeAdorner is null && AdornerLayer.GetAdornerLayer(FilesGrid) is { } layer)
        {
            _marqueeAdorner = new SelectionRectangleAdorner(FilesGrid, (Brush)FindResource("Accent"));
            layer.Add(_marqueeAdorner);
        }
        if (_marqueeAdorner is not null)
        {
            Point visualOrigin = new(origin.X - (GetFilesScrollViewer()?.HorizontalOffset ?? 0) + viewport.Left, origin.Y - VerticalPixels() + viewport.Top);
            _marqueeAdorner.SetRectangle(new Rect(visualOrigin, clamped), viewport);
        }
    }
    private void SetMarqueeSelection(HashSet<int> indices)
    {
        _reloadingSelection = true;
        _marqueeUpdating = true;
        try
        {
            foreach (var item in FilesGrid.SelectedItems.Cast<object>().ToArray())
                if (!indices.Contains(FilesGrid.Items.IndexOf(item))) FilesGrid.SelectedItems.Remove(item);
            foreach (int index in indices)
                if (index >= 0 && index < FilesGrid.Items.Count && !FilesGrid.SelectedItems.Contains(FilesGrid.Items[index])) FilesGrid.SelectedItems.Add(FilesGrid.Items[index]);
        }
        finally { _reloadingSelection = false; _marqueeUpdating = false; }
        _pagedSelection.SyncVisible(FilesGrid.Items.OfType<FileEntry>().Select(x => x.FullPath),
            FilesGrid.SelectedItems.OfType<FileEntry>().Select(x => x.FullPath));
        UpdateFooter(); UpdateDetails();
        SelectionContextChanged?.Invoke(this);
    }
    private void MarqueeScrollTick(object? sender, EventArgs e)
    {
        if (_marqueeOrigin is null || Mouse.LeftButton != MouseButtonState.Pressed) { EndMarquee(); return; }
        if (!_marqueeDragging) return;
        var viewport = FileViewport(); var pointer = Mouse.GetPosition(FilesGrid); var scroll = GetFilesScrollViewer();
        if (pointer.Y < viewport.Top + 18) scroll?.LineUp();
        else if (pointer.Y > viewport.Bottom - 18) scroll?.LineDown();
        if (pointer.X < viewport.Left + 14) scroll?.LineLeft();
        else if (pointer.X > viewport.Right - 14) scroll?.LineRight();
        UpdateMarqueeAt(pointer);
    }
    private void FinishMarquee(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeOrigin is null) return;
        if (!_marqueeDragging && !_marqueeControl && !_marqueeShift) SetMarqueeSelection([]);
        EndMarquee(); e.Handled = true;
    }
    private void EndMarquee(bool restore = false)
    {
        bool hadSelection = _marqueeOrigin is not null;
        _marqueeOrigin = null; _marqueeDragging = false;
        _marqueeScrollTimer?.Stop();
        if (_marqueeAdorner is not null)
        {
            AdornerLayer.GetAdornerLayer(FilesGrid)?.Remove(_marqueeAdorner); _marqueeAdorner = null;
        }
        if (hadSelection && restore) SetMarqueeSelection(_marqueeBaseline);
        if (hadSelection && FilesGrid.IsMouseCaptureWithin) Mouse.Capture(null);
    }
    private sealed class SelectionRectangleAdorner : Adorner
    {
        private readonly Brush _fill; private readonly Pen _outline;
        private Rect _rectangle, _clip;
        public SelectionRectangleAdorner(UIElement element, Brush accent) : base(element)
        { IsHitTestVisible = false; _fill = accent.Clone(); _fill.Opacity = .15; _outline = new Pen(accent, 1); }
        public void SetRectangle(Rect rectangle, Rect clip) { _rectangle = rectangle; _clip = clip; InvalidateVisual(); }
        protected override void OnRender(DrawingContext drawing)
        { drawing.PushClip(new RectangleGeometry(_clip)); drawing.DrawRectangle(_fill, _outline, _rectangle); drawing.Pop(); }
    }
}
