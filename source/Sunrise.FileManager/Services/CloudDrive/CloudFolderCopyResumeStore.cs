using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>
/// R43: CurrentUser-DPAPI journal for *explicit* local-folder-to-cloud retries only.
/// No tokens or signed URLs. Writes never delete source/remote content. A malformed
/// checkpoint prevents all new automatic folder creation until manually reviewed.
/// </summary>
public static class CloudFolderCopyResumeStore
{
    private const int MaximumPending = 20;
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Sunrise.FileManager.R43.CloudFolderCopy.V1");
    private static readonly object Gate = new();
    private static string Root => Sunrise.FileManager.Services.SunriseGroupPaths.Local("cloud-folder-copy-resume");

    public sealed record CompletedFile(string Relative, string ItemId, string Sha256, long Length);
    public sealed record Checkpoint(string Id, CloudProvider Provider, string? DriveId,
        string AccountFingerprint, string SourcePath, string ParentId, string RootId,
        string RootName, string ManifestSha256, DateTime CreatedUtc,
        Dictionary<string, string> FolderIds, List<CompletedFile> Completed)
    {
        public override string ToString() => $"CloudFolderCopyCheckpoint({Id}, {Provider})";
    }

    private static bool ValidId(string? id) => id is { Length: 32 } && id.All(Uri.IsHexDigit);
    private static bool ValidSha(string? sha) => sha is { Length: 64 } && sha.All(Uri.IsHexDigit);
    private static bool Linked(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static string RecordPath(string id) => Path.Combine(Root, "folder-" + id + ".sfrfolder");

    private static Checkpoint Load(string path)
    {
        byte[] plain = [];
        try
        {
            if (Linked(path) || new FileInfo(path).Length is < 1 or > MaximumBytes)
                throw new IOException("폴더 복구 기록 크기·연결 경로가 올바르지 않습니다.");
            plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var cp = JsonSerializer.Deserialize<Checkpoint>(plain)
                ?? throw new IOException("폴더 복구 기록 내용이 비어 있습니다.");
            if (!ValidId(cp.Id) || !Path.GetFileName(path).Equals("folder-" + cp.Id + ".sfrfolder", StringComparison.OrdinalIgnoreCase) ||
                !Enum.IsDefined(cp.Provider) || !ValidSha(cp.AccountFingerprint) || !ValidSha(cp.ManifestSha256) ||
                string.IsNullOrWhiteSpace(cp.RootName) || cp.RootName.Length > 255 ||
                !Path.IsPathFullyQualified(cp.SourcePath) ||
                string.IsNullOrWhiteSpace(cp.ParentId) || cp.ParentId.Length > 2048 ||
                string.IsNullOrWhiteSpace(cp.RootId) || cp.RootId.Length > 2048 ||
                cp.FolderIds is null || cp.Completed is null || cp.FolderIds.Count > CrossPaneFolderCopyService.MaximumEntries + 1 ||
                cp.Completed.Count > CrossPaneFolderCopyService.MaximumEntries ||
                !cp.FolderIds.TryGetValue("", out var rootId) || rootId != cp.RootId ||
                cp.FolderIds.Any(kv => kv.Value is null || kv.Value.Length is < 1 or > 2048 || kv.Key.Length > 4096 ||
                    (kv.Key.Length > 0 && (Path.IsPathRooted(kv.Key) || kv.Key.Split(Path.DirectorySeparatorChar).Any(seg => seg is ".." or ".")))) ||
                cp.FolderIds.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != cp.FolderIds.Count ||
                cp.Completed.Any(c => c.Relative.Length is < 1 or > 4096 || Path.IsPathRooted(c.Relative) ||
                    c.Relative.Split(Path.DirectorySeparatorChar).Any(seg => seg is ".." or ".") ||
                    c.ItemId.Length is < 1 or > 2048 ||
                    !ValidSha(c.Sha256) || c.Length < 0) ||
                cp.Completed.Select(c => c.Relative).Distinct(StringComparer.OrdinalIgnoreCase).Count() != cp.Completed.Count ||
                cp.CreatedUtc > DateTime.UtcNow.AddMinutes(5))
                throw new IOException("폴더 복구 기록의 항목·식별자가 올바르지 않습니다.");
            return cp;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException or NullReferenceException or OverflowException)
        { throw new IOException("폴더 복구 기록이 손상되었거나 현재 사용자에게 속하지 않습니다."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static IReadOnlyList<Checkpoint> Pending()
    {
        if (!Directory.Exists(Root)) return [];
        if (Linked(Root)) throw new IOException("폴더 복구 저장소가 연결 경로로 바뀌었습니다.");
        var records = Directory.GetFiles(Root, "folder-*.sfrfolder");
        if (records.Length > MaximumPending)
            throw new IOException("폴더 복구 기록이 너무 많습니다. 사용자 확인이 필요합니다.");
        return records.Select(Load).ToArray(); // No silent skip of corrupt records.
    }

    internal static Checkpoint? Find(string sourcePath)
    {
        lock (Gate)
        {
            string path = Path.GetFullPath(sourcePath);
            var matching = Pending().Where(cp => cp.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length > 1) throw new IOException("동일한 원본 폴더의 복구 기록이 중복됩니다.");
            return matching.SingleOrDefault();
        }
    }

    internal static void Validate(Checkpoint cp, CloudFileTransferService transfer,
        string account, string sourcePath, string parentId, string rootName, string manifest)
    {
        if (DateTime.UtcNow - cp.CreatedUtc > MaximumAge ||
            cp.Provider != transfer.RecoveryProvider ||
            !string.Equals(cp.DriveId, transfer.RecoveryDriveId, StringComparison.Ordinal) ||
            !string.Equals(cp.AccountFingerprint, account, StringComparison.Ordinal) ||
            !cp.SourcePath.Equals(Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(cp.ParentId, parentId, StringComparison.Ordinal) ||
            !string.Equals(cp.RootName, rootName, StringComparison.Ordinal) ||
            !string.Equals(cp.ManifestSha256, manifest, StringComparison.Ordinal))
            throw new IOException("폴더 복구 기록의 계정·대상·원본 구성·유효기간이 달라 자동 재개하지 않습니다. 원본과 복구 기록은 보존합니다.");
    }

    internal static Checkpoint Create(CloudFileTransferService transfer, string account,
        string sourcePath, string parentId, string rootId, string rootName, string manifest)
    {
        if (!ValidSha(account) || !ValidSha(manifest)) throw new IOException("폴더 복구 식별자가 유효하지 않습니다.");
        lock (Gate)
        {
            if (Pending().Count >= MaximumPending) throw new IOException("폴더 복구 기록이 20개입니다.");
            if (Pending().Any(cp => cp.SourcePath.Equals(Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase)))
                throw new IOException("원본 폴더의 복구 기록이 이미 있습니다.");
            var cp = new Checkpoint(Guid.NewGuid().ToString("N"), transfer.RecoveryProvider,
                transfer.RecoveryDriveId, account, Path.GetFullPath(sourcePath), parentId, rootId,
                rootName, manifest, DateTime.UtcNow, new(StringComparer.OrdinalIgnoreCase) { [""] = rootId }, []);
            SaveNew(cp);
            return cp;
        }
    }

    private static byte[] Encrypt(Checkpoint cp)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(cp);
        try
        {
            if (plain.Length > MaximumBytes / 2) throw new IOException("폴더 복구 기록이 허용 크기를 초과합니다.");
            return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static void SaveNew(Checkpoint cp)
    {
        Directory.CreateDirectory(Root);
        if (Linked(Root)) throw new IOException("폴더 복구 저장소가 연결 경로로 바뀌었습니다.");
        byte[] ciphertext = Encrypt(cp);
        string tmp = RecordPath(cp.Id) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(ciphertext);
                output.Flush(flushToDisk: true);
            }
            File.Move(tmp, RecordPath(cp.Id), overwrite: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    internal static void Update(Checkpoint cp)
    {
        lock (Gate)
        {
            if (!ValidId(cp.Id) || !File.Exists(RecordPath(cp.Id)) || Linked(RecordPath(cp.Id)))
                throw new IOException("폴더 복구 기록이 없어 변경할 수 없습니다.");
            // Never allow checkpoint replacement when original user record is invalid.
            if (Load(RecordPath(cp.Id)).Id != cp.Id) throw new IOException("폴더 복구 기록 식별자가 바뀌었습니다.");
            byte[] ciphertext = Encrypt(cp);
            string temp = RecordPath(cp.Id) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(ciphertext);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temp, RecordPath(cp.Id), overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    /// <summary>Only called after explicit UI confirmation. Removes journal only;
    /// already created remote folders/files and local sources are never deleted.</summary>
    public static void Discard(Checkpoint cp)
    {
        if (cp is null) throw new ArgumentNullException(nameof(cp));
        lock (Gate)
        {
            if (!Pending().Any(x => x.Id == cp.Id && x.SourcePath == cp.SourcePath &&
                x.RootId == cp.RootId && x.ParentId == cp.ParentId && x.AccountFingerprint == cp.AccountFingerprint))
                throw new IOException("검증되지 않은 폴더 복구 기록은 정리하지 않습니다.");
            File.Delete(RecordPath(cp.Id));
        }
    }

    internal static void Complete(Checkpoint cp)
    {
        lock (Gate)
        {
            if (!ValidId(cp.Id) || !File.Exists(RecordPath(cp.Id)) || Linked(RecordPath(cp.Id)) ||
                Load(RecordPath(cp.Id)).Id != cp.Id)
                throw new IOException("폴더 복구 기록을 안전하게 완료할 수 없습니다.");
            File.Delete(RecordPath(cp.Id)); // no remote/local content deletion.
        }
    }
}
