using System.Windows;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager;

public partial class MainWindow
{
    private async void RecoveryJournal_Click(object sender, RoutedEventArgs e)
    {
        if (_working || _aiBusy || _activeRecycleJobs > 0) return;
        var jobs = TransferRecoveryJournal.Pending();
        if (jobs.Count == 0) { MessageBox.Show(this, "미완료 전송 복구 기록이 없습니다.", "전송 복구"); return; }
        var latest = jobs[0];
        var safe = TransferRecoveryJournal.EligibleCopySources(latest);
        string examples = string.Join("\n", latest.Sources.Take(5).Select(x => x.Path));
        string status = $"미완료 작업 기록: {jobs.Count}개\n최신: {latest.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm} / {latest.Kind} / {latest.Sources.Count}개\n대상: {latest.Destination}\n원본 예시:\n{examples}\n\n";
        if (latest.Kind != TransferKind.Copy || latest.Sources.Any(s => s.Directory))
        {
            MessageBox.Show(this, status + "폴더 또는 이동 작업은 원본·대상 상태를 직접 확인해야 합니다. 자동으로 재실행하지 않습니다.\n현재 기록은 보존합니다.",
                "전송 복구 · 검토 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (safe.Count == 0)
        {
            if (MessageBox.Show(this, status + "자동으로 재시도 가능한 누락 파일이 없습니다.\n대상 파일을 직접 확인한 경우에만 이 기록을 지우세요.",
                "전송 복구", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                TransferRecoveryJournal.Discard(latest);
            return;
        }
        if (MessageBox.Show(this, status + $"안전 조건에 맞는 {safe.Count}개 파일만 복사를 다시 시도할까요?\n파일 변경 여부를 검사하며 대상 파일은 덮어쓰지 않습니다.",
            "전송 복구 · 승인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunTransferAsync(safe, latest.Destination, TransferKind.Copy, _activePane, _activePane);
        // Original interrupted record is not automatically deleted: collisions and partial
        // completion require user review, even when the new transaction succeeds.
        MessageBox.Show(this, "복구 재시도 절차가 종료되었습니다. 파일별 실제 처리 결과와 대상 폴더를 확인해 주세요.\n실패 또는 건너뜀 항목이 있을 수 있으므로 이전 기록은 보존합니다.", "전송 복구");
    }
}
