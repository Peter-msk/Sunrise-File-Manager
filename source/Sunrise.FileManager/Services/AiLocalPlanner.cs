using System.IO;
using System.Globalization;

namespace Sunrise.FileManager.Services;

/// <summary>Explicit local grammar; unsupported language is never guessed into a file action.</summary>
public static class AiLocalPlanner
{
    public const string Help = "접두어 추가: 사업_\n일련번호: 보고서_\n폴더 생성: 검토완료\n복사 / 이동\n확장자별 / 날짜별 / 유형별 / 크기별 / 연도별 + 복사 또는 이동\n휴지통 이동";
    public static AiPlan Create(AiScope scope, string command)
    {
        command = command.Trim();
        var ops = new List<AiOperation>();
        if (command.StartsWith("폴더 생성:", StringComparison.Ordinal))
            ops.Add(new("mkdir", NewName: command[6..].Trim(), TargetId: "current"));
        else if (command.StartsWith("접두어 추가:", StringComparison.Ordinal))
        {
            string prefix = command[7..].Trim();
            if (prefix.Length == 0) throw new ArgumentException("접두어를 입력하세요.");
            foreach (var entry in scope.Entries) ops.Add(new("rename", entry.Id, prefix + entry.Name));
        }
        else if (command.StartsWith("일련번호:", StringComparison.Ordinal))
        {
            string prefix = command[5..].Trim(); int n = 1;
            foreach (var entry in scope.Entries)
                ops.Add(new("rename", entry.Id, prefix + (n++).ToString("D3", CultureInfo.InvariantCulture) +
                    (entry.Directory ? "" : Path.GetExtension(entry.Name))));
        }
        else if (command == "휴지통 이동")
        { foreach (var entry in scope.Entries) ops.Add(new("recycle", entry.Id)); }
        else if (command is "복사" or "이동" or "확장자별 복사" or "확장자별 이동" or "날짜별 복사" or "날짜별 이동" or
            "유형별 복사" or "유형별 이동" or "크기별 복사" or "크기별 이동" or "연도별 복사" or "연도별 이동")
        {
            string action = command.EndsWith("복사", StringComparison.Ordinal) ? "copy" : "move";
            return CreateGrouped(scope, action, entry =>
            {
                if (command.StartsWith("확장자별", StringComparison.Ordinal))
                    return entry.Directory ? "폴더" : Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "확장자없음";
                if (command.StartsWith("날짜별", StringComparison.Ordinal)) return File.GetLastWriteTime(entry.Path).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (command.StartsWith("연도별", StringComparison.Ordinal)) return File.GetLastWriteTime(entry.Path).ToString("yyyy", CultureInfo.InvariantCulture);
                if (command.StartsWith("유형별", StringComparison.Ordinal)) return Category(entry.Name, entry.Directory);
                if (command.StartsWith("크기별", StringComparison.Ordinal))
                    return entry.Directory ? "폴더" : new FileInfo(entry.Path).Length switch
                    { <= 1024 * 1024 => "작은파일_1MB이하", <= 100L * 1024 * 1024 => "중간파일_100MB이하", _ => "큰파일_100MB초과" };
                return "";
            });
        }
        else throw new ArgumentException("로컬 명령 형식을 확인하세요.\n" + Help);
        if (ops.Count == 0) throw new ArgumentException("탐색기에서 작업할 항목을 선택하세요.");
        return new(1, scope.Id, ops.AsReadOnly());
    }
    internal static AiPlan CreateGrouped(AiScope scope, string action, Func<AiEntry, string?> folderForEntry)
    {
        if (action is not ("copy" or "move")) throw new ArgumentException("복사 또는 이동만 분류 작업으로 허용됩니다.");
        string target = scope.Targets.SingleOrDefault(x => x.Id == "destination")?.Path ??
            throw new ArgumentException("복사·이동 대상 폴더를 선택하세요.");
        var ops = new List<AiOperation>(); var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in scope.Entries)
        {
            string? subfolder = folderForEntry(entry);
            if (subfolder is null) continue;
            if (subfolder.Length > 0)
            {
                AiPlanService.Name(subfolder);
                if (folders.Add(subfolder) && !Directory.Exists(Path.Combine(target, subfolder)))
                    ops.Add(new("mkdir", NewName: subfolder, TargetId: "destination"));
            }
            ops.Add(new(action, entry.Id, TargetId: "destination", Subfolder: subfolder.Length > 0 ? subfolder : null));
        }
        if (ops.Count == 0) throw new ArgumentException("분류 규칙에 맞는 항목이 없습니다.");
        return new(1, scope.Id, ops.AsReadOnly());
    }
    public static string Category(string name, bool directory)
    {
        if (directory) return "폴더";
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".pdf" or ".doc" or ".docx" or ".hwp" or ".hwpx" or ".txt" or ".md" or ".xls" or ".xlsx" or ".csv" or ".ppt" or ".pptx" => "문서",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".tif" or ".tiff" or ".bmp" or ".webp" => "이미지",
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => "동영상",
            ".mp3" or ".wav" or ".flac" or ".m4a" => "오디오",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "압축파일",
            ".cs" or ".py" or ".js" or ".ts" or ".json" or ".xml" or ".html" or ".css" or ".ps1" => "코드_설정",
            _ => "기타"
        };
    }
}
