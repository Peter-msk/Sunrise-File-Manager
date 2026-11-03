using System.IO;
using System.Security.Cryptography;

namespace Sunrise.FileManager.Services;

/// <summary>Owns the memo snapshot of a file move. Only staged originals may be removed.</summary>
public sealed class MemoTransferSnapshot
{
    private sealed record Entry(FileMemoStore.MemoRecord Record, byte[] Hash);
    private readonly string _source;
    private readonly Entry[] _entries;
    private MemoTransferSnapshot(string source, Entry[] entries) { _source = source; _entries = entries; }

    // Callers have already validated the records with the product's memo integrity policy.
    public static MemoTransferSnapshot Capture(string source, IReadOnlyList<FileMemoStore.MemoRecord> records)
    {
        var entries = records.Select(record =>
        {
            if (ReparsePointPolicy.IsBlocked(record.Path) || ReparsePointPolicy.IsBlocked(Path.GetDirectoryName(record.Path)!))
                throw new IOException("연결된 메모 경로는 이동하지 않습니다.");
            return new Entry(record, Digest(record.Path));
        }).ToArray();
        var snapshot = new MemoTransferSnapshot(source, entries);
        snapshot.VerifySource();
        return snapshot;
    }
    public void VerifySource()
    {
        var current = FileMemoStore.ListForFile(_source, out int unreadable);
        if (unreadable != 0 || current.Count != _entries.Length)
            throw new IOException("전송 중 원본 메모 목록이 변경되었습니다. 원본을 유지합니다.");
        foreach (var entry in _entries)
        {
            var record = current.SingleOrDefault(r => string.Equals(r.Path, entry.Record.Path, StringComparison.OrdinalIgnoreCase));
            if (record is null || !SameContent(record, entry.Record) || !Digest(entry.Record.Path).AsSpan().SequenceEqual(entry.Hash))
                throw new IOException("전송 중 원본 메모 내용이 변경되었습니다. 원본을 유지합니다.");
        }
    }
    public void CopyTo(string destination, CancellationToken token)
    {
        VerifySource();
        foreach (var entry in _entries)
        {
            token.ThrowIfCancellationRequested();
            var memo = entry.Record;
            if (File.Exists(FileMemoStore.MemoPath(destination, memo.CreatedLocal, memo.UserName)))
                throw new IOException("대상에 같은 메모가 있어 원본을 유지합니다.");
            FileMemoStore.Save(destination, memo.Text, memo.CreatedLocal, memo.UserName);
        }
        VerifyDestination(destination);
    }
    public void VerifyDestination(string destination)
    {
        foreach (var entry in _entries)
        {
            var expected = entry.Record;
            string path = FileMemoStore.MemoPath(destination, expected.CreatedLocal, expected.UserName);
            var actual = FileMemoStore.Read(path, destination);
            if (!SameContent(actual, expected)) throw new IOException("대상 메모 재검증에 실패했습니다. 원본을 유지합니다.");
        }
    }
    public void DeleteSource(string destination, Action verifySourceFile, CancellationToken token, bool sourceAlreadyMoved = false)
    {
        VerifySource(); VerifyDestination(destination);
        var staged = new List<(string Original, string Temporary)>();
        var locks = new List<FileStream>();
        bool fileDeleted = false;
        try
        {
            foreach (var entry in _entries)
            {
                token.ThrowIfCancellationRequested();
                string temporary = Path.Combine(Path.GetDirectoryName(entry.Record.Path)!, ".sunrise-memo-move-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.Move(entry.Record.Path, temporary);
                staged.Add((entry.Record.Path, temporary));
                var stream = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read);
                locks.Add(stream);
                if (!SHA256.HashData(stream).AsSpan().SequenceEqual(entry.Hash))
                    throw new IOException("원본 메모가 정리 직전에 변경되었습니다. 원본을 유지합니다.");
            }
            // A newly created/replaced memo is never deleted by its original filename.
            var newMemos = FileMemoStore.ListForFile(_source, out int unreadable);
            if (unreadable != 0 || newMemos.Count != 0) throw new IOException("원본에 새 메모가 생성되어 이동 정리를 중단했습니다.");
            verifySourceFile(); VerifyDestination(destination);
            if (FileMemoStore.ListForFile(_source, out unreadable).Count != 0 || unreadable != 0)
                throw new IOException("정리 직전에 새 원본 메모가 발견되어 원본을 유지합니다.");
            token.ThrowIfCancellationRequested();
            if (!sourceAlreadyMoved) File.Delete(_source);
            fileDeleted = true;
        }
        catch (Exception original)
        {
            foreach (var stream in locks) stream.Dispose(); locks.Clear();
            var remaining = new List<string>();
            foreach (var pair in staged.AsEnumerable().Reverse())
            {
                try { File.Move(pair.Temporary, pair.Original); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { remaining.Add(pair.Temporary); }
            }
            if (remaining.Count > 0) throw new IOException("원본 메모를 임시 보존했습니다. 확인할 경로: " + string.Join(" / ", remaining), original);
            throw;
        }
        finally { foreach (var stream in locks) stream.Dispose(); }
        if (fileDeleted)
            foreach (var pair in staged)
            {
                try { File.Delete(pair.Temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { throw new IOException("이동은 완료됐지만 원본 메모 보존본이 남았습니다: " + pair.Temporary, ex); }
            }
    }
    internal static bool DiscardOwnedCopy(string path, byte[] expectedHash)
    {
        if (!File.Exists(path)) return true;
        string staged = Path.Combine(Path.GetDirectoryName(path)!, ".sunrise-memo-rollback-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.Move(path, staged);
        bool owned = false;
        try
        {
            using (var stream = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
                owned = SHA256.HashData(stream).AsSpan().SequenceEqual(expectedHash);
            if (owned) { File.Delete(staged); return true; }
            return false;
        }
        finally
        {
            if (File.Exists(staged))
            {
                if (!File.Exists(path)) File.Move(staged, path);
                else throw new IOException("변경된 대상 메모를 임시 보존했습니다: " + staged);
            }
        }
    }
    private static bool SameContent(FileMemoStore.MemoRecord left, FileMemoStore.MemoRecord right) =>
        left.Text == right.Text && left.UserName == right.UserName && left.CreatedLocal == right.CreatedLocal;
    private static byte[] Digest(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1_100_000) throw new IOException("메모 크기 제한을 초과했습니다.");
        return SHA256.HashData(stream);
    }
}
