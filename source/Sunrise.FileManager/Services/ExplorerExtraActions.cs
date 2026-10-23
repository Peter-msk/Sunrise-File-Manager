using System.IO;
using System.Runtime.InteropServices;

namespace Sunrise.FileManager.Services;

/// <summary>Small Explorer operations using the existing OS and BCL only.</summary>
public static class ExplorerExtraActions
{
    public sealed record ShortcutBatch(IReadOnlyList<string> Created, IReadOnlyList<string> Errors, bool Canceled);
    public static Task<ShortcutBatch> CreateShortcutsAsync(IReadOnlyList<string> targets, string folder, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows 바로가기 기능입니다.");
        if (targets.Count > 1000) throw new ArgumentException("바로가기는 한 번에 1,000개까지 만들 수 있습니다.");
        var completion = new TaskCompletionSource<ShortcutBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var created = new List<string>(); var errors = new List<string>();
            foreach (string target in targets)
            {
                if (token.IsCancellationRequested) break;
                try { created.Add(CreateShortcut(target, folder)); }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                { errors.Add(Path.GetFileName(target) + ": " + ex.Message); }
            }
            completion.TrySetResult(new(created, errors, token.IsCancellationRequested));
        }) { IsBackground = true, Name = "Sunrise shortcuts" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    public static FileAttributes AttributesAfter(FileAttributes current, FileAttributes flag, bool value)
    {
        if (flag is not (FileAttributes.Hidden or FileAttributes.ReadOnly)) throw new ArgumentException("지원하지 않는 속성입니다.");
        var updated = value ? current | flag : current & ~flag;
        updated &= ~FileAttributes.Normal;
        return updated == 0 ? FileAttributes.Normal : updated;
    }
    public static void SetAttribute(string path, FileAttributes flag, bool value)
    {
        FileActionsService.EnsureOrdinaryEntry(path);
        var attrs = File.GetAttributes(path);
        if (flag == FileAttributes.ReadOnly && (attrs & FileAttributes.Directory) != 0)
            throw new IOException("폴더의 읽기 전용 속성은 파일 쓰기를 제한하지 않습니다. 파일을 선택하세요.");
        File.SetAttributes(path, AttributesAfter(attrs, flag, value));
    }
    public static string CreateShortcut(string target, string folder)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows 바로가기 기능입니다.");
        FileActionsService.EnsureOrdinaryEntry(target);
        FileActionsService.EnsureOrdinaryEntry(folder);
        string name = FileActionsService.SuggestNewFileName(folder, Path.GetFileName(Path.TrimEndingDirectorySeparator(target)) + " - 바로 가기.lnk");
        string destination = Path.Combine(folder, name);
        string temporary = Path.Combine(folder, ".sunrise-shortcut-" + Guid.NewGuid().ToString("N") + ".lnk");
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            shortcut = ((dynamic)shell!).CreateShortcut(temporary);
            ((dynamic)shortcut).TargetPath = Path.GetFullPath(target);
            ((dynamic)shortcut).WorkingDirectory = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
            ((dynamic)shortcut).Save();
            if (!File.Exists(temporary)) throw new IOException("바로가기를 만들지 못했습니다.");
            File.Move(temporary, destination); return destination;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
