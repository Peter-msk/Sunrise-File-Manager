using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunrise.FileManager.Services;

/// <summary>
/// R26: opt-in-on-next-copy byte continuation for a single local/SMB/UNC file.
/// Only the explicit copy operation may use this checkpoint: never a move or deletion.
/// Checkpoint metadata is DPAPI CurrentUser-protected, contains no credentials, and is
/// written to a bounded per-user directory. The .part payload lives next to its destination,
/// is never published without full SHA-256 verification, and must not bypass SMB/DLP denials.
/// </summary>
public static class ResumableLocalCopyService
{
    private const int BufferSize = 256 * 1024;
    private const int MaximumPending = 20;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Sunrise.FileManager.R26.LocalResume.V1");
    private static readonly object Gate = new();
    private static string Root => SunriseGroupPaths.Local("copy-resume");

    public sealed record Checkpoint(string Id, string Source, string Destination, string Part,
        long SourceLength, DateTime SourceModifiedUtc, DateTime CreatedUtc);

    private static string RecordPath(string id) => Path.Combine(Root, "copy-" + id + ".sfrcopy");

    private static bool ValidId(string id) => id.Length == 32 && id.All(Uri.IsHexDigit);

    private static Checkpoint? Read(string path)
    {
        try
        {
            if (Reparse(path) || new FileInfo(path).Length is < 1 or > 32 * 1024) return null;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var plan = JsonSerializer.Deserialize<Checkpoint>(plain);
            CryptographicOperations.ZeroMemory(plain);
            if (plan is null || !ValidId(plan.Id) ||
                !Path.GetFileName(path).Equals("copy-" + plan.Id + ".sfrcopy", StringComparison.OrdinalIgnoreCase) ||
                !Path.IsPathFullyQualified(plan.Source) || !Path.IsPathFullyQualified(plan.Destination) ||
                !Path.IsPathFullyQualified(plan.Part) || plan.SourceLength < 0 ||
                plan.CreatedUtc > DateTime.UtcNow.AddMinutes(5) ||
                !plan.Part.Equals(Path.Combine(Path.GetDirectoryName(plan.Destination)!,
                    ".sfm-resume-" + plan.Id + ".part"), StringComparison.OrdinalIgnoreCase)) return null;
            return plan;
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public static IReadOnlyList<Checkpoint> Pending()
    {
        if (!Directory.Exists(Root)) return [];
        if (Reparse(Root)) throw new IOException("중단 복사 기록 저장소가 연결 경로로 변경됐습니다.");
        string[] paths = Directory.GetFiles(Root, "copy-*.sfrcopy");
        if (paths.Length > MaximumPending)
            throw new IOException("중단 복사 기록이 20건을 초과합니다. 사용자 확인이 필요합니다.");
        var checkpoints = new List<Checkpoint>(paths.Length);
        foreach (string path in paths)
        {
            // R46: never silently treat corrupt, unreadable or foreign-user records as absent.
            // Otherwise a new checkpoint could start a second copy of an ambiguous transfer.
            Checkpoint? checkpoint = Read(path);
            if (checkpoint is null)
                throw new IOException("중단 복사 기록이 손상됐거나 현재 사용자 기록이 아닙니다. 기록과 원본을 보존하고 확인하세요.");
            checkpoints.Add(checkpoint);
        }
        return checkpoints;
    }

    /// <summary>Discard a reviewed checkpoint and its exact R26-created part only on explicit user request.</summary>
    public static void Discard(Checkpoint plan)
    {
        if (plan is null || !ValidId(plan.Id) ||
            !Pending().Any(p => p == plan))
            throw new IOException("확인되지 않은 전송 복구 기록은 정리할 수 없습니다.");
        // Never delete arbitrary paths supplied by a caller or a modified journal.
        string expectedPart = Path.Combine(Path.GetDirectoryName(plan.Destination)!,
            ".sfm-resume-" + plan.Id + ".part");
        if (!plan.Part.Equals(expectedPart, StringComparison.OrdinalIgnoreCase))
            throw new IOException("복구 임시파일 경로가 올바르지 않습니다.");
        if (File.Exists(plan.Part)) File.Delete(plan.Part); // only our exact generated part
        File.Delete(RecordPath(plan.Id));
    }

    private static void Save(Checkpoint plan)
    {
        Directory.CreateDirectory(Root);
        if (Reparse(Root)) throw new IOException("중단 복사 기록 저장소가 연결 경로로 변경됐습니다.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(plan);
        byte[] encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(bytes);
        string record = RecordPath(plan.Id);
        string temporary = record + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(encrypted);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, record, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool Reparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static FileStream AcquireTransferLease(string source, string destination)
    {
        Directory.CreateDirectory(Root);
        if (Reparse(Root)) throw new IOException("전송 복구 저장소에 연결 경로가 있습니다.");
        // R46: FileShare.None protects the same copy pair across multiple File Manager
        // processes. The zero-byte lease file has no source/target content or credentials;
        // it is intentionally left in place when the process exits or crashes.
        byte[] fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(
            source.ToUpperInvariant() + "\0" + destination.ToUpperInvariant()));
        string leasePath = Path.Combine(Root, "lease-" + Convert.ToHexString(fingerprint) + ".lck");
        if (File.Exists(leasePath) && Reparse(leasePath))
            throw new IOException("전송 작업 잠금 파일이 연결 경로로 변경됐습니다.");
        try
        {
            return new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException("동일 원본·대상 복사가 다른 창 또는 프로세스에서 진행 중입니다. 중복 전송을 시작하지 않습니다.", ex);
        }
    }

    private static async Task VerifyPrefixAsync(FileStream source, FileStream partial,
        long copied, CancellationToken ct)
    {
        source.Position = 0;
        partial.Position = 0;
        byte[] src = new byte[BufferSize];
        byte[] dst = new byte[BufferSize];
        long remaining = copied;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(remaining, BufferSize);
            await source.ReadExactlyAsync(src.AsMemory(0, count), ct).ConfigureAwait(false);
            await partial.ReadExactlyAsync(dst.AsMemory(0, count), ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(src.AsSpan(0, count), dst.AsSpan(0, count)))
                throw new IOException("복구할 임시파일의 기존 데이터가 원본과 다릅니다. 원본과 임시파일을 보존했습니다.");
            remaining -= count;
        }
        source.Position = copied;
        partial.Position = copied;
    }

    public static async Task CopyOrResumeAsync(string original, string finalDestination,
        IProgress<CloudDrive.CloudTransferProgress>? progress, CancellationToken ct)
    {
        string source = Path.GetFullPath(original);
        string destination = Path.GetFullPath(finalDestination);
        string folder = Path.GetDirectoryName(destination) ?? throw new IOException("대상 폴더가 없습니다.");
        using var transferLease = AcquireTransferLease(source, destination);
        if (!File.Exists(source) || !Directory.Exists(folder) ||
            File.Exists(destination) || Directory.Exists(destination) ||
            source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            Reparse(source) || Reparse(folder))
            throw new IOException("복구 가능한 복사의 원본·대상 또는 링크 조건이 올바르지 않습니다. 덮어쓰지 않습니다.");
        Checkpoint plan;
        lock (Gate)
        {
            var existing = Pending().Where(x => x.Source.Equals(source, StringComparison.OrdinalIgnoreCase) &&
                x.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (existing.Length > 1) throw new IOException("중복 전송 복구 기록이 있어 자동 선택하지 않습니다.");
            if (existing.Length == 1) plan = existing[0];
            else
            {
                if (Directory.Exists(Root) && Directory.GetFiles(Root, "copy-*.sfrcopy").Length >= MaximumPending)
                    throw new IOException("복구 기록 20개가 남아 있어 추가 전송을 시작하지 않습니다.");
                var info = new FileInfo(source);
                string id = Guid.NewGuid().ToString("N");
                plan = new Checkpoint(id, source, destination, Path.Combine(folder, ".sfm-resume-" + id + ".part"),
                    info.Length, info.LastWriteTimeUtc, DateTime.UtcNow);
                Save(plan);
            }
        }
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("전송 중 대상이 생성돼 덮어쓰지 않습니다.");
        var current = new FileInfo(source);
        if (current.Length != plan.SourceLength || current.LastWriteTimeUtc != plan.SourceModifiedUtc ||
            Reparse(source) || Reparse(folder))
            throw new IOException("원본 파일이 복구 기록 이후 변경됐습니다. 원본과 기록을 보존하고 재개하지 않습니다.");
        // Stage is plaintext at the authorized destination; unlike remote-to-remote encrypted
        // staging, this is a LOCAL target publication buffer. Its presence is visible to DLP.
        if (File.Exists(plan.Part) && Reparse(plan.Part))
            throw new IOException("복구 임시파일이 링크이므로 열지 않습니다.");
        await using (var reader = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var writer = new FileStream(plan.Part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (Reparse(plan.Part) || writer.Length > plan.SourceLength)
                throw new IOException("임시파일 경로 또는 크기가 올바르지 않아 재개하지 않습니다.");
            long copied = writer.Length;
            await VerifyPrefixAsync(reader, writer, copied, ct).ConfigureAwait(false);
            progress?.Report(new(copied, plan.SourceLength));
            byte[] buffer = new byte[BufferSize];
            while (copied < plan.SourceLength)
            {
                ct.ThrowIfCancellationRequested();
                int length = (int)Math.Min(buffer.Length, plan.SourceLength - copied);
                await reader.ReadExactlyAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
                await writer.WriteAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
                copied += length;
                progress?.Report(new(copied, plan.SourceLength));
            }
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        current.Refresh();
        if (!current.Exists || current.Length != plan.SourceLength || current.LastWriteTimeUtc != plan.SourceModifiedUtc)
            throw new IOException("복사 중 원본 메타데이터가 변경돼 게시하지 않았습니다.");
        await using (var originalStream = File.OpenRead(source))
        await using (var partialStream = File.OpenRead(plan.Part))
        {
            byte[] expected = await SHA256.HashDataAsync(originalStream, ct).ConfigureAwait(false);
            byte[] actual = await SHA256.HashDataAsync(partialStream, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual) || partialStream.Length != plan.SourceLength)
                throw new IOException("재개 파일 전체 SHA-256 검증 실패. 원본은 보존했습니다.");
        }
        ct.ThrowIfCancellationRequested();
        current.Refresh();
        if (!current.Exists || current.Length != plan.SourceLength ||
            current.LastWriteTimeUtc != plan.SourceModifiedUtc || Reparse(source) || Reparse(folder))
            throw new IOException("최종 검증 중 원본 또는 경로가 변경돼 게시하지 않았습니다. 기록을 보존합니다.");
        File.Move(plan.Part, destination, overwrite: false);
        // Journal deletion may fail after successful publication; the target file remains correct.
        try { File.Delete(RecordPath(plan.Id)); }
        catch (IOException) { System.Diagnostics.Trace.TraceWarning("R26 completed copy checkpoint cleanup pending."); }
        catch (UnauthorizedAccessException) { System.Diagnostics.Trace.TraceWarning("R26 checkpoint cleanup denied."); }
        progress?.Report(new(plan.SourceLength, plan.SourceLength));
    }
}
