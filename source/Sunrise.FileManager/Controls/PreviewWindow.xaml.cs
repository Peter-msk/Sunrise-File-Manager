using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

public partial class PreviewWindow : Window
{
    private readonly string _path;
    private CancellationTokenSource? _pageCancellation;
    private int _requestId;
    private int _page = 1;
    private int _totalPages = 1;
    private bool _closing;
    private bool _pdfTextFallback;
    private enum FitMode { Actual, Width, Page }
    private FitMode _fitMode = FitMode.Width;
    private bool _changingZoomProgrammatically;
    private bool IsPdf => Path.GetExtension(_path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public PreviewWindow(string path)
    {
        InitializeComponent();
        // XAML slider initialization can fire ValueChanged before content is loaded.
        _fitMode = FitMode.Width;
        _path = path;
        DocumentName.Text = Path.GetFileName(path);
        bool hangul = Path.GetExtension(path).Equals(".hwp", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".hwpx", StringComparison.OrdinalIgnoreCase);
        HangulNotice.Visibility = hangul ? Visibility.Visible : Visibility.Collapsed;
        if (Path.GetExtension(path).Equals(".hwp", StringComparison.OrdinalIgnoreCase))
            HangulNotice.Text = "HWP의 내부 페이지 렌더링은 지원하지 않습니다. 연결 앱에서 원본을 열어 주세요.";
        Loaded += (_, _) => _ = LoadPageAsync(1);
    }

    private async Task LoadPageAsync(int requestedPage)
    {
        _pageCancellation?.Cancel();
        var current = new CancellationTokenSource();
        _pageCancellation = current;
        int request = ++_requestId;
        PreviousButton.IsEnabled = NextButton.IsEnabled = false;
        TextFallbackButton.Visibility = IsPdf ? Visibility.Visible : Visibility.Collapsed;
        TextFallbackButton.Content = _pdfTextFallback ? "PDF 페이지 보기" : "PDF 텍스트 보기";
        PageText.Visibility = Visibility.Visible;
        PageImage.Visibility = Visibility.Collapsed;
        PageImage.Source = null;
        PageText.Text = "파일을 읽는 중...";
        try
        {
            // Render PDFs as one true page; the older text view remains an explicit fallback.
            if (IsPdf && !_pdfTextFallback)
            {
                WindowsPdfRenderer.Result rendered = await WindowsPdfRenderer.RenderAsync(_path, requestedPage, current.Token);
                if (_closing || current.IsCancellationRequested || request != _requestId) return;
                _page = rendered.Page;
                _totalPages = rendered.PageCount;
                DocumentDetail.Text = rendered.Detail;
                PageCounter.Text = $"{_page:N0} / {_totalPages:N0}";
                PageImage.Source = rendered.Image;
                PageImage.Visibility = Visibility.Visible;
                PageText.Visibility = Visibility.Collapsed;
                ApplyCurrentZoom();
            PageScroll.ScrollToTop();
                return;
            }
            var result = await Task.Run(() => PagedPreviewService.Read(_path, requestedPage, current.Token), current.Token);
            if (_closing || current.IsCancellationRequested || request != _requestId) return;
            _page = result.Page;
            _totalPages = result.PageCount;
            DocumentDetail.Text = result.Detail;
            PageCounter.Text = $"{_page:N0} / {_totalPages:N0}";
            if (result.Kind == PreviewKind.Image)
            {
                var bitmap = await Task.Run(() => DecodeImage(_path, current.Token, result.ImageBytes), current.Token);
                if (_closing || current.IsCancellationRequested || request != _requestId) return;
                PageImage.Source = bitmap;
                PageImage.Visibility = Visibility.Visible;
                PageText.Visibility = Visibility.Collapsed;
            }
            else PageText.Text = result.Body;
            ApplyCurrentZoom();
            PageScroll.ScrollToTop();
        }
        catch (OperationCanceledException) { /* Newer page request owns the window. */ }
        catch (Exception exception)
        {
            if (!_closing && request == _requestId && !current.IsCancellationRequested)
            {
                DocumentDetail.Text = "미리보기 실패 (원본 파일은 변경되지 않았습니다)";
                PageText.Text = IsPdf && !_pdfTextFallback
                    ? $"PDF 원본 페이지를 렌더링하지 못했습니다. 'PDF 텍스트 보기'를 선택할 수 있습니다.\n{exception.Message}"
                    : $"파일을 표시할 수 없습니다: {exception.Message}";
                _page = _totalPages = 1;
                PageCounter.Text = "1 / 1";
            }
        }
        finally
        {
            if (!_closing && request == _requestId)
            {
                PreviousButton.IsEnabled = _page > 1;
                NextButton.IsEnabled = _page < _totalPages;
            }
            if (ReferenceEquals(_pageCancellation, current)) _pageCancellation = null;
            current.Dispose();
        }
    }

    private static BitmapSource DecodeImage(string path, CancellationToken cancellation, byte[]? imageBytes = null)
    {
        cancellation.ThrowIfCancellationRequested();
        using Stream stream = imageBytes is null ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : new MemoryStream(imageBytes, writable: false);
        return ImageThumbnailService.DecodeBounded(stream, 1600, 2100, cancellation);
    }

    private void TextFallback_Click(object sender, RoutedEventArgs e)
    {
        _pdfTextFallback = !_pdfTextFallback;
        _ = LoadPageAsync(_page);
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_page > 1) _ = LoadPageAsync(_page - 1);
    }
    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_page < _totalPages) _ = LoadPageAsync(_page + 1);
    }
    private double PageContentWidth => PageImage.Visibility == Visibility.Visible && PageImage.Source is not null
        ? Math.Max(1, PageImage.Source.Width) : 700d;
    private double PageContentHeight => PageImage.Visibility == Visibility.Visible && PageImage.Source is not null
        ? Math.Max(1, PageImage.Source.Height) : 900d;

    private void ApplyCurrentZoom()
    {
        if (PageText is null || PageImage is null || ZoomLabel is null || PageSurface is null || PageScroll is null) return;
        if (_fitMode != FitMode.Actual)
        {
            double viewportWidth = PageScroll.ViewportWidth > 0 ? PageScroll.ViewportWidth : PageScroll.ActualWidth;
            double viewportHeight = PageScroll.ViewportHeight > 0 ? PageScroll.ViewportHeight : PageScroll.ActualHeight;
            double widthFactor = Math.Max(0.1, (viewportWidth - 100) / PageContentWidth);
            double heightFactor = Math.Max(0.1, (viewportHeight - 100) / PageContentHeight);
            double fitFactor = _fitMode == FitMode.Page ? Math.Min(widthFactor, heightFactor) : widthFactor;
            _changingZoomProgrammatically = true;
            ZoomSlider.Value = Math.Clamp(fitFactor * 100, ZoomSlider.Minimum, ZoomSlider.Maximum);
            _changingZoomProgrammatically = false;
        }
        double factor = ZoomSlider.Value / 100d;
        PageText.FontSize = 14 * factor;
        PageImage.Width = PageContentWidth * factor;
        PageSurface.Width = PageContentWidth * factor + 68;
        ZoomLabel.Text = $"{ZoomSlider.Value:F0}%";
    }

    private void Zoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_changingZoomProgrammatically) _fitMode = FitMode.Actual;
        ApplyCurrentZoomForManualChange();
    }

    private void ApplyCurrentZoomForManualChange()
    {
        if (PageText is null || PageImage is null || PageSurface is null || ZoomLabel is null) return;
        double factor = ZoomSlider.Value / 100d;
        PageText.FontSize = 14 * factor;
        PageImage.Width = PageContentWidth * factor;
        PageSurface.Width = PageContentWidth * factor + 68;
        ZoomLabel.Text = $"{ZoomSlider.Value:F0}%";
    }
    private void SetManualZoom(double percent)
    {
        _fitMode = FitMode.Actual;
        ZoomSlider.Value = Math.Clamp(percent, ZoomSlider.Minimum, ZoomSlider.Maximum);
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetManualZoom(ZoomSlider.Value + 10);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetManualZoom(ZoomSlider.Value - 10);
    private void ActualSize_Click(object sender, RoutedEventArgs e) => SetManualZoom(100);
    private void FitWidth_Click(object sender, RoutedEventArgs e) { _fitMode = FitMode.Width; ApplyCurrentZoom(); }
    private void FitPage_Click(object sender, RoutedEventArgs e) { _fitMode = FitMode.Page; ApplyCurrentZoom(); }
    private void PageScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitMode != FitMode.Actual) ApplyCurrentZoom();
    }
    private void PageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SetManualZoom(ZoomSlider.Value + (e.Delta > 0 ? 10 : -10));
            e.Handled = true;
        }
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key is Key.Add or Key.OemPlus) SetManualZoom(ZoomSlider.Value + 10);
            else if (e.Key is Key.Subtract or Key.OemMinus) SetManualZoom(ZoomSlider.Value - 10);
            else if (e.Key is Key.D0 or Key.NumPad0) SetManualZoom(100);
            else if (e.Key is Key.D1 or Key.NumPad1) { _fitMode = FitMode.Width; ApplyCurrentZoom(); }
            else if (e.Key is Key.D2 or Key.NumPad2) { _fitMode = FitMode.Page; ApplyCurrentZoom(); }
            else return;
            e.Handled = true;
        }
        else if (e.Key == Key.PageDown && _page < _totalPages) { _ = LoadPageAsync(_page + 1); e.Handled = true; }
        else if (e.Key == Key.PageUp && _page > 1) { _ = LoadPageAsync(_page - 1); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
    private void OpenOriginal_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        { MessageBox.Show(this, ex.Message, "연결 앱에서 열기 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _pageCancellation?.Cancel();
        ++_requestId;
    }
}
