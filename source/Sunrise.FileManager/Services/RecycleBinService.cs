using System.IO;
using System.Runtime.InteropServices;
using Sunrise.FileManager.Models;
namespace Sunrise.FileManager.Services;

// Shell owns the item identities and restoration. No physical $Recycle.Bin file is exposed to file operations.
public sealed record RecycleRestoreResult(int Requested, int Missing, IReadOnlyList<string> Errors);
public static class RecycleBinService
{
    private static string Key(string identity) => "recycle-item:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(identity));
    private static Task<T> Sta<T>(Func<T> action, CancellationToken token)
        => WindowsShellApartment.RunAsync(action, token);
    public static Task<ListingResult> ReadAsync(CancellationToken token) => Sta(() =>
    {
        object? shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application") ?? throw new IOException("Windows Shell을 사용할 수 없습니다."));
            folder = ((dynamic)shell!).NameSpace(10) ?? throw new IOException("휴지통을 열 수 없습니다.");
            items = ((dynamic)folder).Items();
            int count = ((dynamic)items).Count;
            var entries = new List<FileEntry>();
            int skipped = 0;
            for (int i = 0; i < Math.Min(count, 20000); i++)
            {
                token.ThrowIfCancellationRequested();
                object? item = null;
                try
                {
                    item = ((dynamic)items).Item(i);
                    dynamic entry = item!;
                    string identity = (string)entry.Path;
                    string original = Convert.ToString(entry.ExtendedProperty("System.Recycle.DeletedFrom")) ?? "";
                    object? deleted = entry.ExtendedProperty("System.Recycle.DateDeleted");
                    object? size = entry.ExtendedProperty("System.Size");
                    entries.Add(new FileEntry
                    {
                        Name = (string)entry.Name, FullPath = Key(identity), IsDirectory = (bool)entry.IsFolder,
                        TypeName = Convert.ToString(entry.Type) ?? "파일", SizeBytes = size is null ? 0 : Convert.ToInt64(size),
                        ModifiedAt = Convert.ToDateTime(entry.ModifyDate), DeletedAt = deleted is DateTime dt ? dt : DateTime.MinValue,
                        OriginalLocation = original, IsVirtual = true, IsReadOnly = true
                    });
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or FormatException or OverflowException) { skipped++; }
            }
            return new ListingResult(entries, count > 20000 ? "항목이 20,000개를 초과합니다. 검색 전 일부 목록만 표시됩니다." : skipped > 0 ? $"읽을 수 없거나 조회 중 이동된 {skipped}개 항목은 표시하지 못했습니다." : null, DriveSummary: "휴지통 · 원래 위치로 복원 가능");
        }
        finally
        {
            // Shell automation shares RCWs across enumerations/apartments. The CLR owns their
            // lifetime; forcing a release can disconnect a concurrent refresh's live wrapper.
            GC.KeepAlive(items); GC.KeepAlive(folder); GC.KeepAlive(shell);
        }
    }, token);
    public static Task<RecycleRestoreResult> RestoreAsync(IReadOnlyList<string> keys, CancellationToken token, IntPtr owner = default)
        => ExecuteVerbAsync(keys, token, "undelete", owner);
    public static Task<RecycleRestoreResult> RestoreAllAsync(CancellationToken token, IntPtr owner = default)
        => ExecuteVerbAsync(null, token, "undelete", owner);
    public static Task<RecycleRestoreResult> PermanentlyDeleteAsync(IReadOnlyList<string> keys, CancellationToken token, IntPtr owner = default)
        => ExecuteVerbAsync(keys, token, "delete", owner);
    public static Task<RecycleRestoreResult> PropertiesAsync(IReadOnlyList<string> keys, IntPtr owner)
        => ExecuteVerbAsync(keys, CancellationToken.None, "properties", owner);

    public static Task<bool> EmptyAsync(IntPtr owner, CancellationToken token) => Sta(() =>
    {
        // Native aggregate confirmation, progress and errors; no preceding duplicate app dialog.
        int hr = SHEmptyRecycleBin(owner, null, 0);
        if (hr is unchecked((int)0x800704C7) or unchecked((int)0x80004004)) return false;
        Marshal.ThrowExceptionForHR(hr);
        return true;
    }, token);

    public static Task<bool> BinPropertiesAsync(IntPtr owner) => Sta(() =>
    {
        IntPtr root = RecyclePidl();
        try { return WindowsShellContextMenu.InvokeItems(owner, new[] { root }, "properties"); }
        finally { Marshal.FreeCoTaskMem(root); }
    }, CancellationToken.None);

    public static async Task ShowWindowsMenuAsync(System.Windows.Window owner, IReadOnlyList<string> keys)
    {
        // Only owned native PIDL copies cross apartments. Automation objects stay on the worker.
        IntPtr[] selected = await Sta(() => WithSelection(keys, CancellationToken.None, (pidls, missing) =>
            pidls.Select(ILCloneFull).ToArray()), CancellationToken.None);
        try
        {
            if (selected.Length == 0) throw new IOException("선택한 휴지통 항목을 찾을 수 없습니다. 새로 고침하세요.");
            WindowsShellContextMenu.ShowItems(owner, selected);
        }
        finally { foreach (IntPtr pidl in selected) Marshal.FreeCoTaskMem(pidl); }
    }
    public static Task<bool> CutToClipboardAsync(IReadOnlyList<string> keys, IntPtr owner)
        => Sta(() => WithSelection(keys, CancellationToken.None, (pidls, missing) =>
            pidls.Count > 0 && WindowsShellContextMenu.InvokeItems(owner, pidls, "cut")), CancellationToken.None);

    public static Task<bool> PasteShellClipboardAsync(string destination, IntPtr owner) => Sta(() =>
    {
        Marshal.ThrowExceptionForHR(SHParseDisplayName(destination, IntPtr.Zero, out IntPtr target, 0, out _));
        try { return WindowsShellContextMenu.InvokeItems(owner, new[] { target }, "paste"); }
        finally { Marshal.FreeCoTaskMem(target); }
    }, CancellationToken.None);

    private static Task<RecycleRestoreResult> ExecuteVerbAsync(IReadOnlyList<string>? keys, CancellationToken token, string verb, IntPtr owner)
        => Sta(() => WithSelection(keys, token, (pidls, missing) =>
        {
            bool invoked = pidls.Count > 0 && WindowsShellContextMenu.InvokeItems(owner, pidls, verb);
            return new RecycleRestoreResult(invoked ? pidls.Count : 0, missing, Array.Empty<string>());
        }), token);

    private static T WithSelection<T>(IReadOnlyList<string>? keys, CancellationToken token, Func<IReadOnlyList<IntPtr>, int, T> action)
    {
        var requested = keys?.ToHashSet(StringComparer.Ordinal);
        object? shell = null, folder = null, items = null;
        var pidls = new List<IntPtr>();
        IntPtr root = RecyclePidl();
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application") ?? throw new IOException("Windows Shell을 사용할 수 없습니다."));
            folder = ((dynamic)shell!).NameSpace(10) ?? throw new IOException("휴지통을 열 수 없습니다.");
            items = ((dynamic)folder).Items();
            int count = ((dynamic)items).Count;
            // Resolve opaque keys against the live namespace immediately before the single batch.
            // A display-name match or a physical filesystem PIDL is never an acceptable substitute.
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                object? item = null;
                {
                    item = ((dynamic)items).Item(i);
                    if (requested is not null && !requested.Contains(Key((string)((dynamic)item!).Path))) continue;
                    Marshal.ThrowExceptionForHR(SHGetIDListFromObject(item!, out IntPtr pidl));
                    if (!ILIsParent(root, pidl, true))
                    {
                        Marshal.FreeCoTaskMem(pidl);
                        throw new IOException("Windows 휴지통 항목 식별자를 확인하지 못했습니다. 작업을 중단했습니다.");
                    }
                    pidls.Add(pidl);
                }
            }
            token.ThrowIfCancellationRequested();
            return action(pidls, requested is null ? 0 : requested.Count - pidls.Count);
        }
        finally
        {
            foreach (IntPtr pidl in pidls) Marshal.FreeCoTaskMem(pidl);
            Marshal.FreeCoTaskMem(root);
            GC.KeepAlive(items); GC.KeepAlive(folder); GC.KeepAlive(shell);
        }
    }
    private static IntPtr RecyclePidl()
    {
        Guid id = new("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
        Marshal.ThrowExceptionForHR(SHGetKnownFolderIDList(ref id, 0, IntPtr.Zero, out IntPtr pidl));
        return pidl;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string path, IntPtr bind, out IntPtr pidl, uint attributes, out uint result);
    [DllImport("shell32.dll")] private static extern IntPtr ILCloneFull(IntPtr pidl);
    [DllImport("shell32.dll")] private static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object value, out IntPtr pidl);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderIDList(ref Guid id, uint flags, IntPtr token, out IntPtr pidl);
    [DllImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ILIsParent(IntPtr parent, IntPtr child, [MarshalAs(UnmanagedType.Bool)] bool immediate);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr owner, string? root, uint flags);
}
