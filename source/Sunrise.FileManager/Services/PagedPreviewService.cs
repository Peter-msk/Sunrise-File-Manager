using System.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Sunrise.FileManager.Services;

public sealed record PreviewPage(PreviewKind Kind, string Body, string Detail, int Page, int PageCount, byte[]? ImageBytes = null);

/// <summary>Reads only one document page on demand; no disk export and no privilege changes.</summary>
public static class PagedPreviewService
{
    public const int TextPageCharacters = 2_400;

    public static PreviewPage Read(string path, int requestedPage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("파일을 찾을 수 없습니다.", path);
        if (info.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            if (info.Length > FilePreviewService.MaxPdfBytes)
                return new(PreviewKind.Information, "35MB 초과 PDF는 미리보기를 지원하지 않습니다.", info.Name, 1, 1);
            using var document = PdfDocument.Open(path);
            token.ThrowIfCancellationRequested();
            int total = Math.Max(1, document.NumberOfPages);
            int page = Math.Clamp(requestedPage, 1, total);
            string text = document.NumberOfPages == 0 ? "" : ContentOrderTextExtractor.GetText(document.GetPage(page));
            token.ThrowIfCancellationRequested();
            return new(PreviewKind.Text,
                string.IsNullOrWhiteSpace(text) ? "이 페이지에서 추출할 수 있는 텍스트가 없습니다. 스캔 PDF는 지원하지 않습니다." :
                    text.Length > FilePreviewService.MaxTextCharacters ? text[..FilePreviewService.MaxTextCharacters] + "\n… (이 페이지 일부만 표시)" : text,
                $"{info.Name} · PDF 텍스트 추출 (원본 페이지 렌더링 아님)", page, total);
        }
        PreviewContent full = FilePreviewService.Read(path, false, token);
        if (full.Kind is PreviewKind.Image or PreviewKind.Information)
            return new(full.Kind, full.Body, full.Detail, 1, 1, full.ImageBytes);
        IReadOnlyList<string> pages = Paginate(full.Body, TextPageCharacters);
        int index = Math.Clamp(requestedPage, 1, pages.Count);
        string note = info.Extension is ".docx" or ".hwpx" ? " · 텍스트 길이 기준 구분(실제 문서 쪽수 아님)" : " · 화면별 텍스트 구분";
        return new(PreviewKind.Text, pages[index - 1], full.Detail + note, index, pages.Count);
    }

    // Stable text pages; keep the page count bounded by FilePreviewService's maximum text budget.
    public static IReadOnlyList<string> Paginate(string text, int maxChars)
    {
        if (maxChars < 100) throw new ArgumentOutOfRangeException(nameof(maxChars));
        if (text.Length == 0) return ["(표시할 텍스트가 없습니다.)"];
        var pages = new List<string>();
        int cursor = 0;
        while (cursor < text.Length)
        {
            int end = Math.Min(cursor + maxChars, text.Length);
            if (end < text.Length)
            {
                int newline = text.LastIndexOf('\n', end - 1, end - cursor);
                if (newline > cursor + maxChars / 2) end = newline + 1;
            }
            pages.Add(text[cursor..end]);
            cursor = end;
        }
        return pages;
    }
}
