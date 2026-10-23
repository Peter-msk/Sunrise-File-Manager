using System.IO;

namespace Sunrise.FileManager.Services;

public static partial class FileActionsService
{
    /// <summary>Two-phase file-only rename supports cycles and restores originals on failure.</summary>
    public static IReadOnlyList<(string OldPath, string NewPath)> BatchRenameFilesSafely(
        IReadOnlyList<(string SourcePath, string NewName)> operations)
    {
        if (operations.Count > 1000) throw new ArgumentException("한 번에 최대 1,000개 파일을 변경할 수 있습니다.");
        var pairs = operations.Select(op => (OldPath: Path.GetFullPath(op.SourcePath),
            NewPath: Path.Combine(Path.GetDirectoryName(Path.GetFullPath(op.SourcePath))!, ValidateName(op.NewName))))
            .Where(p => !string.Equals(p.OldPath, p.NewPath, StringComparison.Ordinal)).ToArray();
        var sources = pairs.Select(p => p.OldPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = pairs.Select(p => p.NewPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sources.Count != pairs.Length || targets.Count != pairs.Length) throw new IOException("원본 또는 변경 후 이름이 중복됩니다.");
        foreach (var pair in pairs)
        {
            EnsureOrdinaryEntry(pair.OldPath);
            if (!File.Exists(pair.OldPath)) throw new IOException("파일만 일괄 변경할 수 있습니다.");
            if (Path.GetFileName(pair.NewPath).Length > 255) throw new IOException("파일 이름이 255자를 초과합니다.");
            if ((File.Exists(pair.NewPath) || Directory.Exists(pair.NewPath)) && !sources.Contains(pair.NewPath))
                throw new IOException("대상에 같은 이름이 존재합니다: " + pair.NewPath);
        }
        string[] staged = pairs.Select(p => Path.Combine(Path.GetDirectoryName(p.OldPath)!, ".sunrise-eiass-" + Guid.NewGuid().ToString("N") + Path.GetExtension(p.OldPath))).ToArray();
        string[] current = pairs.Select(p => p.OldPath).ToArray();
        int published = 0;
        try
        {
            for (int i = 0; i < pairs.Length; i++) { MoveEntry(current[i], staged[i]); current[i] = staged[i]; }
            for (int i = 0; i < pairs.Length; i++) { MoveEntry(current[i], pairs[i].NewPath); current[i] = pairs[i].NewPath; published++; }
        }
        catch (Exception failure)
        {
            var recoveryErrors = new List<Exception>();
            // Vacate all published names before restoring originals (including name swaps).
            for (int i = 0; i < published; i++)
                try { MoveEntry(current[i], staged[i]); current[i] = staged[i]; }
                catch (Exception ex) { recoveryErrors.Add(ex); }
            for (int i = 0; i < pairs.Length; i++)
                if (!string.Equals(current[i], pairs[i].OldPath, StringComparison.Ordinal))
                    try { MoveEntry(current[i], pairs[i].OldPath); current[i] = pairs[i].OldPath; }
                    catch (Exception ex) { recoveryErrors.Add(new IOException("복구 경로: " + current[i] + " → " + pairs[i].OldPath, ex)); }
            if (recoveryErrors.Count > 0)
                throw new IOException("이름 변경과 일부 복구에 실패했습니다. 파일을 보존한 경로를 확인하세요:\n" +
                    string.Join("\n", current.Where((p, i) => p != pairs[i].OldPath)), new AggregateException(new[] { failure }.Concat(recoveryErrors)));
            throw new IOException("이름 변경에 실패하여 원래 이름으로 복구했습니다. " + failure.Message, failure);
        }
        return pairs;
    }
}
