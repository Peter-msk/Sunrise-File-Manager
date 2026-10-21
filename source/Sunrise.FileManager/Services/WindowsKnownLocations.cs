using System.IO;
using System.Runtime.InteropServices;
namespace Sunrise.FileManager.Services;

public static class WindowsKnownLocations
{
    public const string RecycleBin = "shell:RecycleBinFolder";
    private static readonly (string Key, string Label, string Id, Environment.SpecialFolder Fallback)[] Folders =
    [
        ("Desktop", "바탕화면", "B4BFCC3A-DB2C-424C-B029-7FE99A87C641", Environment.SpecialFolder.DesktopDirectory),
        ("Documents", "문서", "FDD39AD0-238F-46AF-ADB4-6C85480369C7", Environment.SpecialFolder.MyDocuments),
        ("Downloads", "다운로드", "374DE290-123F-4565-9164-39C4925E467B", Environment.SpecialFolder.UserProfile),
        ("Pictures", "사진", "33E28130-4E1E-4676-835A-98395C3BC3BB", Environment.SpecialFolder.MyPictures),
        ("Music", "음악", "4BD8D571-6D19-48D3-BE97-422220080E43", Environment.SpecialFolder.MyMusic),
        ("Videos", "동영상", "18989B1D-99B5-455B-841C-AB7C74E4DDFC", Environment.SpecialFolder.MyVideos)
    ];
    public static bool IsRecycleBin(string? path) => string.Equals(path, RecycleBin, StringComparison.OrdinalIgnoreCase);
    public static bool Exists(string? path) => IsRecycleBin(path) || Directory.Exists(path);
    public static string Resolve(string key)
    {
        if (key == "RecycleBin") return RecycleBin;
        foreach (var f in Folders)
        {
            if (f.Key != key) continue;
            if (OperatingSystem.IsWindows())
            {
                nint address = 0;
                try
                {
                    Guid id = Guid.Parse(f.Id);
                    if (SHGetKnownFolderPath(in id, 0, 0, out address) == 0)
                        return Marshal.PtrToStringUni(address) ?? "";
                }
                finally { if (address != 0) Marshal.FreeCoTaskMem(address); }
                // A failed redirected Downloads lookup must not silently target C:\\Users\\...\\Downloads.
                if (key == "Downloads") return "";
            }
            string value = Environment.GetFolderPath(f.Fallback);
            return key == "Downloads" ? Path.Combine(value, "Downloads") : value;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
    public static (string Path, string Label)? Match(string path)
    {
        foreach (var f in Folders)
        {
            string known = Resolve(f.Key).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (known.Length > 0 && (path.Equals(known, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(known + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                return (known, f.Label);
        }
        return null;
    }
    public static string DisplayPath(string path)
    {
        if (IsRecycleBin(path)) return "휴지통";
        var match = Match(path);
        return match is { } folder ? folder.Label + path[folder.Path.Length..] : path;
    }
    public static string ExpandDisplayPath(string path)
    {
        if (path == "휴지통") return RecycleBin;
        foreach (var folder in Folders)
        {
            if (path == folder.Label || path.StartsWith(folder.Label + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                string actual = Resolve(folder.Key);
                if (actual.Length == 0) return path;
                return actual + path[folder.Label.Length..];
            }
        }
        return path;
    }
    public static string Title(string path)
    {
        if (IsRecycleBin(path)) return "휴지통";
        var match = Match(path);
        if (match is { } f && path.TrimEnd('\\', '/').Equals(f.Path, StringComparison.OrdinalIgnoreCase)) return f.Label;
        return Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
}
