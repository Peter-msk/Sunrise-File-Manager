using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Sunrise.FileManager.Services;

public enum ContentSearchStatus { Match, NoMatch, Unsupported, SizeLimit, NoText }
public sealed record ContentSearchResult(ContentSearchStatus Status, string? Detail = null);
public sealed record ContentMatchesResult(IReadOnlySet<string> Words, ContentSearchStatus Status, string? Detail);

/// <summary>Search owns full-document traversal; preview character/page limits do not apply.</summary>
public static class DocumentContentSearch
{
    public const long MaxDocumentBytes = 128L * 1024 * 1024;
    public const long MaxTextBytes = 16L * 1024 * 1024;
    private const long MaxXmlBytes = 64L * 1024 * 1024;
    private const long MaxTotalXmlBytes = 256L * 1024 * 1024;
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".pdf" or ".docx" or ".hwpx" or ".hwp" or ".xlsx" or ".pptx" or ".txt" or ".md" or ".csv" or ".tsv" or ".json" or ".xml" or ".log" or ".cs" or ".py" or ".html";
    public static ContentSearchResult Find(string path, string query, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("검색어가 비어 있습니다.");
        var matcher = new Matcher(query);
        return ReadCore(path, matcher.Append, () => matcher.HasText, token);
    }
    public static ContentMatchesResult FindMany(string path, IEnumerable<string> words, CancellationToken token)
    {
        string[] queries = words.Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (queries.Length is < 1 or > 200 || queries.Any(q => string.IsNullOrWhiteSpace(q) || q.Length > 100))
            throw new ArgumentException("본문 검색 키워드 한도를 초과했습니다.");
        var matchers = queries.Select(q => new Matcher(q)).ToArray();
        var matched = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var result = ReadCore(path, chunk =>
        {
            for (int i = 0; i < queries.Length; i++) if (!matched.Contains(queries[i]) && matchers[i].Append(chunk)) matched.Add(queries[i]);
            return matched.Count == queries.Length;
        }, () => matchers.Any(m => m.HasText), token);
        return new(matched, result.Status, result.Detail);
    }
    private static ContentSearchResult ReadCore(string path, Func<string, bool> append, Func<bool> hasText, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (!Supports(path))
            return new(ContentSearchStatus.Unsupported);
        long limit = ext is ".pdf" or ".docx" or ".hwpx" or ".hwp" or ".xlsx" or ".pptx" ? MaxDocumentBytes : MaxTextBytes;
        if (new FileInfo(path).Length > limit) return new(ContentSearchStatus.SizeLimit, $"파일 크기 제한 {limit / 1024 / 1024}MiB");
        if (ext is ".hwp" or ".xlsx" or ".pptx")
        {
            try
            {
                bool found = ext == ".hwp" ? HwpTextReader.Visit(path, append, token) : OfficeTextReader.Visit(path, append, token);
                if (found) return new(ContentSearchStatus.Match);
            }
            catch (DocumentReadLimitException ex) { return new(ContentSearchStatus.SizeLimit, ex.Message); }
            catch (NotSupportedException ex) { return new(ContentSearchStatus.Unsupported, ex.Message); }
        }
        else if (ext == ".pdf")
        {
            using var document = PdfDocument.Open(path);
            for (int page = 1; page <= document.NumberOfPages; page++)
            {
                token.ThrowIfCancellationRequested();
                if (append(ContentOrderTextExtractor.GetText(document.GetPage(page))) || append("\n")) return new(ContentSearchStatus.Match);
            }
        }
        else if (ext is ".docx" or ".hwpx")
        {
            using var file = File.OpenRead(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            if (archive.Entries.Count > 10_000) return new(ContentSearchStatus.SizeLimit, "문서 내부 항목 10,000개 초과");
            var entries = archive.Entries.Where(e => IsTextPart(e.FullName, ext))
                .OrderBy(e => e.FullName, NaturalNameComparer.Instance).ToArray();
            long xmlBytes = 0;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                xmlBytes = checked(xmlBytes + entry.Length);
                if (entry.Length > MaxXmlBytes || xmlBytes > MaxTotalXmlBytes)
                    return new(ContentSearchStatus.SizeLimit, "문서 XML 크기 제한으로 일부 미검색");
                using var stream = entry.Open();
                using var xml = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = MaxXmlBytes, IgnoreComments = true
                });
                bool insideText = false;
                char[] buffer = new char[4096];
                while (xml.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "t") insideText = !xml.IsEmptyElement;
                    else if (xml.NodeType == XmlNodeType.EndElement)
                    {
                        if (xml.LocalName == "t") insideText = false;
                        if (xml.LocalName is "p" or "paragraph" && append("\n")) return new(ContentSearchStatus.Match);
                    }
                    else if (insideText && xml.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                    {
                        int read;
                        while ((read = xml.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            if (append(new string(buffer, 0, read))) return new(ContentSearchStatus.Match);
                        }
                    }
                }
                if (append("\n")) return new(ContentSearchStatus.Match);
            }
        }
        else
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            char[] buffer = new char[4096];
            long chars = 0; int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested(); chars += read;
                if (buffer.AsSpan(0, read).Contains('\0')) return new(ContentSearchStatus.Unsupported, "바이너리 내용은 텍스트로 검색하지 않습니다.");
                if (chars > MaxTextBytes) return new(ContentSearchStatus.SizeLimit, "증가 중인 텍스트 파일 읽기 제한");
                if (append(new string(buffer, 0, read))) return new(ContentSearchStatus.Match);
            }
        }
        return new(hasText() ? ContentSearchStatus.NoMatch : ContentSearchStatus.NoText);
    }
    private static bool IsTextPart(string name, string ext)
    {
        if (ext == ".hwpx") return name.StartsWith("Contents/section", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
        return name.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("word/footnotes.xml", StringComparison.OrdinalIgnoreCase) || name.Equals("word/endnotes.xml", StringComparison.OrdinalIgnoreCase) ||
            (name.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) || name.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase)) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    }
    private sealed class Matcher(string query)
    {
        private string _tail = "";
        public bool HasText { get; private set; }
        public bool Append(string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) HasText = true;
            string joined = _tail + value;
            bool match = joined.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            _tail = joined[^Math.Min(query.Length - 1, joined.Length)..];
            return match;
        }
    }
}
