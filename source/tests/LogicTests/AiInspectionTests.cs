using System.Security.Cryptography;
using Sunrise.FileManager.Services;

public static class AiInspectionTests
{
    private static int _passed;
    private static void Check(bool value, string name)
    { if (!value) throw new Exception("FAILED P15: " + name); _passed++; Console.WriteLine("PASS P15: " + name); }
    private static async Task Reject(Func<Task> action, string name)
    {
        bool rejected = false;
        try { await action(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        { rejected = true; }
        Check(rejected, name);
    }
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "SunriseP15_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var unlimited = new AiWorkLimits(1000, 16 * 1024 * 1024);
        try
        {
            string source = Path.Combine(root, "source"), target = Path.Combine(root, "target");
            Directory.CreateDirectory(source); Directory.CreateDirectory(target);
            string a = Path.Combine(source, "alpha.txt"), b = Path.Combine(source, "different-name.bin"), c = Path.Combine(source, "same-size.txt");
            File.WriteAllText(a, "abc"); File.WriteAllText(b, "abc"); File.WriteAllText(c, "xyz");
            string emptyA = Path.Combine(source, "empty1"), emptyB = Path.Combine(source, "empty2");
            File.WriteAllBytes(emptyA, []); File.WriteAllBytes(emptyB, []);
            string singleton = Path.Combine(source, "only-size.dat"); File.WriteAllText(singleton, "unique-size");
            string[] originals = Directory.GetFiles(source);
            var hashes = originals.ToDictionary(x => x, x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))));
            var report = await AiInspectionService.FindDuplicatesAsync([source], true, unlimited, null, CancellationToken.None);
            Check(report.Groups == 2 && report.Rows.Count == 4 && report.Hashed == 5 && !report.Limited, "SHA256 detects different-name duplicates and empty-file group");
            Check(!report.Rows.Any(x => x.Path == c), "equal size with different contents is not a duplicate");
            Check(!report.Rows.Any(x => x.Path == singleton) && report.BytesRead == 9, "singleton sizes are never read");
            Check(originals.All(x => hashes[x] == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x)))) && Directory.GetFiles(source).Length == originals.Length,
                "duplicate inspection has zero source changes");
            var repeated = await AiInspectionService.FindDuplicatesAsync([source, a, a], true, unlimited, null, CancellationToken.None);
            Check(repeated.Discovered == report.Discovered && repeated.Rows.Count == 4, "overlapping root selections deduplicate paths");
            string nested = Path.Combine(source, "nested"); Directory.CreateDirectory(nested); File.WriteAllText(Path.Combine(nested, "copy.txt"), "abc");
            var shallow = await AiInspectionService.FindDuplicatesAsync([source], false, unlimited, null, CancellationToken.None);
            var deep = await AiInspectionService.FindDuplicatesAsync([source], true, unlimited, null, CancellationToken.None);
            Check(shallow.Discovered == originals.Length && deep.Discovered == originals.Length + 1, "recursion is explicit and counts are accurate");
            var capped = await AiInspectionService.FindDuplicatesAsync([source], true, new(2, 1024), null, CancellationToken.None);
            Check(capped.Limited && capped.Discovered <= 2, "file count cap returns a labeled partial report");
            var byteCap = await AiInspectionService.FindDuplicatesAsync([a, b], false, new(10, 3), null, CancellationToken.None);
            Check(byteCap.Limited && byteCap.BytesRead == 3 && byteCap.Rows.Count == 0, "hash byte cap never reads a second full file or claims a complete group");
            using var stop = new CancellationTokenSource(); stop.Cancel();
            var canceled = await AiInspectionService.FindDuplicatesAsync([source], true, unlimited, null, stop.Token);
            Check(canceled.Canceled && canceled.Hashed == 0, "pre-canceled scan has no hash results");
            var missing = await AiInspectionService.FindDuplicatesAsync([Path.Combine(root, "missing")], true, unlimited, null, CancellationToken.None);
            Check(missing.ErrorCount == 1 && missing.Rows.Count == 0, "inaccessible or missing root reports failure instead of a false clean result");
            bool mutated = false;
            var changed = await AiInspectionService.FindDuplicatesAsync([a, b], false, unlimited, new InlineProgress(p =>
            { if (!mutated && p.Phase == "SHA-256 검사") { File.WriteAllText(p.Current, "changed-size"); mutated = true; } }), CancellationToken.None);
            Check(changed.ErrorCount > 0 && changed.Rows.Count == 0, "source changed after enumeration is excluded");
            File.WriteAllText(a, "abc"); File.WriteAllText(b, "abc");
            string link = Path.Combine(source, "link");
            try
            {
                Directory.CreateSymbolicLink(link, nested);
                var linked = await AiInspectionService.FindDuplicatesAsync([source], true, unlimited, null, CancellationToken.None);
                Check(linked.LinksSkipped >= 1 && linked.Discovered == deep.Discovered, "symlink directory is not traversed");
            }
            catch (UnauthorizedAccessException) { Console.WriteLine("NOT_RUN P15: symlink creation requires permission"); }
            foreach (var limit in new[] { new AiWorkLimits(0, 1), new AiWorkLimits(50_001, 1), new AiWorkLimits(1, 0), new AiWorkLimits(1, 9L * 1024 * 1024 * 1024), new AiWorkLimits(1, 1, 201) })
                await Reject(() => { limit.Validate(); return Task.CompletedTask; }, "invalid user cap rejected " + limit);
            await Reject(() => AiInspectionService.FindDuplicatesAsync([], true, unlimited, null, CancellationToken.None), "empty selection is never expanded to whole PC");

            string textFolder = Path.Combine(root, "texts"); Directory.CreateDirectory(textFolder);
            string water = Path.Combine(textFolder, "water.txt"), ambiguous = Path.Combine(textFolder, "ambiguous.txt"), unmatched = Path.Combine(textFolder, "unmatched.txt");
            File.WriteAllText(water, "수질 조사 결과"); File.WriteAllText(ambiguous, "수질 및 대기 조사"); File.WriteAllText(unmatched, "관계없음");
            var scope = AiScope.Capture(textFolder, Directory.GetFiles(textFolder), target, CancellationToken.None);
            string rules = "수질=수질,하천\n대기=대기,미세먼지";
            await Reject(() => { AiContentClassifier.Create(scope, rules, "copy", false, unlimited, CancellationToken.None); return Task.CompletedTask; }, "content reading requires explicit consent");
            var classified = AiContentClassifier.Create(scope, rules, "copy", true, unlimited, CancellationToken.None);
            Check(classified.Plan is not null && classified.Plan.Operations.Count == 2 && classified.Rows.Count == 3, "only one uniquely matching text becomes mkdir plus copy plan");
            Check(classified.Rows.Single(x => x.Before == ambiguous).Status.Contains("확인 필요") && classified.Rows.Single(x => x.Before == unmatched).Status.Contains("미분류"),
                "ambiguous and unmatched texts are visibly excluded");
            var validated = AiPlanService.Validate(scope, classified.Plan!, CollisionPolicy.Skip, CancellationToken.None);
            Check(!Directory.EnumerateFileSystemEntries(target).Any(), "content classification preview has zero destination writes");
            string audit = Path.Combine(root, "audit");
            var execution = await AiPlanService.ExecuteAsync(validated, true, audit, null, CancellationToken.None,
                journal: new AiJournalStore(Path.Combine(audit, "Journal"), new TestJournalProtection()));
            Check(execution.Completed == 2 && File.ReadAllText(Path.Combine(target, "수질", "water.txt")) == File.ReadAllText(water) && File.Exists(ambiguous),
                "classification executes only via approved existing file services");
            foreach (string invalid in new[] { "../escape=x", "CON.txt=x", "folder=", "folder=x\nfolder=y", "not-a-rule" })
                await Reject(() => { AiContentClassifier.ParseRules(invalid); return Task.CompletedTask; }, "invalid classification rule " + invalid);
            await Reject(() => { AiContentClassifier.Create(scope, rules, "copy", true, new(100, 1), CancellationToken.None); return Task.CompletedTask; }, "content read cap rejects oversized selection");
            var noMatch = AiContentClassifier.Create(scope, "없는폴더=없는단어", "copy", true, unlimited, CancellationToken.None);
            Check(noMatch.Plan is null && noMatch.Rows.Count == 3, "zero matches never generate guessed operations");
            string longText = Path.Combine(textFolder, "long.txt"); File.WriteAllText(longText, new string('a', 17_000) + "뒤쪽키워드");
            var prefixScope = AiScope.Capture(textFolder, [longText], target, CancellationToken.None);
            Check(AiContentClassifier.Create(prefixScope, "뒤쪽=뒤쪽키워드", "copy", true, unlimited, CancellationToken.None).Plan is null,
                "truncated text prefix is not described as full-document classification");
            Check(AiContentClassifier.Create(prefixScope, "안내문=내용 일부만 표시", "copy", true, unlimited, CancellationToken.None).Plan is null,
                "generated preview notice cannot become a false keyword match");
            string binary = Path.Combine(textFolder, "binary.txt"); File.WriteAllBytes(binary, [0, 1, 2]);
            var binaryScope = AiScope.Capture(textFolder, [binary], target, CancellationToken.None);
            Check(AiContentClassifier.Create(binaryScope, "기타=파일", "copy", true, unlimited, CancellationToken.None).Plan is null, "binary text is excluded");
            Check(AiLocalPlanner.Category("report.PDF", false) == "문서" && AiLocalPlanner.Category("photo.jpg", false) == "이미지" && AiLocalPlanner.Category("a.py", false) == "코드_설정", "metadata categories are deterministic and case-insensitive");
            var metadata = AiScope.Capture(textFolder, [water], target, CancellationToken.None);
            foreach (string command in new[] { "유형별 복사", "크기별 복사", "연도별 복사" })
            {
                var plan = AiLocalPlanner.Create(metadata, command);
                Check(plan.Operations.Any(x => x.Operation == "copy" && x.Subfolder is not null), "metadata plan " + command);
                AiPlanService.Validate(metadata, plan, CollisionPolicy.Skip, CancellationToken.None);
            }
            string ollamaCfgPath = Path.Combine(root, "ollama_config.json");
            AiOllamaService.SaveConfig(new OllamaUserConfig { HasPromptedSetup = true, UserAcceptedInstall = true, OllamaEnabled = true, Model = "qwen2.5:1.5b" }, ollamaCfgPath);
            var loadedCfg = AiOllamaService.LoadConfig(ollamaCfgPath);
            Check(loadedCfg.HasPromptedSetup && loadedCfg.UserAcceptedInstall && loadedCfg.OllamaEnabled && loadedCfg.Model == "qwen2.5:1.5b", "Ollama onboarding config persists user choice");
            Console.WriteLine($"P15 TESTS PASS: {_passed}");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class InlineProgress(Action<AiInspectionProgress> action) : IProgress<AiInspectionProgress>
    { public void Report(AiInspectionProgress value) => action(value); }
}
