using System.IO;
using System.Text.Json;

namespace Sunrise.FileManager.Services;

/// <summary>Small crash-recovery plans, not a replay of unverified filesystem writes.</summary>
public static class TransferRecoveryJournal
{
    public sealed record Source(string Path, bool Directory, long Size, DateTime LastWriteUtc);
    public sealed class Plan
    {
        public string Id { get; set; } = "";
        public DateTime StartedUtc { get; set; }
        public TransferKind Kind { get; set; }
        public string Destination { get; set; } = "";
        public List<Source> Sources { get; set; } = [];
    }
    private static readonly object Gate = new();
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sunrise", "FileManager", "transfer-recovery");
    public static Plan Begin(IReadOnlyList<string> sources, string destination, TransferKind kind)
        => BeginAt(sources, destination, kind, Root);
    internal static Plan BeginAt(IReadOnlyList<string> sources, string destination, TransferKind kind, string directory)
    {
        if (sources.Count is < 1 or > 1000) throw new IOException("복구 기록은 최상위 1,000개 항목 이하에서 지원합니다.");
        if (!Directory.Exists(destination) || ReparsePointPolicy.IsBlocked(destination)) throw new IOException("대상 폴더를 확인할 수 없습니다.");
        var plan = new Plan { Id = Guid.NewGuid().ToString("N"), Kind = kind, Destination = Path.GetFullPath(destination), StartedUtc = DateTime.UtcNow };
        foreach (string path in sources)
        {
            FileActionsService.EnsureOrdinaryEntry(path);
            bool isDirectory = Directory.Exists(path);
            plan.Sources.Add(new Source(Path.GetFullPath(path), isDirectory, isDirectory ? 0 : new FileInfo(path).Length,
                isDirectory ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path)));
        }
        lock (Gate)
        {
            Directory.CreateDirectory(directory);
            // Do not silently discard older incomplete jobs; limit new journal growth.
            if (Directory.GetFiles(directory, "transfer-*.json").Length >= 20)
                throw new IOException("복구 기록이 20개 있습니다. '전송 복구 기록 확인'에서 이전 작업을 먼저 검토하세요.");
            string destinationPath = Path.Combine(directory, "transfer-" + plan.Id + ".json");
            string temp = destinationPath + ".tmp";
            try
            {
                using (var writer = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    JsonSerializer.Serialize(writer, plan);
                File.Move(temp, destinationPath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return plan;
    }
    public static void Finish(Plan plan, TransferResult result)
    {
        if (result.Failed == 0 && result.Skipped == 0 && !result.Canceled && result.Completed == plan.Sources.Count)
            Discard(plan);
        // Any partial/cancel/uncertain outcome stays for read-only manual recovery review.
    }
    public static void Discard(Plan plan) => DiscardAt(plan, Root);
    internal static void DiscardAt(Plan plan, string directory)
    {
        if (plan.Id.Length != 32 || !plan.Id.All(Uri.IsHexDigit)) throw new IOException("복구 기록 ID가 잘못되었습니다.");
        string path = Path.Combine(directory, "transfer-" + plan.Id + ".json");
        if (File.Exists(path)) File.Delete(path);
    }
    public static IReadOnlyList<Plan> Pending() => PendingAt(Root);
    internal static IReadOnlyList<Plan> PendingAt(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<Plan>();
        foreach (var path in Directory.GetFiles(directory, "transfer-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(20))
        {
            try
            {
                if (new FileInfo(path).Length > 512 * 1024) continue;
                var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(path));
                if (plan is null || string.IsNullOrEmpty(plan.Id) || plan.Id.Length != 32 || !plan.Id.All(Uri.IsHexDigit) ||
                    !Path.GetFileName(path).Equals("transfer-" + plan.Id + ".json", StringComparison.OrdinalIgnoreCase) ||
                    plan.Kind is not (TransferKind.Copy or TransferKind.Move) ||
                    plan.StartedUtc == default || plan.StartedUtc > DateTime.UtcNow.AddMinutes(5) ||
                    plan.Sources is null || plan.Sources.Count is < 1 or > 1000 ||
                    string.IsNullOrWhiteSpace(plan.Destination) || plan.Destination.Length > 32767 ||
                    !Path.IsPathFullyQualified(plan.Destination) || plan.Destination.Contains('\0') ||
                    plan.Sources.Any(s => s is null || string.IsNullOrWhiteSpace(s.Path) || s.Path.Length > 32767 ||
                        s.Path.Contains('\0') || !Path.IsPathFullyQualified(s.Path) || s.Size < 0)) continue;
                result.Add(plan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return result;
    }
    public static IReadOnlyList<string> EligibleCopySources(Plan plan)
    {
        // Directory contents can change without folder LastWriteUtc reliably changing.
        // Never auto-resume moves, directories, or changed sources after a crash.
        if (plan.Kind != TransferKind.Copy || !Directory.Exists(plan.Destination) || HasBlockedAncestor(plan.Destination)) return [];
        var eligible = new List<string>();
        foreach (Source source in plan.Sources)
        {
            if (source.Directory || !File.Exists(source.Path) || HasBlockedAncestor(source.Path)) continue;
            var info = new FileInfo(source.Path);
            if (info.Length != source.Size || info.LastWriteTimeUtc != source.LastWriteUtc) continue;
            string target = Path.Combine(plan.Destination, Path.GetFileName(source.Path));
            if (!File.Exists(target) && !Directory.Exists(target) && !eligible.Contains(source.Path, StringComparer.OrdinalIgnoreCase))
                eligible.Add(source.Path);
        }
        return eligible;
    }
    private static bool HasBlockedAncestor(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); )
        {
            if ((File.Exists(current) || Directory.Exists(current)) && ReparsePointPolicy.IsBlocked(current)) return true;
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        return false;
    }
}
