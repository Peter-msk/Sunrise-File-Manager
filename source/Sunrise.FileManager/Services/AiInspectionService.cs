using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sunrise.FileManager.Services;

public sealed record AiWorkLimits(int MaxFiles = 10_000, long MaxBytes = 2L * 1024 * 1024 * 1024, int MaxRemoteCallsPerDay = 50)
{
    public void Validate()
    {
        if (MaxFiles is < 1 or > 50_000 || MaxBytes is < 1 or > 8L * 1024 * 1024 * 1024 || MaxRemoteCallsPerDay is < 1 or > 200)
            throw new ArgumentException("파일 수는 1~50,000개, 읽기 용량은 1바이트~8GB, 일일 호출은 1~200회 범위여야 합니다.");
    }
}
public sealed record AiInspectionProgress(int Discovered, int Hashed, long BytesRead, string Current, string Phase, int Candidates = 0, int Processed = 0);
public sealed record AiDuplicateRow(int Group, string Path, long Bytes, string Sha256, int Copies);
public sealed record AiDuplicateReport(IReadOnlyList<AiDuplicateRow> Rows, int Discovered, int Hashed,
    long BytesRead, int LinksSkipped, int ErrorCount, IReadOnlyList<string> Errors, bool Limited, bool Canceled)
{
    public int Groups => Rows.Select(x => x.Group).Distinct().Count();
}

/// <summary>Read-only, size-first, streaming SHA-256. Links and inaccessible items are reported, never bypassed.</summary>
public static class AiInspectionService
{
    private sealed record Candidate(string Path, long Length, DateTime Modified, DateTime Created);
    private static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or
        NotSupportedException or ArgumentException or System.Security.SecurityException;
    public static Task<AiDuplicateReport> FindDuplicatesAsync(IReadOnlyList<string> selection, bool recursive,
        AiWorkLimits limits, IProgress<AiInspectionProgress>? progress, CancellationToken token) =>
        Task.Run(() => Find(selection, recursive, limits, progress, token));

    private static AiDuplicateReport Find(IReadOnlyList<string> selection, bool recursive, AiWorkLimits limits,
        IProgress<AiInspectionProgress>? progress, CancellationToken token)
    {
        limits.Validate();
        if (selection.Count is < 1 or > 1000) throw new ArgumentException("1~1,000개 파일·폴더를 명시적으로 선택하세요.");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var visited = new HashSet<string>(comparer); var files = new List<Candidate>();
        var folders = new Stack<(string Path, bool Traverse)>(); var errors = new List<string>();
        int links = 0, errorCount = 0, examined = 0, hashed = 0, processed = 0; long bytesRead = 0;
        bool limited = false, canceled = false;
        void Error(string path, Exception ex)
        { errorCount++; if (errors.Count < 200) errors.Add(path + ": " + ex.GetType().Name); }
        void Add(string path, bool descend)
        {
            token.ThrowIfCancellationRequested();
            string full = Path.GetFullPath(path);
            if (!visited.Add(full)) return;
            if (++examined > 50_000) { limited = true; return; }
            try
            {
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) { links++; return; }
                if (Directory.Exists(full)) folders.Push((full, descend));
                else
                {
                    if (files.Count == limits.MaxFiles) { limited = true; return; }
                    var info = new FileInfo(full);
                    files.Add(new(full, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc));
                }
            }
            catch (Exception ex) when (Expected(ex)) { Error(full, ex); }
        }
        var hashedFiles = new List<(Candidate File, string Hash)>();
        try
        {
            foreach (string root in selection)
            {
                token.ThrowIfCancellationRequested();
                try { Add(AiPlanService.OrdinaryPath(root), recursive); }
                catch (Exception ex) when (Expected(ex)) { Error(root, ex); }
                if (limited) break;
            }
            while (folders.Count > 0 && !limited)
            {
                token.ThrowIfCancellationRequested();
                var folder = folders.Pop();
                progress?.Report(new(files.Count, hashed, bytesRead, folder.Path, "목록 수집"));
                try
                {
                    // The explicitly selected folder is always listed; recursion controls child folders.
                    foreach (string child in Directory.EnumerateFileSystemEntries(folder.Path))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!folder.Traverse && Directory.Exists(child))
                        {
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) links++;
                            if (++examined > 50_000) { limited = true; break; }
                            continue;
                        }
                        Add(child, folder.Traverse);
                        if (examined % 256 == 0) progress?.Report(new(files.Count, hashed, bytesRead, folder.Path, "목록 수집"));
                        if (limited) break;
                    }
                }
                catch (Exception ex) when (Expected(ex)) { Error(folder.Path, ex); }
            }
            // A singleton size cannot have an equal-content partner. Never read it.
            var candidates = files.GroupBy(x => x.Length).Where(x => x.Count() > 1)
                .SelectMany(x => x).OrderBy(x => x.Path, comparer).ToArray();
            byte[] buffer = new byte[256 * 1024];
            foreach (var file in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (file.Length > limits.MaxBytes - bytesRead) { limited = true; break; }
                progress?.Report(new(files.Count, hashed, bytesRead, file.Path, "SHA-256 검사", candidates.Length, processed));
                bool attemptFinished = false;
                try
                {
                    AiPlanService.OrdinaryPath(file.Path);
                    using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        256 * 1024, FileOptions.SequentialScan);
                    if (stream.Length != file.Length) throw new IOException("검사 전 크기 변경");
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    int count; long readForFile = 0;
                    while (readForFile < file.Length)
                    {
                        token.ThrowIfCancellationRequested();
                        count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, file.Length - readForFile));
                        if (count == 0) break;
                        bytesRead = checked(bytesRead + count); readForFile += count;
                        if (bytesRead > limits.MaxBytes || readForFile > file.Length) throw new IOException("검사 중 파일 크기 변경");
                        hash.AppendData(buffer.AsSpan(0, count));
                        progress?.Report(new(files.Count, hashed, bytesRead, file.Path, "SHA-256 검사", candidates.Length, processed));
                    }
                    if (readForFile != file.Length || stream.Length != file.Length || !Unchanged(file)) throw new IOException("검사 중 원본 변경");
                    hashedFiles.Add((file, Convert.ToHexString(hash.GetHashAndReset()))); hashed++; attemptFinished = true;
                }
                catch (Exception ex) when (Expected(ex)) { Error(file.Path, ex); attemptFinished = true; }
                finally
                {
                    if (attemptFinished) processed++;
                    progress?.Report(new(files.Count, hashed, bytesRead, file.Path, "SHA-256 검사", candidates.Length, processed));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; }
        // Exclude files changed between hashing and result creation. No files are automatically selected for deletion.
        var stable = new List<(Candidate File, string Hash)>();
        foreach (var item in hashedFiles)
        {
            try { if (!Unchanged(item.File)) throw new IOException("검사 후 원본 변경"); stable.Add(item); }
            catch (Exception ex) when (Expected(ex)) { Error(item.File.Path, ex); }
        }
        var rows = new List<AiDuplicateRow>(); int group = 0;
        foreach (var matches in stable.GroupBy(x => (x.File.Length, x.Hash)).Where(x => x.Count() > 1).OrderBy(x => x.Key.Length).ThenBy(x => x.Key.Hash, StringComparer.Ordinal))
        {
            group++; int copies = matches.Count();
            foreach (var item in matches.OrderBy(x => x.File.Path, comparer)) rows.Add(new(group, item.File.Path, item.File.Length, item.Hash, copies));
        }
        return new(rows.AsReadOnly(), files.Count, hashed, bytesRead, links, errorCount, errors.AsReadOnly(), limited, canceled);
    }
    private static bool Unchanged(Candidate file)
    {
        var now = new FileInfo(file.Path);
        return now.Exists && (now.Attributes & FileAttributes.ReparsePoint) == 0 && now.Length == file.Length &&
            now.LastWriteTimeUtc == file.Modified && now.CreationTimeUtc == file.Created;
    }
}

public static class AiInspectionSettings
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunrise", "FileManager", "inspection-limits.json");
    public static AiWorkLimits Load(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(FilePath)) return new();
            if (new FileInfo(FilePath).Length > 4096) throw new InvalidDataException("검사 설정 크기 초과");
            var limits = JsonSerializer.Deserialize<AiWorkLimits>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("빈 검사 설정");
            limits.Validate(); return limits;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { warning = "검사 상한 설정을 읽지 못해 기본값을 사용합니다: " + ex.GetType().Name; return new(); }
    }
    public static void Save(AiWorkLimits limits)
    {
        limits.Validate(); string path = FilePath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(limits)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
