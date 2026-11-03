using System.IO;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Sunrise.FileManager.Services;

/// <summary>Uses installed Windows OCR language resources only; never downloads a model.</summary>
public static class WindowsOcrService
{
    public static async Task<string> ReadAsync(string path, int firstPage, int lastPage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ReparsePointPolicy.IsBlocked(path)) throw new IOException("연결된 파일은 OCR에서 제외됩니다.");
        if (new FileInfo(path).Length > FilePreviewService.MaxPdfBytes) throw new IOException("OCR은 35MB 이하 파일을 지원합니다.");
        var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? throw new IOException("Windows에 사용할 수 있는 OCR 언어가 없습니다. 추가 프로그램이나 언어 파일은 자동 설치하지 않습니다.");
        if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            using var input = await file.OpenReadAsync();
            return await Recognize(input, engine, token);
        }
        if (firstPage < 1 || lastPage < firstPage || lastPage - firstPage >= 20) throw new ArgumentException("PDF OCR은 한 번에 최대 20쪽을 지정하세요.");
        var result = new System.Text.StringBuilder();
        for (int page = firstPage; page <= lastPage; page++)
        {
            token.ThrowIfCancellationRequested();
            var rendered = await WindowsPdfRenderer.RenderAsync(path, page, token);
            if (page > rendered.PageCount) throw new ArgumentException("지정한 쪽이 PDF 전체 쪽수를 초과합니다.");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendered.Image));
            using var memory = new MemoryStream(); encoder.Save(memory); memory.Position = 0;
            using var input = memory.AsRandomAccessStream();
            result.AppendLine($"[쪽 {page}]").AppendLine(await Recognize(input, engine, token)).AppendLine();
        }
        return result.ToString();
    }
    private static async Task<string> Recognize(IRandomAccessStream input, OcrEngine engine, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input);
        if ((long)decoder.PixelWidth * decoder.PixelHeight > 80_000_000) throw new IOException("이미지 크기 제한 8천만 화소를 초과합니다.");
        double scale = Math.Min(1, (double)OcrEngine.MaxImageDimension / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform { ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale), ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale) };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        token.ThrowIfCancellationRequested();
        var result = await engine.RecognizeAsync(bitmap);
        token.ThrowIfCancellationRequested();
        return result.Text;
    }
}
