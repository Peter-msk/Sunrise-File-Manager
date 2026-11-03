using System.IO;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>
/// R21: Explicit, bounded folder tree copy. Preflight enumerates/validates the entire
/// source tree BEFORE creating the destination. A cloud destination may retain a partial
/// folder on failure; it is never auto-deleted. Original content is never modified.
/// </summary>
/// <summary>Safe, non-provider exception carrying aggregate progress; never includes signed URLs.</summary>
public sealed class CrossPaneFolderCopyException : IOException
{
    public int CompletedFiles { get; }
    public int TotalFiles { get; }
    public bool PartialRemoteMayRemain { get; }

    public CrossPaneFolderCopyException(int completed, int total, bool partialRemote, Exception cause)
        : base("하위 파일 일부 전송 실패 · 대상 확인 필요", cause)
    {
        CompletedFiles = completed;
        TotalFiles = total;
        PartialRemoteMayRemain = partialRemote;
    }
}

public static class CrossPaneFolderCopyService
{
    public const int MaximumEntries = 10_000;
    public const int MaximumDepth = 64;
    private static readonly ConcurrentDictionary<string, byte> ActiveLocalCloudFolders =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record PlannedFile(string Relative, CrossPaneCopySource Source);
    private sealed record FolderPlan(string RootName, IReadOnlyList<string> Folders, IReadOnlyList<PlannedFile> Files,
        IReadOnlySet<string> RemoteFolderIds);

    private static string SafeName(string candidate) => FileActionsService.ValidateName(candidate);
    private static string Join(string parent, string child) => parent.Length == 0 ? child : Path.Combine(parent, child);
    private static bool EntryExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static async Task<FolderPlan> PreflightAsync(CrossPaneCopySource source, CancellationToken token)
    {
        if (source.LocalFile is not null)
        {
            string root = Path.GetFullPath(source.LocalFile);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("선택한 원본 폴더가 없습니다.");
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("링크 폴더는 복사하지 않습니다.");
            string name = SafeName(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)));
            var directories = new List<string>();
            var files = new List<PlannedFile>();
            var stack = new Stack<(string Path, string Relative, int Depth)>();
            stack.Push((root, "", 0));
            int scanned = 0;
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var current = stack.Pop();
                if (current.Depth > MaximumDepth) throw new IOException("폴더 깊이 제한을 초과했습니다.");
                var siblingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string child in Directory.EnumerateFileSystemEntries(current.Path))
                {
                    token.ThrowIfCancellationRequested();
                    if (++scanned > MaximumEntries) throw new IOException("최대 10,000개 파일·폴더까지만 복사할 수 있습니다.");
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        throw new NotSupportedException("원본에 링크·접합점이 있어 안전하게 복사할 수 없습니다.");
                    string childName = SafeName(Path.GetFileName(child));
                    if (!siblingNames.Add(childName)) throw new IOException("동일한 이름의 원본 항목이 있습니다.");
                    string relative = Join(current.Relative, childName);
                    if (Directory.Exists(child))
                    {
                        directories.Add(relative);
                        stack.Push((child, relative, current.Depth + 1));
                    }
                    else if (File.Exists(child))
                    {
                        files.Add(new(relative, new CrossPaneCopySource(child, null, null)));
                    }
                    else throw new IOException("원본 파일 유형을 확인할 수 없습니다.");
                }
            }
            return new(name, directories.OrderBy(x => x.Count(c => c == Path.DirectorySeparatorChar)).ToArray(),
                files, new HashSet<string>());
        }

        var rootItem = source.RemoteFile ?? throw new ArgumentException("원본 폴더가 비어 있습니다.");
        ICloudDriveApi api = source.RemoteApi ?? throw new InvalidOperationException("원본 클라우드 폴더를 열 수 없습니다.");
        if (!rootItem.IsFolder || source.RemoteTransfer is null) throw new ArgumentException("클라우드 원본 폴더가 아닙니다.");
        var dirs = new List<string>();
        var remoteFiles = new List<PlannedFile>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootItem.Id };
        var pending = new Stack<(string Id, string Relative, int Depth)>();
        pending.Push((rootItem.Id, "", 0));
        int remoteCount = 0;
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = pending.Pop();
            if (current.Depth > MaximumDepth) throw new IOException("클라우드 폴더 깊이 제한을 초과했습니다.");
            var siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CloudItem child in await api.ListChildrenAsync(current.Id, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                if (++remoteCount > MaximumEntries) throw new IOException("원격 폴더 항목 제한(10,000개)을 초과했습니다.");
                string name = SafeName(child.Name);
                if (!siblings.Add(name) || string.IsNullOrEmpty(child.Id))
                    throw new IOException("중복되거나 식별할 수 없는 클라우드 항목이 있습니다.");
                string relative = Join(current.Relative, name);
                if (child.IsFolder)
                {
                    if (!visited.Add(child.Id)) throw new IOException("클라우드 폴더에 중복 참조 또는 순환이 있습니다.");
                    dirs.Add(relative);
                    pending.Push((child.Id, relative, current.Depth + 1));
                }
                else
                {
                    if (child.Size is not long length || length < 0)
                        throw new NotSupportedException("크기를 확인할 수 없는 파일은 안전한 전송에서 제외합니다.");
                    remoteFiles.Add(new(relative, new CrossPaneCopySource(null, child, source.RemoteTransfer, api)));
                }
            }
        }
        return new(SafeName(rootItem.Name), dirs.OrderBy(x => x.Count(c => c == Path.DirectorySeparatorChar)).ToArray(),
            remoteFiles, visited);
    }

    public static async Task CopyAsync(CrossPaneCopySource source, CrossPaneCopyTarget destination,
        IProgress<CloudTransferProgress>? progress, CancellationToken token)
    {
        FolderPlan plan = await PreflightAsync(source, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (destination.LocalFolder is not null)
        {
            string parent = Path.GetFullPath(destination.LocalFolder);
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("대상 폴더가 없습니다.");
            if (source.LocalFile is not null)
            {
                // Reuse the established atomic local directory-transfer engine, with no source deletion.
                var result = await TransferService.RunAsync([source.LocalFile], parent, TransferKind.Copy,
                    null, token, CollisionPolicy.Skip).ConfigureAwait(false);
                if (result.Canceled) throw new OperationCanceledException(token);
                if (result.Completed != 1 || result.Failed != 0 || result.Skipped != 0)
                    throw new IOException("폴더 복사에 실패했거나 대상 이름이 충돌했습니다. 원본을 보존했습니다.");
                progress?.Report(new(100, 100));
                return;
            }
            string output = Path.Combine(parent, plan.RootName);
            if (EntryExists(output)) throw new IOException("동일한 이름의 폴더가 이미 있습니다.");
            // Stage on the destination volume; never present a partially copied local folder.
            string stage = Path.Combine(parent, ".sunrise-folder-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(stage);
                foreach (string relative in plan.Folders)
                {
                    token.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.Combine(stage, relative));
                }
                await CopyFilesAsync(plan.Files, async (file, rel, pct) =>
                {
                    string folder = Path.GetDirectoryName(Path.Combine(stage, rel))!;
                    await CrossPaneCloudCopyService.CopyAsync(file, new CrossPaneCopyTarget(folder, null, null, null),
                        pct, token).ConfigureAwait(false);
                }, progress, token, partialRemoteMayRemain: false).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Directory.Move(stage, output);
            }
            finally
            {
                if (Directory.Exists(stage))
                {
                    try { Directory.Delete(stage, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { throw new IOException("복사 실패 후 임시 폴더 정리가 차단됐습니다. 관리자에게 남은 임시 폴더를 확인받으세요.", ex); }
                }
            }
            progress?.Report(new(100, 100));
            return;
        }
        if (destination.RemoteApi is null || destination.RemoteTransfer is null || destination.RemoteFolderId is null)
            throw new ArgumentException("대상 클라우드 폴더 연결이 올바르지 않습니다.");
        // R44: separate API client instances can point to the *same* account/drive.
        // Reference equality of provider objects is not a cloud-folder identity check.
        // Only probe account identity when the selected destination ID is already in
        // the fully preflighted source tree, so unrelated cloud copies stay unchanged.
        if (source.RemoteTransfer is not null &&
            source.RemoteTransfer.RecoveryProvider == destination.RemoteTransfer.RecoveryProvider &&
            string.Equals(source.RemoteTransfer.RecoveryDriveId,
                destination.RemoteTransfer.RecoveryDriveId, StringComparison.Ordinal) &&
            plan.RemoteFolderIds.Contains(destination.RemoteFolderId))
        {
            string sourceAccount = await source.RemoteTransfer.RecoveryAccountFingerprintAsync(token).ConfigureAwait(false);
            string destinationAccount = await destination.RemoteTransfer.RecoveryAccountFingerprintAsync(token).ConfigureAwait(false);
            if (string.Equals(sourceAccount, destinationAccount, StringComparison.Ordinal))
                throw new IOException("같은 클라우드 계정의 원본 폴더 자신 또는 하위 폴더로 복사할 수 없습니다.");
        }
        // R43: only a local source has a stable, recheckable manifest suitable for
        // explicit folder retry. Cloud-to-cloud retains the earlier safe-copy path.
        if (source.LocalFile is not null)
        {
            await CopyLocalFolderToCloudWithJournalAsync(source, destination, plan, progress, token)
                .ConfigureAwait(false);
            return;
        }
        // Cloud-to-cloud still has no persistent resume. Save a source tree
        // identity before writing so a concurrent source change cannot be reported
        // as a fully completed original snapshot.
        string sourceManifest = RemoteManifestSha256(plan);
        var apiTarget = destination.RemoteApi;
        string rootId = destination.RemoteFolderId;
        if ((await apiTarget.ListChildrenAsync(rootId, token).ConfigureAwait(false))
            .Any(x => x.Name.Equals(plan.RootName, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("대상 클라우드에 같은 이름의 항목이 있습니다.");
        // Cloud writes are NOT transactional. A partial root may remain after failure or cancel;
        // never auto-delete a remote folder that may contain concurrently created user files.
        CloudItem root = await apiTarget.CreateFolderAsync(rootId, plan.RootName, token).ConfigureAwait(false);
        if (!root.IsFolder || string.IsNullOrWhiteSpace(root.Id))
            throw new IOException("새 클라우드 폴더 식별자를 확인하지 못했습니다.");
        // R44: a successful create response is not proof of a unique new remote item.
        // Provider-side races and same-name aliases must stop before any child writes.
        CloudItem confirmedRoot = await ExactChildAsync(apiTarget, rootId, plan.RootName, token).ConfigureAwait(false);
        if (!confirmedRoot.IsFolder || confirmedRoot.Id != root.Id ||
            !string.Equals(confirmedRoot.Name, plan.RootName, StringComparison.Ordinal))
            throw new IOException("새 폴더의 ID·이름을 재확인할 수 없습니다. 자동 복사를 중단하고 원격 상태를 확인하세요.");
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [""] = root.Id };
        foreach (string relative in plan.Folders)
        {
            token.ThrowIfCancellationRequested();
            string parentRelative = Path.GetDirectoryName(relative) ?? "";
            string childName = Path.GetFileName(relative);
            CloudItem folder = await apiTarget.CreateFolderAsync(ids[parentRelative], childName, token).ConfigureAwait(false);
            if (!folder.IsFolder || string.IsNullOrWhiteSpace(folder.Id))
                throw new IOException("클라우드 하위 폴더 생성 결과를 확인하지 못했습니다.");
            CloudItem confirmedChild = await ExactChildAsync(apiTarget, ids[parentRelative], childName, token).ConfigureAwait(false);
            if (!confirmedChild.IsFolder || confirmedChild.Id != folder.Id ||
                !string.Equals(confirmedChild.Name, childName, StringComparison.Ordinal))
                throw new IOException("새 하위 폴더의 ID·이름을 재확인할 수 없습니다. 자동 복사를 중단하고 원격 상태를 확인하세요.");
            ids.Add(relative, folder.Id);
        }
        await CopyFilesAsync(plan.Files, async (file, rel, pct) =>
        {
            string parentRel = Path.GetDirectoryName(rel) ?? "";
            var itemTarget = destination with { RemoteFolderId = ids[parentRel] };
            await CrossPaneCloudCopyService.CopyAsync(file, itemTarget, pct, token).ConfigureAwait(false);
        }, progress, token, partialRemoteMayRemain: true).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        FolderPlan liveSource = await PreflightAsync(source, token).ConfigureAwait(false);
        if (RemoteManifestSha256(liveSource) != sourceManifest)
            throw new IOException("클라우드 원본 폴더의 구성·항목 ID·버전이 복사 중 변경됐습니다. 대상의 부분 복사본은 보존하고 완료 판정을 보류합니다.");
        progress?.Report(new(100, 100));
    }

    private static string RemoteManifestSha256(FolderPlan plan)
    {
        // R47: source cloud tree identity and provider versions must still match
        // the source preflight at the end of a nontransactional remote folder copy.
        // This is a structural guard, NOT durable cloud-to-cloud recovery or proof
        // against providers that omit an opaque version tag.
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            sha.AppendData(BitConverter.GetBytes(bytes.Length));
            sha.AppendData(bytes);
        }
        Add(plan.RootName);
        foreach (string id in plan.RemoteFolderIds.OrderBy(x => x, StringComparer.Ordinal)) Add("I:" + id);
        foreach (string directory in plan.Folders.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) Add("D:" + directory);
        foreach (var file in plan.Files.OrderBy(x => x.Relative, StringComparer.OrdinalIgnoreCase))
        {
            CloudItem item = file.Source.RemoteFile ?? throw new IOException("클라우드 원본 파일 식별자가 없습니다.");
            Add("F:" + file.Relative);
            Add("ID:" + item.Id);
            Add("SZ:" + item.Size?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add("VER:" + (item.VersionTag ?? ""));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static string LocalManifestSha256(FolderPlan plan)
    {
        // A stable structural snapshot, including last-write timestamps and lengths;
        // every completed file is ALSO checked by full source + remote SHA-256 on retry.
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            sha.AppendData(BitConverter.GetBytes(bytes.Length));
            sha.AppendData(bytes);
        }
        Add(plan.RootName);
        foreach (string directory in plan.Folders.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            Add("D:" + directory);
        foreach (var file in plan.Files.OrderBy(x => x.Relative, StringComparer.OrdinalIgnoreCase))
        {
            string full = file.Source.LocalFile ?? throw new IOException("폴더 복구는 로컬 원본만 지원합니다.");
            var info = new FileInfo(full);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("폴더에 원본 파일 변경 또는 연결 경로가 있습니다.");
            Add("F:" + file.Relative);
            Add(info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static async Task<string> ShaLocalAsync(string path, CancellationToken token)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 256, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
    }

    private static async Task<CloudItem> ExactChildAsync(ICloudDriveApi api, string parentId,
        string name, CancellationToken token)
    {
        var children = (await api.ListChildrenAsync(parentId, token).ConfigureAwait(false))
            .Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (children.Length > 1) throw new IOException("클라우드 대상에 동명 항목이 중복돼 복구를 중단했습니다.");
        return children.SingleOrDefault() ?? throw new IOException("클라우드 대상 항목을 확인할 수 없습니다.");
    }

    private static async Task CopyLocalFolderToCloudWithJournalAsync(CrossPaneCopySource source,
        CrossPaneCopyTarget destination, FolderPlan plan,
        IProgress<CloudTransferProgress>? progress, CancellationToken token)
    {
        string sourcePath = Path.GetFullPath(source.LocalFile!);
        if (!ActiveLocalCloudFolders.TryAdd(sourcePath, 0))
            throw new IOException("동일한 원본 폴더의 클라우드 복사가 이미 진행 중입니다.");
        try
        {
        var api = destination.RemoteApi!;
        var transfer = destination.RemoteTransfer!;
        string parentId = destination.RemoteFolderId!;
        string manifest = LocalManifestSha256(plan);
        string identity = await transfer.RecoveryAccountFingerprintAsync(token).ConfigureAwait(false);
        var checkpoint = CloudFolderCopyResumeStore.Find(sourcePath);
        if (checkpoint is not null)
        {
            CloudFolderCopyResumeStore.Validate(checkpoint, transfer, identity, sourcePath,
                parentId, plan.RootName, manifest);
            CloudItem root = await ExactChildAsync(api, parentId, plan.RootName, token).ConfigureAwait(false);
            if (!root.IsFolder || root.Id != checkpoint.RootId)
                throw new IOException("기존 대상 폴더의 식별자가 바뀌었습니다. 자동 재개하지 않습니다.");
        }
        else
        {
            // New folder creation is not atomic with journal persistence. If the app
            // crashes in that gap, subsequent requests see a collision and stop safely.
            if ((await api.ListChildrenAsync(parentId, token).ConfigureAwait(false))
                .Any(x => x.Name.Equals(plan.RootName, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("대상 클라우드에 같은 이름의 항목이 있습니다. 복구 기록 없이 재사용하지 않습니다.");
            CloudItem root = await api.CreateFolderAsync(parentId, plan.RootName, token).ConfigureAwait(false);
            if (!root.IsFolder || string.IsNullOrWhiteSpace(root.Id))
                throw new IOException("클라우드 루트 폴더 생성을 확인하지 못했습니다. 수동 검토가 필요합니다.");
            CloudItem confirmedRoot = await ExactChildAsync(api, parentId, plan.RootName, token).ConfigureAwait(false);
            if (!confirmedRoot.IsFolder || confirmedRoot.Id != root.Id)
                throw new IOException("원격 폴더의 ID를 확인하지 못했습니다. 자동 진행하지 않습니다.");
            checkpoint = CloudFolderCopyResumeStore.Create(transfer, identity, sourcePath,
                parentId, root.Id, plan.RootName, manifest);
        }

        foreach (string relative in plan.Folders)
        {
            token.ThrowIfCancellationRequested();
            string parentRelative = Path.GetDirectoryName(relative) ?? "";
            if (!checkpoint.FolderIds.TryGetValue(parentRelative, out string? remoteParent))
                throw new IOException("복구 기록에 상위 폴더가 없습니다.");
            string childName = Path.GetFileName(relative);
            var matches = (await api.ListChildrenAsync(remoteParent, token).ConfigureAwait(false))
                .Where(x => x.Name.Equals(childName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw new IOException("대상 폴더에 동명 하위 항목이 있습니다.");
            if (checkpoint.FolderIds.TryGetValue(relative, out string? rememberedId))
            {
                if (matches.Length != 1 || !matches[0].IsFolder || matches[0].Id != rememberedId)
                    throw new IOException("복구된 하위 폴더의 식별자가 변경돼 자동 재개하지 않습니다.");
            }
            else
            {
                // A folder created remotely before its journal update is deliberately
                // NOT adopted by name. The user must review the ambiguous operation.
                if (matches.Length != 0)
                    throw new IOException("기록되지 않은 하위 폴더가 이미 있어 자동 재개하지 않습니다.");
                CloudItem created = await api.CreateFolderAsync(remoteParent, childName, token).ConfigureAwait(false);
                if (!created.IsFolder || string.IsNullOrWhiteSpace(created.Id))
                    throw new IOException("원격 하위 폴더 생성 여부를 확인하지 못했습니다.");
                CloudItem confirmed = await ExactChildAsync(api, remoteParent, childName, token).ConfigureAwait(false);
                if (!confirmed.IsFolder || confirmed.Id != created.Id)
                    throw new IOException("원격 하위 폴더의 ID를 확인하지 못했습니다.");
                checkpoint.FolderIds.Add(relative, created.Id);
                CloudFolderCopyResumeStore.Update(checkpoint);
            }
        }

        // Restore only the *recorded and reverified* completed files. If a session
        // previously committed but this journal was not updated, the name collision
        // intentionally stops the operation rather than silently reusing/reuploading.
        var completed = checkpoint.Completed.ToDictionary(x => x.Relative, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < plan.Files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = plan.Files[i];
            string localFile = entry.Source.LocalFile!;
            string parentRel = Path.GetDirectoryName(entry.Relative) ?? "";
            if (!checkpoint.FolderIds.TryGetValue(parentRel, out string? itemParent))
                throw new IOException("복구 기록에서 파일의 상위 폴더를 확인하지 못했습니다.");
            string name = Path.GetFileName(entry.Relative);
            var matches = (await api.ListChildrenAsync(itemParent, token).ConfigureAwait(false))
                .Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw new IOException("클라우드 대상 파일 이름이 중복됩니다.");
            if (completed.TryGetValue(entry.Relative, out var saved))
            {
                if (matches.Length != 1 || matches[0].IsFolder || matches[0].Id != saved.ItemId ||
                    matches[0].Size != saved.Length || !string.Equals(matches[0].Name, name, StringComparison.Ordinal))
                    throw new IOException("이전에 전송한 파일의 원격 ID·크기·이름이 변경돼 재개하지 않습니다.");
                string localSha = await ShaLocalAsync(localFile, token).ConfigureAwait(false);
                if (localSha != saved.Sha256)
                    throw new IOException("원본 파일 내용이 변경돼 자동 재개하지 않습니다.");
                await transfer.VerifyRemoteMatchesLocalAsync(matches[0], localFile, token).ConfigureAwait(false);
            }
            else
            {
                if (matches.Length != 0)
                    throw new IOException("기록되지 않은 대상 파일이 있습니다. 중복 업로드를 방지하기 위해 자동 재개하지 않습니다.");
                // Save the actual provider item ID, not merely a filename/size guess.
                string initialSha = await ShaLocalAsync(localFile, token).ConfigureAwait(false);
                int index = i;
                var itemProgress = new Progress<CloudTransferProgress>(p =>
                    progress?.Report(new(index * 100L + p.Percent, plan.Files.Count * 100L)));
                string uploadedId;
                try
                {
                    uploadedId = await transfer.UploadAsync(itemParent, localFile, api,
                        itemProgress, token).ConfigureAwait(false);
                    var matching = (await api.ListChildrenAsync(itemParent, token).ConfigureAwait(false))
                        .Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matching.Length != 1 || matching[0].IsFolder || matching[0].Id != uploadedId ||
                        matching[0].Size != new FileInfo(localFile).Length)
                        throw new IOException("업로드 파일 식별·크기가 불확실합니다.");
                    await transfer.VerifyRemoteMatchesLocalAsync(matching[0], localFile, token).ConfigureAwait(false);
                    if (await ShaLocalAsync(localFile, token).ConfigureAwait(false) != initialSha)
                        throw new IOException("전송 도중 원본 내용이 변경됐습니다.");
                    checkpoint.Completed.Add(new(entry.Relative, uploadedId, initialSha, matching[0].Size!.Value));
                    CloudFolderCopyResumeStore.Update(checkpoint);
                    completed.Add(entry.Relative, checkpoint.Completed[^1]);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    System.Net.Http.HttpRequestException or InvalidOperationException or NotSupportedException or
                    ArgumentException or CryptographicException or System.Text.Json.JsonException or OperationCanceledException)
                {
                    throw new CrossPaneFolderCopyException(i, plan.Files.Count, true, ex);
                }
            }
            progress?.Report(new((i + 1L) * 100L, Math.Max(1, plan.Files.Count) * 100L));
        }
        token.ThrowIfCancellationRequested();
        // R45: the original FolderPlan contains only entries captured before the transfer.
        // Rehashing that stale plan misses files/folders ADDED while uploading. Re-enumerate
        // the live tree before retiring the journal; never silently mark such work complete.
        FolderPlan currentTree = await PreflightAsync(source, token).ConfigureAwait(false);
        if (LocalManifestSha256(currentTree) != manifest)
            throw new IOException("전송 중 원본 폴더의 파일·하위 폴더 구성이 변경됐습니다. 복구 기록을 보존합니다.");
        CloudFolderCopyResumeStore.Complete(checkpoint);
        progress?.Report(new(100, 100));
        }
        finally { ActiveLocalCloudFolders.TryRemove(sourcePath, out _); }
    }

    private static async Task CopyFilesAsync(IReadOnlyList<PlannedFile> files,
        Func<CrossPaneCopySource, string, IProgress<CloudTransferProgress>, Task> copy,
        IProgress<CloudTransferProgress>? progress, CancellationToken token,
        bool partialRemoteMayRemain)
    {
        if (files.Count == 0) { progress?.Report(new(100, 100)); return; }
        for (int i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            int index = i;
            var item = files[i];
            var itemProgress = new Progress<CloudTransferProgress>(p =>
                progress?.Report(new(index * 100L + p.Percent, files.Count * 100L)));
            try
            {
                await copy(item.Source, item.Relative, itemProgress).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Net.Http.HttpRequestException or InvalidOperationException or NotSupportedException or
                ArgumentException or System.Security.Cryptography.CryptographicException or
                System.Text.Json.JsonException or OperationCanceledException)
            {
                // Per-folder progress is preserved without exposing provider URLs or auth material.
                throw new CrossPaneFolderCopyException(index, files.Count, partialRemoteMayRemain, ex);
            }
            progress?.Report(new((index + 1L) * 100L, files.Count * 100L));
        }
    }
}
