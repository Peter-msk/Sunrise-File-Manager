using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Sunrise.FileManager.Services;

public static class WindowsCloudFilesService
{
    public static IReadOnlyList<ShellLocation> Accounts()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } path && Directory.Exists(path)) paths.Add(path);
        using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
        if (accounts is not null)
            foreach (string name in accounts.GetSubKeyNames())
            {
                using var account = accounts.OpenSubKey(name);
                if (account?.GetValue("UserFolder") is string path && Directory.Exists(path)) paths.Add(path);
            }
        return paths.Select(p => new ShellLocation("OneDrive · " + Path.GetFileName(p), p, true)).ToArray();
    }
    public static Task SetOfflineAsync(string path, bool keepOffline, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        Marshal.ThrowExceptionForHR(CfOpenFileWithOplock(path, 0x1 | 0x2, out IntPtr handle));
        try
        {
            Marshal.ThrowExceptionForHR(CfSetPinState(handle, keepOffline ? 1 : 2, 0, IntPtr.Zero));
            token.ThrowIfCancellationRequested();
            if (keepOffline) Hydrate(handle, token);
            // Unpin expresses user intent; the registered provider owns dehydration timing.
            // Do not force data removal through an obsolete/unsupported dehydration entry point.
            token.ThrowIfCancellationRequested();
        }
        finally { CfCloseHandle(handle); }
    }, token);
    private static void Hydrate(IntPtr protectedHandle, CancellationToken token)
    {
        using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
        IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf<OverlappedData>());
        try
        {
            Marshal.StructureToPtr(new OverlappedData { Event = completed.SafeWaitHandle.DangerousGetHandle() }, data, false);
            IntPtr file = CfGetWin32HandleFromProtectedHandle(protectedHandle);
            token.ThrowIfCancellationRequested();
            int result = CfHydratePlaceholder(protectedHandle, 0, -1, 0, data);
            if (result == unchecked((int)0x800703E5)) // HRESULT_FROM_WIN32(ERROR_IO_PENDING)
            {
                // Do not release OVERLAPPED/event memory until native I/O has terminated.
                using var cancel = token.Register(() => CancelIoEx(file, data));
                if (!GetOverlappedResult(file, data, out _, true))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (token.IsCancellationRequested && error == 995) throw new OperationCanceledException(token);
                    throw new Win32Exception(error);
                }
            }
            else Marshal.ThrowExceptionForHR(result);
            token.ThrowIfCancellationRequested();
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct OverlappedData
    { public IntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }
    [DllImport("cldapi.dll")] private static extern IntPtr CfGetWin32HandleFromProtectedHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(IntPtr file, IntPtr overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("cldapi.dll")] private static extern int CfSetPinState(IntPtr file, int state, uint flags, IntPtr overlapped);
    [DllImport("cldapi.dll")] private static extern int CfHydratePlaceholder(IntPtr file, long start, long length, uint flags, IntPtr overlapped);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] private static extern int CfOpenFileWithOplock(string path, uint flags, out IntPtr handle);
    [DllImport("cldapi.dll")] private static extern void CfCloseHandle(IntPtr handle);
}
