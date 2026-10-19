using System.IO;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Sunrise.FileManager.Services;

/// <summary>
/// Renders one PDF page at a time with Windows' native PDF engine.
/// Never writes rendered pages to disk, starts a browser, or changes file permissions.
/// </summary>
public static class WindowsPdfRenderer
{
    public sealed record Result(BitmapSource Image, int Page, int PageCount, string Detail);
    private const int MaxPixelWidth = 1500;
    private const int MaxPixelHeight = 2100;

    public static async Task<Result> RenderAsync(string path, int requestedPage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        FileInfo source = new(path);
        if (!source.Exists) throw new FileNotFoundException("PDF 파일을 찾을 수 없습니다.", path);
        if (source.Length > FilePreviewService.MaxPdfBytes)
            throw new InvalidOperationException("35MB 이하 PDF만 미리보기를 지원합니다.");

        // WinRT StorageFile opens the original PDF read-only; no temporary export.
        StorageFile file = await StorageFile.GetFileFromPathAsync(source.FullName);
        token.ThrowIfCancellationRequested();
        PdfDocument document = await PdfDocument.LoadFromFileAsync(file);
        token.ThrowIfCancellationRequested();
        int count = checked((int)document.PageCount);
        if (count < 1) throw new InvalidDataException("페이지가 없는 PDF 파일입니다.");
        int current = Math.Clamp(requestedPage, 1, count);
        using PdfPage pdfPage = document.GetPage((uint)(current - 1));
        double width = Math.Max(1, pdfPage.Size.Width);
        double height = Math.Max(1, pdfPage.Size.Height);
        double scale = Math.Min((double)MaxPixelWidth / width, (double)MaxPixelHeight / height);
        uint renderWidth = (uint)Math.Max(1, Math.Round(width * scale));
        uint renderHeight = (uint)Math.Max(1, Math.Round(height * scale));
        var options = new PdfPageRenderOptions { DestinationWidth = renderWidth, DestinationHeight = renderHeight };
        using var output = new InMemoryRandomAccessStream();
        await pdfPage.RenderToStreamAsync(output, options);
        token.ThrowIfCancellationRequested();
        output.Seek(0);
        using Stream imageStream = output.AsStreamForRead();
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = imageStream;
        image.EndInit();
        image.Freeze();
        token.ThrowIfCancellationRequested();
        return new(image, current, count, $"{source.Name} · PDF 원본 페이지 렌더링 · {renderWidth}×{renderHeight}px");
    }
}
