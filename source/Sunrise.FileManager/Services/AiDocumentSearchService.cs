using System.IO;

namespace Sunrise.FileManager.Services;

/// <summary>Read-only, local keyword search for supported office documents.
/// Searches supported document bodies within the approved file/byte budget.</summary>
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
            if (!File.Exists(path) || !DocumentContentSearch.Supports(path))
            { unsupported++; continue; }
            try
            {
                string checkedPath = AiPlanService.OrdinaryPath(path);
                var file = new FileInfo(checkedPath);
                bytes = checked(bytes + file.Length);
                if (bytes > MaxTotalBytes) throw new IOException("본문 검색 총 256MB 상한을 초과했습니다. 범위를 좁혀주세요.");
                inspected++;
                var result = DocumentContentSearch.Find(checkedPath, word, token);
                if (result.Status == ContentSearchStatus.Match) matches.Add(checkedPath);
                else if (result.Status == ContentSearchStatus.Unsupported) unsupported++;
                else if (result.Status == ContentSearchStatus.SizeLimit)
                    throw new IOException("일부 본문이 검색되지 않아 일괄 작업을 중단합니다: " + result.Detail);
            }
            catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or
                System.Security.SecurityException or NotSupportedException or System.Xml.XmlException or
                UglyToad.PdfPig.Core.PdfDocumentFormatException or UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException)
            { unreadable++; }
            // IO errors signal unstable file state; do not silently approve a partial bulk operation.
        }
        return new(matches.AsReadOnly(), inspected, unsupported, unreadable);
    }
}
