namespace Sunrise.FileManager.Services;

/// <summary>
/// Read-only discovery -> immutable scope -> validated proposal.
/// This layer never changes a file. Only AiPlanService.ExecuteAsync may mutate,
/// after the UI separately confirms the validated preview.
/// </summary>
public static class AiAgentOrchestrator
{
    public sealed record Proposal(AiNaturalLanguageService.Intent Intent, IReadOnlyList<string> Matches,
        AiValidatedPlan? ValidatedPlan, IReadOnlyList<AiPreviewRow> PreviewRows,
        int UnreadableDocuments = 0, int UnsupportedDocuments = 0)
    {
        public bool SearchOnly => ValidatedPlan is null;
    }

    public static Proposal Prepare(string folder, IReadOnlyList<string> selected, string? destination,
        string request, bool currentFolderAndDescendants, CollisionPolicy collision, CancellationToken token, bool contentReadApproved = false)
    {
        var intent = AiNaturalLanguageService.Parse(request);
        return PrepareIntent(folder, selected, destination, intent, currentFolderAndDescendants, collision, token, contentReadApproved);
    }

    public static Proposal PrepareIntent(string folder, IReadOnlyList<string> selected, string? destination,
        AiNaturalLanguageService.Intent intent, bool currentFolderAndDescendants, CollisionPolicy collision,
        CancellationToken token, bool contentReadApproved = false)
    {
        if (currentFolderAndDescendants && (intent.Action is "rename" or "recycle"))
            throw new ArgumentException("이름 변경·휴지통 이동은 선택한 항목만 허용합니다.");
        if (intent.NeedsDestination && destination is null)
            throw new ArgumentException("복사·이동 대상 폴더를 먼저 지정하세요.");
        var discovered = AiNaturalLanguageService.Discover(folder, selected, currentFolderAndDescendants, intent, token);
        AiDocumentSearchService.Result? contentResults = null;
        IReadOnlyList<string> matches = discovered;
        if (intent.ContentContains is not null)
        {
            contentResults = AiDocumentSearchService.Filter(discovered, intent.ContentContains, contentReadApproved, token);
            matches = contentResults.Matches;
        }
        token.ThrowIfCancellationRequested();
        if (matches.Count == 0)
            return new(intent, matches, null, Array.Empty<AiPreviewRow>(), contentResults?.Unreadable ?? 0, contentResults?.Unsupported ?? 0);
        if (intent.SearchOnly)
            return new(intent, matches, null,
                matches.Select(path => new AiPreviewRow("검색", path, "원본 유지", "일치 · 변경 없음")).ToArray(),
                contentResults?.Unreadable ?? 0, contentResults?.Unsupported ?? 0);
        var scope = AiScope.Capture(folder, matches, destination, token, allowDescendantEntries: currentFolderAndDescendants);
        var plan = AiNaturalLanguageService.BuildPlan(scope, intent);
        var validated = AiPlanService.Validate(scope, plan, collision, token);
        return new(intent, matches, validated, validated.Rows,
            contentResults?.Unreadable ?? 0, contentResults?.Unsupported ?? 0);
    }
}
