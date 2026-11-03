using System.Runtime.InteropServices;

namespace Sunrise.FileManager.Services;

/// <summary>
/// Permanently removes a selection of *Recycle Bin namespace objects* in one Windows
/// Shell transaction. Never delete the physical $R/$I backing files or treat an item
/// identity as an ordinary user-file path. The caller must obtain confirmation first.
/// </summary>
internal static class RecycleBinNativeDelete
{
    private const uint SHCONTF_FOLDERS = 0x20;
    private const uint SHCONTF_NONFOLDERS = 0x40;
    private const uint SHCONTF_INCLUDEHIDDEN = 0x80;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_NOERRORUI = 0x0400;
    private static readonly Guid FolderInterface = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid ShellItemInterface = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid FileOperationClass = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    private static void Check(int hresult) { if (hresult < 0) Marshal.ThrowExceptionForHR(hresult); }
    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private static string? ItemName(IShellItem item, uint nameKind)
    {
        IntPtr pointer = IntPtr.Zero;
        try
        {
            if (item.GetDisplayName(nameKind, out pointer) != 0 || pointer == IntPtr.Zero) return null;
            return Marshal.PtrToStringUni(pointer);
        }
        finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
    }

    /// <summary>
    /// Called inside RecycleBinService's dedicated STA. Every identity must be matched
    /// against a LIVE child of the Recycle Bin (not SHCreateItemFromParsingName of a
    /// physical $Recycle.Bin path). A missing/ambiguous identity is never queued.
    /// </summary>
    internal static (int Queued, bool Aborted, IReadOnlyList<string> Errors) Delete(
        IReadOnlySet<string> identities, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        IntPtr rootPidl = IntPtr.Zero;
        IShellFolder? parent = null;
        IEnumIDList? iterator = null;
        IFileOperation? operation = null;
        var targets = new Dictionary<string, IShellItem>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        try
        {
            // Primary path: resolve each live automation object to its genuine PIDL.
            // Shell items made from these PIDLs preserve recycle-bin metadata and verbs.
            PopulateFromAutomation(identities, token, targets);
            // Some Shell automation providers do not implement IPersistIDList. Resolve
            // unresolved identities by enumerating the actual native Shell namespace.
            if (targets.Count < identities.Count)
            {
                Check(SHParseDisplayName(WindowsKnownLocations.RecycleBin, IntPtr.Zero,
                    out rootPidl, 0, out _));
                Check(SHBindToObject(IntPtr.Zero, rootPidl, IntPtr.Zero, in FolderInterface, out parent));
                if (parent is null) throw new InvalidOperationException("Recycle Bin Shell namespace returned no parent folder.");
                int enumerationResult = parent.EnumObjects(IntPtr.Zero,
                    SHCONTF_FOLDERS | SHCONTF_NONFOLDERS | SHCONTF_INCLUDEHIDDEN, out iterator);
                Check(enumerationResult);
            }
            while (iterator is not null && iterator.Next(1, out IntPtr childPidl, out uint count) == 0 && count == 1)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    IShellItem? item = null;
                    try
                    {
                        if (SHCreateItemWithParent(rootPidl, parent ?? throw new InvalidOperationException("Recycle Bin Shell parent was lost."), childPidl,
                            in ShellItemInterface, out item) != 0) continue;
                        // Automation FolderItem.Path is the identity used by the existing
                        // list model; accept ONLY exact matches from native Shell metadata.
                        string? identity = ItemName(item!, SIGDN_FILESYSPATH);
                        if (identity is null || !identities.Contains(identity))
                        {
                            identity = ItemName(item!, SIGDN_DESKTOPABSOLUTEPARSING);
                            if (identity is null || !identities.Contains(identity)) continue;
                        }
                        if (targets.ContainsKey(identity)) continue;
                        targets.Add(identity, item!);
                        item = null;
                    }
                    finally { Release(item); }
                }
                finally { Marshal.FreeCoTaskMem(childPidl); }
            }
            token.ThrowIfCancellationRequested();
            if (targets.Count == 0) return (0, false, errors);
            operation = (IFileOperation)(Activator.CreateInstance(Type.GetTypeFromCLSID(FileOperationClass)!)
                ?? throw new COMException("Windows 파일 작업을 초기화할 수 없습니다."));
            // The application already showed ONE aggregate confirmation. Suppress native
            // per-item confirmations and report errors once from the caller.
            Check(operation.SetOperationFlags(FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI));
            int queued = 0;
            foreach (var item in targets.Values)
            {
                token.ThrowIfCancellationRequested();
                int hr = operation.DeleteItem(item, IntPtr.Zero);
                if (hr == 0) queued++;
                else if (errors.Count < 20) errors.Add($"Shell 삭제 예약 실패 (0x{hr:X8}).");
            }
            if (queued == 0) return (0, false, errors);
            Check(operation.PerformOperations());
            Check(operation.GetAnyOperationsAborted(out bool aborted));
            return (queued, aborted, errors);
        }
        finally
        {
            foreach (var item in targets.Values) Release(item);
            Release(operation);
            Release(iterator);
            Release(parent);
            if (rootPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(rootPidl);
        }
    }


    private static void PopulateFromAutomation(IReadOnlySet<string> identities, CancellationToken token,
        Dictionary<string, IShellItem> targets)
    {
        object? shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            folder = ((dynamic)shell!).NameSpace(10);
            if (folder is null) return;
            items = ((dynamic)folder).Items();
            int count = ((dynamic)items).Count;
            for (int i = 0; i < count && targets.Count < identities.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                object? automationItem = null;
                IntPtr pidl = IntPtr.Zero;
                IShellItem? nativeItem = null;
                try
                {
                    automationItem = ((dynamic)items).Item(i);
                    string identity = (string)((dynamic)automationItem!).Path;
                    if (!identities.Contains(identity) || targets.ContainsKey(identity)) continue;
                    if (SHGetIDListFromObject(automationItem!, out pidl) != 0 || pidl == IntPtr.Zero) continue;
                    if (SHCreateItemFromIDList(pidl, in ShellItemInterface, out nativeItem) != 0) continue;
                    targets.Add(identity, nativeItem!);
                    nativeItem = null;
                }
                catch (COMException) { /* Unavailable identity stays unmatched. */ }
                finally
                {
                    Release(nativeItem);
                    if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
                    Release(automationItem);
                }
            }
        }
        finally { Release(items); Release(folder); Release(shell); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object obj, out IntPtr pidl);
    [DllImport("shell32.dll")]
    private static extern int SHCreateItemFromIDList(IntPtr pidl, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")]
    private static extern int SHBindToObject(IntPtr folder, IntPtr pidl, IntPtr context,
        in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder? target);
    [DllImport("shell32.dll")]
    private static extern int SHCreateItemWithParent(IntPtr absoluteParentPidl,
        [MarshalAs(UnmanagedType.Interface)] IShellFolder parent, IntPtr childPidl,
        in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr bindContext, [MarshalAs(UnmanagedType.LPWStr)] string name,
            out uint eaten, out IntPtr pidl, ref uint attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, out IEnumIDList? enumerator);
        void BindToObject(IntPtr pidl, IntPtr bindContext, in Guid iid, out IntPtr value);
        void BindToStorage(IntPtr pidl, IntPtr bindContext, in Guid iid, out IntPtr value);
        void CompareIDs(IntPtr lParam, IntPtr item1, IntPtr item2);
        void CreateViewObject(IntPtr hwnd, in Guid iid, out IntPtr value);
        void GetAttributesOf(uint count, IntPtr apidl, ref uint flags);
        void GetUIObjectOf(IntPtr hwnd, uint count, IntPtr apidl, in Guid iid, IntPtr reserved, out IntPtr value);
        void GetDisplayNameOf(IntPtr pidl, uint flags, out IntPtr result);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name,
            uint flags, out IntPtr newPidl);
    }

    [ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumIDList
    {
        [PreserveSig] int Next(uint count, out IntPtr childPidl, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumIDList cloned);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, in Guid handler, in Guid iid, out IntPtr value);
        void GetParent(out IShellItem item);
        [PreserveSig] int GetDisplayName(uint name, out IntPtr value);
        void GetAttributes(uint mask, out uint attrs);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr hwnd);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void MoveItems(IntPtr items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void CopyItems(IntPtr items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, IntPtr sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
