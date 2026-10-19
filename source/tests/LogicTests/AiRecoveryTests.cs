using System.Security.Cryptography;
using System.Text;
using Sunrise.FileManager.Services;

/// <summary>Authenticated encryption fixture only. Production always defaults to Windows user DPAPI.</summary>
public sealed class TestJournalProtection : IAiJournalProtection
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    public byte[] Protect(byte[] data)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12), cipher = new byte[data.Length], tag = new byte[16];
        using var aes = new AesGcm(Key, 16); aes.Encrypt(nonce, data, cipher, tag);
        return nonce.Concat(tag).Concat(cipher).ToArray();
    }
    public byte[] Unprotect(byte[] data)
    {
        if (data.Length < 28) throw new CryptographicException("bad fixture record");
        var plain = new byte[data.Length - 28]; using var aes = new AesGcm(Key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain); return plain;
    }
}
public static class AiRecoveryTests
{
    private static int _passed;
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception("FAILED P14: " + name); _passed++; Console.WriteLine("PASS P14: " + name); }
    private static async Task Reject(Func<Task> action, string name)
    {
        bool rejected = false;
        try { await action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or CryptographicException or FormatException or NotSupportedException or System.Text.Json.JsonException or OperationCanceledException)
        { rejected = true; }
        Check(rejected, name);
    }
    private static Task<AiExecutionResult> Execute(AiValidatedPlan plan, string audit, AiJournalStore store,
        IProgress<AiPreviewRow>? progress = null, CancellationToken token = default) =>
        AiPlanService.ExecuteAsync(plan, true, audit, progress, token, journal: store);
    private static AiValidatedPlan Plan(string source, string[] paths, string command, string? target = null)
    {
        var scope = AiScope.Capture(source, paths, target, CancellationToken.None);
        return AiPlanService.Validate(scope, AiLocalPlanner.Create(scope, command), CollisionPolicy.Skip, CancellationToken.None);
    }
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "SunriseP14_" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source"), target = Path.Combine(root, "target"), audit = Path.Combine(root, "audit");
        string logs = Path.Combine(audit, "Journal");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        var store = new AiJournalStore(logs, new TestJournalProtection());
        try
        {
            string one = Path.Combine(source, "비공개_사업.txt"); File.WriteAllText(one, "original");
            var rename = Plan(source, [one], "접두어 추가: 완료_");
            var result = await Execute(rename, audit, store);
            var snapshot = new AiJournalStore(logs, new TestJournalProtection()).Read(rename.Id);
            Check(snapshot.State == "completed" && snapshot.Completed == 1 && snapshot.Items[0].Item.Source == one && snapshot.Items[0].AfterFingerprint is not null,
                "durable per-item paths hash approval and final state survive reload");
            byte[] raw = File.ReadAllBytes(Path.Combine(logs, rename.Id + ".sfmj"));
            Check(!Encoding.UTF8.GetString(raw).Contains("비공개") && !Encoding.UTF8.GetString(raw).Contains(source), "journal paths encrypted at rest");
            var undo = AiRecoveryService.PrepareUndo(store, rename.Id, CancellationToken.None);
            Check(undo.EligibleCount == 1 && !File.Exists(one), "undo preview writes no source files");
            await Reject(() => AiRecoveryService.UndoAsync(store, undo, false, null, CancellationToken.None), "undo requires separate approval");
            var undone = await AiRecoveryService.UndoAsync(store, undo, true, null, CancellationToken.None);
            Check(undone.Completed == 1 && File.ReadAllText(one) == "original" && store.Read(rename.Id).Undone == 1, "approved rename inverse restores exact source");
            await Reject(() => AiRecoveryService.UndoAsync(store, undo, true, null, CancellationToken.None), "double undo refused");
            Check(AiRecoveryService.PrepareUndo(store, rename.Id, CancellationToken.None).EligibleCount == 0, "reloaded record cannot undo twice");
            var move = Plan(source, [one], "이동", target);
            await Execute(move, audit, store);
            var inverse = AiRecoveryService.PrepareUndo(store, move.Id, CancellationToken.None);
            var movedBack = await AiRecoveryService.UndoAsync(store, inverse, true, null, CancellationToken.None);
            Check(movedBack.Completed == 1 && File.Exists(one) && !File.Exists(Path.Combine(target, Path.GetFileName(one))), "same-drive move inverse uses existing transfer owner");
            var mutated = Plan(source, [one], "접두어 추가: changed_");
            await Execute(mutated, audit, store);
            File.WriteAllText(mutated.Rows[0].After, "new contents");
            Check(AiRecoveryService.PrepareUndo(store, mutated.Id, CancellationToken.None).EligibleCount == 0 && !File.Exists(one), "changed file blocked without automatic restore");
            File.WriteAllText(one, "next");
            var collision = Plan(source, [one], "접두어 추가: collision_"); await Execute(collision, audit, store);
            var collisionUndo = AiRecoveryService.PrepareUndo(store, collision.Id, CancellationToken.None);
            File.WriteAllText(one, "another file");
            await Reject(() => AiRecoveryService.UndoAsync(store, collisionUndo, true, null, CancellationToken.None), "source reoccupied after preview blocks overwrite");
            Check(File.ReadAllText(one) == "another file" && File.Exists(collision.Rows[0].After), "collision preserves both files");
            var copy = Plan(source, [one], "복사", target); await Execute(copy, audit, store);
            Check(AiRecoveryService.PrepareUndo(store, copy.Id, CancellationToken.None).EligibleCount == 0 && File.Exists(copy.Rows[0].After), "copy undo never deletes destination");
            var mkdir = Plan(source, [], "폴더 생성: keep-folder"); await Execute(mkdir, audit, store);
            Check(AiRecoveryService.PrepareUndo(store, mkdir.Id, CancellationToken.None).EligibleCount == 0 && Directory.Exists(mkdir.Rows[0].After), "mkdir undo never deletes directory");
            var recycle = Plan(source, [one], "휴지통 이동");
            await Reject(() => Execute(recycle, audit, store), "recycle requires second approval before mutation");
            Check(File.Exists(one), "no recycle before second approval");
            if (!OperatingSystem.IsWindows())
            {
                await Reject(() => { FileActionsService.Recycle(one); return Task.CompletedTask; }, "non-Windows recycle fails explicitly");
                await Reject(() => { new AiWindowsJournalProtection().Protect([1]); return Task.CompletedTask; }, "production journal refuses plaintext fallback");
            }
            else
            {
                var dpapi = new AiWindowsJournalProtection(); byte[] data = Encoding.UTF8.GetBytes("Windows DPAPI round trip");
                Check(dpapi.Unprotect(dpapi.Protect(data)).SequenceEqual(data), "native DPAPI roundtrip");
            }

            // Simulate a crash after a durable intent and an atomic rename, before the done record.
            string interruptedFile = Path.Combine(source, "interrupted.txt"); File.WriteAllText(interruptedFile, "crash-safe");
            var interrupted = Plan(source, [interruptedFile], "접두어 추가: recovered_");
            using (store.Lock(interrupted.Id))
            {
                var s = store.Begin(interrupted); s = store.Append(s, "started", 0);
                FileActionsService.Rename(interruptedFile, Path.GetFileName(interrupted.Rows[0].After));
            }
            Check(store.Read(interrupted.Id).Interrupted, "crash intent is detected on reload");
            await Reject(() => { AiRecoveryService.PrepareUndo(store, interrupted.Id, CancellationToken.None); return Task.CompletedTask; }, "unreviewed interrupted record blocks undo");
            var reviewed = AiRecoveryService.InspectInterrupted(store, interrupted.Id, CancellationToken.None);
            Check(reviewed.Items[0].State == "recovered_done" && !File.Exists(interruptedFile), "interruption inspection infers completed rename without writing files");
            var recoveredUndo = AiRecoveryService.PrepareUndo(store, interrupted.Id, CancellationToken.None);
            await AiRecoveryService.UndoAsync(store, recoveredUndo, true, null, CancellationToken.None);
            Check(File.ReadAllText(interruptedFile) == "crash-safe", "reviewed interrupted rename can be explicitly restored");
            var notApplied = Plan(source, [interruptedFile], "접두어 추가: notapplied_");
            using (store.Lock(notApplied.Id)) { var s = store.Begin(notApplied); store.Append(s, "started", 0); }
            Check(AiRecoveryService.InspectInterrupted(store, notApplied.Id, CancellationToken.None).Items[0].State == "not_applied" && File.Exists(interruptedFile),
                "intent without syscall is classified without replaying it");
            var ambiguous = Plan(source, [interruptedFile], "접두어 추가: ambiguous_");
            using (store.Lock(ambiguous.Id)) { var s = store.Begin(ambiguous); store.Append(s, "started", 0); }
            File.WriteAllText(ambiguous.Rows[0].After, "different");
            Check(AiRecoveryService.InspectInterrupted(store, ambiguous.Id, CancellationToken.None).Items[0].State == "uncertain" && File.Exists(interruptedFile), "ambiguous state is never guessed or overwritten");

            Check(store.Read(ambiguous.Id).Interrupted, "unresolved uncertainty remains review-required after inspection");
            Check(store.List().Single(x => x.Id == ambiguous.Id).State == "interrupted", "unresolved job stays visible in startup summaries");
            await Reject(() => { AiRecoveryService.PrepareUndo(store, ambiguous.Id, CancellationToken.None); return Task.CompletedTask; }, "unresolved uncertainty blocks automatic undo");

            var undoCrash = Plan(source, [interruptedFile], "접두어 추가: undocrash_"); await Execute(undoCrash, audit, store);
            using (store.Lock(undoCrash.Id))
            {
                var s = store.Replay(undoCrash.Id); s = store.Append(s, "undo_running"); s = store.Append(s, "undo_started", 0);
                FileActionsService.Rename(undoCrash.Rows[0].After, Path.GetFileName(interruptedFile));
            }
            Check(AiRecoveryService.InspectInterrupted(store, undoCrash.Id, CancellationToken.None).Items[0].State == "recovered_undone" && File.Exists(interruptedFile),
                "crash after inverse rename is reconciled without double mutation");
            using (store.Lock(undoCrash.Id))
                await Reject(() => { store.Read(undoCrash.Id); return Task.CompletedTask; }, "per-job lock blocks concurrent recovery writer");

            string batch = Path.Combine(root, "undo-batch"); Directory.CreateDirectory(batch);
            for (int i = 0; i < 3; i++) File.WriteAllText(Path.Combine(batch, $"item{i}.txt"), "payload" + i);
            var batchPlan = Plan(batch, Directory.GetFiles(batch), "접두어 추가: undo_"); await Execute(batchPlan, audit, store);
            var batchUndo = AiRecoveryService.PrepareUndo(store, batchPlan.Id, CancellationToken.None);
            using var stop = new CancellationTokenSource();
            var partialUndo = await AiRecoveryService.UndoAsync(store, batchUndo, true, new InlineProgress(_ => stop.Cancel()), stop.Token);
            Check(partialUndo.Canceled && partialUndo.Completed == 1 && partialUndo.Rows.Count == 3, "undo cancellation records completed and pending items");
            var remainingUndo = AiRecoveryService.PrepareUndo(store, batchPlan.Id, CancellationToken.None);
            Check(remainingUndo.EligibleCount == 2, "completed inverse is excluded from a new undo plan");
            var resumedUndo = await AiRecoveryService.UndoAsync(store, remainingUndo, true, null, CancellationToken.None);
            Check(resumedUndo.Completed == 2 && Directory.GetFiles(batch).All(p => !Path.GetFileName(p).StartsWith("undo_")), "explicit new approval restores only remaining items");
            var zeroWrites = Plan(batch, Directory.GetFiles(batch), "접두어 추가: stay_"); await Execute(zeroWrites, audit, store);
            var zeroUndo = AiRecoveryService.PrepareUndo(store, zeroWrites.Id, CancellationToken.None);
            using var preCanceled = new CancellationTokenSource(); preCanceled.Cancel();
            await Reject(() => AiRecoveryService.UndoAsync(store, zeroUndo, true, null, preCanceled.Token), "pre-canceled undo performs zero writes");
            Check(zeroWrites.Rows.All(r => File.Exists(r.After)), "pre-cancel keeps all renamed files");
            var staleUndo = AiRecoveryService.PrepareUndo(store, zeroWrites.Id, CancellationToken.None);
            AiRecoveryService.InspectInterrupted(store, zeroWrites.Id, CancellationToken.None);
            await Reject(() => AiRecoveryService.UndoAsync(store, staleUndo, true, null, CancellationToken.None), "journal version change invalidates approval preview");

            string faultFile = Path.Combine(source, "journal-fault.txt"); File.WriteAllText(faultFile, "fault-safe");
            var failBefore = Plan(source, [faultFile], "접두어 추가: failbefore_");
            var badBeforeStore = new AiJournalStore(logs, new FailingProtection(2));
            await Reject(() => Execute(failBefore, audit, badBeforeStore), "journal intent encryption failure stops file syscall");
            Check(File.Exists(faultFile) && !File.Exists(failBefore.Rows[0].After), "failed intent preserves original");
            var failAfter = Plan(source, [faultFile], "접두어 추가: failafter_");
            var badAfterStore = new AiJournalStore(logs, new FailingProtection(3));
            await Reject(() => Execute(failAfter, audit, badAfterStore), "post-syscall journal failure is not reported as success");
            Check(!File.Exists(faultFile) && File.Exists(failAfter.Rows[0].After) && store.Read(failAfter.Id).Items[0].State == "started",
                "post-write failure retains durable intent for review");
            var afterReview = AiRecoveryService.InspectInterrupted(store, failAfter.Id, CancellationToken.None);
            Check(afterReview.Items[0].State == "recovered_done", "post-write journal fault can be reconciled without replay");
            await AiRecoveryService.UndoAsync(store, AiRecoveryService.PrepareUndo(store, failAfter.Id, CancellationToken.None), true, null, CancellationToken.None);
            Check(File.ReadAllText(faultFile) == "fault-safe", "post-write journal fault recovery preserves content");
            string uncertainCopy = Path.Combine(source, "uncertain-copy.txt"); File.WriteAllText(uncertainCopy, "copy");
            var copyCrash = Plan(source, [uncertainCopy], "복사", target);
            using (store.Lock(copyCrash.Id))
            {
                var s = store.Begin(copyCrash); store.Append(s, "started", 0);
                File.Copy(uncertainCopy, copyCrash.Rows[0].After);
            }
            Check(AiRecoveryService.InspectInterrupted(store, copyCrash.Id, CancellationToken.None).Items[0].State == "uncertain" &&
                File.Exists(uncertainCopy) && File.Exists(copyCrash.Rows[0].After), "interrupted copy is never auto-deleted or replayed");
            using (store.Lock(copyCrash.Id))
            {
                var s = store.Replay(copyCrash.Id);
                await Reject(() => { store.Append(s, "undone", 0); return Task.CompletedTask; }, "invalid journal state transition rejected");
                Check(store.Replay(copyCrash.Id).LastHash == s.LastHash, "rejected state transition never corrupts durable log");
            }
            string log = Path.Combine(logs, undoCrash.Id + ".sfmj"); byte[] valid = File.ReadAllBytes(log);
            File.WriteAllBytes(log, valid[..^1]);
            await Reject(() => { store.Read(undoCrash.Id); return Task.CompletedTask; }, "truncated journal fails closed");
            File.WriteAllBytes(log, valid);
            byte[] tampered = valid.ToArray(); tampered[10] = tampered[10] == (byte)'A' ? (byte)'B' : (byte)'A'; File.WriteAllBytes(log, tampered);
            await Reject(() => { store.Read(undoCrash.Id); return Task.CompletedTask; }, "tampered encrypted record rejected");
            File.WriteAllBytes(log, valid);
            var lines = Encoding.ASCII.GetString(valid).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            File.WriteAllText(log, string.Join('\n', lines.Reverse()) + "\n");
            await Reject(() => { store.Read(undoCrash.Id); return Task.CompletedTask; }, "reordered records rejected");
            File.WriteAllBytes(log, valid);
            await Reject(() => { store.Read("../escape"); return Task.CompletedTask; }, "journal ID path traversal rejected");
            await Reject(() => { using var held = store.Lock(undoCrash.Id); store.Begin(undoCrash); return Task.CompletedTask; }, "existing journal never overwritten");
            Check(store.List().Any(x => x.Id == interrupted.Id), "history lists durable jobs across reload");
            Check(File.ReadAllText(interruptedFile) == "crash-safe", "all corruption and recovery tests preserve original contents");
            Console.WriteLine($"P14 TESTS PASS: {_passed}");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class InlineProgress(Action<AiPreviewRow> action) : IProgress<AiPreviewRow>
    { public void Report(AiPreviewRow row) => action(row); }
    private sealed class FailingProtection(int failAt) : IAiJournalProtection
    {
        private int _calls;
        private readonly TestJournalProtection _inner = new();
        public byte[] Protect(byte[] data)
        { if (++_calls == failAt) throw new CryptographicException("simulated journal protection failure"); return _inner.Protect(data); }
        public byte[] Unprotect(byte[] data) => _inner.Unprotect(data);
    }

}
