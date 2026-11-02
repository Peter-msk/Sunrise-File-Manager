using System.IO;

namespace Sunrise.FileManager.Services;

/// <summary>
/// Per-user storage boundary for this product. The installation directory is
/// intentionally independent of user data. Legacy files are never deleted.
/// </summary>
internal static class AppStoragePaths
{
    internal static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunrise Group", "Sunrise File Manager");

    internal static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunrise", "FileManager");

    // Run after acquiring the existing single-instance mutex, before reading
    // sessions, security caches, AI journals or memo integrity anchors.
    internal static void ImportLegacyOnce()
    {
        string oldRoot = LegacyRoot;
        if (!Directory.Exists(oldRoot)) return;
        if ((File.GetAttributes(oldRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("기존 사용자 데이터 폴더가 링크이므로 안전한 경로 이전을 중단합니다.");

        string newRoot = LocalRoot;
        string marker = Path.Combine(newRoot, ".legacy-import-complete");
        if (File.Exists(marker)) return;
        Directory.CreateDirectory(newRoot);
        if ((File.GetAttributes(newRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("새 사용자 데이터 폴더가 링크이므로 경로 이전을 중단합니다.");

        // Copy-only. New-path files win when both layouts already exist.
        // Interrupted copies are retried on the next startup, and legacy files
        // remain available for explicit rollback or investigation.
        CopyDirectory(oldRoot, newRoot, root: true);
        string temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, "Legacy AppData copied; source retained.\n");
            File.Move(temporary, marker);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void CopyDirectory(string source, string destination, bool root)
    {
        foreach (string item in Directory.EnumerateFileSystemEntries(source))
        {
            string name = Path.GetFileName(item);
            // Incomplete installer downloads are ephemeral; do not transfer
            // legacy helper jobs or carry obsolete update handoff state.
            if (root && name.Equals("Updates", StringComparison.OrdinalIgnoreCase)) continue;

            FileAttributes attributes = File.GetAttributes(item);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("연결된 경로를 포함한 사용자 데이터는 자동으로 복사하지 않습니다: " + name);

            string target = Path.Combine(destination, name);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (File.Exists(target))
                    throw new IOException("파일/폴더 이름 충돌: " + name);
                Directory.CreateDirectory(target);
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("대상 경로 링크 감지: " + name);
                CopyDirectory(item, target, root: false);
                continue;
            }
            if (File.Exists(target)) continue; // Never overwrite newer user data.
            if (Directory.Exists(target))
                throw new IOException("파일/폴더 이름 충돌: " + name);

            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(item, temporary);
                // Do not expose partial data if interrupted during copying.
                if (!File.Exists(target)) File.Move(temporary, target);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
