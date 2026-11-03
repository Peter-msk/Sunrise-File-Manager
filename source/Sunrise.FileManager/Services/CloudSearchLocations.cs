using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Sunrise.FileManager.Services;

/// <summary>Read-only Windows locations. Does not authenticate to cloud providers or infer account ownership.</summary>
public static class CloudSearchLocations
{
    // Read-only Windows mapping query. Used solely for friendly drive labels.
    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    private static string WindowsDriveName(DriveInfo drive)
    {
        string letter = drive.Name.TrimEnd('\\');
        string? label = null;
        try
        {
            if (drive.IsReady) label = drive.VolumeLabel?.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (drive.DriveType == DriveType.Network)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var remote = new StringBuilder(1024);
                    int length = remote.Capacity;
                    if (WNetGetConnection(letter, remote, ref length) == 0)
                    {
                        string unc = remote.ToString();
                        if (!string.IsNullOrWhiteSpace(unc))
                            return $"{unc} ({letter})";
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        }
        if (!string.IsNullOrWhiteSpace(label)) return $"{label} ({letter})";
        string prefix = drive.DriveType switch
        {
            DriveType.Network => "네트워크 드라이브",
            DriveType.Removable => "이동식 드라이브",
            DriveType.CDRom => "광학 드라이브",
            DriveType.Fixed => "로컬 디스크",
            _ => "드라이브"
        };
        return $"{prefix} ({letter})";
    }

    // On-demand files may hydrate if a content reader opens them. Name/metadata search is safe.
    public static bool IsOnlineOnly(FileAttributes attrs) =>
        (attrs & FileAttributes.Offline) != 0 || (((uint)attrs & 0x00400000u) != 0);

    public static IReadOnlyList<(string Name, string Path)> GetRoots()
    {
        var result = new List<(string Name, string Path)>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string full = System.IO.Path.GetFullPath(path);
                if (Directory.Exists(full) && added.Add(full)) result.Add((name, full));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        }
        Add("바탕 화면", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("문서", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("OneDrive (개인)", Environment.GetEnvironmentVariable("OneDriveConsumer"));
        Add("OneDrive (회사/학교)", Environment.GetEnvironmentVariable("OneDriveCommercial"));
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDrive"));
        // Drive for desktop may mount a virtual drive letter. It is listed among Windows drives.
        // A custom mirrored Google Drive folder is selected via the Browse button rather than guessed.
        foreach (var drive in DriveInfo.GetDrives()) Add(WindowsDriveName(drive), drive.Name);
        return result;
    }
}
