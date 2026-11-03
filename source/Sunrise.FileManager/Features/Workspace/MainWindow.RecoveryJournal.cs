using System.IO;
using System.Windows;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager;

public partial class MainWindow
{
    private void ResumePartCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (_working || _aiBusy || _activeRecycleJobs > 0 || _crossPaneCopyBusy || AnyCloudOperationBusy) return;
        IReadOnlyList<ResumableLocalCopyService.Checkpoint> jobs;
        try { jobs = ResumableLocalCopyService.Pending(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "중단 복사 기록이 손상됐거나 접근할 수 없습니다. 원본·부분파일을 보존하고 관리자에게 확인받으세요.",
                "전송 복구 기록 점검", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (jobs.Count == 0)
        { MessageBox.Show(this, "정리할 중단 복사 기록이 없습니다.", "복사 임시파일 정리"); return; }
        var latest = jobs.OrderByDescending(x => x.CreatedUtc).First();
        if (MessageBox.Show(this,
            $"가장 최근 복구 기록의 임시파일과 메타데이터를 삭제할까요?\n" +
            $"원본: {latest.Source}\n최종 대상(보존): {latest.Destination}\n" +
            $"삭제할 임시파일: {latest.Part}\n\n" +
            "이 작업으로 중단된 바이트를 재사용할 수 없습니다. 원본 파일과 이미 게시된 최종 파일은 삭제하지 않습니다.",
            "복구 기록 직접 정리 · 재확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { ResumableLocalCopyService.Discard(latest); JobStatus.Text = "승인된 중단 복사 임시파일과 기록을 정리했습니다."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "임시파일 정리가 거부됐습니다. 회사 보안 정책이나 사용 중인 파일을 확인하세요.",
            "복구 정리 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void CloudUploadSessionCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (_working || _aiBusy || _activeRecycleJobs > 0 || _crossPaneCopyBusy || AnyCloudOperationBusy) return;
        try
        {
            var sessions = Sunrise.FileManager.Services.CloudDrive.CloudUploadResumeStore.Pending();
            if (sessions.Count == 0)
            { MessageBox.Show(this, "중단된 클라우드 업로드 세션 기록이 없습니다.", "클라우드 업로드 복구"); return; }
            var latest = sessions.OrderByDescending(x => x.CreatedUtc).First();
            if (MessageBox.Show(this,
                $"가장 최근 중단 업로드의 복구 기록을 삭제할까요?\n" +
                $"공급자: {latest.Provider}\n원본 파일(보존): {latest.LocalPath}\n" +
                $"원격 이름: {latest.Name}\n\n" +
                "암호화된 세션 주소와 복구 메타데이터만 삭제합니다. " +
                "원본 파일·이미 전송된 데이터·클라우드 대상은 삭제하지 않습니다. " +
                "업로드 마지막 응답이 유실된 경우 클라우드 대상 폴더에서 완료 여부를 먼저 직접 확인하세요. " +
                "이 기록을 정리한 뒤 재업로드하면 같은 이름의 파일이 중복될 수 있습니다. " +
                "서버의 기존 업로드 세션은 만료될 때까지 남을 수 있습니다.",
                "R28 업로드 복구 세션 정리 · 사용자 확인", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Sunrise.FileManager.Services.CloudDrive.CloudUploadResumeStore.Discard(latest);
            JobStatus.Text = "승인된 업로드 복구 세션 기록을 정리했습니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "업로드 복구 기록을 정리할 수 없습니다. Windows 사용자 권한·보안정책을 확인하세요.",
            "업로드 복구 기록 오류", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void CloudDownloadPartCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (_working || _aiBusy || _activeRecycleJobs > 0 || _crossPaneCopyBusy || AnyCloudOperationBusy) return;
        IReadOnlyList<Sunrise.FileManager.Services.CloudDrive.CloudDownloadResumeStore.Checkpoint> jobs;
        try { jobs = Sunrise.FileManager.Services.CloudDrive.CloudDownloadResumeStore.Pending(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "클라우드 다운로드 복구 기록에 접근할 수 없습니다. 연결 경로 또는 Windows 권한을 확인하세요.",
                "클라우드 복구 기록 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (jobs.Count == 0)
        { MessageBox.Show(this, "중단 클라우드 다운로드 기록이 없습니다.", "클라우드 복구 정리"); return; }
        var latest = jobs.OrderByDescending(x => x.CreatedUtc).First();
        if (MessageBox.Show(this,
            $"가장 최근 중단 클라우드 다운로드의 임시파일만 삭제할까요?\n" +
            $"서비스: {latest.Provider}\n대상 파일(보존): {latest.Destination}\n" +
            $"삭제 대상 임시파일: {latest.Part}\n\n" +
            "중단된 데이터는 이어받지 못합니다. 원격 원본과 완료된 대상 파일은 삭제하지 않습니다.",
            "R27 클라우드 복구 임시파일 정리 · 사용자 확인", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            Sunrise.FileManager.Services.CloudDrive.CloudDownloadResumeStore.Discard(latest);
            JobStatus.Text = "클라우드 중단 다운로드 임시파일과 복구 기록을 정리했습니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "복구 임시파일 정리가 거부됐습니다. 조직 정책과 파일 사용 상태를 확인하세요.",
            "클라우드 복구 정리 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void CloudFolderRecoveryCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (_working || _aiBusy || _activeRecycleJobs > 0 || _crossPaneCopyBusy || AnyCloudOperationBusy) return;
        try
        {
            var checkpoints = Sunrise.FileManager.Services.CloudDrive.CloudFolderCopyResumeStore.Pending();
            if (checkpoints.Count == 0)
            { MessageBox.Show(this, "중단된 클라우드 폴더 복구 기록이 없습니다.", "폴더 복구 기록"); return; }
            var latest = checkpoints.OrderByDescending(x => x.CreatedUtc).First();
            if (MessageBox.Show(this,
                $"가장 최근 클라우드 폴더 전송 기록을 정리할까요?\n" +
                $"공급자: {latest.Provider}\n로컬 원본(보존): {latest.SourcePath}\n" +
                $"원격 폴더명: {latest.RootName}\n검증 완료 기록: {latest.Completed.Count}개\n\n" +
                "원본 파일과 원격에 이미 생성된 파일·폴더는 삭제되지 않습니다. " +
                "복구 기록을 지우면 이어받을 수 없습니다. 클라우드 대상 내용부터 확인하세요. " +
                "남은 대상 폴더와 충돌할 수 있으므로 무조건 처음부터 재업로드하지 마세요.",
                "중단 폴더 복구 기록만 정리 · 최종 확인", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            Sunrise.FileManager.Services.CloudDrive.CloudFolderCopyResumeStore.Discard(latest);
            JobStatus.Text = "승인된 클라우드 폴더 복구 기록만 정리했습니다. 원본·대상 파일은 보존했습니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "클라우드 폴더 복구 기록을 확인·정리할 수 없습니다. 권한과 기록 무결성을 점검하세요.",
            "폴더 복구 기록 오류", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void RecoveryJournal_Click(object sender, RoutedEventArgs e)
    {
        if (_working || _aiBusy || _activeRecycleJobs > 0) return;
        IReadOnlyList<Sunrise.FileManager.Services.CloudDrive.CloudFolderCopyResumeStore.Checkpoint> folders;
        try { folders = Sunrise.FileManager.Services.CloudDrive.CloudFolderCopyResumeStore.Pending(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "클라우드 폴더 복구 기록을 읽을 수 없습니다. 기존 기록은 보존합니다.",
                "폴더 복구 기록 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (folders.Count > 0)
        {
            var latestFolder = folders.OrderByDescending(x => x.CreatedUtc).First();
            MessageBox.Show(this,
                $"중단된 클라우드 폴더 복사 {folders.Count}건\n" +
                $"가장 최근: {latestFolder.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} / {latestFolder.Provider}\n" +
                $"원본: {latestFolder.SourcePath}\n클라우드 폴더: {latestFolder.RootName}\n" +
                $"검증 완료: {latestFolder.Completed.Count}개\n\n" +
                "동일 계정으로 로그인해 같은 원본 폴더를 선택하고 같은 클라우드 대상에 " +
                "분할창 복사를 다시 명시적으로 실행하세요. 기록된 대상 ID와 완료 파일의 " +
                "SHA-256을 다시 검사한 후 남은 파일을 복사합니다. " +
                "원본 변경·계정 변경·대상 ID 충돌 시 자동 재개하지 않습니다. " +
                "기록을 포기하려면 '중단 클라우드 폴더 복구 기록 정리'를 이용하세요.",
                "R43 클라우드 폴더 복구", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var uploads = Sunrise.FileManager.Services.CloudDrive.CloudUploadResumeStore.Pending();
        if (uploads.Count > 0)
        {
            var recent = uploads.OrderByDescending(x => x.CreatedUtc).First();
            MessageBox.Show(this,
                $"중단된 클라우드 업로드 {uploads.Count}건\n" +
                $"가장 최근: {recent.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} / {recent.Provider}\n" +
                $"원본: {recent.LocalPath}\n대상 파일명: {recent.Name}\n\n" +
                "같은 공급자 계정에 로그인하고, 같은 파일을 선택해 다시 업로드하세요. " +
                "원본 SHA-256·수정 시각과 계정·세션 만료를 확인한 후 서버 수신 위치부터 이어받습니다. " +
                "세션이 만료됐거나 완료 여부가 불명확하면 자동으로 새 세션을 만들거나 기존 파일을 덮어쓰지 않습니다. " +
                "원격 폴더에서 해당 업로드가 완료됐는지 확인한 뒤 복구 기록을 수동 정리하세요. " +
                "인증 토큰과 서명된 세션 URL을 이 화면에 표시하지 않습니다.",
                "R28 클라우드 업로드 복구", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        IReadOnlyList<Sunrise.FileManager.Services.CloudDrive.CloudDownloadResumeStore.Checkpoint> cloudDownloads;
        try { cloudDownloads = Sunrise.FileManager.Services.CloudDrive.CloudDownloadResumeStore.Pending(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "다운로드 복구 기록을 확인할 수 없습니다. 연결 경로·Windows 권한을 점검하세요.",
                "클라우드 복구 기록 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (cloudDownloads.Count > 0)
        {
            var latestDownload = cloudDownloads.OrderByDescending(x => x.CreatedUtc).First();
            MessageBox.Show(this,
                $"중단된 클라우드 다운로드 {cloudDownloads.Count}건\n" +
                $"가장 최근: {latestDownload.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} / {latestDownload.Provider}\n" +
                $"대상: {latestDownload.Destination}\n\n" +
                "같은 계정으로 다시 로그인한 다음 같은 파일·대상을 명시적으로 다운로드하세요. " +
                "원격 파일 ID·버전·크기와 로그인 계정 지문이 일치할 때에만 남은 바이트를 요청합니다. " +
                "이전 형식의 복구 기록은 안전상 자동 재개하지 않으며, 사용자가 직접 정리할 수 있습니다. " +
                "7일이 지났거나 원격 파일이 바뀐 기록은 이어받지 않습니다. " +
                "원격 원본 파일은 절대로 삭제하지 않습니다.",
                "R27 클라우드 다운로드 복구", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        IReadOnlyList<ResumableLocalCopyService.Checkpoint> resumable;
        try { resumable = ResumableLocalCopyService.Pending(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "로컬·NAS 전송 복구 기록이 손상되었거나 접근할 수 없습니다. 임시파일과 원본은 보존합니다. 관리자에게 확인받으세요.",
                "전송 복구 기록 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (resumable.Count > 0)
        {
            var recent = resumable.OrderByDescending(x => x.CreatedUtc).First();
            MessageBox.Show(this,
                $"바이트 이어받기 가능 기록: {resumable.Count}개\n" +
                $"가장 최근: {recent.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
                $"원본: {recent.Source}\n대상: {recent.Destination}\n\n" +
                "다시 해당 원본을 선택하고 대상 분할창에서 '분할창 간 파일 복사'를 명시적으로 실행하세요. " +
                "원본 길이/수정시각과 임시파일 전체 접두 구간을 확인한 다음 남은 바이트만 전송합니다. " +
                "기록이 오래됐거나 원본이 변경됐다면 자동 덮어쓰기·복구를 하지 않습니다. " +
                "작업 미완료 임시파일은 대상 폴더에서 관리자가 확인할 수 있습니다.",
                "R26 로컬·네트워크 전송 복구", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var jobs = TransferRecoveryJournal.Pending();
        if (jobs.Count == 0) { MessageBox.Show(this, "미완료 전송 복구 기록이 없습니다.", "전송 복구"); return; }
        var latest = jobs[0];
        var safe = TransferRecoveryJournal.EligibleCopySources(latest);
        string examples = string.Join("\n", latest.Sources.Take(5).Select(x => x.Path));
        string status = $"미완료 작업 기록: {jobs.Count}개\n최신: {latest.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm} / {latest.Kind} / {latest.Sources.Count}개 · 게시 완료 {latest.Published.Count}개\n대상: {latest.Destination}\n원본 예시:\n{examples}\n\n";
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
