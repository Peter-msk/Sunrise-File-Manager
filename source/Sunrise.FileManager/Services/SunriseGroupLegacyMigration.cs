using System;
using System.IO;

namespace Sunrise.FileManager.Services;

/// <summary>
/// One-time NON-DESTRUCTIVE import of File Manager data from pre-group layouts.
/// Runs under the existing process mutex before session/credentials/control caches load.
/// Never follows junctions/reparse points and never overwrites an existing target.
/// Source data remains untouched; upgrades/uninstallers must never delete it implicitly.
/// </summary>
public static class SunriseGroupLegacyMigration
{
    private const long MaxOneFile = 256L * 1024 * 1024;
    private const long MaxTotal = 1024L * 1024 * 1024;
    private const int MaxFiles = 20000;
    private const string CompletedMarker = "migration-from-legacy-v1.complete";

    public static void ImportOnFirstRun()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local) || !Path.IsPathFullyQualified(local))
            throw new InvalidOperationException("사용자 로컬 AppData 경로를 확인할 수 없습니다.");
        string destination = SunriseGroupPaths.LocalRoot;
        string marker = Path.Combine(destination, CompletedMarker);
        // Do not follow an existing redirected group root.
        AssertNoReparse(Path.GetDirectoryName(destination)!);
        AssertNoReparse(destination);
        if (File.Exists(marker)) return;
        string[] sources =
        {
            Path.Combine(local, "Sunrise", "FileManager"),
            Path.Combine(local, "SunriseGroup", "SunriseFileManager")
        };
        long copiedBytes = 0;
        int visitedFiles = 0;
        foreach (string legacy in sources)
        {
            if (!Directory.Exists(legacy)) continue;
            AssertNoReparse(Path.GetDirectoryName(legacy)!);
            AssertNoReparse(legacy);
            CopyDirectory(legacy, destination, ref copiedBytes, ref visitedFiles);
        }
        // Marker is only an optimization: no migration means no data loss.
        Directory.CreateDirectory(destination);
        AssertNoReparse(destination);
        try
        {
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.WriteByte(1);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(marker)) { /* previously committed */ }
    }

#if SUNRISE_GROUP_LAYOUT_TEST
    // Dedicated GUID temp-root test only; not compiled into the WPF product.
    internal static void CopyLegacyRootForTest(string source, string destination)
    {
        long bytes = 0;
        int count = 0;
        CopyDirectory(source, destination, ref bytes, ref count);
    }
#endif

    private static void CopyDirectory(string source, string destination, ref long copiedBytes, ref int visitedFiles)
    {
        AssertNoReparse(source);
        if (Directory.Exists(destination)) AssertNoReparse(destination);
        else Directory.CreateDirectory(destination);
        foreach (string item in Directory.EnumerateFileSystemEntries(source))
        {
            FileAttributes attributes = File.GetAttributes(item);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("이전 데이터 폴더에 재분석 지점이 있어 자동 이전을 중단했습니다.");
            string name = Path.GetFileName(item);
            // Update payloads and encrypted work-in-progress stage data are transient;
            // never duplicate or resume them across version/layout boundaries.
            if (string.Equals(name, "Updates", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "EncryptedCloudStage", StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(destination, name);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (File.Exists(target))
                    throw new InvalidDataException("기존 파일과 이전 데이터 폴더의 이름이 충돌했습니다.");
                CopyDirectory(item, target, ref copiedBytes, ref visitedFiles);
                continue;
            }
            if (++visitedFiles > MaxFiles)
                throw new InvalidDataException("이전 사용자 데이터 항목이 허용 상한을 초과했습니다.");
            if (Directory.Exists(target))
                throw new InvalidDataException("기존 폴더와 이전 데이터 파일의 이름이 충돌했습니다.");
            if (File.Exists(target)) continue; // already migrated or newly written: never overwrite
            var info = new FileInfo(item);
            if (info.Length > MaxOneFile || copiedBytes > MaxTotal - info.Length)
                throw new InvalidDataException("대용량 이전 데이터는 수동 확인이 필요합니다. 원본 데이터는 보존됩니다.");
            CopyNewFileAtomically(item, target);
            copiedBytes += info.Length;
        }
    }

    private static void CopyNewFileAtomically(string source, string target)
    {
        string directory = Path.GetDirectoryName(target)!;
        AssertNoReparse(directory);
        string temporary = Path.Combine(directory, ".sunrise-import-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool hasTemporary = false;
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                hasTemporary = true;
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            if (!File.Exists(target)) File.Move(temporary, target); // no overwrite
            hasTemporary = false;
        }
        finally
        {
            if (hasTemporary && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void AssertNoReparse(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("AppData에 정션 또는 링크가 포함되어 있어 자동 이전을 중단했습니다.");
        }
    }
}
