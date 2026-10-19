using System.IO;
using System.IO.Compression;
using System.Text;
using System.Buffers.Binary;

namespace Sunrise.FileManager.Services;

/// <summary>Reads only HWPX's stored preview; never renders, converts, extracts to disk, or modifies the document.</summary>
public static class HangulPreviewService
{
    public const long MaxArchiveBytes = 512L * 1024 * 1024;
    public const int MaxPreviewImageBytes = 8 * 1024 * 1024;
    public static PreviewContent? ReadStoredPreview(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (info.Length > MaxArchiveBytes)
            return new(PreviewKind.Information, "512MB를 초과해 저장된 미리보기를 읽지 않습니다. 연결 앱에서 원본을 열어 주세요.", info.Name);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        if (zip.Entries.Count > 10000) throw new InvalidDataException("문서 내부 항목 수가 미리보기 상한을 초과했습니다.");
        ZipArchiveEntry? Entry(string name) => zip.Entries.FirstOrDefault(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
        var image = Entry("Preview/PrvImage.png");
        if (image is not null && image.Length is > 8 and <= MaxPreviewImageBytes)
        {
            using var stream = image.Open();
            using var memory = new MemoryStream();
            byte[] buffer = new byte[8192]; int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (memory.Length + count > MaxPreviewImageBytes) throw new InvalidDataException("저장된 미리보기 이미지가 상한을 초과했습니다.");
                memory.Write(buffer, 0, count);
            }
            byte[] bytes = memory.ToArray();
            ReadOnlySpan<byte> png = [137, 80, 78, 71, 13, 10, 26, 10];
            int width = bytes.Length >= 24 ? BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) : 0;
            int height = bytes.Length >= 24 ? BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) : 0;
            if (bytes.AsSpan().StartsWith(png) && width is > 0 and <= 20000 && height is > 0 and <= 20000 && (long)width * height <= 64_000_000)
                return new(PreviewKind.Image, "저장된 첫 쪽 이미지", $"{info.Name} · 문서에 저장된 첫 쪽 미리보기 · 전체 페이지 렌더링 아님", bytes);
        }
        var text = Entry("Preview/PrvText.txt");
        if (text is not null && text.Length <= 1024 * 1024)
        {
            using var stream = text.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            char[] chars = new char[FilePreviewService.MaxTextCharacters + 1]; int used = 0;
            while (used < chars.Length)
            {
                token.ThrowIfCancellationRequested();
                int read = reader.Read(chars.AsSpan(used, Math.Min(4096, chars.Length - used)));
                if (read == 0) break;
                used += read;
            }
            string body = new(chars, 0, Math.Min(used, FilePreviewService.MaxTextCharacters));
            if (!string.IsNullOrWhiteSpace(body))
                return new(PreviewKind.Text, body + (used > FilePreviewService.MaxTextCharacters ? "\n… 저장된 텍스트 일부만 표시" : ""),
                    $"{info.Name} · 문서에 저장된 미리보기 텍스트 · 실제 쪽 구성과 다를 수 있음");
        }
        return null;
    }
}
