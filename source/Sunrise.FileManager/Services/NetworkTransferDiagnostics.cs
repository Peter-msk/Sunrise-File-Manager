using System.IO;

namespace Sunrise.FileManager.Services;

/// <summary>
/// R21 read-only diagnostics for UNC, mapped SMB, DFS and document-centralization disks.
/// Never probes protected content, elevates privileges, bypasses a security filter or
/// changes policy. An access-denied result cannot identify a particular security product.
/// </summary>
public static class NetworkTransferDiagnostics
{
    public static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            string? root = Path.GetPathRoot(full);
            return !string.IsNullOrWhiteSpace(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            // Unknown drive semantics must not authorize an unverified same-root rename.
            // This selects a more conservative transfer path, not additional permissions.
            return true;
        }
    }

    /// <summary>
    /// Distinct network roots cannot be presumed rename-compatible. Same-root shares
    /// retain fast rename when permitted; ERROR_NOT_SAME_DEVICE(17) alone may trigger
    /// a verified copy/move fallback. Access-denied never does.
    /// </summary>
    public static bool RequiresVerifiedMove(string source, string destination) =>
        (IsNetworkPath(source) || IsNetworkPath(destination)) &&
        !string.Equals(Path.GetPathRoot(Path.GetFullPath(source)),
            Path.GetPathRoot(Path.GetFullPath(destination)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Only retry a copy when no item has been published and a transport error is transient.</summary>
    public static bool IsTransientNetworkFailure(Exception ex, string source, string destination)
    {
        if (!IsNetworkPath(source) && !IsNetworkPath(destination)) return false;
        // R44: a transient outer SMB error can wrap an actual permission or sharing
        // denial from a filter/remote file handle. Never retry on a protected inner
        // failure; never retry an unknown or cyclic/deep exception chain.
        if (ex is UnauthorizedAccessException) return false;
        if (ContainsProtectedFailure(ex)) return false;
        int code = Win32Code(ex);
        return code is 53 or 64 or 67 or 121 or 1231;
    }

    private static bool ContainsProtectedFailure(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        int inspected = 0;
        while (pending.Count > 0)
        {
            // Fail closed on malicious, cyclic or unusually deep exception trees.
            if (++inspected > 32) return true;
            Exception current = pending.Pop();
            if (current is UnauthorizedAccessException || Win32Code(current) is 5 or 65 or 32 or 33)
                return true;
            if (current is AggregateException aggregate)
                foreach (Exception cause in aggregate.InnerExceptions) pending.Push(cause);
            else if (current.InnerException is not null)
                pending.Push(current.InnerException);
        }
        return false;
    }

    public static int Win32Code(Exception exception)
    {
        int code = exception.HResult;
        return (code & unchecked((int)0xffff0000)) == unchecked((int)0x80070000)
            ? code & 0xffff : 0;
    }

    public static string Explain(Exception exception, string? source, string? destination)
    {
        if (!IsNetworkPath(source) && !IsNetworkPath(destination))
        {
            // SecureDisk may expose a virtual disk as a fixed/local drive, not DriveType.Network.
            // An access-denied code remains ambiguous without the organization's product logs.
            return Win32Code(exception) is 5 or 65 || exception is UnauthorizedAccessException
                ? "파일 접근 거부 · Windows 사용자 권한 또는 회사 보안제품의 프로세스/파일 정책 가능성. 보안 통제를 우회하지 않습니다."
                : "";
        }
        return Win32Code(exception) switch
        {
            5 or 65 => "네트워크 접근 거부 · Windows 공유/NTFS 권한 또는 SecureDisk 등 조직 보안정책 가능성. 프로그램에서 우회하지 않습니다. 관리자에게 권한·허용 프로그램 정책을 확인하세요.",
            32 or 33 => "네트워크 파일 사용 중/잠금 · 파일을 사용하는 프로그램과 서버 잠금 상태를 확인하세요.",
            17 => "네트워크 공유의 실제 볼륨이 다릅니다 · 같은 드라이브 문자라도 이동이 지원되지 않을 수 있습니다. 승인된 별도 복사 후 원본 정리를 검토하세요.",
            53 or 64 or 67 or 121 or 1231 => "네트워크 연결/공유 경로 문제 · 서버·VPN·DFS·매핑 상태를 확인하세요.",
            80 or 183 => "대상 파일 또는 폴더 이름 충돌 · 기존 파일을 덮어쓰지 않았습니다.",
            _ when exception is UnauthorizedAccessException =>
                "네트워크 접근 거부 · 파일 권한 또는 회사 보안정책 가능성. SecureDisk 차단 여부는 관리자 로그로만 확인할 수 있습니다.",
            _ => "네트워크 전송 실패 · UNC/매핑 경로, 사용자 권한, 네트워크 연결 및 회사 보안제품 로그를 확인하세요."
        };
    }
}
