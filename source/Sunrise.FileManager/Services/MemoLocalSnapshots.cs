using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunrise.FileManager.Services;

/// <summary>
/// Explicitly opt-in Windows-DPAPI-protected memo-only restoration copies.
/// Copies are bound to the Windows user profile, not portable/shared credentials.
/// Never copies the selected document or stores plaintext memo bodies in AppData.
/// </summary>
internal static class MemoLocalSnapshots
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunrise", "FileManager", "memo-snapshots");
    private static readonly string Setting = Path.Combine(Root, "enabled.txt");
    private const int MaxSnapshotBytes = 1_100_000;
    private const int MaxEntriesPerMemo = 25;
    private sealed record SnapshotEnvelope(string MemoPath, string User, DateTime SavedUtc, byte[] MemoBytes, string Sha256);
    internal sealed record SnapshotInfo(string Path, DateTime SavedUtc, string User, string Sha256)
    {
        public string Label => $"{SavedUtc.ToLocalTime():yyyy.MM.dd HH:mm:ss} · {User}";
    }

    internal static bool Enabled
    {
        get
        {
            try { return OperatingSystem.IsWindows() && File.Exists(Setting) && File.ReadAllText(Setting).Trim() == "yes"; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows 사용자 보호 기능이 필요합니다.");
        Directory.CreateDirectory(Root);
        File.WriteAllText(Setting, enabled ? "yes" : "no");
        // Turning off stops new backups; it never silently erases existing history.
    }

    internal static string? TrySave(string memoPath, string user)
    {
        if (!Enabled) return null;
        try
        {
            string full = Path.GetFullPath(memoPath);
            if (new FileInfo(full).Length > MaxSnapshotBytes)
                throw new InvalidDataException("메모 복원본 크기 제한을 초과했습니다.");
            byte[] raw = File.ReadAllBytes(full);
            if (raw.Length > MaxSnapshotBytes) throw new InvalidDataException("메모 복원본 크기 제한을 초과했습니다.");
            string digest = Convert.ToHexString(SHA256.HashData(raw));
            var existing = List(full);
            if (existing.Count > 0 && existing[0].Sha256 == digest) return null;
            // Prevent unbounded user-profile growth. Existing protected history is never
            // deleted just to make room; the new copy is refused until the user frees space.
            long occupied = 0;
            foreach (string day in Directory.EnumerateDirectories(Root))
            foreach (string stored in Directory.EnumerateFiles(day, "*.dpapi"))
            {
                occupied += new FileInfo(stored).Length;
                if (occupied > 250L * 1024 * 1024) throw new IOException("로컬 메모 이력 총 용량 250MB 제한에 도달했습니다.");
            }
            string dailyDir = Path.Combine(Root, DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dailyDir);
            string target = Path.Combine(dailyDir, PathKey(full) + "-" + DateTime.UtcNow.ToString("HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8] + ".dpapi");
            var payload = new SnapshotEnvelope(full, user, DateTime.UtcNow, raw, digest);
            byte[] secret = MemoIntegrityBaseline.Protect(JsonSerializer.SerializeToUtf8Bytes(payload));
            using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { fs.Write(secret); fs.Flush(flushToDisk: true); }
            var snapshots = List(full);
            foreach (SnapshotInfo old in snapshots.Skip(MaxEntriesPerMemo))
            {
                // Only prune app-created, DPAPI-encrypted snapshots in our private cache tree.
                if (Path.GetFullPath(old.Path).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    File.Delete(old.Path);
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException or System.ComponentModel.Win32Exception)
        { return "암호화된 메모 복원 이력을 저장하지 못했습니다: " + e.Message; }
    }

    internal static IReadOnlyList<SnapshotInfo> List(string memoPath)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(Root)) return [];
        string full = Path.GetFullPath(memoPath);
        string prefix = PathKey(full) + "-";
        var entries = new List<SnapshotInfo>();
        foreach (string day in Directory.EnumerateDirectories(Root))
        {
            foreach (string candidate in Directory.EnumerateFiles(day, prefix + "*.dpapi"))
            {
                try
                {
                    var data = ReadSnapshot(candidate, full);
                    entries.Add(new SnapshotInfo(candidate, data.SavedUtc, data.User, data.Sha256));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException or JsonException or System.ComponentModel.Win32Exception)
                { System.Diagnostics.Debug.WriteLine("Memo snapshot ignored: " + e.Message); }
            }
        }
        return entries.OrderByDescending(x => x.SavedUtc).Take(100).ToArray();
    }

    internal static string PreviewText(string memoPath, SnapshotInfo version)
    {
        string full = Path.GetFullPath(memoPath);
        var data = ReadSnapshot(version.Path, full);
        if (Convert.ToHexString(SHA256.HashData(data.MemoBytes)) != data.Sha256)
            throw new InvalidDataException("복원 이력의 SHA-256 값이 일치하지 않습니다.");
        using var raw = new MemoryStream(data.MemoBytes);
        byte[] signature = "SUNRISE_MEMO_V1\n"u8.ToArray();
        var head = new byte[signature.Length];
        raw.ReadExactly(head);
        if (!head.AsSpan().SequenceEqual(signature)) throw new InvalidDataException("저장된 메모 형식이 잘못되었습니다.");
        using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192]; int count;
        while ((count = gzip.Read(chunk)) > 0)
        {
            if (buffer.Length + count > 900_000) throw new InvalidDataException("복원 이력이 제한 용량을 초과했습니다.");
            buffer.Write(chunk, 0, count);
        }
        var memo = JsonSerializer.Deserialize<MemoPayload>(buffer.ToArray()) ?? throw new InvalidDataException("복원 메모가 비어 있습니다.");
        if (memo.Text is null || memo.Text.Length > FileMemoStore.MaxTextLength)
            throw new InvalidDataException("복원 메모 본문이 올바르지 않습니다.");
        return memo.Text;
    }

    /// <summary>Restores the selected known history only after the caller obtains explicit user approval.</summary>
    internal static void Restore(string memoPath, SnapshotInfo version, string forFilePath)
    {
        if (!Enabled) throw new InvalidOperationException("로컬 메모 복원 기능이 비활성화되어 있습니다.");
        string full = Path.GetFullPath(memoPath);
        var data = ReadSnapshot(version.Path, full);
        if (Convert.ToHexString(SHA256.HashData(data.MemoBytes)) != data.Sha256)
            throw new InvalidDataException("복원본의 해시가 일치하지 않습니다.");
        // Validate the memo's signature, compressed JSON and target document before replacing anything.
        using (var input = new MemoryStream(data.MemoBytes))
        {
            byte[] signature = "SUNRISE_MEMO_V1\n"u8.ToArray();
            var bytes = new byte[signature.Length];
            input.ReadExactly(bytes);
            if (!bytes.AsSpan().SequenceEqual(signature)) throw new InvalidDataException("복원본 메모 형식이 올바르지 않습니다.");
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192]; int count;
            while ((count = gzip.Read(chunk)) > 0)
            {
                if (buffer.Length + count > 900_000) throw new InvalidDataException("복원본 메모가 지나치게 큽니다.");
                buffer.Write(chunk, 0, count);
            }
            var memo = JsonSerializer.Deserialize<MemoPayload>(buffer.ToArray()) ?? throw new InvalidDataException("복원본이 비어 있습니다.");
            if (!string.Equals(memo.FileName, Path.GetFileName(forFilePath), StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(memo.UserName) || memo.Text is null || memo.Text.Length > FileMemoStore.MaxTextLength ||
                !Path.GetFileName(FileMemoStore.MemoPath(forFilePath, memo.CreatedLocal, memo.UserName))
                    .Equals(Path.GetFileName(full), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("복원본이 선택한 파일의 메모가 아닙니다.");
        }
        // Preserve the currently visible version as an encrypted local copy before replacement.
        if (File.Exists(full))
        {
            string? copyError = TrySave(full, data.User);
            if (copyError is not null) throw new IOException("현재 메모를 백업하지 못해 복원을 중단했습니다. " + copyError);
        }
        string staging = Path.Combine(Path.GetDirectoryName(full)!, ".sunrise-restore-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { fs.Write(data.MemoBytes); fs.Flush(flushToDisk: true); }
            File.Move(staging, full, overwrite: true);
            string? issue = MemoIntegrityBaseline.TrustAfterOwnSave(full, data.User);
            if (issue is not null) System.Diagnostics.Debug.WriteLine("Memo restore baseline: " + issue);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static SnapshotEnvelope ReadSnapshot(string path, string requiredPath)
    {
        string root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith(PathKey(requiredPath) + "-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("다른 메모의 복원본이거나 로컬 기록 경로가 잘못되었습니다.");
        if (new FileInfo(path).Length > 2_000_000) throw new InvalidDataException("복원본 크기 제한 초과");
        var item = JsonSerializer.Deserialize<SnapshotEnvelope>(MemoIntegrityBaseline.Unprotect(File.ReadAllBytes(path)))
            ?? throw new InvalidDataException("복원본 데이터가 없습니다.");
        if (string.IsNullOrWhiteSpace(item.MemoPath) || item.MemoBytes is null ||
            !Path.GetFullPath(item.MemoPath).Equals(requiredPath, StringComparison.OrdinalIgnoreCase) ||
            item.MemoBytes.Length > MaxSnapshotBytes) throw new InvalidDataException("복원본 대상이 올바르지 않습니다.");
        return item;
    }

    private static string PathKey(string path) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
}
