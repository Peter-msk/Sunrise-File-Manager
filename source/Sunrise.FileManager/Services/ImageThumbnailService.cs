using System.IO;
using System.Windows.Media.Imaging;

namespace Sunrise.FileManager.Services;

/// <summary>Only loads bounded thumbnails for realized file rows, in-memory and read-only.</summary>
public static class ImageThumbnailService
{
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };

    public static bool CanPreview(string path) => Extensions.Contains(Path.GetExtension(path));

    public static async Task<BitmapSource?> LoadAsync(string path, CancellationToken token)
    {
        if (!CanPreview(path)) return null;
        await Slots.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > FilePreviewService.MaxImageBytes) return null;
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return DecodeBounded(file, 100, 100, token);
            }, token);
        }
        finally { Slots.Release(); }
    }
    // The caller owns the seekable stream. Both preview and thumbnails share the same pixel budget.
    public static BitmapSource DecodeBounded(Stream stream, int maxWidth, int maxHeight, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames.FirstOrDefault() ?? throw new InvalidDataException("이미지 프레임이 없습니다.");
        int width = frame.PixelWidth, height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > 128_000_000) throw new InvalidDataException("이미지 픽셀 수가 미리보기 상한을 초과했습니다.");
        double scale = Math.Min(1, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        stream.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = Math.Max(1, (int)(width * scale));
        bitmap.DecodePixelHeight = Math.Max(1, (int)(height * scale));
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        token.ThrowIfCancellationRequested();
        return bitmap;
    }

}
