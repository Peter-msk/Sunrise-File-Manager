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
    public static IReadOnlyList<string> DuplicateEntries(IReadOnlyList<string> sourcePaths, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        var created = new List<string>();
        foreach (string raw in sourcePaths)
        {
            token.ThrowIfCancellationRequested();
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

            string staging = Path.Combine(parent, ".sunrise-duplicate-" + Guid.NewGuid().ToString("N"));
            try
            {
                int scanned = 0;
                if (isDir) CopyDirectoryRecursive(source, staging, token, ref scanned);
                else CopyDuplicateFile(source, staging, token);
                token.ThrowIfCancellationRequested();
                if (isDir) Directory.Move(staging, target);
                else File.Move(staging, target);
            }
            finally
            {
                if (File.Exists(staging))
                {
                    File.SetAttributes(staging, FileAttributes.Normal);
                    File.Delete(staging);
                }
                else if (Directory.Exists(staging))
                {
                    foreach (string file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(staging, true);
                }
            }
            created.Add(target);
        }
        return created;
    }

    private static void CopyDuplicateFile(string source, string target, CancellationToken token)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            input.CopyToAsync(output, 81920, token).GetAwaiter().GetResult();
        // Preserve File.Copy's metadata behavior while allowing cooperative cancellation.
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        File.SetAttributes(target, File.GetAttributes(source));
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir, CancellationToken token, ref int scanned)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(targetDir);
        foreach (string child in Directory.EnumerateFileSystemEntries(sourceDir))
        {
            token.ThrowIfCancellationRequested();
            if (++scanned > TransferService.MaxPlannedEntries) throw new IOException("사본 생성은 최대 50,000개 항목까지 지원합니다.");
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("연결·접합점이 포함된 폴더의 사본은 생성하지 않습니다.");
            string target = Path.Combine(targetDir, Path.GetFileName(child));
            if (Directory.Exists(child)) CopyDirectoryRecursive(child, target, token, ref scanned);
            else CopyDuplicateFile(child, target, token);
        }
    }

    public static string CompressToZip(IReadOnlyList<string> sourcePaths, string destinationZipPath, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        token.ThrowIfCancellationRequested();
        if (sourcePaths.Count == 0) throw new ArgumentException("압축할 항목을 선택하세요.");
        string zipFull = Path.GetFullPath(destinationZipPath);
        if (!zipFull.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            zipFull += ".zip";
        ValidateName(Path.GetFileName(zipFull));
        if (File.Exists(zipFull) || Directory.Exists(zipFull))
            throw new IOException("같은 이름의 압축 파일이 이미 존재합니다.");

        // Validate top-level names before creating any ZIP: two same-named entries or
        // selecting a folder together with its child creates an archive our extractor rejects.
        string[] selected = sourcePaths.Select(Path.GetFullPath).ToArray();
        var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedFolders = new HashSet<string>(selected.Where(Directory.Exists), StringComparer.OrdinalIgnoreCase);
        foreach (string source in selected)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            if (!rootNames.Add(name))
                throw new IOException("동일한 이름의 압축 항목이 포함되어 있습니다. 다른 이름으로 선택하세요.");
            for (string? parent = Path.GetDirectoryName(source); parent is not null; parent = Path.GetDirectoryName(parent))
            {
                if (selectedFolders.Contains(parent))
                    throw new IOException("상위 폴더와 그 하위 항목을 동시에 압축할 수 없습니다.");
                if (Path.GetDirectoryName(parent) == parent) break;
            }
            if (Directory.Exists(source) && (zipFull.StartsWith(Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || string.Equals(zipFull, source, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("압축할 폴더 내부에는 결과 ZIP을 저장할 수 없습니다.");
        }
        string tempZip = zipFull + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                int scanned = 0;
                foreach (string src in selected)
                {
                    token.ThrowIfCancellationRequested();
                    if (++scanned > 20_000) throw new IOException("ZIP은 최대 20,000개 항목까지 지원합니다.");
                    EnsureOrdinaryEntry(src);
                    if (Directory.Exists(src))
                    {
                        string rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(src));
                        AddDirectoryToZip(archive, src, rootName, token, ref scanned);
                    }
                    else
                    {
                        AddFileToZip(archive, src, Path.GetFileName(src), token);
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(tempZip, zipFull, overwrite: false);
            return zipFull;
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.TraceWarning("ZIP staging retained: {0}", ex.GetType().Name); }
            }
        }
    }

    private static void AddFileToZip(ZipArchive archive, string source, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = entry.Open();
        input.CopyToAsync(output, 81920, token).GetAwaiter().GetResult();
    }

    private static void AddDirectoryToZip(ZipArchive archive, string dirPath, string relativePrefix,
        CancellationToken token, ref int scanned)
    {
        token.ThrowIfCancellationRequested();
        if (++scanned > 20_000) throw new IOException("ZIP은 최대 20,000개 항목까지 지원합니다.");
        bool hasAny = false;
        foreach (string file in Directory.EnumerateFiles(dirPath))
        {
            token.ThrowIfCancellationRequested();
            if (++scanned > 20_000) throw new IOException("ZIP은 최대 20,000개 항목까지 지원합니다.");
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("ZIP에 연결 항목을 포함할 수 없습니다.");
            hasAny = true;
            string entryName = relativePrefix + "/" + Path.GetFileName(file);
            AddFileToZip(archive, file, entryName, token);
        }
        foreach (string sub in Directory.EnumerateDirectories(dirPath))
        {
            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) throw new IOException("ZIP에 연결 항목을 포함할 수 없습니다.");
            hasAny = true;
            AddDirectoryToZip(archive, sub, relativePrefix + "/" + Path.GetFileName(sub), token, ref scanned);
        }
        if (!hasAny)
        {
            archive.CreateEntry(relativePrefix + "/");
        }
    }

    public static string ExtractZip(string zipFilePath, string destinationFolder, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string srcZip = Path.GetFullPath(zipFilePath);
        EnsureOrdinaryEntry(srcZip);
        if (!File.Exists(srcZip) || !srcZip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("유효한 ZIP 압축 파일이 아닙니다.");
        string dest = Path.GetFullPath(destinationFolder);
        if (File.Exists(dest) || Directory.Exists(dest))
            throw new IOException("압축을 풀 대상 폴더 이름이 이미 존재합니다.");

        string staging = dest + ".sunrise-extract-" + Guid.NewGuid().ToString("N");
        bool committed = false;
        try
        {
            using (var archive = ZipFile.OpenRead(srcZip))
            {
                if (archive.Entries.Count is 0 or > 20_000)
                    throw new InvalidDataException("ZIP 내부 항목 수는 1~20,000개여야 합니다.");
                const long maxTotal = 8L * 1024 * 1024 * 1024;
                long expandedBytes = 0;
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var plan = new List<(ZipArchiveEntry Entry, string Relative, bool Directory)>();
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string relative = entry.FullName.Replace('\\', '/');
                    bool isDirectory = relative.EndsWith('/');
                    string[] parts = relative.TrimEnd('/').Split('/');
                    if (relative.StartsWith('/') || parts.Length == 0 || parts.Any(part => part is "" or "." or ".."))
                        throw new InvalidDataException("ZIP 내부 경로가 올바르지 않습니다.");
                    foreach (string part in parts) ValidateName(part);
                    relative = Path.Combine(parts);
                    int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (unixType is not (0 or 0x8000 or 0x4000) ||
                        (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || !paths.Add(relative))
                        throw new InvalidDataException("ZIP에 중복 경로 또는 지원하지 않는 연결 항목이 있습니다.");
                    if (entry.Length > maxTotal - expandedBytes)
                        throw new InvalidDataException("압축 해제 총 용량은 8GB를 초과할 수 없습니다.");
                    expandedBytes += entry.Length;
                    plan.Add((entry, relative, isDirectory));
                }
                // No destination is exposed until every entry is validated and extracted.
                Directory.CreateDirectory(staging);
                foreach (var (entry, relative, isDirectory) in plan)
                {
                    token.ThrowIfCancellationRequested();
                    string target = Path.Combine(staging, relative);
                    if (isDirectory) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var input = entry.Open();
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    byte[] buffer = new byte[81920];
                    long written = 0;
                    int count;
                    while ((count = input.Read(buffer)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        if (count > entry.Length - written) throw new InvalidDataException("ZIP의 선언된 용량을 초과했습니다.");
                        output.Write(buffer, 0, count);
                        written += count;
                    }
                    if (written != entry.Length) throw new InvalidDataException("ZIP 내부 파일이 잘렸습니다.");
                }
            }
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, dest);
            committed = true;
            return dest;
        }
        finally
        {
            if (!committed && Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.TraceWarning("ZIP extraction staging retained: {0}", ex.GetType().Name); }
            }
        }
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
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { truncated = true; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { truncated = true; }
        }
        return new FolderSizeSummary(totalBytes, fileCount, folderCount, truncated);
    }

    public static string ComputeSha256Hex(string filePath)
    {
        string full = Path.GetFullPath(filePath);
        EnsureOrdinaryEntry(full);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.SequentialScan);
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
