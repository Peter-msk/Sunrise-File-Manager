using System.IO;
using System.Text.RegularExpressions;

namespace Sunrise.FileManager.Services;

/// <summary>
/// Deterministic safety firewall for Onboard AI (Ollama) intent responses.
/// Rejects destructive, unrequested, or out-of-scope operations before planning.
/// </summary>
public static class AiModelIntentSafety
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static void CheckRequest(string request)
    {
        if (Regex.IsMatch(request, "영구|완전삭제|즉시삭제|덮어쓰|권한변경|명령어|스크립트|실행파일|강제|제외하고|빼고|하지\\s*마|복사하지|이동하지", Options))
            throw new ArgumentException("제외·부정·영구 삭제·덮어쓰기 명령은 AI 자동 계획에서 지원하지 않습니다.");
    }

    public static void Validate(string request, AiNaturalLanguageService.Intent intent)
    {
        CheckRequest(request);
        if (intent.Action is not ("search" or "copy" or "move" or "rename" or "recycle"))
            throw new InvalidDataException("온보드 AI가 허용되지 않은 작업을 반환했습니다.");
        string action = intent.Action;
        bool asked = action switch
        {
            "search" => Regex.IsMatch(request, "찾아|검색|골라|보여|조회|목록|확인", Options) &&
                !Regex.IsMatch(request, "복사|사본|백업|옮겨|옮기|이동|삭제|휴지통|붙여|이름\\s*변경", Options),
            "copy" => Regex.IsMatch(request, "복사|사본|백업|복제", Options) && !Regex.IsMatch(request, "이동|옮기|휴지통|삭제", Options),
            "move" => Regex.IsMatch(request, "이동|옮겨|옮기|모아", Options) && !Regex.IsMatch(request, "복사|사본|백업|휴지통|삭제", Options),
            "rename" => Regex.IsMatch(request, "파일명|이름", Options) && Regex.IsMatch(request, "붙여|추가|변경|바꿔", Options),
            "recycle" => Regex.IsMatch(request, "휴지통", Options) && Regex.IsMatch(request, "보내|넣어|이동|삭제|버려", Options),
            _ => false
        };
        if (!asked) throw new InvalidDataException("온보드 AI가 요청하지 않은 변경 작업을 제안했습니다. 명령을 명확히 입력하세요.");
        if (intent.Grouping is not ("" or "extension" or "category" or "date" or "year" or "size") ||
            (intent.Grouping.Length > 0 && action is not ("copy" or "move")))
            throw new InvalidDataException("분류 방식이 허용되지 않습니다.");
        if (intent.Grouping.Length > 0 && !Regex.IsMatch(request, "분류|정리|별|폴더별로", Options))
            throw new InvalidDataException("사용자가 요청하지 않은 분류 방식입니다.");
        if (intent.Grouping.Length > 0 && !(intent.Grouping switch
            {
                "extension" => Regex.IsMatch(request, "확장자|파일형식|포맷", Options),
                "category" => Regex.IsMatch(request, "유형|종류", Options),
                "date" => Regex.IsMatch(request, "날짜|일자", Options),
                "year" => Regex.IsMatch(request, "연도|년도", Options),
                "size" => Regex.IsMatch(request, "크기|용량", Options),
                _ => false
            })) throw new InvalidDataException("분류 기준이 요청과 다릅니다.");
        if (intent.Extension is { } ext)
        {
            if (!Regex.IsMatch(ext, "^\\.[a-z0-9]{1,8}$", Options) || !MatchesRequestedExtension(request, ext))
                throw new InvalidDataException("확장자 조건이 요청과 다릅니다.");
        }
        if (intent.Category is { } category)
        {
            bool validCat = category switch
            {
                "이미지" => Regex.IsMatch(request, "이미지|사진|그림", Options),
                "동영상" => Regex.IsMatch(request, "동영상|비디오|영상", Options),
                "문서" => Regex.IsMatch(request, "문서|서류|보고서", Options),
                "오디오" => Regex.IsMatch(request, "오디오|음악|녹음|음성", Options),
                "압축파일" => Regex.IsMatch(request, "압축|아카이브", Options),
                "코드_설정" => Regex.IsMatch(request, "코드|소스|설정", Options),
                _ => false
            };
            if (!validCat) throw new InvalidDataException("유형 조건이 요청과 다릅니다.");
        }
        if (intent.NameContains is { } name && (name.Length is < 1 or > 100 || !request.Contains(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("파일명 조건이 요청과 다릅니다.");
        if (intent.ContentContains is { } content && (content.Length is < 1 or > 100 ||
            !request.Contains(content, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(request, "본문|내용", Options)))
            throw new InvalidDataException("본문 조건이 요청과 다릅니다.");
        if (intent.ModifiedWithinDays is int days)
        {
            bool daysOk = days is >= 1 and <= 365 && (
                Regex.IsMatch(request, $"최근\\s*{days}\\s*일", Options) ||
                (days == 1 && Regex.IsMatch(request, "오늘|금일", Options)) ||
                (days == 2 && Regex.IsMatch(request, "어제", Options)) ||
                (days == 7 && Regex.IsMatch(request, "이번\\s*주|일주일|주간", Options)) ||
                (days == 30 && Regex.IsMatch(request, "이번\\s*달|한\\s*달|월간", Options)));
            if (!daysOk) throw new InvalidDataException("수정일 조건이 요청과 다릅니다.");
        }
        if (intent.RenamePrefix is not null || intent.RenameSuffix is not null)
        {
            if (action != "rename" || (intent.RenamePrefix is null) == (intent.RenameSuffix is null))
                throw new InvalidDataException("이름 변경 조건이 잘못되었습니다.");
            string affix = intent.RenamePrefix ?? intent.RenameSuffix!;
            if (affix.Length is < 1 or > 80 || !request.Contains(affix, StringComparison.Ordinal))
                throw new InvalidDataException("이름 변경 값이 요청과 다릅니다.");
            AiPlanService.Name("sample" + affix + ".txt");
        }
        else if (action == "rename") throw new InvalidDataException("이름 변경 접두어·접미어를 지정해야 합니다.");
        if (action == "recycle" && (intent.ContentContains is not null || intent.Grouping.Length > 0))
            throw new InvalidDataException("휴지통 작업에 자동 내용 분류를 혼합할 수 없습니다.");
    }

    private static bool MatchesRequestedExtension(string request, string ext)
    {
        string bare = ext.TrimStart('.').ToLowerInvariant();
        if (request.Contains(bare, StringComparison.OrdinalIgnoreCase)) return true;
        return bare switch
        {
            "hwpx" or "hwp" => Regex.IsMatch(request, "한글|훈민정음", Options),
            "xlsx" or "xls" or "csv" => Regex.IsMatch(request, "엑셀|스프레드시트|표", Options),
            "docx" or "doc" => Regex.IsMatch(request, "워드|ms워드", Options),
            "pptx" or "ppt" => Regex.IsMatch(request, "파워포인트|발표자료|피피티|프레젠테이션", Options),
            "zip" or "7z" or "rar" => Regex.IsMatch(request, "압축", Options),
            "txt" => Regex.IsMatch(request, "텍스트|메모장", Options),
            "md" => Regex.IsMatch(request, "마크다운", Options),
            _ => false
        };
    }
}
