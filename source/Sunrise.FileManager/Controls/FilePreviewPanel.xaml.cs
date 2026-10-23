using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>Independent, full-height rightmost Explorer-style preview column.</summary>
public partial class FilePreviewPanel : UserControl
{
    private FileEntry? _selected;
    private bool _previewVisible;
    private CancellationTokenSource? _previewCancellation;
    private int _previewGeneration;
    private int _page = 1;
    private int _pageTotal = 1;
    private string? _previewPath;
    private bool _fitWidth = true;
    private double _zoom = 1d;
    public event Action? CloseRequested;
    public event Action? PreferencesChanged;
    public bool FitWidthPreference => _fitWidth;
    public double ZoomPreference => _zoom;
    public void RestorePreferences(bool fitWidth, double zoom) { _fitWidth = fitWidth; _zoom = Math.Clamp(zoom, .1, 4); }
    public string? CurrentFilePath => _selected?.FullPath;

    public FilePreviewPanel()
    {
        InitializeComponent();
        PreviousPage.IsEnabled = NextPage.IsEnabled = false;
    }

    public void SetPreviewVisible(bool visible)
    {
        if (_previewVisible == visible) return;
        _previewVisible = visible;
        if (visible) BeginPreview(_selected, 1);
        else CancelPreview();
    }

    public void SelectFile(FileEntry? entry)
    {
        if (string.Equals(_selected?.FullPath, entry?.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            _selected = entry;
            return;
        }
        _selected = entry;
        if (_previewVisible) BeginPreview(entry, 1);
    }

    private void ClosePreviewButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void CancelPreview()
    {
        ++_previewGeneration;
        _previewCancellation?.Cancel();
        _previewCancellation = null;
        PreviewImage.Source = null;
    }
    public void DisposePending() => CancelPreview();

    private void BeginPreview(FileEntry? entry, int requestedPage)
    {
        CancelPreview();
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Visible;
        PreviewText.Text = entry is null ? "파일을 선택하세요." : entry.IsDirectory ? "폴더 미리보기는 지원하지 않습니다." : "불러오는 중…";
        PreviewInfo.Text = entry?.Name ?? string.Empty;
        PageCount.Text = string.Empty;
        PreviousPage.IsEnabled = NextPage.IsEnabled = false;
        if (!_previewVisible || entry is null || entry.IsDirectory) return;
        _previewPath = entry.FullPath;
        _page = requestedPage;
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        int generation = _previewGeneration;
        _ = LoadPreviewAsync(entry.FullPath, requestedPage, cancellation, generation);
    }

    private async Task LoadPreviewAsync(string path, int requestedPage, CancellationTokenSource cancellation, int generation)
    {
        try
        {
            bool pdf = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
            if (pdf)
            {
                try
                {
                    var rendered = await WindowsPdfRenderer.RenderAsync(path, requestedPage, cancellation.Token);
                    if (!IsCurrentPreview(path, cancellation, generation)) return;
                    _page = rendered.Page; _pageTotal = rendered.PageCount;
                    PreviewImage.Source = rendered.Image;
                    PreviewImage.Visibility = Visibility.Visible;
                    PreviewText.Visibility = Visibility.Collapsed;
                    PreviewInfo.Text = "PDF 원본 페이지";
                    UpdatePreviewPages();
                    ApplyZoom();
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
                {
                    // Text view is an explicit non-rendered fallback for unsupported/locked PDFs.
                    System.Diagnostics.Debug.WriteLine("PDF render fallback: " + ex.Message);
                }
            }
            PreviewPage page = await Task.Run(() => PagedPreviewService.Read(path, requestedPage, cancellation.Token), cancellation.Token);
            if (!IsCurrentPreview(path, cancellation, generation)) return;
            _page = page.Page; _pageTotal = page.PageCount;
            PreviewInfo.Text = page.Detail;
            if (page.Kind == PreviewKind.Image)
            {
                var bitmap = await Task.Run(() => DecodeImage(path, page.ImageBytes, cancellation.Token), cancellation.Token);
                if (!IsCurrentPreview(path, cancellation, generation)) return;
                PreviewImage.Source = bitmap;
                PreviewImage.Visibility = Visibility.Visible;
                PreviewText.Visibility = Visibility.Collapsed;
            }
            else PreviewText.Text = page.Body;
            UpdatePreviewPages();
            ApplyZoom();
        }
        catch (OperationCanceledException) { /* A newer selection owns the inspector. */ }
        catch (Exception ex)
        {
            if (!IsCurrentPreview(path, cancellation, generation)) return;
            PreviewText.Text = "미리보기를 표시할 수 없습니다. 원본은 변경되지 않았습니다.\n" + ex.Message;
            PreviewInfo.Text = "미리보기 실패";
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            cancellation.Dispose();
        }
    }
    private bool IsCurrentPreview(string path, CancellationTokenSource cancellation, int generation) =>
        _previewVisible && generation == _previewGeneration && !cancellation.IsCancellationRequested &&
        string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase);

    private static BitmapSource DecodeImage(string path, byte[]? bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using Stream stream = bytes is null ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : new MemoryStream(bytes);
        return ImageThumbnailService.DecodeBounded(stream, 1000, 1500, token);
    }
    private void UpdatePreviewPages()
    {
        PageCount.Text = $"{_page:N0} / {_pageTotal:N0}";
        PreviousPage.IsEnabled = _page > 1;
        NextPage.IsEnabled = _page < _pageTotal;
    }
    private void PreviousPage_Click(object sender, RoutedEventArgs e) { if (_page > 1) BeginPreview(_selected, _page - 1); }
    private void NextPage_Click(object sender, RoutedEventArgs e) { if (_page < _pageTotal) BeginPreview(_selected, _page + 1); }

    private void ApplyZoom()
    {
        if (PreviewImage is null || PreviewText is null || PreviewScroll is null || ZoomLabel is null) return;
        if (_fitWidth)
        {
            if (PreviewImage.Visibility == Visibility.Visible && PreviewImage.Source is not null)
            {
                double viewport = PreviewScroll.ViewportWidth > 0 ? PreviewScroll.ViewportWidth : PreviewScroll.ActualWidth;
                _zoom = Math.Clamp((viewport - 32) / Math.Max(1, PreviewImage.Source.Width), 0.1, 4.0);
            }
            else _zoom = 1d;  // Text wraps at readable 100% instead of shrinking its font to fit.
        }
        if (PreviewImage.Source is not null) PreviewImage.Width = PreviewImage.Source.Width * _zoom;
        PreviewText.FontSize = 12 * _zoom;
        PreviewText.Width = _fitWidth ? Math.Max(140, PreviewScroll.ActualWidth - 30) : 650 * _zoom;
        ZoomLabel.Text = $"{_zoom * 100:F0}%";
    }
    private void SetManualZoom(double zoom)
    {
        _fitWidth = false;
        _zoom = Math.Clamp(zoom, .1, 4);
        ApplyZoom();
        PreferencesChanged?.Invoke();
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetManualZoom(_zoom + .1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetManualZoom(_zoom - .1);
    private void FitWidth_Click(object sender, RoutedEventArgs e) { _fitWidth = true; ApplyZoom(); PreferencesChanged?.Invoke(); }
    private void PreviewScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitWidth) ApplyZoom();
    }
    private void PreviewScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        SetManualZoom(_zoom + (e.Delta > 0 ? .1 : -.1));
        e.Handled = true;
    }
    private void PreviewPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key is Key.Add or Key.OemPlus) SetManualZoom(_zoom + .1);
            else if (e.Key is Key.Subtract or Key.OemMinus) SetManualZoom(_zoom - .1);
            else if (e.Key is Key.D0 or Key.NumPad0) SetManualZoom(1);
            else if (e.Key is Key.D1 or Key.NumPad1) { _fitWidth = true; ApplyZoom(); PreferencesChanged?.Invoke(); }
            else return;
            e.Handled = true;
        }
        else if (e.Key == Key.PageDown && _page < _pageTotal) { BeginPreview(_selected, _page + 1); e.Handled = true; }
        else if (e.Key == Key.PageUp && _page > 1) { BeginPreview(_selected, _page - 1); e.Handled = true; }
    }
    private void OpenLargePreview_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selected.IsDirectory) return;
        new PreviewWindow(_selected.FullPath) { Owner = Window.GetWindow(this) }.Show();
    }
}
