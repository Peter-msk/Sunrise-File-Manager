using System.IO;
using Sunrise.FileManager.Models;
namespace Sunrise.FileManager.Services;
public static class ExplorerLocationService
{
    public static string? Parent(string path, string desktop)
    {
        if (!string.IsNullOrWhiteSpace(desktop) && string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(desktop), StringComparison.OrdinalIgnoreCase)) return null;
        if (WindowsKnownLocations.IsVirtual(path)) return string.IsNullOrWhiteSpace(desktop) ? null : desktop;
        try
        {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? "";
        if (Path.TrimEndingDirectorySeparator(full).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) return WindowsKnownLocations.ThisPc;
        return Directory.GetParent(full)?.FullName;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }
    public static Task<ListingResult> ReadThisPcAsync(CancellationToken token) => Task.Run(() =>
    {
        var entries = new List<FileEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            token.ThrowIfCancellationRequested();
            string label = drive.DriveType == DriveType.Network ? "네트워크 드라이브" : "로컬 디스크";
            string capacity = "준비되지 않음";
            long free = 0;
            try
            {
                if (drive.IsReady)
                {
                    if (!string.IsNullOrWhiteSpace(drive.VolumeLabel)) label = drive.VolumeLabel;
                    free = drive.AvailableFreeSpace;
                    capacity = $"여유 {FileEntry.HumanSize(free)} / {FileEntry.HumanSize(drive.TotalSize)}";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { capacity = "장치에 접근할 수 없음"; }
            entries.Add(new FileEntry { Name = $"{label} ({drive.Name.TrimEnd('\\', '/')})", FullPath = drive.RootDirectory.FullName,
                IsDirectory = true, TypeName = drive.DriveType == DriveType.Network ? "네트워크 드라이브" : "드라이브",
                SizeBytes = free, ModifiedAt = DateTime.MinValue, LocationSummary = capacity, IsNavigationLocation = true });
        }
        return new ListingResult(entries, null, DriveSummary: "내 PC · 드라이브를 두 번 클릭하여 열기");
    }, token);
}
