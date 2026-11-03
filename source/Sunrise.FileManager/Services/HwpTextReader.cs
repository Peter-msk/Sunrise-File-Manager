using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Sunrise.FileManager.Services;

/// <summary>Read-only HWP 5 body records. Never invokes Hancom, executes scripts or renders pages.</summary>
internal static class HwpTextReader
{
    internal static bool Visit(string path, Func<string, bool> append, CancellationToken token)
    {
        using var compound = new CompoundReader(path, token);
        byte[] header = compound.Read("FileHeader");
        if (header.Length < 40 || Encoding.ASCII.GetString(header, 0, 17) != "HWP Document File" || header[35] != 5)
            throw new NotSupportedException("일반 HWP 5 문서만 본문 검색을 지원합니다.");
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(36));
        if ((flags & ((1u << 1) | (1u << 2) | (1u << 4) | (1u << 8) | (1u << 10))) != 0)
            throw new NotSupportedException("암호·배포용·DRM HWP는 본문 검색에서 제외됩니다.");
        long expanded = 0;
        var sections = compound.Paths.Where(p => p.StartsWith("BodyText/Section", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(p["BodyText/Section".Length..], out _)).OrderBy(p => p, NaturalNameComparer.Instance).ToArray();
        if (sections.Length == 0) throw new InvalidDataException("HWP 본문 section이 없습니다.");
        foreach (string section in sections)
        {
            token.ThrowIfCancellationRequested();
            using var raw = new MemoryStream(compound.Read(section), writable: false);
            using var inflater = (flags & 1) != 0 ? new DeflateStream(raw, CompressionMode.Decompress, true) : null;
            Stream input = inflater is null ? raw : inflater;
            long sectionBytes = 0;
            byte[] recordHeader = new byte[4];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int first = input.ReadByte();
                if (first == -1) break;
                recordHeader[0] = (byte)first; input.ReadExactly(recordHeader.AsSpan(1));
                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader);
                uint length = bits >> 20;
                sectionBytes += 4; expanded += 4;
                if (length == 0xFFF)
                { input.ReadExactly(recordHeader); length = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader); sectionBytes += 4; expanded += 4; }
                sectionBytes += length; expanded += length;
                if (sectionBytes > 64L * 1024 * 1024 || expanded > 256L * 1024 * 1024)
                    throw new DocumentReadLimitException("HWP 해제 본문 크기 제한으로 일부 미검색");
                if ((bits & 0x3FF) == 67) // HWPTAG_PARA_TEXT
                {
                    if ((length & 1) != 0) throw new InvalidDataException("HWP 문단 텍스트 길이 오류");
                    byte[] record = new byte[checked((int)length)]; input.ReadExactly(record);
                    var text = new StringBuilder();
                    for (int at = 0; at < record.Length;)
                    {
                        if ((at & 4095) == 0) token.ThrowIfCancellationRequested();
                        ushort code = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(at));
                        if (code is >= 1 and <= 9 or >= 11 and <= 12 or >= 14 and <= 23)
                        {
                            if (at + 16 > record.Length) throw new InvalidDataException("HWP 제어 문자 잘림");
                            if (code == 9) text.Append('\t');
                            at += 16;
                        }
                        else
                        {
                            at += 2;
                            if (code >= 32) text.Append((char)code);
                            else if (code is 10 or 13) text.Append('\n');
                            else if (code is 30 or 31) text.Append(' ');
                            else if (code == 24) text.Append('-');
                        }
                        if (text.Length >= 4096) { if (append(text.ToString())) return true; text.Clear(); }
                    }
                    if (append(text.ToString()) || append("\n")) return true;
                }
                else
                {
                    byte[] skip = new byte[8192];
                    while (length > 0)
                    { token.ThrowIfCancellationRequested(); int count = (int)Math.Min((uint)skip.Length, length); input.ReadExactly(skip.AsSpan(0, count)); length -= (uint)count; }
                }
            }
        }
        return false;
    }

    // Minimal bounded MS-CFB reader, including v3/v4 FAT, DIFAT and mini-stream allocation.
    // Only directory names and requested streams are read. OLE objects are never activated.
    private sealed class CompoundReader : IDisposable
    {
        private const uint End = 0xFFFFFFFE, Free = 0xFFFFFFFF;
        private readonly FileStream _file;
        private readonly CancellationToken _token;
        private readonly int _sector, _sectorCount;
        private readonly uint[] _fat;
        private uint[] _miniFat = [];
        private byte[] _mini = [];
        private readonly Dictionary<string, Entry> _streams = new(StringComparer.OrdinalIgnoreCase);
        private sealed record Entry(string Name, byte Type, uint Left, uint Right, uint Child, uint Start, long Length);
        public IEnumerable<string> Paths => _streams.Keys;
        public CompoundReader(string path, CancellationToken token)
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _token = token;
            try
            {
                byte[] h = new byte[512]; _file.ReadExactly(h);
                if (!h.AsSpan(0, 8).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
                    throw new NotSupportedException("HWP 5 복합 문서 형식이 아닙니다.");
                ushort major = U16(h, 26), shift = U16(h, 30);
                if (U16(h, 28) != 0xFFFE || U16(h, 32) != 6 || U32(h, 56) != 4096 ||
                    !((major == 3 && shift == 9) || (major == 4 && shift == 12))) throw Bad();
                _sector = 1 << shift;
                if (_file.Length % _sector != 0 || _file.Length > DocumentContentSearch.MaxDocumentBytes) throw Bad();
                _sectorCount = checked((int)(_file.Length / _sector - 1));
                uint fatCount = U32(h, 44), difatCount = U32(h, 72);
                if (fatCount == 0 || fatCount > _sectorCount || difatCount > _sectorCount) throw Bad();
                var locations = new List<uint>();
                for (int i = 0; i < 109; i++) { uint id = U32(h, 76 + i * 4); if (id != Free) locations.Add(id); }
                uint next = U32(h, 68); var seenDifat = new HashSet<uint>();
                for (int i = 0; i < difatCount; i++)
                {
                    if (!seenDifat.Add(next)) throw Bad();
                    byte[] block = Sector(next);
                    for (int at = 0; at < _sector - 4; at += 4) { uint id = U32(block, at); if (id != Free) locations.Add(id); }
                    next = U32(block, _sector - 4);
                }
                if (locations.Count != fatCount || locations.Distinct().Count() != locations.Count) throw Bad();
                _fat = locations.SelectMany(id => Words(Sector(id))).ToArray();
                byte[] directory = Chain(U32(h, 48), null, 4 * 1024 * 1024);
                var entries = new List<Entry>();
                for (int at = 0; at + 128 <= directory.Length; at += 128)
                {
                    int nameBytes = U16(directory, at + 64); byte type = directory[at + 66];
                    if (type != 0 && (nameBytes is < 2 or > 64 || nameBytes % 2 != 0)) throw Bad();
                    ulong length = BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(at + 120));
                    if (length > long.MaxValue) throw Bad();
                    entries.Add(new(type == 0 ? "" : Encoding.Unicode.GetString(directory, at, nameBytes - 2), type,
                        U32(directory, at + 68), U32(directory, at + 72), U32(directory, at + 76), U32(directory, at + 116), (long)length));
                }
                if (entries.Count == 0 || entries[0].Type != 5) throw Bad();
                var pending = new Stack<(uint Id, string Prefix, int Depth)>();
                pending.Push((entries[0].Child, "", 0)); var visited = new HashSet<uint>();
                while (pending.Count > 0)
                {
                    _token.ThrowIfCancellationRequested(); var item = pending.Pop();
                    if (item.Id == Free) continue;
                    if (item.Id >= entries.Count || !visited.Add(item.Id) || item.Depth > 64) throw Bad();
                    var entry = entries[(int)item.Id];
                    if (entry.Type is not (1 or 2) || entry.Name.Length == 0 || entry.Name.Contains('/')) throw Bad();
                    pending.Push((entry.Left, item.Prefix, item.Depth)); pending.Push((entry.Right, item.Prefix, item.Depth));
                    string full = item.Prefix + entry.Name;
                    if (entry.Type == 1) pending.Push((entry.Child, full + "/", item.Depth + 1));
                    else if (!_streams.TryAdd(full, entry)) throw Bad();
                }
                uint miniCount = U32(h, 64);
                if (miniCount > _sectorCount) throw Bad();
                if (miniCount > 0)
                {
                    _miniFat = Words(Chain(U32(h, 60), checked((long)miniCount * _sector), 16 * 1024 * 1024)).ToArray();
                    _mini = Chain(entries[0].Start, entries[0].Length, 64 * 1024 * 1024);
                }
            }
            catch { _file.Dispose(); throw; }
        }
        public byte[] Read(string name)
        {
            if (!_streams.TryGetValue(name, out var entry)) throw new InvalidDataException("HWP 필수 스트림이 없습니다: " + name);
            if (entry.Length >= 4096) return Chain(entry.Start, entry.Length, 64 * 1024 * 1024);
            byte[] result = new byte[checked((int)entry.Length)];
            uint next = entry.Start; int copied = 0; var seen = new HashSet<uint>();
            while (copied < result.Length)
            {
                _token.ThrowIfCancellationRequested();
                if (next >= _miniFat.Length || !seen.Add(next) || (long)next * 64 + 64 > _mini.Length) throw Bad();
                int count = Math.Min(64, result.Length - copied);
                _mini.AsSpan(checked((int)next * 64), count).CopyTo(result.AsSpan(copied));
                copied += count; next = _miniFat[next];
            }
            if (result.Length > 0 && next != End) throw Bad();
            return result;
        }
        private byte[] Chain(uint next, long? length, int limit)
        {
            if (length > limit) throw new DocumentReadLimitException("HWP 스트림 크기 제한");
            using var output = new MemoryStream(); var seen = new HashSet<uint>();
            while (next != End)
            {
                _token.ThrowIfCancellationRequested();
                if (next >= _fat.Length || !seen.Add(next)) throw Bad();
                byte[] block = Sector(next);
                if (output.Length + block.Length > limit) throw new DocumentReadLimitException("HWP 스트림 크기 제한");
                output.Write(block); next = _fat[next];
                if (length.HasValue && output.Length >= length.Value && next != End) throw Bad();
            }
            if (length.HasValue)
            {
                if (output.Length < length.Value || output.Length - length.Value >= _sector) throw Bad();
                output.SetLength(length.Value);
            }
            return output.ToArray();
        }
        private byte[] Sector(uint id)
        {
            _token.ThrowIfCancellationRequested();
            if (id >= _sectorCount) throw Bad();
            _file.Position = ((long)id + 1) * _sector;
            byte[] data = new byte[_sector]; _file.ReadExactly(data); return data;
        }
        private static IEnumerable<uint> Words(byte[] bytes) { for (int at = 0; at < bytes.Length; at += 4) yield return U32(bytes, at); }
        private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
        private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
        private static InvalidDataException Bad() => new("HWP 복합 문서 구조가 손상되었거나 지원되지 않습니다.");
        public void Dispose() => _file.Dispose();
    }
}
