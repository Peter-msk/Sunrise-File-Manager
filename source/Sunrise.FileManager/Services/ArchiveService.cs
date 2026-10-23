using System.Formats.Tar;
using System.IO;
using SharpCompress.Readers;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;

namespace Sunrise.FileManager.Services;

/// <summary>Extracts ordinary files into private staging; archives never create links.</summary>
public static class ArchiveService
{
    public sealed class PasswordRequiredException() : IOException("암호가 필요한 압축 파일입니다.");
    public const long MaxExpandedBytes = 8L * 1024 * 1024 * 1024;
    public const int MaxEntries = 20_000;
    public sealed record Item(string Key, long Bytes, bool Directory, bool Encrypted);
    public static IReadOnlyList<Item> List(string path, CancellationToken token, string? password = null)
    {
        if (ReparsePointPolicy.IsBlocked(path)) throw new IOException("연결된 압축 파일은 열지 않습니다.");
        using var stream = File.OpenRead(path);
        var options = new ReaderOptions { Password = password, LeaveStreamOpen = true };
        using var archive = path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ? ArchiveFactory.OpenArchive(stream, options) : null;
        using var reader = archive is not null ? archive.ExtractAllEntries() : ReaderFactory.OpenReader(stream, options);
        var items = new List<Item>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.MoveToNextEntry())
        {
            token.ThrowIfCancellationRequested();
            if (items.Count == MaxEntries) throw new IOException("압축 항목 20,000개 제한을 초과했습니다.");
            var entry = reader.Entry;
            if (!string.IsNullOrEmpty(entry.LinkTarget)) throw new IOException("링크 항목이 포함된 압축 파일은 열지 않습니다.");
            string? key = NormalizeKey(entry.Key, entry.IsDirectory);
            if (key is null) continue;
            if (!names.Add(key)) throw new IOException("압축 파일에 중복 이름이 있습니다.");
            items.Add(new(key, entry.Size, entry.IsDirectory, entry.IsEncrypted));
        }
        return items;
    }
    public static bool CanExtract(string path) => new[] { ".zip", ".7z", ".rar", ".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tar.xz" }
        .Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    public static string Extract(string archivePath, string destination, CancellationToken token, string? password = null, IReadOnlySet<string>? selectedKeys = null)
    {
        string final = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(final) ?? throw new IOException("대상 폴더가 올바르지 않습니다.");
        if (!Directory.Exists(parent) || File.Exists(final) || Directory.Exists(final))
            throw new IOException("대상은 기존 폴더 아래의 새 폴더여야 합니다.");
        RejectLinkedAncestors(parent);
        if (ReparsePointPolicy.IsBlocked(archivePath)) throw new IOException("링크 압축 파일은 지원하지 않습니다.");
        string stage = Path.Combine(parent, ".sunrise-archive-" + Guid.NewGuid().ToString("N"));
        if (selectedKeys is { Count: 0 }) throw new ArgumentException("압축을 풀 파일을 선택하세요.");
        Directory.CreateDirectory(stage);
        try
        {
            using var stream = File.OpenRead(archivePath);
            var options = new ReaderOptions { Password = password, LeaveStreamOpen = true };
            using var archive = archivePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ? ArchiveFactory.OpenArchive(stream, options) : null;
            using var reader = archive is not null ? archive.ExtractAllEntries() : ReaderFactory.OpenReader(stream, options);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var extractedKeys = new HashSet<string>(StringComparer.Ordinal);
            long total = 0; int count = 0;
            byte[] buffer = new byte[256 * 1024];
            while (reader.MoveToNextEntry())
            {
                token.ThrowIfCancellationRequested();
                if (++count > MaxEntries) throw new IOException("압축 항목이 20,000개를 초과했습니다.");
                var entry = reader.Entry;
                if (entry.IsEncrypted && string.IsNullOrEmpty(password)) throw new PasswordRequiredException();
                if (!string.IsNullOrEmpty(entry.LinkTarget)) throw new IOException("압축 파일의 링크 항목은 해제하지 않습니다.");
                string? key = NormalizeKey(entry.Key, entry.IsDirectory);
                if (key is null) continue;
                string[] parts = key.Split('/');
                string output = Path.GetFullPath(Path.Combine(stage, Path.Combine(parts)));
                if (!output.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("압축 항목이 대상 폴더를 벗어납니다.");
                if (!names.Add(output)) throw new IOException("압축 파일에 중복 이름이 있습니다.");
                if (selectedKeys is not null && !selectedKeys.Contains(key)) continue;
                extractedKeys.Add(key);
                if (entry.IsDirectory) { Directory.CreateDirectory(output); continue; }
                if (entry.Size < 0 || entry.Size > MaxExpandedBytes - total) throw new IOException("압축 해제 크기 8GiB 제한을 초과했습니다.");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var input = reader.OpenEntryStream();
                using var writer = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long fileBytes = 0; int bytes;
                while ((bytes = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    total = checked(total + bytes); fileBytes = checked(fileBytes + bytes);
                    if (total > MaxExpandedBytes) throw new IOException("압축 해제 크기 8GiB 제한을 초과했습니다.");
                    writer.Write(buffer, 0, bytes);
                }
                if (entry.Size > 0 && fileBytes != entry.Size) throw new IOException("압축 항목이 불완전합니다.");
            }
            token.ThrowIfCancellationRequested();
            if (selectedKeys is not null && selectedKeys.Any(k => !extractedKeys.Contains(k))) throw new IOException("선택한 압축 항목이 없어졌거나 변경되었습니다.");
            Directory.Move(stage, final);
            return final;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    private static string? NormalizeKey(string? name, bool directory)
    {
        string raw = (name ?? "").Replace('\\', '/');
        if (raw.StartsWith('/')) throw new IOException("압축 항목의 절대 경로는 지원하지 않습니다.");
        string key = raw.TrimEnd('/');
        while (key.StartsWith("./", StringComparison.Ordinal)) key = key[2..];
        if (directory && key == ".") return null;
        string[] parts = key.Split('/');
        if (parts.Any(p => p.Length == 0)) throw new IOException("압축 항목 경로가 올바르지 않습니다.");
        foreach (string part in parts) FileActionsService.ValidateName(part);
        return key;
    }
    public static string CompressTar(IReadOnlyList<string> paths, string destination, CancellationToken token) => Compress(paths, destination, false, token);
    public static string CompressSevenZip(IReadOnlyList<string> paths, string destination, CancellationToken token) => Compress(paths, destination, true, token);
    private static string Compress(IReadOnlyList<string> paths, string destination, bool sevenZip, CancellationToken token)
    {
        string final = Path.GetFullPath(destination);
        if (File.Exists(final) || Directory.Exists(final)) throw new IOException("같은 이름의 항목이 이미 있습니다.");
        string parent = Path.GetDirectoryName(final)!;
        RejectLinkedAncestors(parent);
        string stage = Path.Combine(parent, ".sunrise-tar-" + Guid.NewGuid().ToString("N"));
        foreach (string path in paths)
        {
            string source = Path.GetFullPath(path);
            string relative = Path.GetRelativePath(source, final);
            if (Directory.Exists(source) && !Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new IOException("압축 결과는 선택한 폴더 내부에 만들 수 없습니다.");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        try
        {
            using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (IDisposable writer = sevenZip ? new SevenZipWriter(output, new SevenZipWriterOptions(CompressionType.LZMA2))
                : new TarWriter(output, TarEntryFormat.Pax, leaveOpen: false))
            {
                var pending = new Stack<(string Path, string Key)>(paths.Select(p => (Path.GetFullPath(p), Path.GetFileName(Path.TrimEndingDirectorySeparator(p)))));
                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var item = pending.Pop();
                    if (!names.Add(item.Key)) throw new IOException("압축할 항목의 이름이 중복됩니다.");
                    if (++count > MaxEntries) throw new IOException("압축 항목이 20,000개를 초과했습니다.");
                    if (ReparsePointPolicy.IsBlocked(item.Path)) throw new IOException("연결·접합점은 압축하지 않습니다.");
                    if (Directory.Exists(item.Path))
                    {
                        if (writer is TarWriter tar) tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, item.Key));
                        else ((SevenZipWriter)writer).WriteDirectory(item.Key, Directory.GetLastWriteTime(item.Path));
                        foreach (string child in Directory.EnumerateFileSystemEntries(item.Path))
                            pending.Push((child, item.Key + "/" + Path.GetFileName(child)));
                    }
                    else if (writer is TarWriter tar) tar.WriteEntryAsync(item.Path, item.Key, token).GetAwaiter().GetResult();
                    else
                    {
                        using var input = new CancelableReadStream(File.OpenRead(item.Path), token);
                        ((SevenZipWriter)writer).Write(item.Key, input, File.GetLastWriteTime(item.Path));
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(stage, final);
            return final;
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    private sealed class CancelableReadStream(Stream input, CancellationToken token) : Stream
    {
        public override bool CanRead => input.CanRead;
        public override bool CanSeek => input.CanSeek;
        public override bool CanWrite => false;
        public override long Length => input.Length;
        public override long Position { get => input.Position; set => input.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); return input.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { token.ThrowIfCancellationRequested(); return input.Read(buffer); }
        public override long Seek(long offset, SeekOrigin origin) => input.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
    private static void RejectLinkedAncestors(string path)
    {
        for (DirectoryInfo? folder = new DirectoryInfo(path); folder is not null; folder = folder.Parent)
            if (ReparsePointPolicy.IsBlocked(folder.FullName, folder.Attributes)) throw new IOException("링크 폴더 아래에는 압축 결과를 만들지 않습니다.");
    }
}
