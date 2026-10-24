using System.IO;
using System.Runtime.InteropServices;

namespace Sunrise.FileManager.Services;

public sealed record ShellLocation(string Name, string Path, bool IsFolder, bool ShellOnly = false)
{
    public string Display => $"{Name}\n{Path}";
}
public static class WindowsCollectionsService
{
    public static Task<IReadOnlyList<ShellLocation>> ReadAsync(string location, CancellationToken token)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<ShellLocation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? shell = null, folder = null, items = null;
            try
            {
                token.ThrowIfCancellationRequested();
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
                folder = ((dynamic)shell!).NameSpace(location) ?? throw new IOException("Windows 위치를 열 수 없습니다.");
                items = ((dynamic)folder).Items();
                int count = ((dynamic)items).Count;
                var rows = new List<ShellLocation>();
                for (int i = 0; i < Math.Min(count, 2000); i++)
                {
                    token.ThrowIfCancellationRequested();
                    object? item = null, link = null;
                    try
                    {
                        item = ((dynamic)items).Item(i);
                        string path = Convert.ToString(((dynamic)item!).Path) ?? "";
                        string name = Convert.ToString(((dynamic)item!).Name) ?? path;
                        bool isFolder = ((dynamic)item!).IsFolder;
                        if (location == "shell:Recent" && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                        {
                            link = ((dynamic)item!).GetLink;
                            path = Convert.ToString(((dynamic)link).Path) ?? "";
                            isFolder = Directory.Exists(path);
                            if (!isFolder && !File.Exists(path)) continue;
                        }
                        if (path.Length > 0) rows.Add(new(name, path, isFolder,
                            !File.Exists(path) && !Directory.Exists(path) || path.EndsWith(".library-ms", StringComparison.OrdinalIgnoreCase)));
                    }
                    catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { }
                    finally { Release(link); Release(item); }
                }
                if (location == "shell:Recent")
                {
                    var frequent = rows.Where(r => !r.ShellOnly).Select(r => r.IsFolder ? r.Path : System.IO.Path.GetDirectoryName(r.Path))
                        .Where(p => !string.IsNullOrEmpty(p)).GroupBy(p => p!, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(g => g.Count()).Take(12)
                        .Select(g => new ShellLocation($"최근 파일이 많은 폴더 · 파일 {g.Count()}개 · {System.IO.Path.GetFileName(g.Key)}", g.Key, true));
                    rows = frequent.Concat(rows).ToList();
                }
                completion.TrySetResult(rows);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { Release(items); Release(folder); Release(shell); }
        }) { IsBackground = true, Name = "Sunrise Windows Locations" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    public static void ConnectDrive(IntPtr hwnd)
    {
        int result = WNetConnectionDialog(hwnd, 1);
        if (result is not (0 or -1 or 1223)) throw new System.ComponentModel.Win32Exception(result);
    }
    public static void DisconnectDrive(IntPtr hwnd)
    {
        int result = WNetDisconnectDialog(hwnd, 1);
        if (result is not (0 or -1 or 1223)) throw new System.ComponentModel.Win32Exception(result);
    }
    [DllImport("mpr.dll")] private static extern int WNetConnectionDialog(IntPtr hwnd, uint type);
    [DllImport("mpr.dll")] private static extern int WNetDisconnectDialog(IntPtr hwnd, uint type);
}
