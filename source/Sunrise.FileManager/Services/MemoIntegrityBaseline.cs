using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunrise.FileManager.Services;

/// <summary>
/// PC-local comparison anchor for SHARED .sunmemo files. The source notes remain readable and
/// portable. This is not author authentication, a digital signature, or protection against an
/// attacker controlling the same Windows user account. Only hashes/author metadata are stored.
/// No business-document content or memo text is copied to AppData by this service.
/// </summary>
internal static class MemoIntegrityBaseline
{
    internal sealed record Verdict(bool Changed, string? Warning);
    private sealed record Anchor(string MemoFileName, string Author, string Sha256, DateTime FirstSeenUtc, DateTime AcceptedUtc);
    private const int MaxMemoBytes = 1_100_000;
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunrise", "FileManager", "memo-integrity");

    internal static Verdict Inspect(string memoPath, string author)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "Windows 이외 환경에서는 로컬 변경 기준을 검증하지 않습니다.");
        try
        {
            string location = AnchorPath(memoPath);
            string sha = Digest(memoPath);
            if (!File.Exists(location))
            {
                // TOFU: first observation becomes this PC's baseline; cannot attest authorship before it.
                WriteAnchor(location, new Anchor(Path.GetFileName(memoPath), author, sha, DateTime.UtcNow, DateTime.UtcNow));
                return new(false, null);
            }
            var known = JsonSerializer.Deserialize<Anchor>(Unprotect(File.ReadAllBytes(location)))
                ?? throw new InvalidDataException("변경 기준이 비어 있습니다.");
            if (known.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase) &&
                known.Author.Equals(author, StringComparison.Ordinal) &&
                known.MemoFileName.Equals(Path.GetFileName(memoPath), StringComparison.Ordinal))
                return new(false, null);
            string authorInfo = known.Author == author ? "" : $" 작성자: '{known.Author}' → '{author}'.";
            return new(true, "이 PC에서 이전에 확인한 메모와 내용 또는 기록정보가 다릅니다." + authorInfo + " 정상적인 공동 편집일 수도 있습니다. 원본을 표시하되 별도 확인 후 저장하세요.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException or JsonException or Win32Exception)
        {
            // Fail visibly, but never hide an otherwise readable shared memo.
            return new(false, "로컬 변경 기준을 읽거나 기록하지 못해 동일성을 판정할 수 없습니다: " + e.Message);
        }
    }

    internal static string? TrustAfterOwnSave(string memoPath, string author)
    {
        if (!OperatingSystem.IsWindows()) return "Windows 외 환경에서는 무결성 기준을 저장하지 않습니다.";
        try
        {
            string target = AnchorPath(memoPath);
            DateTime firstSeen = DateTime.UtcNow;
            if (File.Exists(target))
            {
                try
                {
                    var old = JsonSerializer.Deserialize<Anchor>(Unprotect(File.ReadAllBytes(target)));
                    if (old is not null) firstSeen = old.FirstSeenUtc;
                }
                catch (Exception ex) when (ex is IOException or CryptographicException or Win32Exception or JsonException)
                {
                    // An unreadable anchor must not be silently overwritten: it could be tampered with.
                    return "기존 로컬 변경 기준을 읽지 못해 새 기준으로 교체하지 않았습니다: " + ex.Message;
                }
            }
            WriteAnchor(target, new Anchor(Path.GetFileName(memoPath), author, Digest(memoPath), firstSeen, DateTime.UtcNow));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException or Win32Exception)
        { return "메모는 저장되었으나 로컬 변경 기준 저장에 실패했습니다: " + ex.Message; }
    }

    internal static string? AcceptCurrentAsReference(string memoPath, string author)
    {
        // Explicit user action. A new reference is not proof that the external memo is genuine.
        if (!OperatingSystem.IsWindows()) return "Windows에서만 변경 기준을 승인할 수 있습니다.";
        try
        {
            WriteAnchor(AnchorPath(memoPath), new Anchor(Path.GetFileName(memoPath), author, Digest(memoPath), DateTime.UtcNow, DateTime.UtcNow));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException or Win32Exception)
        { return ex.Message; }
    }

    private static string AnchorPath(string memoPath)
    {
        string full = Path.GetFullPath(memoPath).ToUpperInvariant();
        return Path.Combine(Root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full))) + ".bin");
    }

    private static string Digest(string memoPath)
    {
        using var stream = new FileStream(memoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > MaxMemoBytes) throw new InvalidDataException("메모 크기가 허용 범위를 초과했습니다.");
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteAnchor(string destination, Anchor anchor)
    {
        byte[] bytes = Protect(JsonSerializer.SerializeToUtf8Bytes(anchor));
        Directory.CreateDirectory(Root);
        string staging = Path.Combine(Root, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { fs.Write(bytes); fs.Flush(flushToDisk: true); }
            File.Move(staging, destination, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr pointer);

    internal static byte[] Protect(byte[] data) => WithBlob(data, (ref DataBlob input, out DataBlob output) =>
        CryptProtectData(ref input, "Sunrise File Manager memo integrity", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, out output));
    internal static byte[] Unprotect(byte[] data) => WithBlob(data, (ref DataBlob input, out DataBlob output) =>
        CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, out output));
    private delegate bool CryptAction(ref DataBlob input, out DataBlob output);
    private static byte[] WithBlob(byte[] data, CryptAction action)
    {
        IntPtr allocated = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, allocated, data.Length);
            var input = new DataBlob { Size = data.Length, Data = allocated };
            if (!action(ref input, out DataBlob output)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (output.Size < 0 || output.Size > 10_000_000) throw new CryptographicException("보호 데이터 길이가 잘못되었습니다.");
                var bytes = new byte[output.Size];
                Marshal.Copy(output.Data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(allocated); }
    }
}
