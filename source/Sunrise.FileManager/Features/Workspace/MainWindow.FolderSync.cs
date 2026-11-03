using System.IO;
using System.Windows;
using Sunrise.FileManager.Controls;
using Sunrise.FileManager.Services.CloudDrive;

namespace Sunrise.FileManager;

public partial class MainWindow
{
    private CrossPaneCopyTarget SnapshotSyncRoot(int index)
    {
        if (index < 0 || index >= _visiblePanels)
            throw new InvalidOperationException("사용할 분할창을 확인하세요.");
        if (_paneLocationModes[index] == "Local")
        {
            string folder = _panes[index].ActiveFolder ??
                throw new InvalidOperationException("로컬 폴더를 먼저 열어 주세요.");
            return new CrossPaneCopyTarget(folder, null, null, null);
        }
        return _cloudSlots[index]?.SnapshotCopyTarget() ??
            throw new InvalidOperationException("클라우드 계정을 연결하고 폴더를 연 뒤 다시 시도하세요.");
    }

    private void FolderSync_Click(object sender, RoutedEventArgs e)
    {
        if (_visiblePanels < 2 || _working || _aiBusy || _activeRecycleJobs > 0 || AnyCloudOperationBusy)
        {
            JobStatus.Text = "폴더 비교에는 두 개 이상의 분할창과 작업 완료 상태가 필요합니다.";
            return;
        }
        int sourceIndex = _activePane;
        CrossPaneCopyTarget source;
        try { source = SnapshotSyncRoot(sourceIndex); }
        catch (InvalidOperationException) { JobStatus.Text = "원본 폴더와 클라우드 연결을 확인하세요."; return; }
        var targets = Enumerable.Range(0, _visiblePanels).Where(i => i != sourceIndex)
            .Select(i => new CrossPaneCopyWindow.DestinationOption(i,
                $"창 {i + 1} · " + (_paneLocationModes[i] == "Local" ? "내 PC · 네트워크" : _paneLocationModes[i])))
            .ToArray();
        var dialog = new FolderSyncWindow(targets,
            async (index, token) =>
            {
                var destination = SnapshotSyncRoot(index);
                return await Task.Run(() => FolderSynchronizationService.PreviewAsync(
                    source, destination, token), token);
            },
            async (index, plan, progress, token) =>
            {
                if (_crossPaneCopyBusy) throw new InvalidOperationException("파일 작업이 진행 중입니다.");
                var actualTarget = SnapshotSyncRoot(index);
                var actualSource = SnapshotSyncRoot(sourceIndex);
                if (!Equals(actualSource.LocalFolder, plan.SourceRoot.LocalFolder) ||
                    !Equals(actualSource.RemoteFolderId, plan.SourceRoot.RemoteFolderId) ||
                    !ReferenceEquals(actualSource.RemoteApi, plan.SourceRoot.RemoteApi) ||
                    !Equals(actualTarget.LocalFolder, plan.TargetRoot.LocalFolder) ||
                    !Equals(actualTarget.RemoteFolderId, plan.TargetRoot.RemoteFolderId) ||
                    !ReferenceEquals(actualTarget.RemoteApi, plan.TargetRoot.RemoteApi))
                    throw new IOException("대상 분할창 위치가 변경됐습니다. 다시 분석해 주세요.");
                _crossPaneCopyBusy = true;
                try
                {
                    var report = await Task.Run(() => FolderSynchronizationService.ApplyAsync(plan, progress, token), token);
                    if (_paneLocationModes[index] == "Local") _panes[index].RefreshCurrent();
                    else if (_cloudSlots[index] is { } pane) await pane.RefreshAsync();
                    return report;
                }
                finally { _crossPaneCopyBusy = false; }
            }) { Owner = this };
        dialog.ShowDialog();
        if (dialog.LastReport is { } result)
            JobStatus.Text = $"폴더 동기화 · 생성 {result.Created}, 실패 {result.Failed}" +
                (result.Canceled ? " · 취소" : "") + " · 기존 항목 보존";
    }
}
