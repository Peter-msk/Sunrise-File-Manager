using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Sunrise.FileManager.Services;

/// <summary>Submission names only: never converts, splits or uploads document content.</summary>
public static class EiassFileNameService
{
    public static readonly string[] Stages = ["초안", "본안", "보완", "재보완", "변경협의본안", "변경협의보완", "보완 1차", "보완 2차"];
    private const string Forbidden = "★●+&＆①<＜>＞*＊\"'“”‘’<>:/\\|?";
    public static bool IsForbidden(char c) => c < 32 || Forbidden.Contains(c) || c is >= '\u2460' and <= '\u2473';
    public static string CleanTitle(string title) => Regex.Replace(new string(title.Where(c => !IsForbidden(c)).ToArray()), @"\s+", " ").Trim();

    public static string InitialTitle(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        return Regex.Replace(stem, @"^(?:\([^)]*\)\s+)?\d{4,5}\s+", "").Trim();
    }

    public static string MakeName(string source, string code, string title, int digits, string? stage, bool clean)
    {
        if (!Path.GetExtension(source).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("PDF 파일만 이름을 변경할 수 있습니다.");
        if (digits is not (4 or 5) || code.Length != digits || code.Any(c => c is < '0' or > '9'))
            throw new ArgumentException($"목차번호는 {digits}자리 숫자여야 합니다.");
        if (stage is not null && !Stages.Contains(stage) && !Regex.IsMatch(stage, @"^보완 [1-9][0-9]*차$"))
            throw new ArgumentException("말머리를 선택하거나 ‘보완 3차’ 형식으로 입력하세요.");
        title = clean ? CleanTitle(title) : title.Trim();
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("문서 제목을 입력하세요.");
        if (title.Any(IsForbidden)) throw new ArgumentException("제목에 금지 문자가 있습니다. 제목을 수정하거나 금지 문자 정리를 선택하세요.");
        string name = (stage is null ? "" : $"({stage}) ") + code + " " + title + Path.GetExtension(source);
        FileActionsService.ValidateName(name);
        if (name.Length > 255) throw new ArgumentException("파일 이름이 255자를 초과합니다.");
        return name;
    }

    public static string[] Number(int count, int digits, string start, string step)
    {
        if (digits is not (4 or 5) || !int.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out int first) ||
            !int.TryParse(step, NumberStyles.None, CultureInfo.InvariantCulture, out int increment) || increment < 1 || first < 0 ||
            (long)first + Math.Max(0, count - 1L) * increment >= (digits == 4 ? 10_000 : 100_000))
            throw new ArgumentException("시작 번호·증가 간격과 4/5자리 번호 범위를 확인하세요.");
        return Enumerable.Range(0, count).Select(i => (first + i * increment).ToString("D" + digits, CultureInfo.InvariantCulture)).ToArray();
    }

    public static IReadOnlyList<string> ParseContents(string text)
    {
        if (text.Length > 1_000_000) throw new ArgumentException("목차 텍스트는 100만 자 이내로 지정하세요.");
        var lines = new List<string>();
        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            // A page number is removed only after a leader, tab or multiple spaces.
            line = Regex.Replace(line, @"(?:[.·…‥]{2,}\s*|\t+| {2,})(?:\d+(?:[-–]\d+)?|[ivxlcdm]+)\s*$", "", RegexOptions.IgnoreCase);
            line = Regex.Replace(line, @"^(?:제\s*\d+\s*[장절편]\s*|\d+(?:\.\d+)*[.)]?\s+)", "").Trim();
            if (line.Length == 0 || Regex.IsMatch(line, @"^\d+$") || Regex.Replace(line, @"\s", "") is "목차" or "차례" or "CONTENTS") continue;
            lines.Add(line);
            if (lines.Count > 2000) throw new ArgumentException("목차는 2,000개 항목 이내로 지정하세요.");
        }
        return lines;
    }

    private static string MatchKey(string title) => Regex.Replace(title.Normalize(NormalizationForm.FormKC), @"\s+", "").ToUpperInvariant();
    public static int?[] MatchContents(IReadOnlyList<string> titles, IReadOnlyList<string> contents)
    {
        var titleKeys = titles.Select(MatchKey).ToArray();
        var contentsKeys = contents.Select(MatchKey).ToArray();
        return titleKeys.Select(key => key.Length > 0 && titleKeys.Count(k => k == key) == 1 && contentsKeys.Count(k => k == key) == 1
            ? (int?)Array.IndexOf(contentsKeys, key) : null).ToArray();
    }
}
