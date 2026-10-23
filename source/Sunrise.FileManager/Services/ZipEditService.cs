using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Sunrise.FileManager.Services;

/// <summary>
/// ZIP-only staged change. A ZIP with unsupported encryption/links or unsafe names is not modified.
/// Other archive formats remain read/extract-only. Windows release validation is still required.
/// </summary>
public static class ZipEditService
{
    public enum Operation { Add, Replace, Remove }
    public const long MaxInputBytes = 512L * 1024 * 1024;
    // Serialize edits inside this process, including edits initiated from other File Manager windows.
    private static readonly object EditGate = new();

    public static string Apply(string archivePath, Operation operation, string entryName, string? localFile, CancellationToken token)
    {
        lock (EditGate) return ApplyLocked(archivePath, operation, entryName, localFile, token);
    }

    private static string ApplyLocked(string archivePath, Operation operation, string entryName, string? localFile, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string original = Path.GetFullPath(archivePath);
        if (!original.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new IOException("ZIP 파일만 내부 수정할 수 있습니다.");
        FileActionsService.EnsureOrdinaryEntry(original);
        ValidateEntry(entryName, directory: false);
        if (new FileInfo(original).Length > MaxInputBytes) throw new IOException("ZIP 직접 수정은 512MiB 이하에서 지원합니다.");
        if (operation is not (Operation.Add or Operation.Replace or Operation.Remove)) throw new ArgumentException("지원하지 않는 압축 작업입니다.");

        // SharpCompress reports encryption; editing encrypted or linked archives with ZipArchive.Update
        // may silently lose unsupported metadata. Disallow instead of manufacturing a partial archive.
        var existingItems = ArchiveService.List(original, token);
        if (existingItems.Any(item => item.Encrypted)) throw new IOException("암호화된 ZIP은 내용을 변경하지 않습니다. 먼저 별도 안전한 사본으로 해제하세요.");

        string? inputPath = null;
        if (operation != Operation.Remove)
        {
            if (localFile is null) throw new IOException("추가·교체할 파일을 선택하세요.");
            inputPath = Path.GetFullPath(localFile);
            if (inputPath.Equals(original, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(inputPath) || ReparsePointPolicy.IsBlocked(inputPath) ||
                new FileInfo(inputPath).Length > MaxInputBytes)
                throw new IOException("추가·교체할 파일이 없거나 너무 큽니다.");
        }
        byte[] originalHash;
        using (var origin = File.OpenRead(original)) originalHash = SHA256.HashData(origin);

        string parent = Path.GetDirectoryName(original)!;
        string staging = Path.Combine(parent, ".sunrise-zip-edit-" + Guid.NewGuid().ToString("N") + ".zip");
        string backup = Path.Combine(parent, ".sunrise-zip-backup-" + Guid.NewGuid().ToString("N") + ".zip");
        bool switched = false;
        byte[]? writtenDigest = null;
        try
        {
            token.ThrowIfCancellationRequested();
            File.Copy(original, staging, overwrite: false);
            using (var stream = new FileStream(staging, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false))
            {
                // Validate EVERY original entry, not just the requested item.
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long expanded = 0;
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    ValidateEntry(entry.FullName, entry.FullName.EndsWith('/'));
                    if (!names.Add(entry.FullName)) throw new IOException("ZIP 내부에 대소문자 중복 이름이 있습니다.");
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new IOException("링크 항목은 변경하지 않습니다.");
                    expanded = checked(expanded + entry.Length);
                    if (expanded > ArchiveService.MaxExpandedBytes || names.Count > ArchiveService.MaxEntries)
                        throw new IOException("압축 파일 항목 또는 해제 크기 제한을 초과했습니다.");
                }
                var existing = zip.Entries.FirstOrDefault(x => x.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase));
                if (operation == Operation.Add && existing is not null) throw new IOException("같은 이름의 압축 항목이 있습니다.");
                if (operation != Operation.Add && existing is null) throw new IOException("선택한 압축 항목이 없습니다.");
                if (existing is not null && existing.FullName.EndsWith('/')) throw new IOException("디렉터리 항목은 직접 수정하지 않습니다.");

                // Enforce post-edit expanded size and count BEFORE modifying the staging archive.
                long removeBytes = existing?.Length ?? 0;
                long newBytes = inputPath is null ? 0 : new FileInfo(inputPath).Length;
                if (expanded - removeBytes > ArchiveService.MaxExpandedBytes - newBytes ||
                    names.Count + (operation == Operation.Add ? 1 : operation == Operation.Remove ? -1 : 0) > ArchiveService.MaxEntries)
                    throw new IOException("ZIP 수정 후 크기/항목 수 제한을 초과합니다.");

                existing?.Delete();
                if (inputPath is not null)
                {
                    var added = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var source = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var writer = added.Open();
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[128 * 1024]; int count;
                    while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        writer.Write(buffer, 0, count);
                        digest.AppendData(buffer, 0, count);
                    }
                    writtenDigest = digest.GetHashAndReset();
                }
            }
            token.ThrowIfCancellationRequested();
            // Reopen the staged ZIP and independently verify the changed entry CONTENT, not only its header.
            using (var verified = ZipFile.OpenRead(staging))
            {
                var changed = verified.Entries.FirstOrDefault(x => x.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase));
                if ((operation == Operation.Remove) == (changed is not null)) throw new IOException("ZIP 항목 변경 검증에 실패했습니다.");
                if (writtenDigest is not null)
                {
                    using var input = changed!.Open();
                    if (!SHA256.HashData(input).AsSpan().SequenceEqual(writtenDigest))
                        throw new IOException("ZIP 변경 파일 내용의 무결성 확인에 실패했습니다.");
                }
                long length = 0;
                foreach (var item in verified.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    length = checked(length + item.Length);
                    if (length > ArchiveService.MaxExpandedBytes) throw new IOException("ZIP 검증 크기 제한을 초과했습니다.");
                }
            }
            // Prevent publishing against an externally modified original.
            using (var origin = File.OpenRead(original))
                if (!SHA256.HashData(origin).AsSpan().SequenceEqual(originalHash))
                    throw new IOException("다른 프로그램에서 ZIP 원본을 변경했습니다. 덮어쓰지 않습니다.");
            token.ThrowIfCancellationRequested();
            File.Replace(staging, original, backup);
            switched = true;
            try
            {
                using var reopened = ZipFile.OpenRead(original);
                var changed = reopened.Entries.FirstOrDefault(x => x.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase));
                if ((operation == Operation.Remove) == (changed is not null)) throw new IOException("ZIP 교체 후 항목 확인에 실패했습니다.");
                if (writtenDigest is not null)
                {
                    using var input = changed!.Open();
                    if (!SHA256.HashData(input).AsSpan().SequenceEqual(writtenDigest))
                        throw new IOException("ZIP 교체 후 파일 내용 검증에 실패했습니다.");
                }
            }
            catch
            {
                // A failed rollback preserves the backup for manual recovery; never erase it in finally.
                File.Replace(backup, original, null);
                throw;
            }
            File.Delete(backup);
            return original;
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
            if (!switched && File.Exists(backup) && !File.Exists(original)) File.Move(backup, original);
        }
    }

    private static void ValidateEntry(string entryName, bool directory)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName.StartsWith('/') || entryName.Contains('\\'))
            throw new IOException("ZIP 항목 이름이 올바르지 않습니다.");
        string name = directory ? entryName.TrimEnd('/') : entryName;
        if (name.Length == 0 || name.Split('/').Any(part => part.Length == 0))
            throw new IOException("ZIP에 비정상적인 경로 구분이 있습니다.");
        foreach (string part in name.Split('/'))
            FileActionsService.ValidateName(part);
    }
}
