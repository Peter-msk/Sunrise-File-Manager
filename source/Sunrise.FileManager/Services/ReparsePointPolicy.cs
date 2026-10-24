using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sunrise.FileManager.Services;

/// <summary>Cloud-filter placeholders are ordinary provider-owned paths; name-surrogate links stay blocked.</summary>
public static class ReparsePointPolicy
{
    public static bool IsCloudTag(uint tag) => (tag & 0xFFFF0FFFu) == 0x9000001Au;
    public static bool IsBlocked(string path) => IsBlocked(path, File.GetAttributes(path));
    public static bool IsBlocked(string path, FileAttributes attributes)
    {
        if (!attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        if (!OperatingSystem.IsWindows()) return true;
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var info, 8)) return true;
        return !IsCloudTag(info.Tag);
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTagInfo { public uint Attributes, Tag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int type, out AttributeTagInfo info, uint size);
}
