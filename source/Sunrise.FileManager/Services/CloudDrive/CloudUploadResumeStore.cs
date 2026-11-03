using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>
/// R28: per-Windows-user encrypted upload-session journal. SessionUrl is a preauthorized
/// bearer-equivalent capability: it is ONLY serialized inside a CurrentUser DPAPI blob.
/// It is never logged, put into clipboard, or shipped with release artifacts.
/// Explicitly retrying the same file is the only way to resume; startup never sends bytes.
/// </summary>
public static class CloudUploadResumeStore
{
    private const int MaximumPending = 20;
    private const int MaximumBytes = 32 * 1024;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(2);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Sunrise.FileManager.R28.CloudUpload.V1");
    private static readonly object Gate = new();
    private static string Root => Sunrise.FileManager.Services.SunriseGroupPaths.Local("cloud-upload-resume");

    public sealed record Checkpoint(string Id, CloudProvider Provider, string? DriveId, string ParentId,
        string Name, string LocalPath, long Length, string SourceSha256, long SourceLastWriteUtcTicks,
        string AccountFingerprint, string SessionUrl, DateTimeOffset ExpiresUtc, DateTime CreatedUtc)
    {
        // The default record ToString() would expose the preauthorized upload URL.
        public override string ToString() => $"CloudUploadCheckpoint({Id}, {Provider}, expires {ExpiresUtc:O})";
    }

    private static string PathFor(string id) => Path.Combine(Root, "upload-" + id + ".sfrupload");
    private static bool ValidId(string id) => id.Length == 32 && id.All(Uri.IsHexDigit);
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool Expired(Checkpoint p) => p.CreatedUtc > DateTime.UtcNow.AddMinutes(5) ||
        DateTime.UtcNow - p.CreatedUtc > MaximumAge || p.ExpiresUtc <= DateTimeOffset.UtcNow.AddMinutes(1);

    private static Checkpoint Read(string path)
    {
        byte[] plain = [];
        try
        {
            if (IsLink(path) || new FileInfo(path).Length > MaximumBytes)
                throw new IOException("업로드 복구 기록의 파일 형식이 잘못됐습니다.");
            plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var checkpoint = JsonSerializer.Deserialize<Checkpoint>(plain) ?? throw new IOException("업로드 기록이 비어 있습니다.");
            if (!ValidId(checkpoint.Id) || !Path.GetFileName(path).Equals(Path.GetFileName(PathFor(checkpoint.Id)), StringComparison.OrdinalIgnoreCase) ||
                !Enum.IsDefined(checkpoint.Provider) || checkpoint.Length <= 0 ||
                string.IsNullOrWhiteSpace(checkpoint.ParentId) || checkpoint.ParentId.Length > 2048 ||
                string.IsNullOrWhiteSpace(checkpoint.Name) || checkpoint.Name.Length > 255 ||
                !Path.IsPathFullyQualified(checkpoint.LocalPath) ||
                checkpoint.SourceSha256.Length != 64 || !checkpoint.SourceSha256.All(Uri.IsHexDigit) ||
                checkpoint.AccountFingerprint.Length != 64 || !checkpoint.AccountFingerprint.All(Uri.IsHexDigit) ||
                checkpoint.SessionUrl.Length > 4096 || checkpoint.SessionUrl.Length < 10 ||
                !Uri.TryCreate(checkpoint.SessionUrl, UriKind.Absolute, out var session) ||
                !CloudFileTransferService.IsTransferHost(session, checkpoint.Provider, upload: true) ||
                checkpoint.ExpiresUtc > DateTimeOffset.UtcNow.AddDays(7) ||
                checkpoint.CreatedUtc > DateTime.UtcNow.AddMinutes(5))
                throw new IOException("업로드 기록의 인증/식별 정보가 유효하지 않습니다.");
            return checkpoint;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException or NullReferenceException or OverflowException)
        {
            throw new IOException("업로드 복구 기록이 손상되었거나 현재 Windows 사용자에게 속하지 않습니다.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static IReadOnlyList<Checkpoint> Pending()
    {
        lock (Gate)
        {
            if (!Directory.Exists(Root)) return [];
            if (IsLink(Root)) throw new IOException("업로드 기록 폴더가 연결 경로로 바뀌었습니다.");
            var files = Directory.GetFiles(Root, "upload-*.sfrupload");
            if (files.Length > MaximumPending) throw new IOException("업로드 복구 기록이 허용 개수를 초과했습니다.");
            // Fail closed on malformed records; do not accidentally create a second session.
            return files.Select(Read).ToArray();
        }
    }

    public static Checkpoint? Find(CloudProvider provider, string localPath)
    {
        string full = Path.GetFullPath(localPath);
        var matches = Pending().Where(p => p.Provider == provider &&
            p.LocalPath.Equals(full, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new IOException("중복 업로드 복구 기록을 자동 선택하지 않습니다.");
        return matches.SingleOrDefault();
    }

    public static void Validate(Checkpoint plan, CloudProvider provider, string? driveId, string parentId,
        string name, string localPath, long length, string sha256, long lastWriteUtcTicks, string account)
    {
        if (Expired(plan) || plan.Provider != provider || !string.Equals(plan.DriveId, driveId, StringComparison.Ordinal) ||
            !string.Equals(plan.ParentId, parentId, StringComparison.Ordinal) ||
            !string.Equals(plan.Name, name, StringComparison.Ordinal) ||
            !plan.LocalPath.Equals(Path.GetFullPath(localPath), StringComparison.OrdinalIgnoreCase) ||
            plan.Length != length || plan.SourceLastWriteUtcTicks != lastWriteUtcTicks ||
            !string.Equals(plan.SourceSha256, sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.AccountFingerprint, account, StringComparison.Ordinal))
            throw new IOException("계정·대상·원본 내용·수정 시각·세션 만료 조건이 달라 업로드를 재개하지 않습니다.");
        if (!File.Exists(plan.LocalPath) || IsLink(plan.LocalPath))
            throw new IOException("업로드 원본 파일 또는 경로가 변경됐습니다.");
    }

    public static Checkpoint Create(CloudProvider provider, string? driveId, string parentId, string name,
        string localPath, long length, string sha256, long lastWriteUtcTicks, string account,
        Uri session, DateTimeOffset expires)
    {
        if (length <= 0 || expires <= DateTimeOffset.UtcNow.AddMinutes(1) ||
            expires > DateTimeOffset.UtcNow.AddDays(7) ||
            !CloudFileTransferService.IsTransferHost(session, provider, upload: true))
            throw new IOException("업로드 세션의 용량·유효기간·주소를 확인하지 못했습니다.");
        lock (Gate)
        {
            if (Pending().Any(p => p.Provider == provider && p.LocalPath.Equals(Path.GetFullPath(localPath), StringComparison.OrdinalIgnoreCase)))
                throw new IOException("같은 파일의 업로드 세션이 이미 있습니다. 먼저 해당 복구 기록을 확인하세요.");
            if (Pending().Count >= MaximumPending)
                throw new IOException("업로드 복구 기록은 최대 20개입니다.");
            var p = new Checkpoint(Guid.NewGuid().ToString("N"), provider, driveId, parentId, name,
                Path.GetFullPath(localPath), length, sha256, lastWriteUtcTicks, account,
                session.AbsoluteUri, expires, DateTime.UtcNow);
            Directory.CreateDirectory(Root);
            if (IsLink(Root)) throw new IOException("업로드 기록 폴더가 연결 경로로 바뀌었습니다.");
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(p);
            byte[] cipher;
            try { cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            string path = PathFor(p.Id), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) f.Write(cipher);
                File.Move(temp, path, overwrite: false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(cipher);
                if (File.Exists(temp)) File.Delete(temp);
            }
            return p;
        }
    }

    public static void Complete(Checkpoint plan)
    {
        if (plan is null || !ValidId(plan.Id)) throw new ArgumentException("업로드 식별자가 유효하지 않습니다.");
        lock (Gate) { File.Delete(PathFor(plan.Id)); }
    }

    /// <summary>Called only on explicit account disconnect, not on ordinary application exit.</summary>
    public static void DiscardAllForProvider(CloudProvider provider)
    {
        foreach (var plan in Pending().Where(p => p.Provider == provider)) Discard(plan);
    }

    /// <summary>Explicit user cleanup only; never deletes any local/remote source or destination.</summary>
    public static void Discard(Checkpoint plan)
    {
        if (plan is null || !Pending().Any(p => p == plan))
            throw new IOException("유효하지 않은 업로드 복구 기록은 정리하지 않습니다.");
        Complete(plan);
    }
}
