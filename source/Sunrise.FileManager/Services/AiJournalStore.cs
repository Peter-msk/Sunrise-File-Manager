using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace Sunrise.FileManager.Services;

public interface IAiJournalProtection
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>Current Windows user's DPAPI. Refuse plaintext or another operating system.</summary>
public sealed class AiWindowsJournalProtection : IAiJournalProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public byte[] Protect(byte[] data) => Transform(data, true);
    public byte[] Unprotect(byte[] data) => Transform(data, false);
    private static byte[] Transform(byte[] data, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("작업 기록 암호화는 Windows 사용자 계정에서만 지원합니다.");
        var input = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            bool ok = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("작업 기록 보호에 실패했습니다.", new Win32Exception(Marshal.GetLastWin32Error()));
            if (output.Length is < 1 or > 8 * 1024 * 1024) throw new CryptographicException("작업 기록 크기가 올바르지 않습니다.");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            Marshal.FreeHGlobal(input.Data);
        }
    }
}

public sealed record AiJournalItem(int Index, string Operation, string? Source, string Destination,
    string? BeforeFingerprint, bool Skip);
public sealed record AiJournalHeader(int Version, string Id, string ScopeId, DateTimeOffset ApprovedUtc,
    IReadOnlyList<AiJournalItem> Items);
public sealed record AiJournalEvent(int Sequence, string PreviousHash, DateTimeOffset Utc,
    string Kind, int Index = -1, string? Fingerprint = null, string? ErrorCode = null, AiJournalHeader? Header = null);
public sealed record AiJournalItemState(AiJournalItem Item, string State, string? AfterFingerprint, string? ErrorCode);
public sealed record AiJournalSnapshot(AiJournalHeader Header, IReadOnlyList<AiJournalItemState> Items,
    string State, int LastSequence, string LastHash, long Length = 0)
{
    public int Completed => Items.Count(x => x.State is "done" or "recovered_done");
    public int Undone => Items.Count(x => x.State is "undone" or "recovered_undone");
    public bool Interrupted => State is "approved_running" or "undo_running" || Items.Any(x => x.State is "started" or "uncertain" or "undo_started" or "undo_uncertain" or "undo_failed");
}
public sealed record AiJournalSummary(string Id, DateTimeOffset? ApprovedUtc, string State, int Completed, int Undone, string? Error)
{
    public string Display => $"{ApprovedUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Describe(State)} · 완료 {Completed} · 되돌림 {Undone} · {Id}";
    public static string Describe(string state) => state switch
    {
        "completed" or "done" or "recovered_done" => "완료",
        "undone" or "recovered_undone" or "undo_completed" => "되돌림 완료",
        "pending" or "not_applied" => "미실행",
        "skipped" => "건너뜀",
        "canceled" or "undo_canceled" => "사용자 취소",
        "blocked" or "failed" or "partial_failure" or "undo_partial_failure" => "실패 / 후속 작업 중단",
        "interrupted" or "started" or "undo_started" => "중단 · 상태 검사 필요",
        "interrupted_reviewed" => "중단 상태 검사됨",
        "unavailable" => "기록 확인 불가 / 사용 중",
        _ => "확인 필요"
    };
}

/// <summary>Encrypted append-only, hash-linked records. Every intent/result is flushed before the next operation.</summary>
public sealed class AiJournalStore
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunrise", "FileManager", "AiAudit", "Journal");
    private readonly string _directory;
    private readonly IAiJournalProtection _protection;
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 16 };
    public AiJournalStore(string directory, IAiJournalProtection? protection = null)
    { _directory = Path.GetFullPath(directory); _protection = protection ?? new AiWindowsJournalProtection(); }
    private string PathFor(string id, string suffix)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("작업 ID 형식이 올바르지 않습니다.");
        return Path.Combine(_directory, id + suffix);
    }
    internal IDisposable Lock(string id)
    {
        Directory.CreateDirectory(_directory); AiPlanService.OrdinaryPath(_directory);
        return new FileStream(PathFor(id, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public AiJournalSnapshot Begin(AiValidatedPlan plan)
    {
        var header = new AiJournalHeader(1, plan.Id, plan.Scope.Id, DateTimeOffset.UtcNow,
            plan.Steps.Select((s, i) => new AiJournalItem(i, s.Operation.Operation, s.Source?.Path, s.Destination,
                s.Source?.Fingerprint, s.Skip)).ToArray());
        return Begin(header);
    }
    internal AiJournalSnapshot Begin(AiJournalHeader header)
    {
        // Caller holds the per-job lock. Never truncate an existing journal, even if its plan ID repeats.
        var first = new AiJournalEvent(1, "", DateTimeOffset.UtcNow, "approved_running", Header: header);
        byte[] line = Encode(first);
        using (var stream = new FileStream(PathFor(header.Id, ".sfmj"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { stream.Write(line); stream.Flush(true); }
        return Replay(header.Id);
    }
    private byte[] Encode(AiJournalEvent entry)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(entry, Options);
        try { return Encoding.ASCII.GetBytes(Convert.ToBase64String(_protection.Protect(plaintext)) + "\n"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public AiJournalSnapshot Read(string id)
    {
        using var held = Lock(id); return Replay(id);
    }
    internal AiJournalSnapshot Append(AiJournalSnapshot snapshot, string kind, int index = -1, string? fingerprint = null, string? errorCode = null)
    {
        // The job lock is held for the full operation. Check the tail length without replaying
        // the entire encrypted log per item (which would make a batch quadratic).
        if (new FileInfo(PathFor(snapshot.Header.Id, ".sfmj")).Length != snapshot.Length)
            throw new IOException("작업 기록이 변경되었습니다. 다시 조회하세요.");
        var entry = new AiJournalEvent(snapshot.LastSequence + 1, snapshot.LastHash, DateTimeOffset.UtcNow, kind, index, fingerprint, errorCode);
        byte[] line = Encode(entry);
        var next = Apply(snapshot, entry, Convert.ToHexString(SHA256.HashData(line)), line.Length);
        if (next.Length > 32 * 1024 * 1024) throw new IOException("작업 기록은 32MB 이하여야 합니다.");
        using (var stream = new FileStream(PathFor(snapshot.Header.Id, ".sfmj"), FileMode.Append, FileAccess.Write, FileShare.Read))
        { stream.Write(line); stream.Flush(true); }
        return next;
    }
    internal AiJournalSnapshot Replay(string id)
    {
        string path = PathFor(id, ".sfmj");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("작업 기록 연결 경로를 거부했습니다.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 32 * 1024 * 1024) throw new IOException("작업 기록은 32MB 이하여야 합니다.");
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        if (bytes.Length == 0 || bytes[^1] != (byte)'\n') throw new InvalidDataException("중간에서 끊긴 작업 기록입니다. 자동 복구를 차단했습니다.");
        AiJournalSnapshot? snapshot = null; int offset = 0;
        while (offset < bytes.Length)
        {
            int end = Array.IndexOf(bytes, (byte)'\n', offset);
            if (end - offset > 8 * 1024 * 1024) throw new InvalidDataException("작업 기록 한도가 초과되었습니다.");
            byte[] plain = _protection.Unprotect(Convert.FromBase64String(Encoding.ASCII.GetString(bytes, offset, end - offset)));
            AiJournalEvent entry;
            try { entry = JsonSerializer.Deserialize<AiJournalEvent>(plain, Options) ?? throw new InvalidDataException("빈 작업 기록입니다."); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            string hash = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(offset, end - offset + 1)));
            if (snapshot is null)
            {
                if (entry.Sequence != 1 || entry.PreviousHash != "" || entry.Kind != "approved_running" || entry.Header is null ||
                    entry.Header.Id != id || entry.Header.Version != 1 || entry.Header.Items is null || entry.Header.Items.Count is < 1 or > 2000)
                    throw new InvalidDataException("작업 기록 머리글이 올바르지 않습니다.");
                var items = entry.Header.Items;
                if (items.Where((x, i) => x is null || x.Index != i || x.Operation is not ("rename" or "mkdir" or "copy" or "move" or "recycle")).Any())
                    throw new InvalidDataException("작업 항목 형식이 올바르지 않습니다.");
                snapshot = new(entry.Header, items.Select(x => new AiJournalItemState(x, "pending", null, null)).ToArray(), entry.Kind, 1, hash, end + 1);
            }
            else
            {
                if (entry.Sequence != snapshot.LastSequence + 1 || entry.PreviousHash != snapshot.LastHash || entry.Header is not null)
                    throw new InvalidDataException("작업 기록 순서 또는 무결성이 올바르지 않습니다.");
                snapshot = Apply(snapshot, entry, hash, end - offset + 1);
            }
            offset = end + 1;
        }
        return snapshot!;
    }
    private static AiJournalSnapshot Apply(AiJournalSnapshot snapshot, AiJournalEvent entry, string hash, int addedBytes)
    {
        var items = snapshot.Items.ToArray(); string state = snapshot.State;
        if (entry.Index >= 0)
        {
            if (entry.Index >= items.Length) throw new InvalidDataException("작업 항목 번호가 올바르지 않습니다.");
            var item = items[entry.Index];
            bool valid = entry.Kind switch
            {
                "started" or "skipped" or "blocked" => item.State == "pending",
                "done" or "failed" or "uncertain" or "not_applied" or "recovered_done" => item.State is "started" or "uncertain",
                "undo_started" => item.State is "done" or "recovered_done",
                "undone" or "undo_failed" or "undo_uncertain" or "recovered_undone" or "undo_not_applied" => item.State is "undo_started" or "undo_uncertain" or "undo_failed",
                _ => false
            };
            if (!valid) throw new InvalidDataException("허용되지 않은 작업 기록 상태전이입니다.");
            string next = entry.Kind == "undo_not_applied" ? "done" : entry.Kind;
            items[entry.Index] = item with { State = next, AfterFingerprint = entry.Fingerprint ?? item.AfterFingerprint, ErrorCode = entry.ErrorCode };
        }
        else
        {
            if (entry.Kind is not ("completed" or "canceled" or "partial_failure" or "interrupted_reviewed" or "undo_running" or "undo_completed" or "undo_canceled" or "undo_partial_failure"))
                throw new InvalidDataException("알 수 없는 작업 상태입니다.");
            state = entry.Kind;
        }
        return new(snapshot.Header, items, state, entry.Sequence, hash, snapshot.Length + addedBytes);
    }
    public IReadOnlyList<AiJournalSummary> List(int limit = 100)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        if (!Directory.Exists(_directory)) return Array.Empty<AiJournalSummary>();
        var result = new List<AiJournalSummary>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.sfmj").OrderByDescending(File.GetLastWriteTimeUtc).Take(limit))
        {
            string id = Path.GetFileNameWithoutExtension(path);
            try
            {
                var snapshot = Read(id);
                result.Add(new(id, snapshot.Header.ApprovedUtc, snapshot.Interrupted ? "interrupted" : snapshot.State, snapshot.Completed, snapshot.Undone, null));
            }
            catch (Exception ex) when (IsRecordError(ex))
            { result.Add(new(id, null, "unavailable", 0, 0, "기록 읽기 불가: " + ex.GetType().Name)); }
        }
        return result.AsReadOnly();
    }
    internal static bool IsRecordError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
        CryptographicException or FormatException or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
