using System.Text;
using Sunrise.FileManager.Services;

static class WindowsSmokeTests
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows native smoke requires Windows.");
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS WINDOWS: " + name); }
        string root = Path.Combine(Path.GetTempPath(), "SunriseWindowsSmoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var protection = new AiWindowsJournalProtection();
            byte[] original = Encoding.UTF8.GetBytes("Sunrise smoke fixture");
            byte[] encrypted = protection.Protect(original);
            Check(!encrypted.SequenceEqual(original) && protection.Unprotect(encrypted).SequenceEqual(original), "current-user DPAPI roundtrip");
            string input = Path.Combine(root, "fixture.txt"), audit = Path.Combine(root, "audit");
            File.WriteAllBytes(input, original);
            var scope = AiScope.Capture(root, [input], null, CancellationToken.None);
            var plan = AiPlanService.Validate(scope, AiLocalPlanner.Create(scope, "접두어 추가: checked_"), CollisionPolicy.Skip, CancellationToken.None);
            var result = await AiPlanService.ExecuteAsync(plan, true, audit, null, CancellationToken.None);
            Check(result.Completed == 1 && !File.Exists(input) && File.ReadAllBytes(plan.Rows[0].After).SequenceEqual(original), "approved rename with native journal");
            var store = new AiJournalStore(Path.Combine(audit, "Journal"));
            Check(store.Read(plan.Id).Completed == 1, "DPAPI journal can be reopened");
            var undo = AiRecoveryService.PrepareUndo(store, plan.Id, CancellationToken.None);
            var restored = await AiRecoveryService.UndoAsync(store, undo, true, null, CancellationToken.None);
            Check(restored.Completed == 1 && File.ReadAllBytes(input).SequenceEqual(original), "approved undo restores original bytes");
            string target = Path.Combine(root, "target"); Directory.CreateDirectory(target);
            var copied = await TransferService.RunAsync([input], target, TransferKind.Copy, null, CancellationToken.None);
            Check(copied.Completed == 1 && File.ReadAllBytes(Path.Combine(target, "fixture.txt")).SequenceEqual(original), "staged copy preserves bytes");
            Console.WriteLine($"WINDOWS NATIVE SMOKE PASS: {passed}");
        }
        finally { Directory.Delete(root, true); }
    }
}
