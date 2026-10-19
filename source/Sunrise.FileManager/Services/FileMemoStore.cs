using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunrise.FileManager.Services;

/// <summary>File-local, app-specific memo format. Not encryption or an access-control boundary.</summary>
internal sealed record MemoPayload(string FileName, string UserName, DateTime CreatedLocal, string Text);

public static class FileMemoStore
{
    public const string DirectoryName = "#memo";
    public const string Extension = ".sunmemo";
    public const int MaxTextLength = 200_000;
    private static readonly byte[] Signature = "SUNRISE_MEMO_V1\n"u8.ToArray();
    public sealed record MemoRecord(string Path, string UserName, DateTime CreatedLocal, string Text)
    {
        public string? IntegrityWarning { get; init; }
        public bool HasIntegrityWarning => !string.IsNullOrWhiteSpace(IntegrityWarning);
        public string Label => $"{CreatedLocal:yyyy.MM.dd HH:mm} · {UserName}" + (HasIntegrityWarning ? " ⚠" : "");
    }

    private static string MemoDirectory(string filePath) => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? throw new ArgumentException("경로가 올바르지 않습니다."), "#memo");

    private static string SafeComponent(string value, int maxLength)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        safe = safe.Trim().TrimEnd('.');
        if (safe.Length > maxLength)
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..10];
            safe = safe[..(maxLength - 11)].TrimEnd(' ', '.') + "_" + hash;
        }
        return string.IsNullOrWhiteSpace(safe) ? "unnamed" : safe;
    }

    public static string MemoPath(string filePath, DateTime localDate, string userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) throw new ArgumentException("PC 사용자 이름이 없습니다.", nameof(userName));
        return Path.Combine(MemoDirectory(filePath),
            $"{SafeComponent(Path.GetFileName(filePath), 96)}_memo_{localDate:yy.MM.dd}_{SafeComponent(userName, 40)}{Extension}");
    }

    public static MemoRecord Save(string filePath, string text, DateTime? localDate = null, string? userName = null)
    {
        if (Path.GetExtension(filePath).Equals(Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("메모 파일 자체에는 메모를 작성할 수 없습니다.");
        if (!File.Exists(filePath)) throw new FileNotFoundException("선택한 파일이 존재하지 않습니다.", filePath);
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxTextLength) throw new ArgumentOutOfRangeException(nameof(text), "메모는 최대 200,000자까지 저장할 수 있습니다.");
        string user = string.IsNullOrWhiteSpace(userName) ? Environment.UserName : userName.Trim();
        if (!string.IsNullOrWhiteSpace(userName) && (user.Length > 40 || user.Any(char.IsControl)))
            throw new ArgumentException("작성자 이름은 최대 40자이며 제어문자를 포함할 수 없습니다.", nameof(userName));
        DateTime now = localDate ?? DateTime.Now;
        string path = MemoPath(filePath, now, user);
        string directory = MemoDirectory(filePath);
        string? previousFingerprint = null;
        if (File.Exists(path))
        {
            // Do not silently replace an unreadable or externally changed same-day memo.
            MemoRecord existing = Read(path, filePath);
            previousFingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            var preSave = MemoIntegrityBaseline.Inspect(path, user);
            if (existing.HasIntegrityWarning || preSave.Changed || (preSave.Warning?.Contains("판정할 수 없습니다", StringComparison.Ordinal) == true))
                throw new InvalidDataException("기존 메모가 이 PC에서 확인한 내용과 달라졌거나 비교할 수 없습니다. 저장을 중단했습니다. 메모 기록을 확인하세요. " + existing.IntegrityWarning + " " + preSave.Warning);
        }
        var payload = new MemoPayload(Path.GetFileName(filePath), user, now, text);
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (raw.Length > 900_000) throw new InvalidDataException("메모 파일의 저장 크기가 제한을 초과했습니다.");
        // Only create the memo directory after verifying the selected file and payload.
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Signature);
                using (var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true))
                    gzip.Write(raw);
                stream.Flush(flushToDisk: true);
            }
            // Optimistic concurrency check: an external/shared edit after initial inspection
            // must not be overwritten without notice. The final rename remains atomic.
            if (previousFingerprint is not null)
            {
                if (!File.Exists(path) || !previousFingerprint.Equals(
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("메모 편집 중 다른 프로그램이나 사용자가 같은 메모를 수정했습니다. 덮어쓰지 않고 저장을 중단했습니다.");
            }
            else if (File.Exists(path))
                throw new IOException("메모 편집 중 동일한 이름의 메모가 새로 생성되었습니다. 덮어쓰지 않습니다.");
            File.Move(temporary, path, overwrite: true);
            string? auditWarning = MemoIntegrityBaseline.TrustAfterOwnSave(path, user);
            string? snapshotWarning = MemoLocalSnapshots.TrySave(path, user);
            string? combined = string.Join(" ", new[] { auditWarning, snapshotWarning }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return new MemoRecord(path, user, now, text)
            { IntegrityWarning = string.IsNullOrWhiteSpace(combined) ? null : combined };
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static MemoRecord Read(string path, string forFilePath)
    {
        if (!string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Sunrise 메모 확장자가 아닙니다.");
        if (!string.Equals(Path.GetFullPath(Path.GetDirectoryName(path)!), MemoDirectory(forFilePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("다른 폴더의 메모는 읽을 수 없습니다.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (input.Length > 1_100_000) throw new InvalidDataException("메모 파일 크기가 제한을 초과했습니다.");
        var signature = new byte[Signature.Length];
        input.ReadExactly(signature);
        if (!signature.AsSpan().SequenceEqual(Signature)) throw new InvalidDataException("Sunrise 메모 형식이 아닙니다.");
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = gzip.Read(chunk)) > 0)
        {
            if (buffer.Length + count > 900_000) throw new InvalidDataException("메모 복원 용량 제한 초과");
            buffer.Write(chunk, 0, count);
        }
        var payload = JsonSerializer.Deserialize<MemoPayload>(buffer.ToArray()) ?? throw new InvalidDataException("메모 본문이 비어 있습니다.");
        if (!string.Equals(payload.FileName, Path.GetFileName(forFilePath), StringComparison.OrdinalIgnoreCase) ||
            payload.Text is null || payload.Text.Length > MaxTextLength || string.IsNullOrWhiteSpace(payload.UserName))
            throw new InvalidDataException("선택한 파일의 메모가 아니거나 메모 내용이 올바르지 않습니다.");
        string expectedName = Path.GetFileName(MemoPath(forFilePath, payload.CreatedLocal, payload.UserName));
        string? filenameWarning = string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase)
            ? null : "메모 파일명과 내부 작성자·날짜 정보가 다릅니다. 파일명 또는 메타데이터가 수정되었을 수 있습니다.";
        var comparison = MemoIntegrityBaseline.Inspect(path, payload.UserName);
        // Reading/listing is side-effect-light: never take a full encrypted snapshot on
        // each selection, which would turn large folder browsing into heavy AppData I/O.
        // Snapshots are created on an explicit opt-in, own save, or controlled file move.
        string? warning = string.Join(" ", new[] { filenameWarning, comparison.Warning }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new MemoRecord(path, payload.UserName, payload.CreatedLocal, payload.Text)
        { IntegrityWarning = string.IsNullOrWhiteSpace(warning) ? null : warning };
    }

    /// <summary>
    /// Moves or renames a regular file together with all matching .sunmemo records.
    /// Prepares rewritten records first; originals are not removed until every new record
    /// is published. If publishing fails, the file is moved back and the originals remain.
    /// A failed rollback is reported explicitly; no source memo is intentionally deleted.
    /// Directory moves need no special handling: their internal #memo folders move with them.
    /// </summary>
    public static void MoveFileWithMemos(string sourcePath, string destinationPath)
    {
        string source = Path.GetFullPath(sourcePath);
        string destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("같은 파일명 또는 대소문자만 변경하는 이동은 지원하지 않습니다.");
        if (!File.Exists(source)) throw new FileNotFoundException("원본 파일을 찾을 수 없습니다.", source);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("이동 대상에 같은 이름이 있습니다. 덮어쓰지 않습니다.");
        if (!Directory.Exists(Path.GetDirectoryName(destination)))
            throw new DirectoryNotFoundException("대상 폴더가 존재하지 않습니다.");

        var records = ListForFile(source, out int unreadable);
        if (records.Any(r => r.HasIntegrityWarning))
            throw new IOException("메모 중 일부에서 파일명·작성자 또는 로컬 변경 기준의 불일치가 감지되어 이동을 중단했습니다. 먼저 변경 내용을 확인하세요.");
        if (unreadable > 0)
            throw new IOException($"원본 메모 {unreadable}개가 손상되거나 읽을 수 없어 파일 이동을 중단했습니다.");
        if (records.Count == 0) { File.Move(source, destination); return; }

        string newDir = MemoDirectory(destination);
        if (Directory.Exists(newDir) && (File.GetAttributes(newDir) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("메모 대상 폴더가 링크로 연결되어 있어 안전하게 이동할 수 없습니다.");
        var pending = new List<(string Original, string Staged, string Destination)>();
        bool createdDirectory = !Directory.Exists(newDir);
        bool movedFile = false;
        try
        {
            // Read and prepare *all* destination memo payloads before modifying the source.
            // Preserve the original author/date even if the user's file name changes.
            Directory.CreateDirectory(newDir);
            foreach (MemoRecord record in records)
            {
                string targetMemo = MemoPath(destination, record.CreatedLocal, record.UserName);
                if (File.Exists(targetMemo) || Directory.Exists(targetMemo))
                    throw new IOException("대상 폴더에 동일 날짜·사용자의 메모가 존재하여 이동을 중단했습니다: " + targetMemo);
                string staged = Path.Combine(newDir, ".sunrise-memo-stage-" + Guid.NewGuid().ToString("N") + ".tmp");
                // Add to cleanup list *before* writing; an interrupted write must also be removed.
                pending.Add((record.Path, staged, targetMemo));
                byte[] raw = JsonSerializer.SerializeToUtf8Bytes(
                    new MemoPayload(Path.GetFileName(destination), record.UserName, record.CreatedLocal, record.Text));
                if (raw.Length > 900_000) throw new InvalidDataException("메모를 변환한 결과 저장 크기 제한을 초과했습니다.");
                using var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(Signature);
                using (var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(raw);
                stream.Flush(flushToDisk: true);
            }
            File.Move(source, destination);
            movedFile = true;
            foreach (var memo in pending)
                File.Move(memo.Staged, memo.Destination); // never overwrite existing memos
        }
        catch (Exception original) when (original is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (movedFile)
            {
                try { File.Move(destination, source); }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    // Keep new memo copies if the original file could not be restored.
                    // Original memos still remain intact; report both failures.
                    throw new IOException("파일 이동 후 메모 처리에 실패하고 원본 파일 복구도 실패했습니다. 두 경로를 확인하세요: " +
                        source + " / " + destination, new AggregateException(original, rollback));
                }
            }
            foreach (var memo in pending)
            {
                // At this point the file is at its original path; destination records are disposable.
                try { if (File.Exists(memo.Destination)) File.Delete(memo.Destination); }
                catch (IOException ex) { System.Diagnostics.Debug.WriteLine("Memo rollback cleanup: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine("Memo rollback cleanup: " + ex.Message); }
            }
            throw;
        }
        finally
        {
            foreach (var memo in pending)
            {
                try { if (File.Exists(memo.Staged)) File.Delete(memo.Staged); }
                catch (IOException ex) { System.Diagnostics.Debug.WriteLine("Memo stage cleanup: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine("Memo stage cleanup: " + ex.Message); }
            }
            if (createdDirectory)
            {
                try { if (Directory.Exists(newDir) && !Directory.EnumerateFileSystemEntries(newDir).Any()) Directory.Delete(newDir); }
                catch (IOException) { /* the created memo directory may be in use */ }
                catch (UnauthorizedAccessException) { /* retained rather than deleting other data */ }
            }
        }
        // Commit point: the file and all rewritten memos are present in the new location.
        // Cleanup is best-effort: never revert a completed move because cleanup was denied.
        foreach (var memo in pending)
        {
            // Rename reserializes embedded source filenames. Establish a new PC-local baseline.
            MemoRecord? record = records.FirstOrDefault(r => string.Equals(r.Path, memo.Original, StringComparison.OrdinalIgnoreCase));
            if (record is not null)
            {
                string? issue = MemoIntegrityBaseline.TrustAfterOwnSave(memo.Destination, record.UserName);
                if (issue is not null) System.Diagnostics.Debug.WriteLine("Memo rename baseline: " + issue);
                string? snapshotIssue = MemoLocalSnapshots.TrySave(memo.Destination, record.UserName);
                if (snapshotIssue is not null) System.Diagnostics.Debug.WriteLine("Memo rename history: " + snapshotIssue);
            }
            try { File.Delete(memo.Original); }
            catch (IOException ex) { System.Diagnostics.Debug.WriteLine("Old memo retained: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine("Old memo retained: " + ex.Message); }
        }
    }

    public static IReadOnlyList<MemoRecord> ListForFile(string filePath) => ListForFile(filePath, out _);

    public static IReadOnlyList<MemoRecord> ListForFile(string filePath, out int unreadable)
    {
        unreadable = 0;
        string dir = MemoDirectory(filePath);
        if (!Directory.Exists(dir)) return [];
        string prefix = SafeComponent(Path.GetFileName(filePath), 96) + "_memo_";
        var records = new List<MemoRecord>();
        int scanned = 0;
        foreach (string path in Directory.EnumerateFiles(dir, "*" + Extension))
        {
            if (++scanned > 20_000) throw new InvalidDataException("메모 목록이 20,000개를 초과했습니다.");
            if (!Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            try { records.Add(Read(path, filePath)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                // A damaged unrelated memo does not prevent valid memos from appearing.
                unreadable++;
                System.Diagnostics.Debug.WriteLine($"Memo unreadable: {path}: {ex.Message}");
            }
        }
        return records.OrderByDescending(item => item.CreatedLocal).ThenByDescending(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
