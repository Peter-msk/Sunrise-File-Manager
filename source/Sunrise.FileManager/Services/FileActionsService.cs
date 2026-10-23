using System.IO;
using System.Threading;

namespace Sunrise.FileManager.Services;

public sealed record FolderSizeSummary(long TotalBytes, int FileCount, int FolderCount, bool Truncated);

/// <summary>
/// Interactive file actions use the caller's Windows permissions. Delete is recycle-bin only
/// unless the user explicitly invokes Shift+Delete permanent deletion with confirmation.
/// Core validation, creation, rename, move, and recycle operations live in this file;
/// archive (ZIP), folder metrics, hash, duplication, and shell integration are modularized in
/// Features/FileSystem/FileActionsService.ArchiveAndShell.cs.
/// </summary>
public static partial class FileActionsService
{
    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.TrimEnd(' ', '.') != name ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) ||
            name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("사용할 수 없는 파일 또는 폴더 이름입니다.");
        string stem = name.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                  stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                                  (stem[3] is >= '1' and <= '9' || "¹²³".Contains(stem[3]))))
            throw new ArgumentException("Windows에서 예약된 파일 이름입니다.");
        return name;
    }

    public static string SuggestNewFolderName(string parentFolder, string baseName = "새 폴더")
    {
        string parent = Path.GetFullPath(parentFolder);
        string cleanBase = ValidateName(baseName);
        if (!Directory.Exists(parent)) return cleanBase;
        string first = Path.Combine(parent, cleanBase);
        if (!File.Exists(first) && !Directory.Exists(first)) return cleanBase;
        for (int i = 2; i <= 999; i++)
        {
            string candidate = $"{cleanBase} ({i})";
            string full = Path.Combine(parent, candidate);
            if (!File.Exists(full) && !Directory.Exists(full)) return candidate;
        }
        return cleanBase;
    }

    public static string SuggestNewFileName(string parentFolder, string baseFileName = "새 텍스트 문서.txt")
    {
        string parent = Path.GetFullPath(parentFolder);
        string cleanBase = ValidateName(baseFileName);
        if (!Directory.Exists(parent)) return cleanBase;
        string first = Path.Combine(parent, cleanBase);
        if (!File.Exists(first) && !Directory.Exists(first)) return cleanBase;
        string ext = Path.GetExtension(cleanBase);
        string stem = cleanBase[..(cleanBase.Length - ext.Length)];
        for (int i = 2; i <= 999; i++)
        {
            string candidate = $"{stem} ({i}){ext}";
            string full = Path.Combine(parent, candidate);
            if (!File.Exists(full) && !Directory.Exists(full)) return candidate;
        }
        return cleanBase;
    }

    public static string CreateFolder(string parentFolder, string name)
    {
        string parent = Path.GetFullPath(parentFolder);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("현재 폴더를 찾을 수 없습니다.");
        string path = Path.Combine(parent, ValidateName(name));
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("같은 이름의 항목이 이미 존재합니다.");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string CreateFile(string parentFolder, string name, string initialContent = "")
    {
        string parent = Path.GetFullPath(parentFolder);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("현재 폴더를 찾을 수 없습니다.");
        string path = Path.Combine(parent, ValidateName(name));
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("같은 이름의 항목이 이미 존재합니다.");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            writer.Write(initialContent);
        return path;
    }

    public static string Rename(string sourcePath, string name)
    {
        string source = Path.GetFullPath(sourcePath);
        EnsureOrdinaryEntry(source);
        string target = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source))!, ValidateName(name));
        if (string.Equals(source, target, StringComparison.Ordinal)) return source;
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException("같은 이름의 항목이 이미 존재합니다. 덮어쓰지 않습니다.");
        MoveEntry(source, target);
        return target;
    }

    public static IReadOnlyList<(string OldPath, string NewPath)> BatchRename(
        IReadOnlyList<(string SourcePath, string NewName)> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var results = new List<(string OldPath, string NewPath)>(operations.Count);
        foreach (var (sourcePath, newName) in operations)
        {
            string renamed = Rename(sourcePath, newName);
            results.Add((sourcePath, renamed));
        }
        return results;
    }

    /// <summary>Single authoritative path for user-initiated moves and renames.</summary>
    public static void MoveEntry(string sourcePath, string destinationPath)
    {
        string source = Path.GetFullPath(sourcePath);
        string destination = Path.GetFullPath(destinationPath);
        EnsureOrdinaryEntry(source);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("대상에 같은 이름이 존재합니다. 덮어쓰지 않습니다.");
        if (TransferService.IsDifferentVolume(source, destination))
        {
            // Undo/redo and direct file actions use the same safe cross-volume move owner.
            string parent = Path.GetDirectoryName(destination)!;
            var exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [source] = destination };
            TransferResult result = TransferService.RunAsync([source], parent, TransferKind.Move,
                null, CancellationToken.None, CollisionPolicy.Skip, exact).GetAwaiter().GetResult();
            if (result.Failed != 0 || result.Completed != 1)
                throw new IOException(result.Errors.FirstOrDefault() ?? "다른 드라이브로 이동하지 못했습니다.");
            return;
        }
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else FileMemoStore.MoveFileWithMemos(source, destination);
    }

    public static void Recycle(string sourcePath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("휴지통 작업은 Windows에서만 지원합니다.");
        string source = Path.GetFullPath(sourcePath);
        EnsureOrdinaryEntry(source);
        string? root = Path.GetPathRoot(source);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new IOException("휴지통 보장이 어려운 네트워크·이동식 위치에서는 삭제를 지원하지 않습니다.");

        WindowsRecycleService.Recycle(source);
    }

    public sealed record RecycleBatchResult(int Completed, IReadOnlyList<string> Errors)
    {
        public int Failed => Errors.Count;
    }

    public static Task<RecycleBatchResult> RecycleManyAsync(IReadOnlyList<string> paths,
        IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("휴지통 작업은 Windows에서만 지원합니다.");
        string[] requested = paths.ToArray();
        var completion = new TaskCompletionSource<RecycleBatchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                int completed = 0;
                var errors = new List<string>();
                foreach (string path in requested)
                {
                    try { Recycle(path); completed++; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception or
                        OperationCanceledException or System.Security.SecurityException)
                    {
                        errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                    }
                    progress?.Report(completed + errors.Count);
                }
                completion.TrySetResult(new RecycleBatchResult(completed, errors));
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "Sunrise Recycle STA" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    internal static void EnsureOrdinaryEntry(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("선택한 항목이 존재하지 않습니다.", path);
        string root = Path.GetPathRoot(path) ?? string.Empty;
        if (string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("드라이브 루트는 수정 또는 삭제할 수 없습니다.");
        if (ReparsePointPolicy.IsBlocked(path))
            throw new IOException("연결·접합점은 수정 또는 삭제 대상이 아닙니다.");
    }
}
