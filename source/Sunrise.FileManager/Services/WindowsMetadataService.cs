using System.Runtime.InteropServices;

namespace Sunrise.FileManager.Services;

/// <summary>Uses the installed Windows property handler; unsupported writes fail visibly.</summary>
public static class WindowsMetadataService
{
    public static readonly string[] Names = ["System.Title", "System.Subject", "System.Comment", "System.Keywords", "System.Author", "System.Rating"];
    public static Task<Dictionary<string, string>> ReadAsync(string path) => Sta(() => Read(path));
    public static Task WriteAsync(string path, IReadOnlyDictionary<string, string> values) => Sta(() => { Write(path, values); return true; });
    private static Task<T> Sta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() => { try { completion.TrySetResult(action()); } catch (Exception ex) { completion.TrySetException(ex); } })
            { IsBackground = true, Name = "Sunrise Property Handler" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start(); return completion.Task;
    }
    public static Dictionary<string, string> Read(string path)
    {
        var store = Open(path, 0);
        try
        {
            var result = new Dictionary<string, string>();
            foreach (string name in Names)
            {
                Marshal.ThrowExceptionForHR(PSGetPropertyKeyFromName(name, out var key));
                Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
                try { result[name] = ToText(value); }
                finally { PropVariantClear(ref value); }
            }
            return result;
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }
    public static void Write(string path, IReadOnlyDictionary<string, string> values)
    {
        var store = Open(path, 2); // GPS_READWRITE
        try
        {
            foreach (string name in Names)
            {
                if (!values.TryGetValue(name, out string? text)) continue;
                Marshal.ThrowExceptionForHR(PSGetPropertyKeyFromName(name, out var key));
                var value = new Variant();
                if (name == "System.Rating") { value.Type = 19; value.Number = uint.Parse(text); }
                else if (name is "System.Keywords" or "System.Author")
                {
                    string[] parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    value.Type = 0x101F; value.Number = (uint)parts.Length;
                    value.Vector = Marshal.AllocCoTaskMem(parts.Length * IntPtr.Size);
                    for (int i = 0; i < parts.Length; i++) Marshal.WriteIntPtr(value.Vector, i * IntPtr.Size, Marshal.StringToCoTaskMemUni(parts[i]));
                }
                else { value.Type = 31; value.Pointer = Marshal.StringToCoTaskMemUni(text); }
                try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value)); }
                finally { PropVariantClear(ref value); }
            }
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }
    private static string ToText(Variant value)
    {
        if (value.Type == 31) return Marshal.PtrToStringUni(value.Pointer) ?? "";
        if (value.Type == 19) return value.Number.ToString();
        if (value.Type == 0x101F)
            return string.Join("; ", Enumerable.Range(0, (int)Math.Min(value.Number, 1024u)).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(value.Vector, i * IntPtr.Size)) ?? ""));
        return "";
    }
    private static IPropertyStore Open(string path, uint flags)
    {
        Guid iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, flags, ref iid, out var store));
        return store;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(8)] public uint Number;
        [FieldOffset(16)] public IntPtr Vector;
    }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out Variant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref Variant value);
        [PreserveSig] int Commit();
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bind, uint flags, ref Guid iid, out IPropertyStore store);
    [DllImport("propsys.dll", CharSet = CharSet.Unicode)] private static extern int PSGetPropertyKeyFromName(string name, out PropertyKey key);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Variant value);
}
