using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Sunrise.FileManager.Services;

/// <summary>The Windows share sheet owns recipients, apps and nearby-share availability.</summary>
public sealed class WindowsShareService : IDisposable
{
    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr hwnd, ref Guid iid);
        void ShowShareUIForWindow(IntPtr hwnd);
    }
    private DataTransferManager? _manager;
    private string[] _paths = [];
    public void Show(Window owner, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || paths.Count > 100) throw new IOException("공유할 파일 또는 폴더를 1~100개 선택하세요.");
        Dispose();
        _paths = paths.ToArray();
        IntPtr hwnd = new WindowInteropHelper(owner).Handle;
        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        Guid iid = new("a5caee9b-8708-49d1-8d36-67d25a8da00c");
        IntPtr pointer = interop.GetForWindow(hwnd, ref iid);
        try { _manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
        _manager.DataRequested += OnDataRequested;
        interop.ShowShareUIForWindow(hwnd);
    }
    private async void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        string[] snapshot = _paths;
        try
        {
            var items = new List<IStorageItem>();
            foreach (string path in snapshot)
                items.Add(Directory.Exists(path) ? await StorageFolder.GetFolderFromPathAsync(path)
                    : await StorageFile.GetFileFromPathAsync(path));
            args.Request.Data.Properties.Title = snapshot.Length == 1 ? Path.GetFileName(snapshot[0]) : $"파일 {snapshot.Length}개";
            args.Request.Data.SetStorageItems(items);
        }
        catch (Exception ex) { args.Request.FailWithDisplayText("공유할 항목을 읽지 못했습니다. " + ex.Message); }
        finally { deferral.Complete(); }
    }
    public void Dispose()
    {
        if (_manager is not null) _manager.DataRequested -= OnDataRequested;
        _manager = null; _paths = [];
    }
}
