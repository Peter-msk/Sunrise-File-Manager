using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
namespace Sunrise.FileManager.Services;

public static class WindowsHardwareProbe
{
    public static PcHardware Read()
    {
        string cpu = "CPU 정보 확인 불가";
        if (OperatingSystem.IsWindows())
            try { using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"); cpu = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? cpu; } catch { }
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        bool memoryOk = OperatingSystem.IsWindows() && GlobalMemoryStatusEx(ref memory);
        string path = Environment.GetEnvironmentVariable("OLLAMA_MODELS")?.Trim('"') ?? "";
        if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
        path = Environment.ExpandEnvironmentVariables(path);
        string? modelRoot = null;
        try { modelRoot = Path.GetPathRoot(Path.GetFullPath(path)); } catch { }
        var drives = new List<StorageHardware>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                drives.Add(new StorageHardware(drive.Name, Medium(drive.Name), drive.AvailableFreeSpace / 1073741824d,
                    string.Equals(drive.Name, modelRoot, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new PcHardware(cpu, Environment.ProcessorCount, memoryOk ? memory.TotalPhysical / 1073741824d : 0,
            memoryOk ? memory.AvailablePhysical / 1073741824d : 0, ReadGpus(), drives, path,
            memoryOk ? null : "RAM 조회에 실패했습니다. 자동 추천을 확인한 뒤 직접 선택하세요.");
    }
    private static string Medium(string root)
    {
        if (!OperatingSystem.IsWindows() || root.Length < 2 || root[1] != ':') return "종류 미확인";
        try
        {
            using var handle = CreateFile(@"\\.\" + root[..2], 0, 3, 0, 3, 0, 0);
            if (handle.IsInvalid) return "종류 미확인";
            byte[] query = new byte[12]; query[0] = 7; // StorageDeviceSeekPenaltyProperty, PropertyStandardQuery.
            byte[] result = new byte[32];
            if (DeviceIoControl(handle, 0x002D1400, query, query.Length, result, result.Length, out int count, 0) && count >= 9)
                return result[8] == 0 ? "SSD" : "HDD";
        }
        catch { }
        return "종류 미확인";
    }
    private static IReadOnlyList<GpuHardware> ReadGpus()
    {
        var list = new List<GpuHardware>();
        if (!OperatingSystem.IsWindows()) return list;
        Factory? factory = null;
        try
        {
            Guid id = typeof(Factory).GUID;
            if (CreateDXGIFactory(in id, out factory) != 0 || factory is null) return list;
            for (uint i = 0; i < 16; i++)
            {
                if (factory.EnumAdapters(i, out Adapter adapter) != 0) break;
                try
                {
                    if (adapter.GetDesc(out AdapterDescription desc) == 0 && desc.VendorId != 0x1414)
                        list.Add(new GpuHardware(desc.Description.Trim(), (double)desc.DedicatedVideoMemory / 1073741824d));
                }
                finally { Marshal.FinalReleaseComObject(adapter); }
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException) { }
        finally { if (factory is not null) Marshal.FinalReleaseComObject(factory); }
        return list;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSystemId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh;
    }
    // Slots through EnumAdapters/GetDesc match IDXGIObject -> IDXGIFactory/IDXGIAdapter exactly.
    [ComImport, Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface Factory
    {
        [PreserveSig] int SetPrivateData(in Guid id, uint size, nint data);
        [PreserveSig] int SetPrivateDataInterface(in Guid id, nint value);
        [PreserveSig] int GetPrivateData(in Guid id, ref uint size, nint data);
        [PreserveSig] int GetParent(in Guid id, out nint parent);
        [PreserveSig] int EnumAdapters(uint index, out Adapter adapter);
    }
    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface Adapter
    {
        [PreserveSig] int SetPrivateData(in Guid id, uint size, nint data);
        [PreserveSig] int SetPrivateDataInterface(in Guid id, nint value);
        [PreserveSig] int GetPrivateData(in Guid id, ref uint size, nint data);
        [PreserveSig] int GetParent(in Guid id, out nint parent);
        [PreserveSig] int EnumOutputs(uint index, out nint output);
        [PreserveSig] int GetDesc(out AdapterDescription description);
    }
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory(in Guid id, out Factory factory);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, nint overlapped);
}
