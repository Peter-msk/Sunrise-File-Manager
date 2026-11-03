using System.IO;
using System.Windows;
using Sunrise.FileManager.Controls;
using Sunrise.FileManager.Services.CloudDrive;

namespace Sunrise.FileManager;

/// <summary>R25: explicit menu-only file move. Clipboard cut / drag remain copy-only for cloud.</summary>
public partial class MainWindow
{
    private void CrossPaneMove_Click(object sender, RoutedEventArgs e)
    {
        if (_visiblePanels < 2 || _activePane < 0 || _activePane >= _visiblePanels ||
            _working || _aiBusy || _activeRecycleJobs > 0 || _crossPaneCopyBusy || AnyCloudOperationBusy)
        { JobStatus.Text = "다른 작업이 없는 2개 이상의 분할창에서 파일을 선택하세요."; return; }
        int sourceIndex = _activePane;
        string originalMode = _paneLocationModes[sourceIndex];
        IReadOnlyList<CrossPaneCopySource> sources;
        if (originalMode == "Local")
        {
            var paths = _panes[sourceIndex].SelectedPaths.ToArray();
            if (paths.Length == 0 || paths.Length > CrossPaneBatchCopyService.MaximumFiles ||
                paths.Any(path => !File.Exists(path) || Directory.Exists(path)))
            { JobStatus.Text = "이동할 파일만 선택해 주세요. 폴더 이동은 아직 지원하지 않습니다."; return; }
            sources = paths.Select(path => new CrossPaneCopySource(path, null, null)).ToArray();
        }
        else
        {
            sources = _cloudSlots[sourceIndex]?.SnapshotCopySources() ?? [];
            if (sources.Count == 0 || sources.Any(s => s.RemoteFile is null || s.RemoteFile.IsFolder))
            { JobStatus.Text = "클라우드에서 이동할 파일만 선택하세요. 폴더 이동은 아직 지원하지 않습니다."; return; }
            // Safety first: unavailable provider-atomic retirement must not launch a 'move' dialog.
            // Cloud COPY remains available for Google Drive and unversioned Graph items.
            if (sources.Any(s => s.RemoteApi is not IConditionalCloudRetirement ||
                string.IsNullOrWhiteSpace(s.RemoteFile?.VersionTag)))
            { JobStatus.Text = "이 클라우드 파일은 버전 보호 이동을 지원하지 않습니다. '복사' 기능을 사용하세요."; return; }
        }
        var options = Enumerable.Range(0, _visiblePanels).Where(i => i != sourceIndex)
            .Select(i => new CrossPaneCopyWindow.DestinationOption(i,
                $"창 {i + 1} · " + (_paneLocationModes[i] == "Local" ? "내 PC · 네트워크" : _paneLocationModes[i]))).ToArray();
        bool refreshWarning = false;
        var dialog = new CrossPaneCopyWindow("검증 후 안전 이동 · 성공한 파일만 원본 정리", sources.Count,
            options, async (destinationIndex, progress, token) =>
            {
                if (_crossPaneCopyBusy || _paneLocationModes[sourceIndex] != originalMode)
                    throw new InvalidOperationException("원본 분할창이 변경됐거나 이동 중입니다.");
                string targetMode = _paneLocationModes[destinationIndex];
                CrossPaneCopyTarget target = targetMode == "Local"
                    ? new CrossPaneCopyTarget(_panes[destinationIndex].ActiveFolder ??
                        throw new InvalidOperationException("대상 폴더를 확인하세요."), null, null, null)
                    : _cloudSlots[destinationIndex]?.SnapshotCopyTarget() ??
                        throw new InvalidOperationException("대상 클라우드 계정 또는 폴더가 없습니다.");
                _crossPaneCopyBusy = true;
                try
                {
                    var report = await CrossPaneVerifiedMoveService.MoveAsync(sources, target, progress, token);
                    // Both list refreshes are best effort: a refresh exception must not
                    // erase the actual, already-verified per-file retirement outcomes.
                    try
                    {
                        if (originalMode == "Local") _panes[sourceIndex].RefreshCurrent();
                        else if (_cloudSlots[sourceIndex] is { } remoteSource) await remoteSource.RefreshAsync();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        InvalidOperationException or System.Net.Http.HttpRequestException or OperationCanceledException)
                    {
                        refreshWarning = true;
                    }
                    try
                    {
                        if (targetMode == "Local") _panes[destinationIndex].RefreshCurrent();
                        else if (_cloudSlots[destinationIndex] is { } remoteTarget) await remoteTarget.RefreshAsync();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        InvalidOperationException or System.Net.Http.HttpRequestException or OperationCanceledException)
                    {
                        refreshWarning = true;
                    }
                    return report;
                }
                finally { _crossPaneCopyBusy = false; }
            }, move: true) { Owner = this };
        dialog.ShowDialog();
        if (dialog.CompletionMessage is { } message)
            JobStatus.Text = "안전 이동 · " + message +
                (refreshWarning ? " · 원본/대상 목록 갱신 실패(수동 새로고침 필요)" : "");
    }
}
