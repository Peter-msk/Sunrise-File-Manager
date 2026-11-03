namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>
/// R56: one authoritative, safe-to-display outcome summary for the transfer dialog
/// and all parent-window entry points (pane, clipboard, drag/drop, verified move).
/// Never interpolate provider exception text, URLs, tokens or raw remote paths.
/// </summary>
public static class BatchCopyUiSummary
{
    public static string Format(BatchCopyReport report, bool move)
    {
        ArgumentNullException.ThrowIfNull(report);
        // "Failed" is the legacy all-non-success count; the visible summary must
        // use exclusive outcomes to avoid treating skipped/canceled as failed.
        string text = $"선택 {report.Requested} · 처리 {report.Items.Count} · 성공 {report.Succeeded} · " +
            $"실패 {report.FailedOnly} · 건너뜀 {report.Skipped} · 취소 {report.CanceledItems} · " +
            $"미시작 {report.NotStarted}";
        if (report.Canceled) text += " · 작업 취소됨";
        text += move ? " · 성공 항목만 원본 정리" : " · 원본 보존";
        if (NeedsReview(report)) text += " · 대상 확인 필요";
        return text;
    }

    public static bool NeedsReview(BatchCopyReport report) => report.Canceled ||
        report.FailedOnly > 0 || report.Skipped > 0 || report.CanceledItems > 0 || report.NotStarted > 0;

    public static string Incomplete(bool move, bool userCanceled) =>
        userCanceled
            ? (move ? "이동 취소 요청 · 완료 항목은 원본이 정리됐을 수 있음 · 원본과 대상 모두 확인 필요"
                    : "복사 취소 요청 · 원본 보존 · 완료되거나 일부 생성된 대상 확인 필요")
            : (move ? "이동 결과 미확정 · 원본과 대상 모두 확인 필요 · 자동 재시도 금지"
                    : "복사 결과 미확정 · 원본 보존 · 일부 생성된 대상 확인 필요 · 자동 재시도 금지");
}
