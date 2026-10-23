using System.Globalization;
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using Sunrise.FileManager.Models;

namespace Sunrise.FileManager.Services;

/// <summary>Small local filter syntax, not an emulation of every Windows AQS property.</summary>
public sealed class ExplorerQuery
{
    private readonly List<Func<FileEntry, bool>> _tests = [];
    public bool Matches(FileEntry entry) => _tests.All(test => test(entry));
    public static bool IsStructured(string query) => Regex.IsMatch(query, @"(?:^|\s)(?:name|filename|ext|extension|size|modified|datemodified|created|datecreated|kind|type|is|path|folder|date):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static ExplorerQuery Parse(string query)
    {
        var plan = new ExplorerQuery();
        if (query.Length > 2000) throw new ArgumentException("검색 조건은 2,000자 이하여야 합니다.");
        bool structured = IsStructured(query);
        if (!structured)
        {
            if (query.Length > 0) plan._tests.Add(e => Name(e.Name, query));
            return plan;
        }
        if (query.Count(c => c == '"') % 2 != 0)
            throw new ArgumentException("검색어의 따옴표가 닫히지 않았습니다.");
        foreach (Match match in Regex.Matches(query, "(?:[^\\s\"]|\"[^\"]*\")+"))
        {
            string part = match.Value; int split = part.IndexOf(':');
            if (split < 0) { string text = part.Trim('"'); plan._tests.Add(e => Name(e.Name, text)); continue; }
            string key = part[..split].ToLowerInvariant(), value = part[(split + 1)..].Trim('"');
            if (value.Length == 0) throw new ArgumentException("검색 조건 값이 비어 있습니다.");
            switch (key)
            {
                case "name": case "filename": plan._tests.Add(e => Name(e.Name, value)); break;
                case "path": case "folder": plan._tests.Add(e => Name(e.FullPath, value)); break;
                case "ext": case "extension":
                    var extensions = value.Split(',', StringSplitOptions.TrimEntries).Select(v => "." + v.TrimStart('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    plan._tests.Add(e => !e.IsDirectory && extensions.Contains(System.IO.Path.GetExtension(e.Name))); break;
                case "kind": case "type":
                    value = value.ToLowerInvariant();
                    if (value is not ("file" or "folder")) throw new ArgumentException("kind는 file 또는 folder를 지정하세요.");
                    plan._tests.Add(e => e.IsDirectory == (value == "folder")); break;
                case "is":
                    value = value.ToLowerInvariant();
                    if (value is not ("hidden" or "readonly" or "memo" or "folder" or "file"))
                        throw new ArgumentException("is는 hidden, readonly, memo, folder, file 중 선택하세요.");
                    string predicate = value;
                    plan._tests.Add(e => predicate switch
                    {
                        "hidden" => e.IsHidden, "readonly" => e.IsReadOnly,
                        "memo" => e.HasMemo, "folder" => e.IsDirectory, _ => !e.IsDirectory
                    }); break;
                case "size":
                    var size = Regex.Match(value, @"^(>=|<=|>|<|=)?(\d+(?:\.\d+)?)(B|KB|MB|GB|KiB|MiB|GiB)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (!size.Success || !decimal.TryParse(size.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number)) throw new ArgumentException("크기 예: size:>=10MiB");
                    decimal factor = size.Groups[3].Value.ToUpperInvariant() switch { "KB" => 1000, "MB" => 1000000, "GB" => 1000000000, "KIB" => 1024, "MIB" => 1048576, "GIB" => 1073741824, _ => 1 };
                    if (number > long.MaxValue / factor) throw new ArgumentException("크기 조건이 너무 큽니다.");
                    long bytes = (long)(number * factor); string sizeOp = size.Groups[1].Value;
                    plan._tests.Add(e => !e.IsDirectory && Compare(e.SizeBytes.CompareTo(bytes), sizeOp)); break;
                case "modified": case "datemodified": case "date": case "created": case "datecreated":
                    var date = Regex.Match(value, @"^(>=|<=|>|<|=)?(\d{4}-\d{2}-\d{2})$");
                    if (!date.Success || !DateTime.TryParseExact(date.Groups[2].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day)) throw new ArgumentException("날짜 예: modified:>=2026-01-01");
                    string dateOp = date.Groups[1].Value;
                    plan._tests.Add(e => Compare((key is "modified" or "datemodified" or "date" ? e.ModifiedAt : e.CreatedAt).Date.CompareTo(day), dateOp)); break;
                default: throw new ArgumentException("지원하지 않는 검색 조건입니다: " + key);
            }
        }
        return plan;
    }
    private static bool Name(string name, string query) => query.IndexOfAny(['*', '?']) >= 0
        ? FileSystemName.MatchesSimpleExpression(query, name, ignoreCase: true)
        : name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    private static bool Compare(int order, string op) => op switch { ">=" => order >= 0, "<=" => order <= 0, ">" => order > 0, "<" => order < 0, _ => order == 0 };
}
