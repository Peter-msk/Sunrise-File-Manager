using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;

namespace Sunrise.FileManager.Services;

/// <summary>
/// Feature module for FileActionsService: duplication, ZIP compression/extraction,
/// permanent deletion, file attribute toggles, recursive folder size & SHA-256 calculation,
/// and Windows Explorer / cmd.exe shell integration.
/// </summary>
public static partial class FileActionsService
{
    public static IReadOnlyList<string> DuplicateEntries(IReadOnlyList<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        var created = new List<string>();
        foreach (string raw in sourcePaths)
        {
            string source = Path.GetFullPath(raw);
            EnsureOrdinaryEntry(source);
            bool isDir = Directory.Exists(source);
            string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source))
                ?? throw new IOException("상위 폴더를 확인할 수 없습니다.");
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            string ext = isDir ? string.Empty : Path.GetExtension(name);
            string stem = isDir ? name : name[..(name.Length - ext.Length)];

            string target = string.Empty;
            for (int n = 1; n <= 999; n++)
            {
                string suffix = n == 1 ? " - 복사본" : $" - 복사본 ({n})";
                string candidate = Path.Combine(parent, ValidateName($"{stem}{suffix}{ext}"));
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    target = candidate;
                    break;
                }
            }
            if (string.IsNullOrEmpty(target))
                throw new IOException("복사본 이름 후보(999개)가 모두 사용 중입니다.");

            if (isDir)
            {
                CopyDirectoryRecursive(source, target);
            }
            else
            {
                File.Copy(source, target, overwrite: false);
            }
            created.Add(target);
        }
        return created;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            string destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: false);
        }
        foreach (string subDir in Directory.EnumerateDirectories(sourceDir))
        {
            if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0) continue;
            string destSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectoryRecursive(subDir, destSubDir);
        }
    }

    public static string CompressToZip(IReadOnlyList<string> sourcePaths, string destinationZipPath)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0) throw new ArgumentException("압축할 항목을 선택하세요.");
        string zipFull = Path.GetFullPath(destinationZipPath);
        if (!zipFull.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            zipFull += ".zip";
        ValidateName(Path.GetFileName(zipFull));
        if (File.Exists(zipFull) || Directory.Exists(zipFull))
            throw new IOException("같은 이름의 압축 파일이 이미 존재합니다.");

        string tempZip = zipFull + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                foreach (string raw in sourcePaths)
                {
                    string src = Path.GetFullPath(raw);
                    EnsureOrdinaryEntry(src);
                    if (Directory.Exists(src))
                    {
                        string rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(src));
                        AddDirectoryToZip(archive, src, rootName);
                    }
                    else
                    {
                        archive.CreateEntryFromFile(src, Path.GetFileName(src), CompressionLevel.Optimal);
                    }
                }
            }
            File.Move(tempZip, zipFull, overwrite: false);
            return zipFull;
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
    }

    private static void AddDirectoryToZip(ZipArchive archive, string dirPath, string relativePrefix)
    {
        bool hasAny = false;
        foreach (string file in Directory.EnumerateFiles(dirPath))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            hasAny = true;
            string entryName = relativePrefix + "/" + Path.GetFileName(file);
            archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
        }
        foreach (string sub in Directory.EnumerateDirectories(dirPath))
        {
            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
            hasAny = true;
            AddDirectoryToZip(archive, sub, relativePrefix + "/" + Path.GetFileName(sub));
        }
        if (!hasAny)
        {
            archive.CreateEntry(relativePrefix + "/");
        }
    }

    public static string ExtractZip(string zipFilePath, string destinationFolder)
    {
        string srcZip = Path.GetFullPath(zipFilePath);
        EnsureOrdinaryEntry(srcZip);
        if (!File.Exists(srcZip) || !srcZip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("유효한 ZIP 압축 파일이 아닙니다.");
        string dest = Path.GetFullPath(destinationFolder);
        if (File.Exists(dest) || Directory.Exists(dest))
            throw new IOException("압축을 풀 대상 폴더 이름이 이미 존재합니다.");

        Directory.CreateDirectory(dest);
        string destPrefix = Path.EndsInDirectorySeparator(dest) ? dest : dest + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(srcZip);
        if (archive.Entries.Count > 20_000)
            throw new InvalidDataException("압축 파일 내부 항목이 20,000개를 초과하여 중지했습니다.");

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string targetPath = Path.GetFullPath(Path.Combine(dest, entry.FullName));
            if (!targetPath.StartsWith(destPrefix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(targetPath, dest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("압축 파일에 상위 폴더 탈출(Zip-Slip) 경로가 포함되어 차단했습니다.");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: false);
            }
        }
        return dest;
    }

    public static RecycleBatchResult DeletePermanently(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        int completed = 0;
        var errors = new List<string>();
        foreach (string raw in paths)
        {
            try
            {
                string full = Path.GetFullPath(raw);
                EnsureOrdinaryEntry(full);
                if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
                else if (File.Exists(full)) File.Delete(full);
                completed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                errors.Add($"{Path.GetFileName(raw)}: {ex.Message}");
            }
        }
        return new RecycleBatchResult(completed, errors);
    }

    public static void SetFileAttributes(string path, bool isReadOnly, bool isHidden)
    {
        string full = Path.GetFullPath(path);
        EnsureOrdinaryEntry(full);
        FileAttributes attrs = File.GetAttributes(full);
        if (isReadOnly) attrs |= FileAttributes.ReadOnly;
        else attrs &= ~FileAttributes.ReadOnly;

        if (isHidden) attrs |= FileAttributes.Hidden;
        else attrs &= ~FileAttributes.Hidden;

        File.SetAttributes(full, attrs);
    }

    public static FolderSizeSummary CalculateFolderSize(string folderPath, CancellationToken token, int maxItems = 50_000)
    {
        string full = Path.GetFullPath(folderPath);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("폴더를 찾을 수 없습니다.");
        long totalBytes = 0;
        int fileCount = 0;
        int folderCount = 0;
        bool truncated = false;
        int scanned = 0;

        var stack = new Stack<string>();
        stack.Push(full);
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string current = stack.Pop();
            try
            {
                foreach (FileSystemInfo info in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (++scanned > maxItems)
                    {
                        truncated = true;
                        return new FolderSizeSummary(totalBytes, fileCount, folderCount, truncated);
                    }
                    try
                    {
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (info is DirectoryInfo subDir)
                        {
                            if (subDir.Name.Equals(FileMemoStore.DirectoryName, StringComparison.OrdinalIgnoreCase)) continue;
                            folderCount++;
                            stack.Push(subDir.FullName);
                        }
                        else if (info is FileInfo file)
                        {
                            fileCount++;
                            totalBytes = checked(totalBytes + file.Length);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new FolderSizeSummary(totalBytes, fileCount, folderCount, truncated);
    }

    public static string ComputeSha256Hex(string filePath)
    {
        string full = Path.GetFullPath(filePath);
        EnsureOrdinaryEntry(full);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void OpenCmdPromptHere(string folderPath)
    {
        string full = Path.GetFullPath(folderPath);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("폴더를 찾을 수 없습니다.");
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = full,
            UseShellExecute = true
        });
    }

    public static void OpenInWindowsExplorer(string path)
    {
        string full = Path.GetFullPath(path);
        if (File.Exists(full))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = true });
        }
        else if (Directory.Exists(full))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{full}\"") { UseShellExecute = true });
        }
    }

    public static void OpenWithDialog(string filePath)
    {
        string full = Path.GetFullPath(filePath);
        if (!File.Exists(full)) throw new FileNotFoundException("파일을 찾을 수 없습니다.", full);
        Process.Start(new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments = $"shell32.dll,OpenAs_RunDLL {full}",
            UseShellExecute = false
        });
    }

    public static string ExpandAndResolveFolderPath(string rawInput, string? currentFolder = null)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            throw new ArgumentException("폴더 경로를 입력하세요.");
        string trimmed = rawInput.Trim().Trim('"');
        if (trimmed == "~" || trimmed.StartsWith(@"~", StringComparison.Ordinal) || trimmed.StartsWith("~/", StringComparison.Ordinal))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            trimmed = trimmed.Length == 1 ? home : Path.Combine(home, trimmed[2..]);
        }
        string expanded = Environment.ExpandEnvironmentVariables(trimmed);
        if (!Path.IsPathRooted(expanded) && !string.IsNullOrWhiteSpace(currentFolder))
        {
            expanded = Path.Combine(currentFolder, expanded);
        }
        return Path.GetFullPath(expanded);
    }

    public static string FormatDirectoryListingCsv(string folderPath, IReadOnlyList<Sunrise.FileManager.Models.FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# 폴더 경로: {folderPath}");
        sb.AppendLine("구분,이름,유형,크기(Bytes),크기(표시),수정한 날짜,만든 날짜,메모여부,전체 경로");
        foreach (var item in entries)
        {
            string kind = item.IsDirectory ? "폴더" : "파일";
            string safeName = "\"" + item.Name.Replace("\"", "\"\"") + "\"";
            string safePath = "\"" + item.FullPath.Replace("\"", "\"\"") + "\"";
            sb.AppendLine($"{kind},{safeName},{item.TypeName},{item.SizeBytes},{item.SizeDisplay},{item.ModifiedDisplay},{item.CreatedDisplay},{(item.HasMemo ? "Y" : "N")},{safePath}");
        }
        return sb.ToString();
    }

    public static string ExportDirectoryListingToFile(string folderPath, IReadOnlyList<Sunrise.FileManager.Models.FileEntry> entries, string? customFileName = null)
    {
        string parent = Path.GetFullPath(folderPath);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("폴더를 찾을 수 없습니다.");
        string baseName = string.IsNullOrWhiteSpace(customFileName)
            ? $"폴더목록_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            : ValidateName(customFileName);
        if (!baseName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
            !baseName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            baseName += ".csv";
        string target = Path.Combine(parent, SuggestNewFileName(parent, baseName));
        string csv = FormatDirectoryListingCsv(parent, entries);
        File.WriteAllText(target, csv, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return target;
    }
}
