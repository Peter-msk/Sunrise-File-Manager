using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Sunrise.FileManager.Services;

/// <summary>Read-only discovery of Windows ShellNew templates. Never execute registry commands.</summary>
public static class ShellNewTemplateService
{
    public sealed record Template(string Extension, string Title, string Kind, byte[]? Data, string? Source);
    private const int MaxTemplateBytes = 4 * 1024 * 1024;
    public static IReadOnlyList<Template> Discover()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var templates = new List<Template>();
        foreach (string extension in Registry.ClassesRoot.GetSubKeyNames())
        {
            if (!Regex.IsMatch(extension, @"^\.[a-zA-Z0-9]{1,15}$")) continue;
            using var registration = Registry.ClassesRoot.OpenSubKey(extension);
            using var shellNew = registration?.OpenSubKey("ShellNew");
            if (shellNew is null) continue;
            // Command/Handler operations may run code. Only inert byte templates are supported.
            if (shellNew.GetValue("Command") is not null || shellNew.GetValue("Handler") is not null) continue;
            string? kind = null, source = null;
            byte[]? data = null;
            if (shellNew.GetValue("FileName") is string fileName && !string.IsNullOrWhiteSpace(fileName))
            {
                // A file template is read from the OS template directory, never an arbitrary registry path.
                if (Path.GetFileName(fileName) != fileName) continue;
                string basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew");
                source = Path.Combine(basePath, fileName);
                if (!File.Exists(source) || ReparsePointPolicy.IsBlocked(source) || new FileInfo(source).Length > MaxTemplateBytes) continue;
                kind = "FileName";
            }
            else if (shellNew.GetValue("Data") is byte[] bytes && bytes.Length <= MaxTemplateBytes)
            { data = bytes; kind = "Data"; }
            else if (shellNew.GetValueNames().Contains("NullFile", StringComparer.OrdinalIgnoreCase)) kind = "NullFile";
            if (kind is null) continue;
            string title = extension.TrimStart('.').ToUpperInvariant() + " 문서 (" + extension + ")";
            templates.Add(new(extension, title, kind, data, source));
            if (templates.Count >= 80) break;
        }
        return templates.OrderBy(x => x.Extension, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static string Create(string folder, Template template)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows 파일 템플릿만 지원합니다.");
        FileActionsService.EnsureOrdinaryEntry(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        for (DirectoryInfo? parent = new DirectoryInfo(folder); parent is not null; parent = parent.Parent)
            if (ReparsePointPolicy.IsBlocked(parent.FullName, parent.Attributes))
                throw new IOException("연결·접합점 아래에는 Windows 문서를 생성하지 않습니다.");
        if (!Regex.IsMatch(template.Extension, @"^\.[a-zA-Z0-9]{1,15}$")) throw new IOException("템플릿 확장자가 올바르지 않습니다.");
        // Re-read registry before committing; stale or forged menu values must not write arbitrary bytes.
        var current = Discover().FirstOrDefault(x => x.Extension.Equals(template.Extension, StringComparison.OrdinalIgnoreCase));
        if (current is null || current.Kind != template.Kind || current.Source != template.Source)
            throw new IOException("Windows 파일 템플릿 설정이 변경되었습니다. 다시 선택하세요.");
        string name = FileActionsService.SuggestNewFileName(folder, "새 " + template.Extension.TrimStart('.') + " 문서" + template.Extension);
        string target = Path.Combine(folder, FileActionsService.ValidateName(name));
        using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            if (current.Kind == "Data") stream.Write(current.Data!);
            else if (current.Kind == "FileName")
            {
                if (current.Source is null || ReparsePointPolicy.IsBlocked(current.Source) || new FileInfo(current.Source).Length > MaxTemplateBytes)
                    throw new IOException("템플릿 파일이 안전하지 않습니다.");
                using var input = File.OpenRead(current.Source);
                input.CopyTo(stream);
            }
            stream.Flush(true);
            return target;
        }
        catch
        {
            stream.Dispose();
            // Only the just-created empty or partial output is rolled back; source remains untouched.
            File.Delete(target);
            throw;
        }
    }
}
