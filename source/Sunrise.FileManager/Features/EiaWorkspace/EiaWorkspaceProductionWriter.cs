using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// v1.8.1: explicit, project-scoped, directory-only execution.
/// The existing read-only acknowledgement/manifest remains an observation, NOT a
/// transferable write token. This separate command requires a new final consent,
/// re-observes the exact approved tree, and performs only handle-relative creates.
/// Files, folders already present, existing settings and source data are never
/// moved, removed, renamed, truncated or overwritten.
/// </summary>
internal static class EiaWorkspaceProductionWriter
{
    public static EiaFolderCreationResult ExecuteReviewed(
        EiaWorkspaceExecutionReadiness review,
        EiaWorkspaceProjectReviewAcknowledgement acknowledgement,
        EiaWorkspaceExecutionPreflightResult displayedPreflight,
        string finalConfirmation,
        CancellationToken cancellationToken = default,
        Action<int, int, string>? onProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        ArgumentNullException.ThrowIfNull(displayedPreflight);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("EIASS 실제 폴더 생성은 Windows에서만 지원합니다.");
        // No separate company template is required. Only the built-in baseline
        // tree can be written; external UNAPPROVED profile drafts are excluded.
        if (review.SourceLabel != "기본안")
            throw new InvalidDataException("회사/발주처 미승인 초안을 실제 폴더 생성에 사용할 수 없습니다. 기본안을 선택하세요.");
        if (!string.Equals(finalConfirmation, "CREATE " + review.WorkspaceFolderName,
                StringComparison.Ordinal))
            throw new InvalidDataException("사업별 최종 폴더 생성 확인 문구가 일치하지 않습니다.");
        if (DateTimeOffset.UtcNow - displayedPreflight.CheckedAtUtc > TimeSpan.FromMinutes(5) ||
            displayedPreflight.CheckedAtUtc > DateTimeOffset.UtcNow)
            throw new InvalidDataException("점검 후 시간이 지났습니다. 다시 재점검하세요.");

        // Validates all original fields, source hashes, path list, and state of the
        // shown manifest, then performs a second independent path snapshot.
        EiaWorkspaceProjectReviewPlanner.RequireSameReview(acknowledgement, review);
        var shown = EiaWorkspaceExecutionService.PrepareReadOnlyManifest(
            review, acknowledgement, displayedPreflight, cancellationToken);
        var fresh = EiaWorkspaceExecutionPreflight.RecheckReadOnly(
            review, acknowledgement, cancellationToken);
        var current = EiaWorkspaceExecutionService.PrepareReadOnlyManifest(
            review, acknowledgement, fresh, cancellationToken);
        if (!string.Equals(shown.ObservedFingerprintSha256, current.ObservedFingerprintSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(shown.SourceFingerprintSha256, current.SourceFingerprintSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(shown.BaseDirectory, current.BaseDirectory, StringComparison.Ordinal) ||
            !string.Equals(shown.WorkspacePath, current.WorkspacePath, StringComparison.Ordinal) ||
            shown.Paths.Count != current.Paths.Count)
            throw new InvalidDataException("승인 후 폴더 계획/현재 상태가 변경됐습니다. 생성 전 재점검하세요.");
        for (int i = 0; i < shown.Paths.Count; i++)
        {
            if (!Equals(shown.Paths[i], current.Paths[i]))
                throw new InvalidDataException("승인 후 경로 목록이 변경됐습니다. 생성 전 재점검하세요.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        string rootName = review.WorkspaceFolderName;
        var completed = new List<EiaFolderCreationOutcome>(current.Paths.Count);
        int created = 0, existing = 0, failed = 0, notAttempted = 0;
        bool stopped = false;
        using var native = EiaWorkspaceNativeDirectoryWriter.OpenPinnedBase(current.BaseDirectory);
        for (int i = 0; i < current.Paths.Count; i++)
        {
            var item = current.Paths[i];
            if (stopped || cancellationToken.IsCancellationRequested)
            {
                stopped = true;
                notAttempted++;
                completed.Add(new(item.RelativePath, "NOT_ATTEMPTED", "중단되어 생성하지 않음"));
                continue;
            }
            bool enteredNative = false;
            try
            {
                // Fail on an intervening file/junction/reparse, unexpected directory,
                // or missing ancestor. Never silently create unplanned parents.
                EiaWorkspaceCreationEngine.VerifyExistingChain(current.BaseDirectory, cancellationToken);
                bool targetExists = EiaWorkspaceCreationEngine.VerifyExistingChain(
                    item.AbsolutePath, cancellationToken);
                if (item.BeforeState == "EXISTS_DIRECTORY")
                {
                    if (!targetExists) throw new InvalidDataException("기존 폴더가 검사 후 없어졌습니다.");
                    enteredNative = true;
                    native.CreateOrVerify(item, rootName);
                    existing++;
                    completed.Add(new(item.RelativePath, "ALREADY_EXISTS", "기존 폴더 유지"));
                }
                else if (item.BeforeState == "MISSING")
                {
                    if (targetExists) throw new InvalidDataException("생성 직전 다른 프로그램이 폴더를 만들었습니다.");
                    string parent = Path.GetDirectoryName(item.AbsolutePath) ??
                        throw new InvalidDataException("생성 경로 상위 폴더가 없습니다.");
                    if (!EiaWorkspaceCreationEngine.VerifyExistingChain(parent, cancellationToken))
                        throw new InvalidDataException("생성 경로의 상위 폴더가 확인되지 않습니다.");
                    cancellationToken.ThrowIfCancellationRequested();
                    enteredNative = true;
                    native.CreateOrVerify(item, rootName); // one component, FILE_CREATE
                    if (!EiaWorkspaceCreationEngine.VerifyExistingChain(item.AbsolutePath))
                        throw new IOException("생성 후 디렉터리 재조회 실패");
                    created++;
                    completed.Add(new(item.RelativePath, "CREATED", "신규 폴더 확인"));
                }
                else throw new InvalidDataException("계획에 없는 폴더 상태입니다.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopped = true;
                if (enteredNative)
                {
                    failed++;
                    completed.Add(new(item.RelativePath, "FAILED", "중단 후 파일시스템 반영 여부를 재점검해야 합니다."));
                }
                else
                {
                    notAttempted++;
                    completed.Add(new(item.RelativePath, "NOT_ATTEMPTED", "생성 전 중단"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or System.Security.SecurityException or ArgumentException or
                NotSupportedException)
            {
                failed++;
                stopped = true;
                // Do not delete created folders: they may already contain user data.
                completed.Add(new(item.RelativePath, "FAILED", ex.GetType().Name + ": " + ex.Message));
            }
            if (i == current.Paths.Count - 1 || (i + 1) % 25 == 0 || stopped)
                onProgress?.Invoke(i + 1, current.Paths.Count, item.RelativePath);
        }
        return new EiaFolderCreationResult(rootName, current.ObservedFingerprintSha256,
            Array.AsReadOnly(completed.ToArray()), created, existing, failed,
            notAttempted, failed == 0 && notAttempted == 0, true);
    }
}
