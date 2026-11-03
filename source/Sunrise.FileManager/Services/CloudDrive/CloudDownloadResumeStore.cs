using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>
/// R27: explicit-repeat, per-Windows-user cloud download checkpoint. A record stores only
/// identity/version/size and a local part path, NEVER access tokens or preauthorized URLs.
/// Partial file bytes stay in the authorized destination folder and are not encrypted.
/// The user must explicitly retry the same download; no transfer runs on application launch.
/// </summary>
public static class CloudDownloadResumeStore
{
    private const int MaximumPending = 20;
    private const int MaximumRecordBytes = 32 * 1024;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Sunrise.FileManager.R27.CloudDownload.V1");
    private static readonly object Gate = new();
    private static string Root => Sunrise.FileManager.Services.SunriseGroupPaths.Local("cloud-download-resume");

    public sealed record Checkpoint(string Id, CloudProvider Provider, string? DriveId, string RemoteId,
        string Destination, string Part, long Length, string Revision, DateTime CreatedUtc,
        string? AccountFingerprint = null);

    private static string RecordPath(string id) => Path.Combine(Root, "download-" + id + ".sfrdownload");
    private static bool ValidId(string id) => id.Length == 32 && id.All(Uri.IsHexDigit);
    private static bool ValidFingerprint(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool Expired(Checkpoint plan) => plan.CreatedUtc > DateTime.UtcNow.AddMinutes(5) ||
        DateTime.UtcNow - plan.CreatedUtc > MaximumAge;

    private static Checkpoint? Read(string path)
    {
        byte[]? plain = null;
        try
        {
            if (IsLink(path) || new FileInfo(path).Length > MaximumRecordBytes) return null;
            plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var plan = JsonSerializer.Deserialize<Checkpoint>(plain);
            if (plan is null || !ValidId(plan.Id) ||
                !Path.GetFileName(path).Equals("download-" + plan.Id + ".sfrdownload", StringComparison.OrdinalIgnoreCase) ||
                !Enum.IsDefined(plan.Provider) || string.IsNullOrWhiteSpace(plan.RemoteId) ||
                plan.RemoteId.Length > 2048 || plan.Length < 0 || string.IsNullOrWhiteSpace(plan.Revision) ||
                plan.Revision.Length > 512 ||
                // Pre-R40 journals have no account fingerprint. Read them for explicit cleanup,
                // but GetOrCreate must NEVER resume them or silently discard their partial bytes.
                (plan.AccountFingerprint is not null && !ValidFingerprint(plan.AccountFingerprint)) ||
                !Path.IsPathFullyQualified(plan.Destination) ||
                !Path.IsPathFullyQualified(plan.Part) ||
                !plan.Part.Equals(Path.Combine(Path.GetDirectoryName(plan.Destination)!,
                    ".sfm-cloud-resume-" + plan.Id + ".part"), StringComparison.OrdinalIgnoreCase)) return null;
            return plan;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            CryptographicException or ArgumentException or NotSupportedException) { return null; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public static IReadOnlyList<Checkpoint> Pending()
    {
        if (!Directory.Exists(Root)) return [];
        if (IsLink(Root)) throw new IOException("다운로드 복구 기록 폴더가 연결 경로로 바뀌었습니다.");
        return Directory.GetFiles(Root, "download-*.sfrdownload").Take(MaximumPending + 1)
            .Select(Read).OfType<Checkpoint>().ToArray();
    }

    public static void Discard(Checkpoint plan)
    {
        if (plan is null || !ValidId(plan.Id) || !Pending().Any(p => p == plan))
            throw new IOException("검증되지 않은 다운로드 복구 기록은 삭제할 수 없습니다.");
        if (File.Exists(plan.Part))
        {
            if (IsLink(plan.Part)) throw new IOException("임시파일이 연결 경로로 변경돼 삭제하지 않습니다.");
            File.Delete(plan.Part);
        }
        File.Delete(RecordPath(plan.Id));
    }

    private static void Save(Checkpoint plan)
    {
        Directory.CreateDirectory(Root);
        if (IsLink(Root)) throw new IOException("다운로드 기록 폴더가 연결 경로로 바뀌었습니다.");
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(plan);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string temporary = RecordPath(plan.Id) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Write(encrypted);
            File.Move(temporary, RecordPath(plan.Id), overwrite: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Reject a changed cloud version, account target, expired record, collision or symlink.</summary>
    public static Checkpoint GetOrCreate(CloudProvider provider, string? driveId, string remoteId,
        string destination, long length, string revision, string accountFingerprint)
    {
        destination = Path.GetFullPath(destination);
        if (length < 0 || string.IsNullOrWhiteSpace(revision) || revision.Length > 512 ||
            string.IsNullOrWhiteSpace(remoteId) || remoteId.Length > 2048 ||
            !ValidFingerprint(accountFingerprint))
            throw new IOException("원격 파일 버전·크기 식별정보가 없어 이어받기를 허용하지 않습니다.");
        var folder = Path.GetDirectoryName(destination) ?? throw new IOException("대상 폴더를 확인할 수 없습니다.");
        if (!Directory.Exists(folder) || IsLink(folder) || File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("대상 폴더·파일 충돌·연결 경로 조건이 올바르지 않습니다.");
        lock (Gate)
        {
            var matching = Pending().Where(p => p.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length > 1) throw new IOException("대상 파일의 복구 기록이 중복돼 자동 선택하지 않습니다.");
            if (matching.Length == 1)
            {
                var plan = matching[0];
                if (Expired(plan) || plan.Provider != provider ||
                    !string.Equals(plan.DriveId, driveId, StringComparison.Ordinal) ||
                    !string.Equals(plan.RemoteId, remoteId, StringComparison.Ordinal) ||
                    plan.Length != length || !string.Equals(plan.Revision, revision, StringComparison.Ordinal) ||
                    !string.Equals(plan.AccountFingerprint, accountFingerprint, StringComparison.Ordinal))
                    throw new IOException("클라우드 원본 버전·로그인 계정 또는 복구 기록 유효기간이 변경됐습니다. " +
                        "기존 기록의 계정 정보가 없는 경우에도 임시파일을 보존하고 재개하지 않습니다. 사용자 확인 후 정리하세요.");
                if (File.Exists(plan.Part) && IsLink(plan.Part))
                    throw new IOException("임시파일이 연결 경로이므로 재개하지 않습니다.");
                return plan;
            }
            if (Directory.Exists(Root) && Directory.GetFiles(Root, "download-*.sfrdownload").Length >= MaximumPending)
                throw new IOException("클라우드 다운로드 복구 기록이 20개여서 추가 생성하지 않습니다.");
            string id = Guid.NewGuid().ToString("N");
            var created = new Checkpoint(id, provider, driveId, remoteId, destination,
                Path.Combine(folder, ".sfm-cloud-resume-" + id + ".part"), length, revision, DateTime.UtcNow,
                accountFingerprint);
            Save(created);
            return created;
        }
    }

    public static void Complete(Checkpoint plan)
    {
        // Never delete user data. The completed download has already been atomically published.
        try { File.Delete(RecordPath(plan.Id)); }
        catch (IOException) { System.Diagnostics.Trace.TraceWarning("R27 download resume checkpoint cleanup pending."); }
        catch (UnauthorizedAccessException) { System.Diagnostics.Trace.TraceWarning("R27 download resume checkpoint cleanup denied."); }
    }
}
