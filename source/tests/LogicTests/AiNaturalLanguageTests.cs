using Sunrise.FileManager.Services;

public static class AiNaturalLanguageTests
{
    private static int _passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED natural-language: " + name);
        _passed++;
        Console.WriteLine("PASS natural-language: " + name);
    }
    private static void Reject(Action action, string name)
    {
        bool rejected = false;
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        { rejected = true; }
        Check(rejected, name);
    }
    public static async Task RunAsync()
    {
        var find = AiNaturalLanguageService.Parse("파일명에 '안성천' 포함된 PDF 파일 찾아줘");
        Check(find.SearchOnly && find.NameContains == "안성천" && find.Extension == ".pdf", "Korean keyword and extension search");
        var transfer = AiNaturalLanguageService.Parse("최근 7일 수정된 PDF 파일만 복사해 줘");
        Check(transfer.Action == "copy" && transfer.ModifiedWithinDays == 7, "recent PDF copy intent");
        var group = AiNaturalLanguageService.Parse("이미지 파일을 유형별로 분류해서 이동해 줘");
        Check(group.Action == "move" && group.Grouping == "category" && group.Category == "이미지", "grouped move intent");
        var copyGroup = AiNaturalLanguageService.Parse("유형별로 분류해서 복사해 줘");
        Check(copyGroup.Action == "copy" && copyGroup.Grouping == "category", "classification not confused with move");
        Reject(() => AiNaturalLanguageService.Parse("중복 삭제해 줘"), "delete blocked");
        Reject(() => AiNaturalLanguageService.Parse("복사하고 이동해 줘"), "conflicting intents blocked");
        Reject(() => AiNaturalLanguageService.Parse("보고서 관련 파일을 복사해 줘"), "unsupported semantic inference blocked");
        Reject(() => AiNaturalLanguageService.Parse("오늘 수정된 파일 찾아줘"), "ambiguous relative date blocked");
        Reject(() => AiNaturalLanguageService.Parse("유형별로 분류해 줘"), "no action specified");
        Reject(() => AiNaturalLanguageService.Parse("PDF를 제외하고 복사해 줘"), "negative filtering blocked");
        Reject(() => AiNaturalLanguageService.Parse("PDF랑 HWPX 찾아줘"), "unsupported multiple extensions blocked");

        string root = Path.Combine(Path.GetTempPath(), "SfmNL_" + Guid.NewGuid().ToString("N"));
        string nested = Path.Combine(root, "조사"), memo = Path.Combine(nested, "#memo");
        // Destination intentionally outside scope, preserving copy source and avoiding self-copy.
        string destination = root + "_output";
        try
        {
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(memo);
            Directory.CreateDirectory(destination);
            string chosen = Path.Combine(nested, "안성천_현황.pdf");
            string excluded = Path.Combine(nested, "다른사업.pdf");
            File.WriteAllText(chosen, "original");
            File.WriteAllText(excluded, "other");
            File.WriteAllText(Path.Combine(memo, "안성천_현황.pdf_memo_26.10.18_user.sunmemo"), "private memo");
            var search = AiAgentOrchestrator.Prepare(root, [], null, "파일명에 '안성천' 포함된 PDF 파일 찾아줘", true,
                CollisionPolicy.Skip, CancellationToken.None);
            Check(search.SearchOnly && search.Matches.Count == 1 && search.Matches[0] == chosen &&
                !search.PreviewRows.Any(x => x.Action == "move"), "recursive search is read-only and excludes #memo");
            var planned = AiAgentOrchestrator.Prepare(root, [], destination, "파일명에 '안성천' 포함된 PDF 파일 복사해 줘", true,
                CollisionPolicy.Skip, CancellationToken.None);
            Check(!planned.SearchOnly && planned.ValidatedPlan is not null && planned.PreviewRows.Count == 1 &&
                planned.PreviewRows[0].Action == "copy", "copy proposal uses old validated engine");
            Check(File.Exists(chosen) && !Directory.EnumerateFileSystemEntries(destination).Any(), "planning never writes files");
            string audit = Path.Combine(root + "_audit");
            var journal = new AiJournalStore(Path.Combine(audit, "Journal"), new TestJournalProtection());
            var executed = await AiPlanService.ExecuteAsync(planned.ValidatedPlan!, true, audit, null,
                CancellationToken.None, journal: journal);
            Check(executed.Completed == 1 && executed.Failed == 0 &&
                File.Exists(Path.Combine(destination, Path.GetFileName(chosen))) && File.Exists(chosen),
                "approved natural copy executes through shared transfer engine");
            Check(!File.Exists(Path.Combine(destination, Path.GetFileName(excluded))), "unmatched source never copied");
            File.Delete(Path.Combine(memo, "안성천_현황.pdf_memo_26.10.18_user.sunmemo"));
            FileMemoStore.Save(chosen, "file-associated memo", new DateTime(2026, 10, 18), "SfmAiTest");
            string moveDest = root + "_moved";
            Directory.CreateDirectory(moveDest);
            try
            {
                var preparedMove = AiAgentOrchestrator.Prepare(root, [], moveDest,
                    "파일명에 '안성천' 포함된 PDF 파일 이동해 줘", true,
                    CollisionPolicy.Skip, CancellationToken.None);
                var moved = await AiPlanService.ExecuteAsync(preparedMove.ValidatedPlan!, true, audit, null,
                    CancellationToken.None, journal: journal);
                string movedFile = Path.Combine(moveDest, Path.GetFileName(chosen));
                var memos = FileMemoStore.ListForFile(movedFile, out int unreadable);
                Check(moved.Completed == 1 && !File.Exists(chosen) && File.Exists(movedFile) &&
                    unreadable == 0 && memos.Count == 1 && memos[0].Text == "file-associated memo",
                    "approved AI move retains file memo binding");
            }
            finally { try { Directory.Delete(moveDest, true); } catch (IOException) { } }
            Reject(() => AiAgentOrchestrator.Prepare(root, [], null, "PDF 파일 복사해 줘", true,
                CollisionPolicy.Skip, CancellationToken.None), "copy without destination is blocked");
            Reject(() => AiAgentOrchestrator.Prepare(root, [], destination, "PDF 파일 이동해 줘", false,
                CollisionPolicy.Skip, CancellationToken.None), "no implicit selection or scope expansion");
            var empty = AiAgentOrchestrator.Prepare(root, [], null, "PNG 파일 찾아줘", true,
                CollisionPolicy.Skip, CancellationToken.None);
            Check(empty.SearchOnly && empty.Matches.Count == 0 && empty.PreviewRows.Count == 0, "empty search creates no operations");
            Reject(() => AiScope.Capture(root, [Path.Combine(memo, "안성천_현황.pdf_memo_26.10.18_user.sunmemo")],
                destination, CancellationToken.None, allowDescendantEntries: true), "direct memo source rejected");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
            try { Directory.Delete(destination, true); } catch (IOException) { }
            try { Directory.Delete(root + "_audit", true); } catch (IOException) { }
        }
        Console.WriteLine($"Natural-language agent checks: {_passed} PASS");
    }
}
