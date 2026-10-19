using System.Text.Json;
using System.Security.Cryptography;
using Sunrise.FileManager.Services;

public static class AiPlanTests
{
    private static int _passed;
    private static void Check(bool value, string name)
    { if (!value) throw new Exception("FAILED: " + name); _passed++; Console.WriteLine("PASS AI: " + name); }
    private static async Task Reject(Func<Task> action, string name)
    {
        bool rejected = false;
        try { await action(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or JsonException or OperationCanceledException)
        { rejected = true; }
        Check(rejected, name);
    }
    private static Task Validate(AiScope scope, AiPlan plan, CollisionPolicy policy = CollisionPolicy.Skip)
    { AiPlanService.Validate(scope, plan, policy, CancellationToken.None); return Task.CompletedTask; }
    private static Task<AiExecutionResult> Execute(AiValidatedPlan plan, bool approved, string audit,
        IProgress<AiPreviewRow>? progress, CancellationToken token) => AiPlanService.ExecuteAsync(plan, approved, audit, progress, token,
            journal: new AiJournalStore(Path.Combine(audit, "Journal"), new TestJournalProtection()));
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "SunriseAi_" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source"), target = Path.Combine(root, "target"), audit = Path.Combine(root, "audit");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        try
        {
            for (int i = 0; i < 30; i++) File.WriteAllText(Path.Combine(source, $"file{i:D3}.txt"), "contents-" + i);
            string[] originals = Directory.GetFiles(source);
            var scope = AiScope.Capture(source, originals, target, CancellationToken.None);
            var local = AiLocalPlanner.Create(scope, "접두어 추가: 사업_");
            Check(local.Operations.Count == 30 && local.Operations[0].NewName!.StartsWith("사업_"), "P12 local prefix grammar");
            var preview = AiPlanService.Validate(scope, AiPlanService.Parse(AiPlanService.Serialize(local)), CollisionPolicy.Skip, CancellationToken.None);
            Check(originals.All(File.Exists) && Directory.GetFiles(source).Length == 30, "P12 preview changes zero source files");
            await Reject(() => Execute(preview, false, audit, null, CancellationToken.None), "P12 approval mandatory");
            Check(originals.All(File.Exists), "P12 rejected approval preserves originals");
            var renamed = await Execute(preview, true, audit, null, CancellationToken.None);
            Check(renamed.Completed == 30 && renamed.Failed == 0 && Directory.GetFiles(source).All(x => Path.GetFileName(x).StartsWith("사업_")), "P12 30 approved renames");
            Check(Directory.GetFiles(source).All(x => File.ReadAllText(x).StartsWith("contents-")), "P12 rename content preserved");
            await Reject(() => Execute(preview, true, audit, null, CancellationToken.None), "P12 duplicate execution rejected");
            string journal = File.ReadAllText(renamed.AuditPath);
            Check(!journal.Contains(source) && !journal.Contains("사업_"), "audit excludes filenames and absolute paths");

            var legacySession = JsonSerializer.Deserialize<AppSession>("{\"VisiblePanels\":2,\"LayoutVersion\":10,\"Panels\":[{\"TabPaths\":[\"C:/Work\"],\"ChromeActive\":true,\"BrowserTabs\":[\"https://example.com\"]}]}");
            Check(legacySession?.VisiblePanels == 2 && legacySession.Panels[0].TabPaths[0] == "C:/Work" &&
                !JsonSerializer.Serialize(legacySession).Contains("ChromeActive"), "P12 legacy browser session preserves explorer paths");
            var empty = AiScope.Capture(source, [], null, CancellationToken.None);
            var mkdir = AiPlanService.Validate(empty, AiLocalPlanner.Create(empty, "폴더 생성: 검토완료"), CollisionPolicy.Skip, CancellationToken.None);
            Check(!Directory.Exists(Path.Combine(source, "검토완료")), "mkdir preview has no write");
            var made = await Execute(mkdir, true, audit, null, CancellationToken.None);
            Check(made.Completed == 1 && Directory.Exists(Path.Combine(source, "검토완료")), "mkdir uses existing action service");
            scope = AiScope.Capture(source, Directory.GetFiles(source), target, CancellationToken.None);
            string id = scope.Entries[0].Id;
            await Reject(() => Validate(scope, new(1, "stale", [new("rename", id, "x.txt")])), "stale scope");
            foreach (string op in new[] { "delete", "shell", "powershell", "cmd" })
                await Reject(() => Validate(scope, new(1, scope.Id, [new(op, id)])), "unsupported operation " + op);
            foreach (string name in new[] { "../escape.txt", "..\\escape.txt", "CON.txt", "COM¹.txt", "bad.", "bad ", "a:b", "", "C:\\out" })
                await Reject(() => Validate(scope, new(1, scope.Id, [new("rename", id, name)])), "invalid name " + name);
            await Reject(() => Validate(scope, new(1, scope.Id, [new("rename", "outside", "safe.txt")])), "unknown entry ID");
            await Reject(() => Validate(scope, new(1, scope.Id, [new("rename", id, "safe.txt"), new("rename", id, "again.txt")])), "duplicate source");
            await Reject(() => Validate(scope, new(1, scope.Id, [new("rename", id, scope.Entries[1].Name)])), "rename collision");
            await Reject(() => Validate(scope, new(1, scope.Id, [new("move", id, TargetId: "current")])), "same source destination");
            await Reject(() => { AiPlanService.Parse("{\"schema_version\":1,\"schema_version\":2}"); return Task.CompletedTask; }, "duplicate JSON fields");
            await Reject(() => { AiPlanService.Parse("{\"schema_version\":1,\"scope_id\":\"x\",\"operations\":[],\"shell\":\"cmd\"}"); return Task.CompletedTask; }, "unknown JSON fields");
            await Reject(() => { AiPlanService.Parse("not json"); return Task.CompletedTask; }, "malformed JSON");
            await Reject(() => { AiPlanService.Parse(new string('x', 1024 * 1024 + 1)); return Task.CompletedTask; }, "response byte limit");
            var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Reject(() => Task.Run(() => AiScope.Capture(source, Directory.GetFiles(source), target, canceled.Token)), "snapshot cancellation");
            var stale = AiPlanService.Validate(scope, AiLocalPlanner.Create(scope, "접두어 추가: stale_"), CollisionPolicy.Skip, CancellationToken.None);
            string changed = scope.Entries[0].Path;
            DateTime stamp = File.GetLastWriteTimeUtc(changed);
            string before = File.ReadAllText(changed);
            File.WriteAllText(changed, new string('X', before.Length)); File.SetLastWriteTimeUtc(changed, stamp);
            await Reject(() => Execute(stale, true, audit, null, CancellationToken.None), "same length timestamp edit detected by hash");
            Check(scope.Entries.All(x => File.Exists(x.Path)), "stale plan has no partial write");
            File.WriteAllText(changed, before);
            scope = AiScope.Capture(source, Directory.GetFiles(source), target, CancellationToken.None);
            var collisionPreview = AiPlanService.Validate(scope, new(1, scope.Id, [new("copy", scope.Entries[0].Id, TargetId: "destination")]), CollisionPolicy.Skip, CancellationToken.None);
            File.WriteAllText(Path.Combine(target, scope.Entries[0].Name), "keep-target");
            await Reject(() => Execute(collisionPreview, true, audit, null, CancellationToken.None), "target appeared after approval preview");
            var skip = AiPlanService.Validate(scope, new(1, scope.Id, [new("copy", scope.Entries[0].Id, TargetId: "destination")]), CollisionPolicy.Skip, CancellationToken.None);
            var skipped = await Execute(skip, true, audit, null, CancellationToken.None);
            Check(skipped.Skipped == 1 && File.ReadAllText(Path.Combine(target, scope.Entries[0].Name)) == "keep-target", "P13 skip never overwrites");
            var numbered = AiPlanService.Validate(scope, new(1, scope.Id, [new("copy", scope.Entries[0].Id, TargetId: "destination")]), CollisionPolicy.Rename, CancellationToken.None);
            Check(numbered.Rows[0].After.Contains(" (1)"), "P13 exact collision name visible before execution");
            var numberedResult = await Execute(numbered, true, audit, null, CancellationToken.None);
            Check(numberedResult.Completed == 1 && File.Exists(numbered.Rows[0].After), "P13 executes exact preview destination");

            string batch = Path.Combine(root, "batch"), copied = Path.Combine(root, "copied"), moved = Path.Combine(root, "moved");
            Directory.CreateDirectory(batch); Directory.CreateDirectory(copied); Directory.CreateDirectory(moved);
            for (int i = 0; i < 1000; i++) File.WriteAllText(Path.Combine(batch, $"batch{i:D4}.dat"), "payload:" + i);
            var hashes = Directory.GetFiles(batch).ToDictionary(p => Path.GetFileName(p)!, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            var copyScope = AiScope.Capture(batch, Directory.GetFiles(batch), copied, CancellationToken.None);
            var copyPlan = AiPlanService.Validate(copyScope, AiLocalPlanner.Create(copyScope, "복사"), CollisionPolicy.Skip, CancellationToken.None);
            Check(Directory.GetFiles(copied).Length == 0, "P13 1000-copy preview zero writes");
            var copyResult = await Execute(copyPlan, true, audit, null, CancellationToken.None);
            Check(copyResult.Completed == 1000 && Directory.GetFiles(batch).Length == 1000 && Directory.GetFiles(copied).Length == 1000, "P13 1000 file copy counts and source preservation");
            Check(Directory.GetFiles(copied).All(p => hashes[Path.GetFileName(p)] == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))), "P13 1000 copied SHA256 hashes");
            var moveScope = AiScope.Capture(copied, Directory.GetFiles(copied), moved, CancellationToken.None);
            var movePlan = AiPlanService.Validate(moveScope, AiLocalPlanner.Create(moveScope, "이동"), CollisionPolicy.Skip, CancellationToken.None);
            var moveResult = await Execute(movePlan, true, audit, null, CancellationToken.None);
            Check(moveResult.Completed == 1000 && Directory.GetFiles(copied).Length == 0 && Directory.GetFiles(moved).Length == 1000, "P13 1000 file move counts");
            Check(Directory.GetFiles(moved).All(p => hashes[Path.GetFileName(p)] == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))), "P13 1000 moved SHA256 hashes");
            var classification = AiScope.Capture(batch, Directory.GetFiles(batch).Take(3).ToArray(), target, CancellationToken.None);
            var group = AiPlanService.Validate(classification, AiLocalPlanner.Create(classification, "확장자별 복사"), CollisionPolicy.Skip, CancellationToken.None);
            var groupResult = await Execute(group, true, audit, null, CancellationToken.None);
            Check(groupResult.Completed == 4 && Directory.GetFiles(Path.Combine(target, "DAT")).Length == 3, "P13 extension grouping mkdir and copy");
            var datePlan = AiPlanService.Validate(classification, AiLocalPlanner.Create(classification, "날짜별 복사"), CollisionPolicy.Skip, CancellationToken.None);
            Check(datePlan.Rows.Count == 4 && datePlan.Rows[0].Action == "mkdir", "P13 date grouping plan");
            var stopScope = AiScope.Capture(batch, Directory.GetFiles(batch).Take(3).ToArray(), moved, CancellationToken.None);
            var stopPlan = AiPlanService.Validate(stopScope, AiLocalPlanner.Create(stopScope, "접두어 추가: cancel_"), CollisionPolicy.Skip, CancellationToken.None);
            using var stop = new CancellationTokenSource();
            var stopResult = await Execute(stopPlan, true, audit, new InlineProgress(_ => stop.Cancel()), stop.Token);
            Check(stopResult.Canceled && stopResult.Completed == 1 && stopResult.Rows.Count == 3 && stopResult.Rows[1].Status.Contains("미실행"), "P12 cancel after first step records partial completion");
            var failureScope = AiScope.Capture(batch, Directory.GetFiles(batch).Where(x => !Path.GetFileName(x).StartsWith("cancel_")).Take(3).ToArray(), target, CancellationToken.None);
            var failurePlan = AiPlanService.Validate(failureScope, AiLocalPlanner.Create(failureScope, "접두어 추가: partial_"), CollisionPolicy.Skip, CancellationToken.None);
            var failureResult = await Execute(failurePlan, true, audit, new InlineProgress(_ =>
            {
                if (File.Exists(failureScope.Entries[1].Path)) File.WriteAllText(failureScope.Entries[1].Path, "changed after first operation");
            }), CancellationToken.None);
            Check(failureResult.Completed == 1 && failureResult.Failed == 1 && failureResult.Rows[2].Status.Contains("미실행") &&
                File.Exists(failureScope.Entries[2].Path), "P12 partial failure stops later writes and reports pending items");
            var deniedScope = AiScope.Capture(batch, Directory.GetFiles(batch).Take(1).ToArray(), target, CancellationToken.None);
            var deniedPlan = AiPlanService.Validate(deniedScope, AiLocalPlanner.Create(deniedScope, "접두어 추가: denied_"), CollisionPolicy.Skip, CancellationToken.None);
            string invalidAudit = Path.Combine(root, "audit-is-file"); File.WriteAllText(invalidAudit, "keep");
            await Reject(() => Execute(deniedPlan, true, invalidAudit, null, CancellationToken.None), "audit write failure blocks file operations");
            Check(File.Exists(deniedScope.Entries[0].Path), "failed audit preserves source");
            var counterPlan = AiLocalPlanner.Create(deniedScope, "일련번호: 자료_");
            Check(counterPlan.Operations[0].NewName == "자료_001.dat", "P12 sequential names retain extension");
            foreach (string reserved in new[] { "CON .txt", "LPT².txt" })
                await Reject(() => { FileActionsService.ValidateName(reserved); return Task.CompletedTask; }, "shared name validator reserved alias " + reserved);
            string folderSource = Path.Combine(root, "folder-source"), folderTarget = Path.Combine(root, "folder-target");
            Directory.CreateDirectory(folderSource); Directory.CreateDirectory(folderTarget);
            string nested = Path.Combine(folderSource, "nested"); Directory.CreateDirectory(nested); File.WriteAllText(Path.Combine(nested, "child.txt"), "child");
            var nestedScope = AiScope.Capture(folderSource, [nested], folderTarget, CancellationToken.None);
            var nestedPlan = AiPlanService.Validate(nestedScope, AiLocalPlanner.Create(nestedScope, "복사"), CollisionPolicy.Skip, CancellationToken.None);
            var nestedResult = await Execute(nestedPlan, true, audit, null, CancellationToken.None);
            Check(nestedResult.Completed == 1 && File.ReadAllText(Path.Combine(folderTarget, "nested", "child.txt")) == "child", "P13 recursive folder copy");
            var selfScope = AiScope.Capture(folderSource, [nested], nested, CancellationToken.None);
            await Reject(() => Validate(selfScope, AiLocalPlanner.Create(selfScope, "이동")), "P13 folder into itself rejected");
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                string link = Path.Combine(folderSource, "link");
                try
                {
                    Directory.CreateSymbolicLink(link, folderTarget);
                    await Reject(() => Task.Run(() => AiScope.Capture(link, [], null, CancellationToken.None)), "junction or symlink ancestor rejected");
                }
                catch (UnauthorizedAccessException) { Console.WriteLine("NOT_RUN: symlink creation requires Windows developer mode"); }
            }
            Check(AiOllamaService.IsAllowedLoopback(new Uri("http://127.0.0.1:11434")) &&
                  !AiOllamaService.IsAllowedLoopback(new Uri("https://generativelanguage.googleapis.com")),
                  "Ollama enforces loopback-only endpoint and blocks cloud URLs");
            Console.WriteLine($"AI TESTS PASS: {_passed}");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class InlineProgress(Action<AiPreviewRow> action) : IProgress<AiPreviewRow>
    { public void Report(AiPreviewRow value) => action(value); }
}
