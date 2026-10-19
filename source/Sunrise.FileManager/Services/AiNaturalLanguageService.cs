using System.IO;
using System.Text.RegularExpressions;

namespace Sunrise.FileManager.Services;

/// <summary>
/// Offline, bounded natural-language command interpreter. This is a deterministic
/// command parser, NOT an LLM. Ambiguous or unsupported requests fail closed.
/// The model/proxy path remains separately opt-in in AiCommandPanel.
/// </summary>
public static class AiNaturalLanguageService
{
    public const string Examples =
        "예: PDF 파일 찾아줘\n" +
        "예: 파일명에 '안성천' 포함된 파일 찾아줘\n" +
        "예: 최근 7일 수정된 PDF 파일만 복사해 줘\n" +
        "예: 이미지 파일을 유형별로 분류해서 이동해 줘\n" +
        "예: 파일명 앞에 '완료_' 붙여줘\n" +
        "예: 선택한 파일을 휴지통으로 보내줘 (2차 승인 필요)\n" +
        "예: 본문에 '안성천' 포함된 PDF 파일 찾아줘\n" +
        "검색 위치는 '선택 항목' 또는 '현재 폴더 및 하위 폴더'에서 고릅니다. 문서 본문 검색은 PDF 앞 4쪽, DOCX/HWPX 앞부분 최대 16,000자입니다.";

    public sealed record Intent(string Action, string? Extension, string? Category, string? NameContains,
        int? ModifiedWithinDays, string Grouping, string Description,
        string? ContentContains = null, string? RenamePrefix = null, string? RenameSuffix = null)
    {
        public bool NeedsDestination => Action is "copy" or "move";
        public bool SearchOnly => Action == "search";
    }
    private static readonly Regex NameFilter = new(
        @"(?:파일명|이름)\s*에\s*(?:'(?<quoted>[^']{1,100})'|""(?<quoted>[^""]{1,100})""|(?<word>[가-힣A-Za-z0-9_.-]{1,100}))\s*(?:이|가)?\s*(?:포함|들어간|있는)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BodyFilter = new(
        @"(?:본문|내용)\s*에\s*(?:'(?<word>[^']{1,100})'|""(?<word>[^""]{1,100})"")\s*(?:이|가)?\s*(?:포함|들어간|있는)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RenamePart = new(
        @"(?:파일명|이름)\s*(?<position>앞|뒤)\s*에\s*(?:'(?<word>[^']{1,80})'|""(?<word>[^""]{1,80})"")\s*(?:을|를)?\s*(?:붙여|추가)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RelativeDays = new(@"최근\s*(?<days>\d{1,3})\s*일", RegexOptions.Compiled);
    private static readonly string[] Extensions = ["HWPX", "DOCX", "XLSX", "PPTX", "PDF", "HWP", "DOC", "XLS", "PPT", "JPG", "JPEG", "PNG", "TXT", "CSV", "ZIP", "MP4"];

    public static Intent Parse(string request)
    {
        string text = request.Trim();
        if (text.Length is < 2 or > 4000) throw new ArgumentException("명령을 2~4,000자로 입력하세요.");
        if (Regex.IsMatch(text, @"영구|초기화|명령어|스크립트|실행파일|권한변경|덮어쓰|강제|복사하지|이동하지|옮기지|제외하고|빼고|완전삭제|즉시삭제"))
            throw new ArgumentException("영구 삭제·부정 조건·덮어쓰기는 AI 명령에서 허용하지 않습니다.");
        bool copy = Regex.IsMatch(text, @"복사|사본");
        bool move = Regex.IsMatch(text, @"이동|옮겨|옮기");
        bool search = Regex.IsMatch(text, @"찾아|검색|골라|보여|조회");
        bool rename = Regex.IsMatch(text, @"붙여|이름 변경|이름 바꿔|이름바꿔");
        bool recycle = Regex.IsMatch(text, @"휴지통") && Regex.IsMatch(text, @"보내|넣어|이동|버려|삭제");
        bool delete = Regex.IsMatch(text, @"삭제|지워|제거|버려");
        if (delete && !recycle) throw new ArgumentException("삭제 명령은 '휴지통으로 보내줘'만 지원합니다. 영구 삭제는 지원하지 않습니다.");
        if (recycle) move = false; // '휴지통으로 이동'은 일반 폴더 이동이 아닙니다.
        bool categorize = Regex.IsMatch(text, @"정리해|정리하|분류해|분류하");
        if ((copy ? 1 : 0) + (move ? 1 : 0) + (rename ? 1 : 0) + (recycle ? 1 : 0) > 1)
            throw new ArgumentException("서로 다른 변경 작업이 혼합되었습니다. 한 번에 한 작업만 지정하세요.");
        string grouping = Regex.IsMatch(text, @"확장자별|파일형식별") ? "extension" :
            Regex.IsMatch(text, @"유형별|종류별") ? "category" :
            Regex.IsMatch(text, @"날짜별") ? "date" :
            Regex.IsMatch(text, @"연도별") ? "year" :
            Regex.IsMatch(text, @"크기별") ? "size" : "";
        if (categorize && grouping.Length == 0)
            throw new ArgumentException("'정리' 기준이 불명확합니다. 예: '유형별로 분류해서 이동해 줘'.");
        if (!copy && !move && !search && !rename && !recycle)
            throw new ArgumentException("검색·복사·이동·이름 변경·휴지통 중 작업을 지정하세요.\n" + Examples);
        if (grouping.Length > 0 && !copy && !move)
            throw new ArgumentException("분류 기준과 함께 복사 또는 이동 작업을 지정하세요.");
        if (categorize && (rename || recycle)) throw new ArgumentException("이름 변경과 휴지통 이동에는 분류 기준을 함께 사용할 수 없습니다.");
        if ((rename || recycle) && search) throw new ArgumentException("조회와 변경 명령을 혼합하지 마세요. 작업을 구분해 주세요.");
        string action = copy ? "copy" : move ? "move" : rename ? "rename" : recycle ? "recycle" : "search";
        Match renamePart = RenamePart.Match(text);
        if (rename && !renamePart.Success)
            throw new ArgumentException("이름 변경은 '파일명 앞에 '완료_' 붙여줘' 또는 '파일명 뒤에 '_최종' 붙여줘' 형식만 지원합니다.");
        string? renamePrefix = rename && renamePart.Groups["position"].Value == "앞" ? renamePart.Groups["word"].Value : null;
        string? renameSuffix = rename && renamePart.Groups["position"].Value == "뒤" ? renamePart.Groups["word"].Value : null;
        if (renamePrefix is not null) AiPlanService.Name(renamePrefix + "sample.txt");
        if (renameSuffix is not null) AiPlanService.Name("sample" + renameSuffix + ".txt");
        string? extension = null;
        foreach (string candidate in Extensions)
        {
            // A literal .pdf or a standalone Korean-suffixed 'PDF 파일/만/을'.
            if (Regex.IsMatch(text, @"\." + candidate + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(text, @"(?<![A-Za-z])" + candidate + @"(?=\s|$|[가-힣]|[,./])", RegexOptions.IgnoreCase))
            {
                if (extension is not null && extension != "." + candidate.ToLowerInvariant())
                    throw new ArgumentException("여러 확장자를 동시에 지정하는 검색은 아직 지원하지 않습니다. 하나만 선택하세요.");
                extension = "." + candidate.ToLowerInvariant();
            }
        }
        string? category = Regex.IsMatch(text, @"이미지|사진") ? "이미지" :
            Regex.IsMatch(text, @"동영상|비디오") ? "동영상" :
            Regex.IsMatch(text, @"문서") ? "문서" : null;
        Match filter = NameFilter.Match(text);
        string? keyword = filter.Success ? (filter.Groups["quoted"].Success ? filter.Groups["quoted"].Value : filter.Groups["word"].Value) : null;
        if (Regex.IsMatch(text, @"파일명\s*에|이름\s*에") && keyword is null)
            throw new ArgumentException("파일명 조건을 이해하지 못했습니다. 예: 파일명에 '안성천' 포함된 파일 찾아줘.");
        Match days = RelativeDays.Match(text);
        int? within = null;
        if (days.Success)
        {
            within = int.Parse(days.Groups["days"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (within < 1 || within > 365) throw new ArgumentException("최근 수정일 검색은 1~365일까지 지원합니다.");
        }
        else if (Regex.IsMatch(text, @"어제|오늘|지난달|작년|이번주|수정일|작성일|최신|가장 최근"))
            throw new ArgumentException("날짜 조건이 모호합니다. 예: '최근 7일 수정된 파일 찾아줘'.");
        Match bodyFilter = BodyFilter.Match(text);
        string? content = bodyFilter.Success ? bodyFilter.Groups["word"].Value : null;
        if (Regex.IsMatch(text, @"본문|내용") && content is null)
            throw new ArgumentException("본문 조건은 '본문에 '검색어' 포함된 PDF 파일 찾아줘' 형식으로 지정하세요.");
        if (Regex.IsMatch(text, @"중복|동일한|유사한|최종본|최신본|하위폴더만|관련|사업별|프로젝트별"))
            throw new ArgumentException("중복·최종본·의미적 관련성 판단은 이 단계에서 지원하지 않습니다.");
        if (content is not null && extension is not null && extension is not (".pdf" or ".docx" or ".hwpx"))
            throw new ArgumentException("본문 검색은 PDF·DOCX·HWPX에서만 지원합니다.");
        if (content is not null && (rename || recycle))
            throw new ArgumentException("본문 검색과 이름 변경·휴지통 이동을 한 번에 실행할 수 없습니다. 먼저 검색하여 선택한 뒤 작업하세요.");
        if (category is not null && extension is not null && AiLocalPlanner.Category("a" + extension, false) != category)
            throw new ArgumentException("파일 유형과 확장자 조건이 서로 맞지 않습니다.");
        string description = $"{(action == "search" ? "파일 검색" : action == "copy" ? "복사 계획" : action == "move" ? "이동 계획" : action == "rename" ? "이름 변경 계획" : "휴지통 계획")}" +
            (keyword is null ? "" : $" · 파일명 '{keyword}' 포함") +
            (content is null ? "" : $" · 본문 '{content}' 포함") +
            (extension is null ? "" : $" · {extension} 형식") +
            (category is null ? "" : $" · {category}") +
            (within is null ? "" : $" · 최근 {within}일 수정") +
            (grouping.Length == 0 ? "" : $" · {grouping}별 분류") +
            (renamePrefix is null ? "" : $" · 접두어 '{renamePrefix}'") +
            (renameSuffix is null ? "" : $" · 접미어 '{renameSuffix}'");
        return new(action, extension, category, keyword, within, grouping, description, content, renamePrefix, renameSuffix);
    }

    public static IReadOnlyList<string> Discover(string folder, IReadOnlyList<string> selection, bool includeSubfolders,
        Intent intent, CancellationToken token, DateTime? nowLocal = null)
    {
        string root = AiPlanService.OrdinaryPath(folder);
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int examined = 0;
        var pending = new Stack<(string Path, int Depth)>();
        if (includeSubfolders) pending.Push((root, 0));
        else
        {
            if (selection.Count == 0) throw new ArgumentException("선택된 파일이 없습니다. 파일을 선택하거나 검색 범위를 '현재 폴더 및 하위'로 변경하세요.");
            foreach (string path in selection.Reverse())
            {
                string full = AiPlanService.OrdinaryPath(path);
                if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("현재 탐색기에서 직접 선택한 항목만 허용됩니다.");
                pending.Push((full, -1));
            }
        }
        DateTime now = nowLocal ?? DateTime.Now;
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (path, depth) = pending.Pop();
            FileAttributes attr = File.GetAttributes(path);
            if ((attr & FileAttributes.ReparsePoint) != 0) continue;
            bool directory = (attr & FileAttributes.Directory) != 0;
            if (directory && Path.GetFileName(path).Equals("#memo", StringComparison.OrdinalIgnoreCase)) continue;
            if (++examined > 5000) throw new IOException("검색 검토 한도 5,000개를 초과했습니다. 폴더 범위를 좁혀 다시 검색하세요.");
            if (directory && depth >= 0)
            {
                if (depth > 12) throw new IOException("검색 깊이 12단계를 초과했습니다. 범위를 좁혀주세요.");
                // Depth 12 is inspected but no more children are traversed.
                if (depth == 12)
                {
                    if (Directory.EnumerateFileSystemEntries(path).Any()) throw new IOException("검색 깊이 12단계를 초과했습니다. 범위를 좁혀주세요.");
                    continue;
                }
                string[] children = Directory.EnumerateFileSystemEntries(path).Take(5001).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                if (children.Length > 5000) throw new IOException("한 폴더의 검색 항목이 5,000개를 초과했습니다.");
                foreach (var child in children) pending.Push((child, depth + 1));
                continue;
            }
            if (!directory && (path.EndsWith(".sunmemo", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))) continue;
            if (intent.Extension is not null && (directory || !Path.GetExtension(path).Equals(intent.Extension, StringComparison.OrdinalIgnoreCase))) continue;
            if (intent.Category is not null && (directory || AiLocalPlanner.Category(Path.GetFileName(path), false) != intent.Category)) continue;
            if (intent.NameContains is not null && !Path.GetFileName(path).Contains(intent.NameContains, StringComparison.OrdinalIgnoreCase)) continue;
            if (intent.ModifiedWithinDays is int days)
            {
                DateTime changed = File.GetLastWriteTime(path);
                if (changed < now.AddDays(-days) || changed > now.AddMinutes(5)) continue;
            }
            if (seen.Add(path))
            {
                if (found.Count >= 1000) throw new IOException("검색 결과가 1,000개를 초과했습니다. 검색 조건을 좁혀주세요.");
                found.Add(path);
            }
        }
        return found.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static AiPlan BuildPlan(AiScope scope, Intent intent)
    {
        if (intent.SearchOnly) throw new InvalidOperationException("검색 결과에는 변경 계획이 없습니다.");
        if (scope.Entries.Count == 0) throw new ArgumentException("조건에 맞는 파일이 없습니다.");
        if (intent.Action == "recycle")
            return new AiPlan(1, scope.Id, scope.Entries.Select(entry => new AiOperation("recycle", entry.Id)).ToArray());
        if (intent.Action == "rename")
            return new AiPlan(1, scope.Id, scope.Entries.Select(entry =>
            {
                string ext = entry.Directory ? "" : Path.GetExtension(entry.Name);
                string stem = entry.Name[..(entry.Name.Length - ext.Length)];
                string updated = AiPlanService.Name((intent.RenamePrefix ?? "") + stem + (intent.RenameSuffix ?? "") + ext);
                return new AiOperation("rename", entry.Id, NewName: updated);
            }).ToArray());
        if (intent.Grouping.Length > 0)
        {
            return AiLocalPlanner.CreateGrouped(scope, intent.Action, entry => intent.Grouping switch
            {
                "extension" => entry.Directory ? "폴더" : Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "확장자없음",
                "category" => AiLocalPlanner.Category(entry.Name, entry.Directory),
                "date" => File.GetLastWriteTime(entry.Path).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                "year" => File.GetLastWriteTime(entry.Path).ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
                "size" => entry.Directory ? "폴더" : new FileInfo(entry.Path).Length switch
                { <= 1024 * 1024 => "작은파일_1MB이하", <= 100L * 1024 * 1024 => "중간파일_100MB이하", _ => "큰파일_100MB초과" },
                _ => throw new ArgumentException("지원하지 않는 분류 기준입니다.")
            });
        }
        return new AiPlan(1, scope.Id, scope.Entries.Select(entry => new AiOperation(intent.Action, entry.Id, TargetId: "destination")).ToArray());
    }
}
