using System.IO;

namespace Sunrise.FileManager.Services;

/// <summary>Read-only, local keyword search for supported office documents.
/// Search examines a bounded prefix only; unreadable documents are never counted as matches.</summary>
public static class AiDocumentSearchService
{
    public sealed record Result(IReadOnlyList<string> Matches, int Inspected, int Unsupported, int Unreadable);
    private const int MaxFiles = 500;
    private const long MaxTotalBytes = 256L * 1024 * 1024;

    public static Result Filter(IReadOnlyList<string> candidates, string word, bool approved, CancellationToken token)
    {
        if (!approved) throw new InvalidOperationException("문서 본문 읽기를 명시적으로 승인하세요.");
        if (string.IsNullOrWhiteSpace(word) || word.Length > 100) throw new ArgumentException("본문 검색어가 올바르지 않습니다.");
        if (candidates.Count > MaxFiles) throw new IOException("본문 검색은 최대 500개 파일까지 지원합니다. 범위를 좁혀주세요.");
        var matches = new List<string>(); int inspected = 0, unsupported = 0, unreadable = 0;
        long bytes = 0;
        foreach (string path in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path) || Path.GetExtension(path).ToLowerInvariant() is not (".pdf" or ".hwpx" or ".docx"))
            { unsupported++; continue; }
            try
            {
                string checkedPath = AiPlanService.OrdinaryPath(path);
                var file = new FileInfo(checkedPath);
                long max = file.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                    ? FilePreviewService.MaxPdfBytes : FilePreviewService.MaxOfficeBytes;
                if (file.Length > max) { unreadable++; continue; }
                bytes = checked(bytes + file.Length);
                if (bytes > MaxTotalBytes) throw new IOException("본문 검색 총 256MB 상한을 초과했습니다. 범위를 좁혀주세요.");
                inspected++;
                string? body = FilePreviewService.ReadSearchableDocumentText(checkedPath, token);
                if (body is null) { unreadable++; continue; }
                if (body.Contains(word, StringComparison.OrdinalIgnoreCase)) matches.Add(checkedPath);
            }
            catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or
                System.Security.SecurityException or NotSupportedException or System.Xml.XmlException)
            { unreadable++; }
            // IO errors signal unstable file state; do not silently approve a partial bulk operation.
        }
        return new(matches.AsReadOnly(), inspected, unsupported, unreadable);
    }
}
