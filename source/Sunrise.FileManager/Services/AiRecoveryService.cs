using System.IO;
using System.Collections.ObjectModel;

namespace Sunrise.FileManager.Services;

public sealed class AiUndoPlan
{
    public string JournalId { get; }
    public IReadOnlyList<AiPreviewRow> Rows { get; }
    public int EligibleCount => Items.Count;
    internal AiJournalSnapshot Snapshot { get; }
    internal IReadOnlyList<AiJournalItemState> Items { get; }
    private int _claimed;
    internal AiUndoPlan(AiJournalSnapshot snapshot, List<AiJournalItemState> items, List<AiPreviewRow> rows)
    { JournalId = snapshot.Header.Id; Snapshot = snapshot; Items = items.AsReadOnly(); Rows = rows.AsReadOnly(); }
    internal void Claim()
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0) throw new InvalidOperationException("이미 사용한 되돌리기 계획입니다.");
    }
}
public sealed record AiUndoResult(int Completed, int Failed, bool Canceled, IReadOnlyList<AiPreviewRow> Rows);

/// <summary>Read/inspect interrupted work, then explicitly approve an inverse rename/move. Never replay original writes.</summary>
public static class AiRecoveryService
{
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static string Hash(string path, CancellationToken token) =>
        AiPlanService.Fingerprint(AiPlanService.OrdinaryPath(path), new AiPlanService.ScanBudget(), token);
    private static string? Check(AiJournalItemState state, CancellationToken token)
    {
        var item = state.Item;
        if (item.Operation is not ("rename" or "move") || item.Source is null || state.AfterFingerprint is null)
            return "이 작업은 자동 되돌리기를 지원하지 않습니다.";
        if (!Exists(item.Destination)) return "변경 후 항목이 없습니다.";
        if (Exists(item.Source)) return "원래 경로가 사용 중입니다. 덮어쓰지 않습니다.";
        AiPlanService.OrdinaryPath(Path.GetDirectoryName(item.Source)!);
        if (Hash(item.Destination, token) != state.AfterFingerprint) return "파일 내용·메타데이터가 변경되었습니다.";
        return null;
    }
    public static AiUndoPlan PrepareUndo(AiJournalStore store, string id, CancellationToken token)
    {
        using var held = store.Lock(id);
        var snapshot = store.Replay(id);
        if (snapshot.Interrupted) throw new IOException("중단된 작업은 먼저 상태 검사를 진행하세요.");
        var eligible = new List<AiJournalItemState>(); var rows = new List<AiPreviewRow>();
        // Reverse order mirrors the original operations; unsupported operations are never undone by deletion.
        foreach (var state in snapshot.Items.Reverse())
        {
            token.ThrowIfCancellationRequested();
            if (state.State is not ("done" or "recovered_done")) continue;
            string? reason;
            try { reason = Check(state, token); }
            catch (Exception ex) when (AiJournalStore.IsRecordError(ex)) { reason = "검증 실패: " + ex.GetType().Name; }
            rows.Add(new("되돌리기 " + state.Item.Operation, state.Item.Destination, state.Item.Source ?? "—",
                reason ?? "검증 통과 · 승인 대기"));
            if (reason is null) eligible.Add(state);
        }
        return new(snapshot, eligible, rows);
    }
    public static async Task<AiUndoResult> UndoAsync(AiJournalStore store, AiUndoPlan plan, bool approved,
        IProgress<AiPreviewRow>? progress, CancellationToken token)
    {
        if (!approved) throw new InvalidOperationException("되돌리기 승인이 필요합니다.");
        plan.Claim();
        using var held = store.Lock(plan.JournalId);
        var snapshot = store.Replay(plan.JournalId);
        if (snapshot.LastHash != plan.Snapshot.LastHash || snapshot.LastSequence != plan.Snapshot.LastSequence)
            throw new IOException("작업 기록이 바뀌었습니다. 다시 미리보기하세요.");
        if (plan.EligibleCount == 0) throw new IOException("되돌릴 수 있는 항목이 없습니다.");
        foreach (var state in plan.Items)
        {
            token.ThrowIfCancellationRequested();
            string? reason = Check(state, token);
            if (reason is not null) throw new IOException(reason + " 다시 미리보기하세요.");
        }
        snapshot = store.Append(snapshot, "undo_running");
        var rows = new List<AiPreviewRow>(); int completed = 0, failed = 0; bool canceled = false;
        foreach (var state in plan.Items)
        {
            if (token.IsCancellationRequested) { canceled = true; break; }
            var item = state.Item; bool intent = false;
            try
            {
                string? reason = Check(state, token);
                if (reason is not null) throw new IOException(reason);
                snapshot = store.Append(snapshot, "undo_started", item.Index); intent = true;
                if (item.Operation == "rename") FileActionsService.Rename(item.Destination, Path.GetFileName(item.Source!));
                else
                {
                    var transfer = await TransferService.RunAsync([item.Destination], Path.GetDirectoryName(item.Source!)!, TransferKind.Move,
                        null, token, CollisionPolicy.Skip, new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        { [item.Destination] = item.Source! })).ConfigureAwait(false);
                    if (transfer.Canceled) { canceled = true; snapshot = store.Append(snapshot, "undo_uncertain", item.Index, errorCode: "CANCELED"); break; }
                    if (transfer.Completed != 1 || transfer.Failed != 0 || transfer.Skipped != 0) throw new IOException("역방향 이동에 실패했습니다.");
                }
                string restored = Hash(item.Source!, CancellationToken.None);
                snapshot = store.Append(snapshot, "undone", item.Index, restored);
                completed++;
                var row = new AiPreviewRow("되돌리기", item.Destination, item.Source!, "되돌림 완료");
                rows.Add(row); progress?.Report(row);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (intent) snapshot = store.Append(snapshot, "undo_uncertain", item.Index, errorCode: "CANCELED");
                canceled = true; break;
            }
            catch (Exception ex) when (AiJournalStore.IsRecordError(ex))
            {
                if (intent) snapshot = store.Append(snapshot, "undo_uncertain", item.Index, errorCode: ex.GetType().Name);
                failed++;
                var row = new AiPreviewRow("되돌리기", item.Destination, item.Source!, "중단 · 확인 필요: " + ex.Message);
                rows.Add(row); progress?.Report(row); break;
            }
        }
        for (int i = rows.Count; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i].Item;
            rows.Add(new("되돌리기", item.Destination, item.Source!, "미실행"));
        }
        snapshot = store.Append(snapshot, canceled ? "undo_canceled" : failed > 0 ? "undo_partial_failure" : "undo_completed");
        return new(completed, failed, canceled, rows.AsReadOnly());
    }

    public static AiJournalSnapshot InspectInterrupted(AiJournalStore store, string id, CancellationToken token)
    {
        using var held = store.Lock(id);
        var snapshot = store.Replay(id);
        foreach (var state in snapshot.Items.ToArray())
        {
            token.ThrowIfCancellationRequested();
            var item = state.Item;
            if (state.State is not ("started" or "uncertain" or "undo_started" or "undo_uncertain" or "undo_failed")) continue;
            bool undo = state.State.StartsWith("undo", StringComparison.Ordinal);
            string kind = undo ? "undo_uncertain" : "uncertain";
            string? fingerprint = null;
            try
            {
                if (item.Operation is "rename" or "move" && item.Source is not null)
                {
                    string expected = undo ? state.AfterFingerprint ?? "" : item.BeforeFingerprint ?? "";
                    bool source = Exists(item.Source), destination = Exists(item.Destination);
                    if (source && !destination && Hash(item.Source, token) == expected)
                        kind = undo ? "recovered_undone" : "not_applied";
                    else if (!source && destination && Hash(item.Destination, token) == expected)
                    { kind = undo ? "undo_not_applied" : "recovered_done"; fingerprint = expected; }
                }
            }
            catch (Exception ex) when (AiJournalStore.IsRecordError(ex)) { kind = undo ? "undo_uncertain" : "uncertain"; }
            snapshot = store.Append(snapshot, kind, item.Index, fingerprint,
                kind is "uncertain" or "undo_uncertain" ? "MANUAL_REVIEW_REQUIRED" : null);
        }
        return store.Append(snapshot, "interrupted_reviewed");
    }
}
