using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Sunrise.FileManager.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Sunrise.FileManager.Services;

public enum PreviewKind { Text, Image, Information }

public sealed record PreviewContent(PreviewKind Kind, string Body, string Detail, byte[]? ImageBytes = null);

public static class FilePreviewService
{
    public const int MaxTextCharacters = 16_000;
    public const long MaxImageBytes = 25L * 1024 * 1024;
    public const long MaxPdfBytes = 35L * 1024 * 1024;
    public const long MaxOfficeBytes = 30L * 1024 * 1024;
    private const long MaxXmlEntryBytes = 3L * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".log", ".ini", ".yaml", ".yml",
        ".cs", ".xaml", ".py", ".js", ".ts", ".html", ".css", ".sql", ".cmd", ".bat", ".sh", ".toml", ".cfg"
    };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };

    public static PreviewContent Read(string path, bool directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (directory)
            return new(PreviewKind.Information, "폴더를 선택했습니다. 파일을 선택하면 내용이 표시됩니다.", path);

        var info = new FileInfo(path);
        string extension = info.Extension.ToLowerInvariant();
        string detail = $"{info.Name}  ·  {info.Length:N0} 바이트  ·  {info.LastWriteTime:yyyy-MM-dd HH:mm}";
        if (TextExtensions.Contains(extension)) return ReadText(path, detail, token);
        if (ImageExtensions.Contains(extension))
            return info.Length <= MaxImageBytes
                ? new(PreviewKind.Image, string.Empty, detail)
                : new(PreviewKind.Information, "이미지는 25MB 이하인 경우에만 미리봅니다.", detail);
        if (extension == ".zip")
            return ReadZipInventory(path, detail, token);
        if (extension == ".pdf")
            return info.Length <= MaxPdfBytes
                ? ReadPdf(path, detail, token)
                : new(PreviewKind.Information, "PDF는 35MB 이하인 경우에만 텍스트 미리보기를 제공합니다.", detail);
        if (extension == ".hwp")
            return new(PreviewKind.Information, "HWP 원본의 내부 페이지 렌더링은 지원하지 않습니다. 상단의 '연결 앱에서 열기'로 한글 프로그램에서 열어 주세요. 파일을 변환하거나 수정하지 않습니다.", detail);
        if (extension == ".hwpx")
        {
            var stored = HangulPreviewService.ReadStoredPreview(path, token);
            if (stored is not null) return stored;
            if (info.Length > MaxOfficeBytes)
                return new(PreviewKind.Information, "이 HWPX에는 읽을 수 있는 저장된 미리보기가 없습니다. 30MB를 초과하는 본문 추출 대신 연결 앱에서 원본을 열어 주세요.", detail);
        }
        if (extension is ".docx" or ".hwpx")
            return info.Length <= MaxOfficeBytes
                ? ReadOfficeZip(path, extension, detail, token)
                : new(PreviewKind.Information, "DOCX/HWPX는 30MB 이하인 경우에만 텍스트 미리보기를 제공합니다.", detail);
        return new(PreviewKind.Information,
            "이 파일 형식은 현재 내용 미리보기를 지원하지 않습니다. 원본 파일은 변경되지 않았습니다.", detail);
    }

    private static PreviewContent ReadZipInventory(string path, string detail, CancellationToken token)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var sb = new StringBuilder();
            sb.AppendLine($"[ZIP 압축 아카이브 내부 목록 · 총 {archive.Entries.Count:N0}개 항목]");
            sb.AppendLine();
            int shown = 0;
            foreach (ZipArchiveEntry entry in archive.Entries.Take(300))
            {
                token.ThrowIfCancellationRequested();
                bool isDir = string.IsNullOrEmpty(entry.Name);
                string sizeStr = isDir ? "폴더" : FileEntry.HumanSize(entry.Length);
                sb.AppendLine($"• {entry.FullName}  ({sizeStr})");
                shown++;
            }
            if (archive.Entries.Count > shown)
                sb.AppendLine($"\n… 외 {archive.Entries.Count - shown:N0}개 항목 더 있음");
            return new(PreviewKind.Text, sb.ToString(), detail + "  ·  ZIP 내부 목록 미리보기");
        }
        catch (Exception ex)
        {
            return new(PreviewKind.Information, "ZIP 압축 파일 목록을 읽을 수 없습니다: " + ex.Message, detail);
        }
    }

    public static string? ReadSearchableDocumentText(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".pdf" or ".docx" or ".hwpx")) return null;
        string full = AiPlanService.OrdinaryPath(path);
        var info = new FileInfo(full);
        long limit = extension == ".pdf" ? MaxPdfBytes : MaxOfficeBytes;
        if (info.Length > limit) return null;
        if (extension == ".pdf")
        {
            using var pdf = PdfDocument.Open(full);
            var content = new StringBuilder();
            for (int page = 1; page <= Math.Min(pdf.NumberOfPages, 4) && content.Length < MaxTextCharacters; page++)
            {
                token.ThrowIfCancellationRequested();
                string paragraph = ContentOrderTextExtractor.GetText(pdf.GetPage(page));
                if (string.IsNullOrWhiteSpace(paragraph)) continue;
                content.Append(paragraph.AsSpan(0, Math.Min(paragraph.Length, MaxTextCharacters - content.Length)));
                if (content.Length < MaxTextCharacters) content.Append(' ');
            }
            return content.Length == 0 ? null : content.ToString();
        }
        PreviewContent result = ReadOfficeZip(full, extension, info.Name, token);
        if (result.Kind != PreviewKind.Text || result.Body.StartsWith("미리볼 수 있는 문서 텍스트가 없습니다.", StringComparison.Ordinal) ||
            result.Body.StartsWith("추출할 수 있는 텍스트가 없습니다.", StringComparison.Ordinal)) return null;
        const string generatedTruncation = "\n… (일부만 표시)";
        return result.Body.EndsWith(generatedTruncation, StringComparison.Ordinal)
            ? result.Body[..^generatedTruncation.Length] : result.Body;
    }

    private static PreviewContent ReadText(string path, string detail, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var chars = new char[MaxTextCharacters + 1];
        int total = 0;
        while (total < chars.Length)
        {
            token.ThrowIfCancellationRequested();
            int count = reader.Read(chars.AsSpan(total, Math.Min(4096, chars.Length - total)));
            if (count == 0) break;
            total += count;
        }
        var content = new string(chars, 0, Math.Min(total, MaxTextCharacters));
        if (content.Contains('\0'))
            return new(PreviewKind.Information, "이 파일은 바이너리 내용이 포함되어 텍스트로 미리볼 수 없습니다.", detail);
        return new(PreviewKind.Text, content + (total > MaxTextCharacters ? "\n\n… (내용 일부만 표시)" : ""), detail);
    }

    private static PreviewContent ReadPdf(string path, string detail, CancellationToken token)
    {
        using var document = PdfDocument.Open(path);
        int count = Math.Min(document.NumberOfPages, 4);
        var output = new StringBuilder();
        bool extractedText = false;
        for (int pageIndex = 1; pageIndex <= count && output.Length < MaxTextCharacters; pageIndex++)
        {
            token.ThrowIfCancellationRequested();
            string text = ContentOrderTextExtractor.GetText(document.GetPage(pageIndex));
            if (!string.IsNullOrWhiteSpace(text)) extractedText = true;
            output.Append("[페이지 ").Append(pageIndex).Append("]\n");
            if (output.Length < MaxTextCharacters)
                output.Append(text.AsSpan(0, Math.Min(text.Length, MaxTextCharacters - output.Length)));
            output.AppendLine().AppendLine();
        }
        token.ThrowIfCancellationRequested();
        var note = $"PDF 전체 {document.NumberOfPages:N0}쪽 · 앞쪽 {count}쪽에서 텍스트 추출 (페이지 렌더링 아님)";
        return new(PreviewKind.Text,
            !extractedText ? "추출할 수 있는 텍스트가 없습니다. 이미지 스캔 PDF는 지원하지 않습니다." : output.ToString(),
            detail + "  ·  " + note);
    }

    private static PreviewContent ReadOfficeZip(string path, string extension, string detail, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > 10_000)
            throw new InvalidDataException("문서 내부 항목이 10,000개를 초과하여 내용 읽기를 중단합니다.");
        var xmlEntries = extension == ".docx"
            ? archive.Entries.Where(e => e.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase))
            : archive.Entries.Where(e => e.FullName.StartsWith("Contents/section", StringComparison.OrdinalIgnoreCase)
                         && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).Take(5);
        var content = new StringBuilder();
        foreach (var entry in xmlEntries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Length > MaxXmlEntryBytes)
                return new(PreviewKind.Information, "문서의 내부 XML이 3MB를 넘어 미리보기를 중단했습니다.", detail);
            using var entryStream = entry.Open();
            using var xml = XmlReader.Create(entryStream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaxXmlEntryBytes, IgnoreComments = true
            });
            bool insideText = false;
            while (xml.Read() && content.Length < MaxTextCharacters)
            {
                token.ThrowIfCancellationRequested();
                if (xml.NodeType == XmlNodeType.Element)
                {
                    if (xml.LocalName == "t" && !xml.IsEmptyElement) insideText = true;
                    else if (xml.LocalName is "p" or "paragraph") content.AppendLine();
                }
                else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "t") insideText = false;
                else if (insideText && xml.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                {
                    string value = xml.Value;
                    content.Append(value.AsSpan(0, Math.Min(value.Length, MaxTextCharacters - content.Length)));
                    if (content.Length < MaxTextCharacters) content.Append(' ');
                }
            }
            if (content.Length >= MaxTextCharacters) break;
        }
        return new(PreviewKind.Text,
            content.Length == 0 ? "미리볼 수 있는 문서 텍스트가 없습니다. 암호화 또는 비표준 문서일 수 있습니다." :
            content.ToString() + (content.Length >= MaxTextCharacters ? "\n… (일부만 표시)" : ""),
            detail + "  ·  본문 텍스트만 표시 (서식·표·도형 제외)");
    }
}
