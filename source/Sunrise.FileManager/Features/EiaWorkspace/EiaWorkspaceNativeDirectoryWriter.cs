// Windows-only production primitive for NEW directories in an explicitly approved EIA workspace.
// Reuses the tested handle-relative one-component NT creation algorithm. No file
// moves, deletion, replacement, parent auto-create or path-based fallback.
// Local NTFS/ReFS only until separate SMB/NAS Windows acceptance is completed.
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sunrise.FileManager.Features.EiaWorkspace;

internal sealed class EiaWorkspaceNativeDirectoryWriter : IDisposable
{
    private const uint ReadAttributes = 0x80;
    private const uint ListDirectory = 0x1;
    private const uint Traverse = 0x20;
    private const uint AddSubdirectory = 0x4;
    private const uint Synchronize = 0x00100000;
    private const uint Access = ReadAttributes | ListDirectory | Traverse | AddSubdirectory | Synchronize;
    // Do NOT share delete. This helps pin a parent against rename while the handle lives.
    private const uint ShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileOpen = 1;
    private const uint DriveFixed = 3;
    private const uint FileNameNormalized = 0;
    private const uint VolumeNameDos = 0;
    private const uint FileCreate = 2;
    private const uint ObjCaseInsensitive = 0x40;
    private readonly SafeFileHandle _base;

    private EiaWorkspaceNativeDirectoryWriter(SafeFileHandle pinnedBase)
    {
        _base = pinnedBase;
    }

    /// <summary>Pin the already existing, verified destination base directory.</summary>
    internal static EiaWorkspaceNativeDirectoryWriter OpenPinnedBase(string baseDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        if (!Directory.Exists(path) || path.Equals(Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("기존 기준 폴더를 선택해야 합니다.");
        // Until native SMB/ACL acceptance, never silently fall back to a path-based writer.
        RequireSupportedLocalVolume(path);
        SafeFileHandle handle = CreateFileW(path, Access, ShareReadWrite, IntPtr.Zero,
            OpenExisting, FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException("기준 폴더 핸들 열기 실패: " + Marshal.GetLastWin32Error());
        }
        try
        {
            RequireRealDirectory(handle);
            RequirePinnedPath(handle, path);
            return new EiaWorkspaceNativeDirectoryWriter(handle);
        }
        catch { handle.Dispose(); throw; }
    }

    // Each path component is resolved relative to a PERSISTENT open parent handle.
    // No full-path Directory.CreateDirectory or implicit intermediate parent creation.
    // FILE_CREATE fails closed on name collisions; FILE_OPEN never creates.
    internal void CreateOrVerify(EiaFolderCreationItem item, string expectedRootName)
    {
        if (string.IsNullOrWhiteSpace(expectedRootName) ||
            expectedRootName.Contains('/') || expectedRootName.Contains('\\'))
            throw new InvalidDataException("사업 폴더 이름이 잘못됐습니다.");
        string[] parts;
        if (string.Equals(item.RelativePath, expectedRootName, StringComparison.OrdinalIgnoreCase))
            parts = [expectedRootName];
        else
        {
            string relative = EiaWorkspacePreviewPlanner.ValidateRelativePath(item.RelativePath);
            parts = [expectedRootName, .. relative.Split('/')];
        }
        // IMPORTANT: reject a mismatching plan BEFORE FILE_CREATE. A post-create
        // assertion would be too late even in a disposable test sandbox.
        string basePinnedPath = GetFinalPath(_base);
        string expectedTarget = parts.Aggregate(basePinnedPath, Path.Combine);
        if (!Path.GetFullPath(item.AbsolutePath).Equals(
                Path.GetFullPath(expectedTarget), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("생성 대상이 고정된 운영 경로와 다릅니다.");
        var opened = new List<SafeFileHandle>();
        try
        {
            SafeFileHandle parent = _base;
            // Each segment is opened relative to the pinned user-selected base directory.
            string pinnedPath = basePinnedPath;
            // Never use the claimed item's path to choose which parent to open.
            for (int i = 0; i < parts.Length; i++)
            {
                string segment = EiaWorkspacePreviewPlanner.ValidateSegment(parts[i], "폴더 경로", 220);
                bool target = i == parts.Length - 1;
                bool create = target && item.BeforeState == "MISSING";
                if (target && !create && item.BeforeState != "EXISTS_DIRECTORY")
                    throw new InvalidDataException("계획에 없는 디렉터리 상태입니다.");
                SafeFileHandle child = OpenRelative(parent, segment,
                    create ? FileCreate : FileOpen);
                try
                {
                    pinnedPath = Path.Combine(pinnedPath, segment);
                    RequirePinnedPath(child, pinnedPath);
                    if (target && !Path.GetFullPath(item.AbsolutePath).Equals(
                            Path.GetFullPath(pinnedPath), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("생성된 폴더의 실제 경로와 승인 계획이 다릅니다.");
                    opened.Add(child);
                    parent = child;
                }
                catch { child.Dispose(); throw; }
            }
        }
        finally
        {
            for (int i = opened.Count - 1; i >= 0; i--) opened[i].Dispose();
        }
    }

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name,
        uint disposition)
    {
        // Using ONE component per call is essential: the kernel never resolves
        // arbitrary slashes or traversals under this untrusted root.
        if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." ||
            name.Contains('/') || name.Contains('\\') || name.Contains(':'))
            throw new InvalidDataException("단일 디렉터리 구성요소만 허용합니다.");
        IntPtr utf16 = IntPtr.Zero;
        IntPtr unicodePtr = IntPtr.Zero;
        SafeFileHandle? result = null;
        try
        {
            utf16 = Marshal.StringToHGlobalUni(name);
            var unicode = new UNICODE_STRING
            {
                Length = checked((ushort)(name.Length * sizeof(char))),
                MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                Buffer = utf16
            };
            unicodePtr = Marshal.AllocHGlobal(Marshal.SizeOf<UNICODE_STRING>());
            Marshal.StructureToPtr(unicode, unicodePtr, false);
            var attrs = new OBJECT_ATTRIBUTES
            {
                Length = Marshal.SizeOf<OBJECT_ATTRIBUTES>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodePtr,
                Attributes = ObjCaseInsensitive
            };
            int status = NtCreateFile(out result, Access, ref attrs, out _, IntPtr.Zero,
                (uint)FileAttributes.Directory, ShareReadWrite, disposition,
                FileDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                IntPtr.Zero, 0);
            if (status < 0 || result is null || result.IsInvalid)
            {
                result?.Dispose();
                throw new IOException("디렉터리 핸들 상대 생성/열기 실패 NTSTATUS=0x" +
                    unchecked((uint)status).ToString("X8"));
            }
            RequireRealDirectory(result);
            return result;
        }
        catch { result?.Dispose(); throw; }
        finally
        {
            if (unicodePtr != IntPtr.Zero) Marshal.FreeHGlobal(unicodePtr);
            if (utf16 != IntPtr.Zero) Marshal.FreeHGlobal(utf16);
        }
    }

    private static void RequireRealDirectory(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9 /*FileAttributeTagInfo*/,
                out FILE_ATTRIBUTE_TAG_INFO info,
                (uint)Marshal.SizeOf<FILE_ATTRIBUTE_TAG_INFO>()))
            throw new IOException("열린 핸들의 파일 속성을 확인할 수 없습니다: " +
                Marshal.GetLastWin32Error());
        if ((info.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
            (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            info.ReparseTag != 0)
            throw new InvalidDataException("정션·심볼릭 링크·일반 파일은 사용하지 않습니다.");
    }

    // Fail closed if the Windows handle actually points to a different directory.
    // Never accept a path verification error as a reason for path-based fallback.
    private static void RequirePinnedPath(SafeFileHandle handle, string expected)
    {
        string actual = GetFinalPath(handle);
        if (!Path.GetFullPath(actual).Equals(Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("열린 디렉터리 핸들 경로가 검토 계획과 다릅니다.");
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        const int MaxCharacters = 32767;
        var text = new System.Text.StringBuilder(512);
        uint needed = GetFinalPathNameByHandleW(handle, text, (uint)text.Capacity,
            FileNameNormalized | VolumeNameDos);
        if (needed == 0 || needed >= MaxCharacters)
            throw new IOException("디렉터리 핸들 최종 경로 확인 실패: " + Marshal.GetLastWin32Error());
        if (needed >= text.Capacity)
        {
            text.EnsureCapacity(checked((int)needed + 1));
            uint filled = GetFinalPathNameByHandleW(handle, text, (uint)text.Capacity,
                FileNameNormalized | VolumeNameDos);
            if (filled == 0 || filled >= text.Capacity)
                throw new IOException("디렉터리 핸들 최종 경로 재조회 실패: " + Marshal.GetLastWin32Error());
        }
        string resolved = text.ToString();
        if (!resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("운영 생성의 네트워크/불명확한 핸들 경로는 허용하지 않습니다.");
        return Path.TrimEndingDirectorySeparator(resolved[4..]);
    }

    private static void RequireSupportedLocalVolume(string temp)
    {
        string volumeRoot = Path.GetPathRoot(temp) ??
            throw new InvalidDataException("볼륨 식별 실패");
        if (volumeRoot.StartsWith(@"\\", StringComparison.Ordinal) ||
            GetDriveTypeW(volumeRoot) != DriveFixed)
            throw new InvalidDataException("운영 생성은 검증된 로컬 고정 드라이브만 지원합니다. NAS/SMB는 검증 대기입니다.");
        var filesystem = new System.Text.StringBuilder(64);
        if (!GetVolumeInformationW(volumeRoot, null, 0, out _, out _, out _,
                filesystem, (uint)filesystem.Capacity))
            throw new IOException("로컬 파일시스템 유형 확인 실패: " + Marshal.GetLastWin32Error());
        if (!filesystem.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase) &&
            !filesystem.ToString().Equals("ReFS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("로컬 NTFS/ReFS 외 파일시스템은 운영 생성 검증 대기입니다.");
    }

    public void Dispose() => _base.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_STATUS_BLOCK
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string filename, uint access,
        uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,
        int infoClass, out FILE_ATTRIBUTE_TAG_INFO information, uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle,
        System.Text.StringBuilder path, uint pathLength, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string root,
        System.Text.StringBuilder? volumeName, uint volumeNameLength,
        out uint serial, out uint maxComponentLength, out uint flags,
        System.Text.StringBuilder filesystem, uint filesystemLength);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access,
        ref OBJECT_ATTRIBUTES attributes, out IO_STATUS_BLOCK ioStatus,
        IntPtr allocationSize, uint fileAttributes, uint shareAccess,
        uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);
}
