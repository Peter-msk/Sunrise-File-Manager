using System.IO;
using System.Runtime.InteropServices;

namespace Sunrise.FileManager.Services;

/// <summary>IFileOperation's Windows 8+ recycle-only contract. No permanent-delete fallback.</summary>
internal static class WindowsRecycleService
{
    private const uint RecycleOnly = 0x00080000; // FOFX_RECYCLEONDELETE
    private const uint EarlyFailure = 0x00100000; // FOFX_EARLYFAILURE
    private const uint NoErrorUi = 0x00000400;
    private const uint Silent = 0x00000004;
    private const uint NoConfirmation = 0x00000010;
    public static void Recycle(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows 휴지통만 지원합니다.");
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) { RecycleOnSta(path); return; }
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { RecycleOnSta(path); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true, Name = "Sunrise Recycle Only" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw new IOException("휴지통 이동에 실패했습니다. 영구 삭제로 전환하지 않습니다.", error);
    }
    private static void RecycleOnSta(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        IFileOperation? operation = null; IShellItem? item = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!;
            operation = (IFileOperation)Activator.CreateInstance(type)!;
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(RecycleOnly | EarlyFailure | NoErrorUi | Silent | NoConfirmation));
            Guid iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out bool aborted));
            if (aborted || File.Exists(path) || Directory.Exists(path)) throw new IOException("Windows 휴지통 작업이 완료되지 않았습니다.");
        }
        catch (COMException ex) { throw new IOException("Windows 휴지통 이동에 실패했습니다. 영구 삭제는 시도하지 않습니다.", ex); }
        finally
        {
            if (item is not null) Marshal.FinalReleaseComObject(item);
            if (operation is not null) Marshal.FinalReleaseComObject(operation);
        }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }
    // Preserve the complete native vtable order; unused entries are intentionally not callable by the UI.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int MoveItems(IntPtr items, IShellItem destination);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int CopyItems(IntPtr items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, IntPtr sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
