using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Sunrise.FileManager.Services;

internal sealed class DocumentReadLimitException(string message) : IOException(message);

/// <summary>OOXML text only. No Office installation, macros, external links or formula execution.</summary>
internal static class OfficeTextReader
{
    internal static bool Visit(string path, Func<string, bool> append, CancellationToken token)
    {
        using var file = File.OpenRead(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        if (archive.Entries.Count > 10_000) throw new DocumentReadLimitException("문서 내부 항목 10,000개 제한");
        long total = 0;
        XmlReader Open(ZipArchiveEntry part)
        {
            total = checked(total + part.Length);
            if (part.Length > 64L * 1024 * 1024 || total > 256L * 1024 * 1024)
                throw new DocumentReadLimitException("문서 XML 크기 제한으로 일부 미검색");
            return XmlReader.Create(part.Open(), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64L * 1024 * 1024, CloseInput = true });
        }
        if (Path.GetExtension(path).Equals(".pptx", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in archive.Entries.Where(e =>
                (e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) || e.FullName.StartsWith("ppt/notesSlides/notesSlide", StringComparison.OrdinalIgnoreCase)) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, NaturalNameComparer.Instance))
            {
                using var xml = Open(part);
                if (Text(xml, append, token, true) || append("\n")) return true;
            }
            return false;
        }
        // Shared strings are searched only when referenced by a cell, avoiding unused text matches.
        var shared = new List<string>();
        if (archive.GetEntry("xl/sharedStrings.xml") is { } strings)
        {
            using var xml = Open(strings);
            while (xml.Read())
            {
                token.ThrowIfCancellationRequested();
                if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "si") continue;
                if (shared.Count == 1_000_000) throw new DocumentReadLimitException("공유 문자열 개수 제한");
                using var part = xml.ReadSubtree();
                var value = new StringBuilder();
                Text(part, s => { value.Append(s); return false; }, token, false);
                shared.Add(value.ToString());
            }
        }
        foreach (var part in archive.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName, NaturalNameComparer.Instance))
        {
            using var xml = Open(part);
            while (xml.Read())
            {
                token.ThrowIfCancellationRequested();
                if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "c") continue;
                string? type = xml.GetAttribute("t");
                using var cell = xml.ReadSubtree();
                string value = "";
                if (type == "inlineStr")
                {
                    var inline = new StringBuilder();
                    Text(cell, s => { inline.Append(s); return false; }, token, false);
                    value = inline.ToString();
                }
                else
                {
                    while (cell.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "v") { value = cell.ReadElementContentAsString(); break; }
                    }
                    if (type == "s")
                    {
                        if (!int.TryParse(value, out int index) || index < 0 || index >= shared.Count) throw new InvalidDataException("엑셀 공유 문자열 참조 오류");
                        value = shared[index];
                    }
                }
                if (append(value) || append("\n")) return true;
            }
        }
        return false;
    }
    private static bool Text(XmlReader xml, Func<string, bool> append, CancellationToken token, bool breaks)
    {
        bool inText = false;
        char[] buffer = new char[4096];
        while (xml.Read())
        {
            token.ThrowIfCancellationRequested();
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "t") inText = !xml.IsEmptyElement;
            else if (xml.NodeType == XmlNodeType.EndElement)
            {
                if (xml.LocalName == "t") inText = false;
                if (breaks && xml.LocalName == "p" && append("\n")) return true;
            }
            else if (inText && xml.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
            {
                int read;
                while ((read = xml.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
                { token.ThrowIfCancellationRequested(); if (append(new string(buffer, 0, read))) return true; }
            }
        }
        return false;
    }
}
