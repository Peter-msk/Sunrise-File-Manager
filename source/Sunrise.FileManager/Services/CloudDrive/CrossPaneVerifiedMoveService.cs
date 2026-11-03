using System.IO;
using System.Security.Cryptography;

namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>
/// R25 explicit, file-only copy-verify-retire operation. The source is never retired
/// unless a fresh destination read and a second source read match the initial SHA-256.
/// Any denial, conflict, cancellation or source mutation preserves the original.
/// A failed retirement may leave BOTH copies; it is reported, never called success.
/// This does not infer a move from drag/drop or a Windows clipboard cut.
/// </summary>
public static class CrossPaneVerifiedMoveService
{
    private sealed record Snapshot(string Name, long Length, byte[] Hash, DateTime? LocalWriteUtc,
        string? RemoteParentId, string? RemoteVersionTag);

    public static async Task<BatchCopyReport> MoveAsync(IReadOnlyList<CrossPaneCopySource> sources,
        CrossPaneCopyTarget target, IProgress<BatchCopyProgress>? progress, CancellationToken token)
    {
        if (sources is null || sources.Count is < 1 or > CrossPaneBatchCopyService.MaximumFiles)
            throw new ArgumentException("이동할 파일은 1~128개여야 합니다.");
        if (target is null) throw new ArgumentNullException(nameof(target));
        var results = new List<BatchCopyItemResult>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool canceled = false;
        for (int index = 0; index < sources.Count; index++)
        {
            if (token.IsCancellationRequested) { canceled = true; break; }
            var source = sources[index];
            string label = source.LocalFile is not null ? Path.GetFileName(source.LocalFile.TrimEnd(Path.DirectorySeparatorChar)) :
                source.RemoteFile?.Name ?? "원본 없음";
            if (!names.Add(label))
            {
                results.Add(new(label, false, "동일 이름의 원본 중복 · 원본 보존")
                { Outcome = BatchCopyItemOutcome.Skipped });
                progress?.Report(new(index + 1, sources.Count, label, 0));
                continue;
            }
            bool copied = false;
            try
            {
                // Fail closed for folders until remote recursive copy verification and rollback is implemented.
                if (source.LocalFile is { } local && Directory.Exists(local) || source.RemoteFile?.IsFolder == true)
                    throw new NotSupportedException("폴더 이동은 하위 항목 전체 검증 및 복구 구현 후 지원합니다.");
                // Preflight BEFORE making any destination copy: Google Drive v3 has no
                // verified atomic compare-and-trash contract in this implementation.
                if (source.RemoteFile is not null &&
                    (source.RemoteApi is not IConditionalCloudRetirement ||
                     string.IsNullOrWhiteSpace(source.RemoteFile.VersionTag)))
                    throw new NotSupportedException("클라우드 원본의 조건부 휴지통 이동을 지원하지 않아 복사만 가능합니다.");
                var before = await CaptureAsync(source, token).ConfigureAwait(false);
                int completed = index;
                var step = new Progress<BatchCopyProgress>(p =>
                    progress?.Report(new(completed, sources.Count, label, p.FilePercent)));
                var report = await CrossPaneBatchCopyService.CopyAsync([source], target, step, token).ConfigureAwait(false);
                if (report.Canceled) throw new OperationCanceledException(token);
                if (report.Succeeded != 1 || report.Failed != 0)
                    throw new IOException("검증 복사에 실패했습니다. 원본은 그대로 보존합니다.");
                copied = true;
                // A fresh read of the published destination (not only the upload acknowledgement).
                byte[] destinationHash = await HashDestinationAsync(source, target, before, token).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(destinationHash, before.Hash))
                    throw new IOException("이동 대상 데이터가 원본과 일치하지 않습니다.");
                // A fresh read of the source immediately before its retirement; metadata alone is insufficient.
                await EnsureSourceUnchangedAsync(source, before, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (source.LocalFile is { } path)
                    File.Delete(Path.GetFullPath(path));
                else
                    await ((IConditionalCloudRetirement)source.RemoteApi!).MoveToTrashIfVersionMatchesAsync(
                        source.RemoteFile!.Id, before.RemoteVersionTag!, token).ConfigureAwait(false);
                results.Add(new(label, true, "복사·SHA-256 재검증 후 원본 정리 완료"));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                results.Add(new(label, false, copied ? "취소 · 대상 복사본이 남을 수 있음 · 원본 정리 미확인" : "취소 · 원본 보존")
                { Outcome = BatchCopyItemOutcome.Canceled });
                canceled = true;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Net.Http.HttpRequestException or InvalidOperationException or
                ArgumentException or NotSupportedException or InvalidDataException or CryptographicException)
            {
                // Never show untrusted provider exception contents (signed URLs or account identifiers).
                results.Add(new(label, false, copied
                    ? "대상 복사는 생성됨 · 검증 또는 원본 정리 실패 · 두 위치 모두 확인 필요"
                    : "복사 중단 또는 대상 충돌 · 원본 보존"));
            }
            progress?.Report(new(index + 1, sources.Count, label, 0));
        }
        return new BatchCopyReport(results, canceled) { Requested = sources.Count };
    }

    private static async Task<Snapshot> CaptureAsync(CrossPaneCopySource source, CancellationToken ct)
    {
        if (source.LocalFile is { } path)
        {
            string full = Path.GetFullPath(path);
            var file = new FileInfo(full);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("원본 파일이 없거나 재분석 지점입니다.");
            // Hold file through hash read; denies concurrent writers while stream is open.
            byte[] hash = await HashLocalAsync(full, ct).ConfigureAwait(false);
            file.Refresh();
            return new(file.Name, file.Length, hash, file.LastWriteTimeUtc, null, null);
        }
        var item = source.RemoteFile ?? throw new ArgumentException("원본 파일이 없습니다.");
        if (item.IsFolder || item.Size is not long size || size < 0 ||
            source.RemoteTransfer is null || source.RemoteApi is null || string.IsNullOrWhiteSpace(item.ParentId))
            throw new NotSupportedException("원본 클라우드 파일의 크기·상위 폴더·연결 정보를 확인할 수 없습니다.");
        var candidates = await source.RemoteApi.ListChildrenAsync(item.ParentId, ct).ConfigureAwait(false);
        var current = candidates.SingleOrDefault(x => x.Id == item.Id);
        if (current is null || current.IsFolder || current.Name != item.Name || current.Size != size ||
            current.ParentId != item.ParentId || current.VersionTag != item.VersionTag ||
            string.IsNullOrWhiteSpace(current.VersionTag))
            throw new IOException("원본 클라우드 버전이 변경되어 안전 이동을 중단했습니다.");
        byte[] remoteHash = await source.RemoteTransfer.ComputeRemoteSha256Async(current, ct).ConfigureAwait(false);
        return new(item.Name, size, remoteHash, null, item.ParentId, current.VersionTag);
    }

    private static async Task EnsureSourceUnchangedAsync(CrossPaneCopySource source, Snapshot snapshot, CancellationToken ct)
    {
        if (source.LocalFile is { } path)
        {
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                file.Length != snapshot.Length || file.LastWriteTimeUtc != snapshot.LocalWriteUtc)
                throw new IOException("복사 중 원본 파일이 변경됐습니다.");
            byte[] actual = await HashLocalAsync(file.FullName, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(snapshot.Hash, actual))
                throw new IOException("복사 중 원본 내용이 변경됐습니다.");
            return;
        }
        var original = source.RemoteFile!;
        var children = await source.RemoteApi!.ListChildrenAsync(snapshot.RemoteParentId!, ct).ConfigureAwait(false);
        var current = children.SingleOrDefault(x => x.Id == original.Id);
        if (current is null || current.IsFolder || current.ParentId != snapshot.RemoteParentId ||
            current.Name != snapshot.Name || current.Size != snapshot.Length ||
            current.VersionTag != snapshot.RemoteVersionTag)
            throw new IOException("원본 클라우드 파일이 이동·변경됐습니다.");
        byte[] actualRemote = await source.RemoteTransfer!.ComputeRemoteSha256Async(current, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(snapshot.Hash, actualRemote))
            throw new IOException("원본 클라우드 데이터가 변경됐습니다.");
    }

    private static async Task<byte[]> HashDestinationAsync(CrossPaneCopySource source, CrossPaneCopyTarget target,
        Snapshot snapshot, CancellationToken ct)
    {
        if (target.LocalFolder is { } folder)
        {
            string path = Path.Combine(Path.GetFullPath(folder), snapshot.Name);
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length != snapshot.Length)
                throw new IOException("이동 대상 파일을 검증할 수 없습니다.");
            return await HashLocalAsync(path, ct).ConfigureAwait(false);
        }
        if (target.RemoteApi is null || target.RemoteTransfer is null || target.RemoteFolderId is null)
            throw new InvalidOperationException("이동 대상 연결이 없습니다.");
        var matches = (await target.RemoteApi.ListChildrenAsync(target.RemoteFolderId, ct).ConfigureAwait(false))
            .Where(x => x.Name.Equals(snapshot.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].IsFolder || matches[0].Size != snapshot.Length)
            throw new IOException("이동 대상 이름이 중복되었거나 크기가 다릅니다.");
        return await target.RemoteTransfer.ComputeRemoteSha256Async(matches[0], ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> HashLocalAsync(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
    }
}
