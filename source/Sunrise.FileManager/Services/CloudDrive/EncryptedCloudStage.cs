using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>
/// R19: single-use, per-transfer encrypted disk staging for cloud-to-cloud copies.
/// Plaintext exists only in bounded memory buffers and HTTPS transfer streams.
/// The AES-256-GCM key is random and never written to disk; orphaned ciphertext is
/// intentionally unrecoverable after a process crash. This is not a resumable journal.
/// </summary>
internal sealed class EncryptedCloudStage : IDisposable
{
    private const int BlockSize = 128 * 1024;
    private const int TagSize = 16;
    private const int HeaderSize = 24;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SFMSTG19");
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _prefix = RandomNumberGenerator.GetBytes(8);
    private readonly string _folder;
    private readonly string _file;
    private long _length = -1;
    private bool _disposed;

    private EncryptedCloudStage(string folder)
    {
        _folder = folder;
        _file = Path.Combine(folder, "cloud-stage.sfcstage");
    }

    public string CiphertextPath => _file; // diagnostics may inspect ciphertext only, never disclose tokens.
    public long Length => _length < 0 ? throw new InvalidOperationException("전송 준비 중입니다.") : _length;

    public static EncryptedCloudStage Create(long expectedBytes)
    {
        string owned = Sunrise.FileManager.Services.SunriseGroupPaths.Local("EncryptedCloudStage", Guid.NewGuid().ToString("N"));
        if (expectedBytes < 0 || expectedBytes > long.MaxValue - HeaderSize)
            throw new ArgumentOutOfRangeException(nameof(expectedBytes));
        long overhead = (expectedBytes / BlockSize + 1) * TagSize + HeaderSize;
        var drive = new DriveInfo(Path.GetPathRoot(owned)!);
        // Reserve working space. No plaintext staging or silent disk fill.
        if (expectedBytes > drive.AvailableFreeSpace - overhead - 16L * 1024 * 1024)
            throw new IOException("암호화 스테이징에 필요한 저장 공간이 부족합니다.");
        Directory.CreateDirectory(owned);
        return new EncryptedCloudStage(owned);
    }

    private byte[] Header(long length)
    {
        byte[] header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8, 8), length);
        _prefix.CopyTo(header, 16);
        return header;
    }

    private byte[] Nonce(uint index)
    {
        byte[] nonce = new byte[12];
        _prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8, 4), index);
        return nonce;
    }

    private static byte[] AAD(byte[] header, uint index)
    {
        byte[] aad = new byte[HeaderSize + 4];
        header.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(HeaderSize, 4), index);
        return aad;
    }

    public async Task CaptureAsync(Stream source, long expected, IProgress<CloudTransferProgress>? progress, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_length != -1) throw new InvalidOperationException("암호화 스테이지는 한 번만 기록할 수 있습니다.");
        if (expected < 0 || (expected > 0 && (expected - 1) / BlockSize >= uint.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(expected));
        byte[] header = Header(expected);
        byte[] clear = new byte[BlockSize];
        byte[] cipher = new byte[BlockSize];
        byte[] tag = new byte[TagSize];
        long total = 0;
        uint index = 0;
        try
        {
            await using var output = new FileStream(_file, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BlockSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await output.WriteAsync(header, ct).ConfigureAwait(false);
            using var aes = new AesGcm(_key, TagSize);
            while (total < expected)
            {
                ct.ThrowIfCancellationRequested();
                int count = (int)Math.Min(BlockSize, expected - total);
                await source.ReadExactlyAsync(clear.AsMemory(0, count), ct).ConfigureAwait(false);
                aes.Encrypt(Nonce(index), clear.AsSpan(0, count), cipher.AsSpan(0, count), tag, AAD(header, index));
                await output.WriteAsync(cipher.AsMemory(0, count), ct).ConfigureAwait(false);
                await output.WriteAsync(tag, ct).ConfigureAwait(false);
                total += count;
                index++;
                progress?.Report(new(total, expected));
            }
            if (await source.ReadAsync(clear.AsMemory(0, 1), ct).ConfigureAwait(false) != 0)
                throw new IOException("원격 파일이 예상 크기보다 큽니다.");
            await output.FlushAsync(ct).ConfigureAwait(false);
            _length = expected;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(cipher);
            if (_length < 0)
                try { File.Delete(_file); } catch (IOException) { /* owner Dispose retries */ }
        }
    }

    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = Length;
        return new DecryptedReader(this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
        try { if (File.Exists(_file)) File.Delete(_file); }
        catch (IOException) { /* ciphertext alone has no persisted key */ }
        catch (UnauthorizedAccessException) { /* ciphertext alone has no persisted key */ }
        try { Directory.Delete(_folder); }
        catch (IOException) { /* orphan ciphertext is not plaintext */ }
        catch (UnauthorizedAccessException) { /* orphan ciphertext is not plaintext */ }
    }

    private sealed class DecryptedReader : Stream
    {
        private readonly EncryptedCloudStage _owner;
        private readonly FileStream _input;
        private readonly byte[] _header;
        private byte[] _cache = [];
        private long _cachedBlock = -1;
        private long _position;

        public DecryptedReader(EncryptedCloudStage owner)
        {
            _owner = owner;
            _input = new FileStream(owner._file, FileMode.Open, FileAccess.Read, FileShare.Read,
                BlockSize, FileOptions.Asynchronous | FileOptions.RandomAccess);
            _header = owner.Header(owner.Length);
            Span<byte> existingHeader = stackalloc byte[HeaderSize];
            try
            {
                _input.ReadExactly(existingHeader);
                if (!CryptographicOperations.FixedTimeEquals(existingHeader, _header))
                    throw new InvalidDataException("암호화 임시파일 헤더가 변경됐습니다.");
                long chunks = (Length + BlockSize - 1) / BlockSize;
                if (_input.Length != HeaderSize + Length + chunks * TagSize)
                    throw new InvalidDataException("암호화 임시파일 길이가 올바르지 않습니다.");
            }
            catch { _input.Dispose(); throw; }
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _owner.Length;
        public override long Position
        {
            get => _position;
            set { if (value < 0 || value > Length) throw new ArgumentOutOfRangeException(nameof(value)); _position = value; }
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = position;
            return Position;
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override int Read(Span<byte> buffer)
        {
            byte[] data = new byte[buffer.Length];
            int read = Read(data, 0, data.Length);
            data.AsSpan(0, read).CopyTo(buffer);
            CryptographicOperations.ZeroMemory(data);
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
            if (_position >= Length || buffer.Length == 0) return 0;
            long block = _position / BlockSize;
            if (_cachedBlock != block) await LoadBlockAsync(block, ct).ConfigureAwait(false);
            int within = (int)(_position % BlockSize);
            int count = (int)Math.Min(buffer.Length, Math.Min(_cache.Length - within, Length - _position));
            _cache.AsMemory(within, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        private async Task LoadBlockAsync(long block, CancellationToken ct)
        {
            if (_cache.Length > 0) CryptographicOperations.ZeroMemory(_cache);
            int clearLength = (int)Math.Min(BlockSize, Length - block * BlockSize);
            byte[] cipher = new byte[clearLength];
            byte[] tag = new byte[TagSize];
            _cache = new byte[clearLength];
            try
            {
                _input.Position = HeaderSize + block * (BlockSize + TagSize);
                await _input.ReadExactlyAsync(cipher, ct).ConfigureAwait(false);
                await _input.ReadExactlyAsync(tag, ct).ConfigureAwait(false);
                using var aes = new AesGcm(_owner._key, TagSize);
                aes.Decrypt(_owner.Nonce(checked((uint)block)), cipher, tag, _cache,
                    AAD(_header, checked((uint)block)));
                _cachedBlock = block;
            }
            catch (CryptographicException ex)
            {
                CryptographicOperations.ZeroMemory(_cache);
                _cachedBlock = -1;
                throw new InvalidDataException("암호화 임시파일의 인증 태그 검증에 실패했습니다.", ex);
            }
            finally { CryptographicOperations.ZeroMemory(cipher); }
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CryptographicOperations.ZeroMemory(_cache);
                _input.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
