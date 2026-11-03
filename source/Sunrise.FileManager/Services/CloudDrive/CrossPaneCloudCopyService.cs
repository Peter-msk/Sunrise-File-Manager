using System.IO;

namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>
/// R18: explicit, single-file, no-overwrite copies with destination content-hash verification.
/// Never uses local shell operations on remote IDs. Neither original nor remote item is deleted.
/// Remote-to-remote plaintext is never staged on disk: per-operation AES-GCM chunks
/// are decrypted only into short-lived memory buffers. Failed uploads can leave remote orphans.
/// </summary>
public sealed record CrossPaneCopySource(string? LocalFile, CloudItem? RemoteFile,
    CloudFileTransferService? RemoteTransfer, ICloudDriveApi? RemoteApi = null);

public sealed record CrossPaneCopyTarget(string? LocalFolder, string? RemoteFolderId,
    ICloudDriveApi? RemoteApi, CloudFileTransferService? RemoteTransfer);

public static class CrossPaneCloudCopyService
{
    private static string ValidateSource(CrossPaneCopySource source)
    {
        bool local = source.LocalFile is not null;
        if (local == (source.RemoteFile is not null) ||
            (local && source.RemoteTransfer is not null) ||
            (!local && source.RemoteTransfer is null))
            throw new ArgumentException("복사 원본이 명확하지 않습니다.");
        if (local)
        {
            string path = Path.GetFullPath(source.LocalFile!);
            if (!File.Exists(path)) throw new FileNotFoundException("선택한 원본 파일을 확인할 수 없습니다.");
            return Path.GetFileName(path);
        }
        if (source.RemoteFile!.IsFolder)
            throw new NotSupportedException("폴더 전체 전송은 검증 후 지원합니다.");
        if (source.RemoteFile.Size is not long size || size < 0)
            throw new NotSupportedException("원본 파일의 크기를 확인할 수 없어 안전한 복사를 중단했습니다.");
        return Sunrise.FileManager.Services.FileActionsService.ValidateName(source.RemoteFile.Name);
    }

    private static bool DestinationIsRemote(CrossPaneCopyTarget target)
    {
        bool remote = target.RemoteFolderId is not null;
        if (remote && (target.RemoteApi is null || target.RemoteTransfer is null || target.LocalFolder is not null))
            throw new ArgumentException("클라우드 대상 연결이 올바르지 않습니다.");
        if (!remote && (target.LocalFolder is null || target.RemoteApi is not null || target.RemoteTransfer is not null))
            throw new ArgumentException("로컬 대상 연결이 올바르지 않습니다.");
        return remote;
    }

    private static async Task<CloudItem> VerifyRemoteAsync(CrossPaneCopyTarget destination, string uploadedId,
        long expectedLength, string expectedName, CancellationToken token)
    {
        var uploaded = (await destination.RemoteApi!.ListChildrenAsync(destination.RemoteFolderId!, token)
            .ConfigureAwait(false)).SingleOrDefault(x => x.Id == uploadedId);
        if (uploaded is null || uploaded.IsFolder || uploaded.Size != expectedLength ||
            !uploaded.Name.Equals(expectedName, StringComparison.Ordinal))
            throw new IOException("대상 파일의 이름·크기·ID를 확인하지 못했습니다. 원본은 그대로 보존했습니다. 대상 클라우드에서 완료 여부를 확인하세요.");
        return uploaded;
    }

    private static IProgress<CloudTransferProgress>? StageProgress(IProgress<CloudTransferProgress>? progress,
        int offset, int share)
    {
        if (progress is null) return null;
        return new Progress<CloudTransferProgress>(p =>
        {
            int percent = p.Total > 0 ? p.Percent : 0;
            progress.Report(new CloudTransferProgress(Math.Clamp(offset + percent * share / 100, 0, 100), 100));
        });
    }

    public static async Task CopyAsync(CrossPaneCopySource source, CrossPaneCopyTarget destination,
        IProgress<CloudTransferProgress>? progress, CancellationToken token)
    {
        string name = ValidateSource(source);
        bool remoteTarget = DestinationIsRemote(destination);
        token.ThrowIfCancellationRequested();
        if (!remoteTarget)
        {
            if (source.RemoteFile is null)
                throw new NotSupportedException("PC 간 복사는 기존 전송 엔진을 사용하세요.");
            string folder = Path.GetFullPath(destination.LocalFolder!);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("대상 폴더가 없습니다.");
            string targetFile = Path.Combine(folder, name);
            if (File.Exists(targetFile) || Directory.Exists(targetFile))
                throw new IOException("대상 이름이 이미 있어 덮어쓰기를 중단했습니다.");
            await source.RemoteTransfer!.DownloadAsync(source.RemoteFile, targetFile, progress, token)
                .ConfigureAwait(false);
            // DownloadAsync commits only after streaming and matching the known remote length.
            return;
        }

        string? original = source.LocalFile;
        if (original is not null)
        {
            string full = Path.GetFullPath(original);
            long size = new FileInfo(full).Length;
            string localUploadId = await destination.RemoteTransfer!.UploadAsync(destination.RemoteFolderId!, full,
                destination.RemoteApi!, StageProgress(progress, 0, 95), token).ConfigureAwait(false);
            // Fail closed if the provider cannot confirm the final item; never delete the source.
            var localUploaded = await VerifyRemoteAsync(destination, localUploadId, size, name, token).ConfigureAwait(false);
            await destination.RemoteTransfer.VerifyRemoteMatchesLocalAsync(localUploaded, full, token).ConfigureAwait(false);
            progress?.Report(new(100, 100));
            return;
        }

        // R19: encrypted staging is owned solely by this transfer. Encryption key lives in
        // process memory, not in the temporary folder or a persisted token vault.
        // Crashes can leave ciphertext, but never a recoverable plaintext working copy.
        CloudItem cloudFile = source.RemoteFile!;
        using var stage = EncryptedCloudStage.Create(cloudFile.Size!.Value);
        await source.RemoteTransfer!.DownloadToEncryptedStageAsync(cloudFile, stage,
            StageProgress(progress, 0, 50), token).ConfigureAwait(false);
        using var readable = stage.OpenRead();
        string id = await destination.RemoteTransfer!.UploadReadableAsync(destination.RemoteFolderId!, name,
            readable, destination.RemoteApi!, StageProgress(progress, 50, 45), token).ConfigureAwait(false);
        var uploaded = await VerifyRemoteAsync(destination, id, cloudFile.Size!.Value, name, token).ConfigureAwait(false);
        await destination.RemoteTransfer.VerifyRemoteMatchesReadableAsync(uploaded, readable, token)
            .ConfigureAwait(false);
        progress?.Report(new(100, 100));
    }
}
