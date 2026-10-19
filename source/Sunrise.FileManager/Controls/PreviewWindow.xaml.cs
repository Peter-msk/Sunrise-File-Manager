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
    private bool IsPdf => Path.GetExtension(_path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public PreviewWindow(string path)
    {
        InitializeComponent();
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
                PageImage.Width = 700 * ZoomSlider.Value / 100d;
                PageImage.Visibility = Visibility.Visible;
                PageText.Visibility = Visibility.Collapsed;
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
                PageImage.Width = 700 * ZoomSlider.Value / 100d;
                PageImage.Visibility = Visibility.Visible;
                PageText.Visibility = Visibility.Collapsed;
            }
            else PageText.Text = result.Body;
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
    private void Zoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PageText is null || PageImage is null || ZoomLabel is null || PageSurface is null) return;
        double factor = e.NewValue / 100d;
        PageText.FontSize = 14 * factor;
        PageImage.Width = 700 * factor;
        PageSurface.Width = 700 * factor + 68;
        ZoomLabel.Text = $"{e.NewValue:F0}%";
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.PageDown && _page < _totalPages) { _ = LoadPageAsync(_page + 1); e.Handled = true; }
        else if (e.Key == Key.PageUp && _page > 1) { _ = LoadPageAsync(_page - 1); e.Handled = true; }
        else if (e.Key == Key.Escape) Close();
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
