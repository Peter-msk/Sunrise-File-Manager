using System.IO;
using System.Security.Cryptography;

namespace Sunrise.FileManager.Services.CloudDrive;

public sealed record BatchCopyProgress(int Completed, int Total, string CurrentFile, int FilePercent);
public enum BatchCopyItemOutcome { Succeeded, Failed, Skipped, Canceled }
public sealed record BatchCopyItemResult(string FileName, bool Succeeded, string Status)
{
    // Keep the three-argument constructor used by existing callers and older test contracts.
    public BatchCopyItemOutcome Outcome { get; init; } = Succeeded
        ? BatchCopyItemOutcome.Succeeded : BatchCopyItemOutcome.Failed;
}
public sealed record BatchCopyReport(IReadOnlyList<BatchCopyItemResult> Items, bool Canceled)
{
    public int Requested { get; init; } = Items.Count;
    public int Succeeded => Items.Count(item => item.Succeeded);
    // Backward-compatible non-success count, including duplicate names and canceled items.
    public int Failed => Items.Count(item => !item.Succeeded);
    public int FailedOnly => Items.Count(item => item.Outcome == BatchCopyItemOutcome.Failed);
    public int Skipped => Items.Count(item => item.Outcome == BatchCopyItemOutcome.Skipped);
    public int CanceledItems => Items.Count(item => item.Outcome == BatchCopyItemOutcome.Canceled);
    public int NotStarted => Math.Max(0, Requested - Items.Count);
}

/// <summary>
/// R21: serial, bounded, explicit batch copies including recursive folder trees.
/// Original bytes are never deleted. Remote file copy uses R18 SHA-256 verification,
/// cloud-to-cloud file transport uses R19 encrypted staging; no automatic overwrite/move.
/// </summary>
public static class CrossPaneBatchCopyService
{
    public const int MaximumFiles = 128;

    private static string NameOf(CrossPaneCopySource source) =>
        source.LocalFile is not null ? Path.GetFileName(Path.TrimEndingDirectorySeparator(source.LocalFile)) :
        source.RemoteFile?.Name ?? throw new ArgumentException("복사 원본이 비어 있습니다.");

    public static async Task<BatchCopyReport> CopyAsync(IReadOnlyList<CrossPaneCopySource> sources,
        CrossPaneCopyTarget target, IProgress<BatchCopyProgress>? progress, CancellationToken cancellationToken)
    {
        if (sources is null || sources.Count == 0 || sources.Count > MaximumFiles)
            throw new ArgumentException($"한 번에 1~{MaximumFiles}개 파일을 선택해 주세요.");
        if (target is null) throw new ArgumentNullException(nameof(target));

        var reports = new List<BatchCopyItemResult>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool canceled = false;
        for (int i = 0; i < sources.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) { canceled = true; break; }
            var source = sources[i];
            string name;
            try { name = source is null ? throw new ArgumentException("원본이 없습니다.") : NameOf(source); }
            catch (ArgumentException)
            {
                reports.Add(new($"선택 항목 {i + 1}", false, "원본 식별 실패 · 다음 항목 계속 진행"));
                progress?.Report(new(i + 1, sources.Count, $"선택 항목 {i + 1}", 0));
                continue;
            }
            if (!names.Add(name))
            {
                reports.Add(new(name, false, "같은 이름의 원본이 여러 개 있어 건너뜀")
                { Outcome = BatchCopyItemOutcome.Skipped });
                progress?.Report(new(i + 1, sources.Count, name, 0));
                continue;
            }
            try
            {
                int completedBefore = i; // Progress callbacks may execute after the loop advances.
                var itemProgress = new Progress<CloudTransferProgress>(p =>
                    progress?.Report(new(completedBefore, sources.Count, name, p.Percent)));
                bool isFolder = source.LocalFile is not null ? Directory.Exists(source.LocalFile) :
                    source.RemoteFile?.IsFolder == true;
                if (isFolder)
                    await CrossPaneFolderCopyService.CopyAsync(source, target, itemProgress, cancellationToken);
                else if (source.LocalFile is not null && target.LocalFolder is not null)
                    await CopyLocalFileAsync(source.LocalFile, target.LocalFolder, itemProgress, cancellationToken);
                else
                    await CrossPaneCloudCopyService.CopyAsync(source, target, itemProgress, cancellationToken);
                reports.Add(new(name, true, isFolder ? "하위 폴더·파일 복사 완료" : "복사 및 검증 완료"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                reports.Add(new(name, false, "사용자 취소 · 부분 원격 폴더는 대상 확인 필요 · 원본 보존")
                { Outcome = BatchCopyItemOutcome.Canceled });
                canceled = true;
                break;
            }
            catch (CrossPaneFolderCopyException ex)
            {
                reports.Add(new(name, false, $"폴더 복사 미완료 · 내부 파일 {ex.CompletedFiles}/{ex.TotalFiles}개 완료 · " +
                    (ex.PartialRemoteMayRemain ? "원격 대상에 부분 파일이 남을 수 있음 · 원본 보존" :
                        "대상 임시 작업이 정리됐는지 확인 필요 · 원본 보존")));
            }
            catch (CloudUploadReviewRequiredException)
            {
                reports.Add(new(name, false, "업로드 완료 여부 미확정 · 복구 기록 보존 · 자동 재업로드 금지 · 대상 폴더 확인 필요"));
            }
            catch (OperationCanceledException)
            {
                // A provider-side timeout is not the same as a user-requested cancellation.
                reports.Add(new(name, false, "서비스 응답 중단 · 완료 여부 확인 필요 · 자동 재전송 금지"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Net.Http.HttpRequestException or InvalidOperationException or
                NotSupportedException or ArgumentException or CryptographicException or
                System.Text.Json.JsonException)
            {
                // Provider exception messages can contain private paths, signed URLs, account IDs.
                string diagnosis = source.LocalFile is not null && target.LocalFolder is not null
                    ? Sunrise.FileManager.Services.NetworkTransferDiagnostics.Explain(ex, source.LocalFile, target.LocalFolder)
                    : "";
                reports.Add(new(name, false, string.IsNullOrWhiteSpace(diagnosis)
                    ? "복사 또는 검증 실패 · 원본 보존 · 부분 원격 폴더 확인 필요"
                    : diagnosis));
            }
            // Completed means number of finished selections; do not double-count FilePercent.
            progress?.Report(new(i + 1, sources.Count, name, 0));
        }
        return new BatchCopyReport(reports, canceled) { Requested = sources.Count };
    }

    private static async Task CopyLocalFileAsync(string sourcePath, string destinationFolder,
        IProgress<CloudTransferProgress>? progress, CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(sourcePath);
        string folder = Path.GetFullPath(destinationFolder);
        if (!Directory.Exists(folder) || !File.Exists(source))
            throw new IOException("원본 파일 또는 대상 폴더가 없습니다.");
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new NotSupportedException("재분석 지점은 일괄 복사 대상에서 제외합니다.");
        string name = Sunrise.FileManager.Services.FileActionsService.ValidateName(Path.GetFileName(source));
        string output = Path.Combine(folder, name);
        if (File.Exists(output) || Directory.Exists(output))
            throw new IOException("동일한 대상 이름이 이미 있어 덮어쓰지 않았습니다.");
        // R26: a canceled/temporary SMB interruption retains an authorized, DPAPI-indexed
        // partial copy. A later explicit Copy action first rechecks the entire prefix.
        await Sunrise.FileManager.Services.ResumableLocalCopyService.CopyOrResumeAsync(
            source, output, progress, cancellationToken).ConfigureAwait(false);
    }
}
