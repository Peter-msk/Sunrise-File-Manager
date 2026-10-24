using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Sunrise.FileManager.Services;

/// <summary>Hosts the registered Windows legacy shell menu, including IContextMenu2/3 message forwarding.</summary>
public static class WindowsShellContextMenu
{
    public static void Show(Window owner, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || paths.Count > 1000) throw new IOException("1~1,000개 항목을 선택하세요.");
        string? parentPath = Path.GetDirectoryName(paths[0]);
        if (paths.Any(p => !string.Equals(Path.GetDirectoryName(p), parentPath, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Windows 메뉴는 같은 폴더의 항목을 선택한 경우에 지원합니다.");
        var pidls = new List<IntPtr>();
        try
        {
            foreach (string path in paths)
            {
                Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _));
                pidls.Add(pidl);
            }
            ShowItems(owner, pidls);
        }
        finally { foreach (IntPtr pidl in pidls) Marshal.FreeCoTaskMem(pidl); }
    }

    // PIDLs are borrowed for this synchronous call, never reconstructed from physical recycle paths.
    internal static void ShowItems(Window owner, IReadOnlyList<IntPtr> pidls)
    {
        IShellFolder? folder = null;
        object? context = null;
        IntPtr menu = IntPtr.Zero;
        HwndSource? source = null;
        HwndSourceHook? hook = null;
        IntPtr hwnd = new WindowInteropHelper(owner).Handle;
        try
        {
            Guid folderId = typeof(IShellFolder).GUID;
            Marshal.ThrowExceptionForHR(SHBindToParent(pidls[0], ref folderId, out folder, out _));
            IntPtr[] children = pidls.Select(ILFindLastID).ToArray();
            Guid contextId = typeof(IContextMenu).GUID;
            Marshal.ThrowExceptionForHR(folder.GetUIObjectOf(hwnd, (uint)children.Length, children, ref contextId, IntPtr.Zero, out context));
            var contextMenu = (IContextMenu)context;
            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            Marshal.ThrowExceptionForHR(contextMenu.QueryContextMenu(menu, 0, 1, 0x7FFF, 0x100)); // CMF_EXTENDEDVERBS
            var advanced = context as IContextMenu3;
            var legacy = context as IContextMenu2;
            hook = (IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg is 0x117 or 0x2B or 0x2C or 0x120)
                {
                    if (advanced is not null && advanced.HandleMenuMsg2((uint)msg, w, l, out IntPtr result) == 0)
                    { handled = true; return result; }
                    if (legacy is not null && legacy.HandleMenuMsg((uint)msg, w, l) == 0) handled = true;
                }
                return IntPtr.Zero;
            };
            source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(hook);
            GetCursorPos(out POINT point);
            uint selected = TrackPopupMenuEx(menu, 0x100 | 0x2, point.X, point.Y, hwnd, IntPtr.Zero);
            if (selected != 0)
            {
                var command = new CommandInfo { Size = Marshal.SizeOf<CommandInfo>(), Window = hwnd,
                    Verb = new IntPtr(selected - 1), Show = 1 };
                Marshal.ThrowExceptionForHR(contextMenu.InvokeCommand(ref command));
            }
        }
        finally
        {
            if (hook is not null) source?.RemoveHook(hook);
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (context is not null) Marshal.ReleaseComObject(context);
            if (folder is not null) Marshal.ReleaseComObject(folder);
        }
    }

    internal static bool InvokeItems(IntPtr hwnd, IReadOnlyList<IntPtr> pidls, string verb)
    {
        if (pidls.Count == 0) return false;
        IShellFolder? folder = null; object? context = null;
        IntPtr menu = IntPtr.Zero, verbPtr = IntPtr.Zero;
        try
        {
            Guid folderId = typeof(IShellFolder).GUID;
            Marshal.ThrowExceptionForHR(SHBindToParent(pidls[0], ref folderId, out folder, out _));
            Guid contextId = typeof(IContextMenu).GUID;
            Marshal.ThrowExceptionForHR(folder.GetUIObjectOf(hwnd, (uint)pidls.Count,
                pidls.Select(ILFindLastID).ToArray(), ref contextId, IntPtr.Zero, out context));
            var commands = (IContextMenu)context;
            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            Marshal.ThrowExceptionForHR(commands.QueryContextMenu(menu, 0, 1, 0x7FFF, 0));
            verbPtr = Marshal.StringToCoTaskMemAnsi(verb);
            var info = new CommandInfo { Size = Marshal.SizeOf<CommandInfo>(), Window = hwnd, Verb = verbPtr, Show = 1 };
            // One invocation owns the complete selection. Windows owns its aggregate confirmation,
            // conflict/error UI and operation. Never invoke a delete verb in an item loop.
            int hr = commands.InvokeCommand(ref info);
            if (hr is unchecked((int)0x800704C7) or unchecked((int)0x80004004)) return false;
            Marshal.ThrowExceptionForHR(hr);
            return true;
        }
        finally
        {
            if (verbPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(verbPtr);
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (context is not null) Marshal.ReleaseComObject(context);
            if (folder is not null) Marshal.ReleaseComObject(folder);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct CommandInfo
    {
        public int Size; public uint Mask; public IntPtr Window, Verb, Parameters, Directory;
        public int Show, HotKey; public IntPtr Icon;
    }
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(); void EnumObjects(); void BindToObject(); void BindToStorage();
        void CompareIDs(); void CreateViewObject(); void GetAttributesOf();
        [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint count,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] pidls,
            ref Guid iid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object context);
    }
    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfo info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);
    }
    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfo info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr w, IntPtr l);
    }
    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfo info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr w, IntPtr l);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr w, IntPtr l, out IntPtr result);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string path, IntPtr bind, out IntPtr pidl, uint attributes, out uint result);
    [DllImport("shell32.dll")] private static extern int SHBindToParent(IntPtr pidl, ref Guid iid, out IShellFolder parent, out IntPtr child);
    [DllImport("shell32.dll")] private static extern IntPtr ILFindLastID(IntPtr pidl);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out POINT point);
}
