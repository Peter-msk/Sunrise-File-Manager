using System;
using System.IO;

namespace Sunrise.FileManager.Services;

/// <summary>
/// Sunrise Group product-scoped directory contract for installed and portable editions.
/// Do not move user data alongside a portable executable or under Program Files.
/// Third-party products (e.g. Ollama) keep their vendor-owned directories.
/// </summary>
public static class SunriseGroupPaths
{
    public const string GroupFolder = "Sunrise Group";
    public const string ProductFolder = "Sunrise File Manager";

    public static string LocalRoot => Root(Environment.SpecialFolder.LocalApplicationData);
    public static string RoamingRoot => Root(Environment.SpecialFolder.ApplicationData);

    public static string Local(params string[] parts) => Combine(LocalRoot, parts);
    public static string Roaming(params string[] parts) => Combine(RoamingRoot, parts);

    private static string Root(Environment.SpecialFolder folder)
    {
        string value = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException("사용자 AppData 경로를 확인할 수 없습니다.");
        return Path.Combine(Path.GetFullPath(value), GroupFolder, ProductFolder);
    }

    private static string Combine(string root, string[] parts)
    {
        string path = root;
        foreach (string part in parts)
        {
            if (string.IsNullOrWhiteSpace(part) || part == "." || part == ".." ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                part.Contains('/') || part.Contains('\\') || Path.IsPathRooted(part))
                throw new ArgumentException("제품 AppData의 하위 경로는 안전한 단일 이름이어야 합니다.", nameof(parts));
            path = Path.Combine(path, part);
        }
        return path;
    }
}
