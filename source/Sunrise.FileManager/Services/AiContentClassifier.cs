using System.IO;

namespace Sunrise.FileManager.Services;

public enum AiDataGrade { Internal, General, Protected }
public sealed record AiContentRule(string Folder, IReadOnlyList<string> Keywords);
public sealed record AiClassificationReport(AiPlan? Plan, IReadOnlyList<AiPreviewRow> Rows);

/// <summary>Local-only literal keyword matching over supported document bodies. No document upload or shell commands.</summary>
public static class AiContentClassifier
{
    public const long MaxContentFileBytes = 1024 * 1024;
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".log", ".pdf", ".docx", ".hwpx", ".hwp", ".xlsx", ".pptx" };
    public static IReadOnlyList<AiContentRule> ParseRules(string text)
    {
        if (text.Length > 10_000) throw new ArgumentException("분류 규칙은 10,000자 이하여야 합니다.");
        var rules = new List<AiContentRule>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int split = line.IndexOf('=');
            if (split <= 0 || rules.Count == 20) throw new ArgumentException("최대 20개 규칙을 '폴더=키워드1,키워드2' 형식으로 입력하세요.");
            string name = AiPlanService.Name(line[..split].Trim());
            if (!names.Add(name)) throw new ArgumentException("분류 폴더 이름이 중복됩니다.");
            string[] words = line[(split + 1)..].Split(',', StringSplitOptions.TrimEntries);
            if (words.Length is < 1 or > 10 || words.Any(w => w.Length is < 1 or > 100))
                throw new ArgumentException("규칙당 1~10개 키워드, 키워드당 1~100자를 입력하세요.");
            rules.Add(new(name, Array.AsReadOnly(words)));
        }
        if (rules.Count == 0) throw new ArgumentException("분류 규칙을 입력하세요.");
        return rules.AsReadOnly();
    }
    public static AiClassificationReport Create(AiScope scope, string rulesText, string action,
        bool contentReadApproved, AiWorkLimits limits, CancellationToken token)
    {
        if (!contentReadApproved) throw new InvalidOperationException("내용 읽기에 대한 명시적 동의가 필요합니다.");
        limits.Validate(); var rules = ParseRules(rulesText);
        if (scope.Entries.Count > limits.MaxFiles) throw new IOException("설정된 파일 수 상한을 초과했습니다.");
        var rows = new List<AiPreviewRow>(); var groups = new Dictionary<string, string>(); long bytes = 0;
        foreach (var entry in scope.Entries)
        {
            token.ThrowIfCancellationRequested();
            string status; string? folder = null;
            if (entry.Directory || !Supported.Contains(Path.GetExtension(entry.Name))) status = "건너뜀 · 지원하지 않는 파일 형식";
            else
            {
                long length = new FileInfo(AiPlanService.OrdinaryPath(entry.Path)).Length;
                string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                bool office = ext is ".pdf" or ".docx" or ".hwpx" or ".hwp" or ".xlsx" or ".pptx";
                long max = office ? DocumentContentSearch.MaxDocumentBytes : MaxContentFileBytes;
                if (length > max) status = "건너뜀 · 이 파일 형식의 본문 읽기 크기 상한 초과";
                else
                {
                    bytes = checked(bytes + length);
                    if (bytes > limits.MaxBytes) throw new IOException("설정된 내용 읽기 용량을 초과했습니다.");
                    var result = DocumentContentSearch.FindMany(entry.Path, rules.SelectMany(r => r.Keywords), token);
                    if (result.Status is ContentSearchStatus.Unsupported or ContentSearchStatus.NoText or ContentSearchStatus.SizeLimit)
                        status = "건너뜀 · " + (result.Detail ?? result.Status.ToString());
                    else
                    {
                        var matched = rules.Where(rule => rule.Keywords.Any(result.Words.Contains)).ToArray();
                        status = matched.Length == 0 ? "미분류 · 키워드 없음" : matched.Length > 1 ? "확인 필요 · 여러 규칙에 일치" : "분류 제안 · 지원 본문 전체 검색";
                        if (matched.Length == 1) { folder = matched[0].Folder; groups.Add(entry.Id, folder); }
                    }
                }
            }
            rows.Add(new("로컬 내용 분류", entry.Path, folder ?? "—", status));
        }
        AiPlan? plan = groups.Count == 0 ? null : AiLocalPlanner.CreateGrouped(scope, action,
            entry => groups.TryGetValue(entry.Id, out string? folder) ? folder : null);
        return new(plan, rows.AsReadOnly());
    }
    public static void RequireExternalConsent(AiDataGrade grade, bool approved)
    {
        if (grade != AiDataGrade.General || !approved)
            throw new InvalidOperationException("외부 AI는 일반 자료로 분류하고 전송 내용에 동의한 경우에만 사용할 수 있습니다.");
    }
}
