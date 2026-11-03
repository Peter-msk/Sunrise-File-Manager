using System.IO;
using System.Windows;
using Sunrise.FileManager.Controls;
using Sunrise.FileManager.Services.CloudDrive;

namespace Sunrise.FileManager;

/// <summary>
/// R24: copy-only drag routing between any visible local/UNC and cloud split panes.
/// All writes pass through the same bounded batch copy, no-overwrite and hash verifier
/// as R23 clipboard operations. No cloud path is impersonated as a Windows path.
/// </summary>
public partial class MainWindow
{
    private void Pane_CloudDragDropRequested(ExplorerPane.CloudDragDropRequest request)
    {
        int index = Array.IndexOf(_panes, request.Pane);
        if (index < 0 || index >= _visiblePanels || _paneLocationModes[index] != "Local") return;
        OpenCloudDragCopy(index, request.Sources, request.TargetFolder, null);
    }

    private void CloudPane_DragCopyRequested(CloudWorkspacePane pane,
        IReadOnlyList<CrossPaneCopySource> sources, string folderId)
    {
        int index = Array.IndexOf(_cloudSlots, pane);
        if (index < 0 || index >= _visiblePanels || _paneLocationModes[index] == "Local") return;
        OpenCloudDragCopy(index, sources, null, folderId);
    }

    private void OpenCloudDragCopy(int destinationIndex, IReadOnlyList<CrossPaneCopySource> sources,
        string? localFolder, string? remoteFolderId)
    {
        if (_working || _aiBusy || _activeRecycleJobs > 0 || _crossPaneCopyBusy || AnyCloudOperationBusy)
        { JobStatus.Text = "다른 파일 작업이 끝난 뒤 드래그 복사를 시작하세요."; return; }
        if (sources.Count == 0 || sources.Count > CrossPaneBatchCopyService.MaximumFiles) return;
        if (destinationIndex < 0 || destinationIndex >= _visiblePanels) return;
        bool cloudOrigin = sources.Any(source => source.RemoteFile is not null);
        if (cloudOrigin && (!sources.All(source => source.RemoteFile is not null) ||
                            !WorkspaceCloudDragSession.IsCurrent(sources)))
        { JobStatus.Text = "클라우드 드래그 세션이 만료됐습니다. 다시 끌어주세요."; return; }
        string mode = _paneLocationModes[destinationIndex];
        var destinations = new[] { new CrossPaneCopyWindow.DestinationOption(destinationIndex,
            $"창 {destinationIndex + 1} · " + (mode == "Local" ? "내 PC · 네트워크" : mode)) };
        bool refreshWarning = false;
        var dialog = new CrossPaneCopyWindow("드래그 복사 · 원본 보존 · 잘라내기/이동 아님", sources.Count,
            destinations, async (_, progress, token) =>
            {
                if (_crossPaneCopyBusy || _paneLocationModes[destinationIndex] != mode)
                    throw new InvalidOperationException("대상 분할창이 변경됐거나 복사 작업 중입니다.");
                if (cloudOrigin && !WorkspaceCloudDragSession.IsCurrent(sources))
                    throw new InvalidOperationException("원본 클라우드 계정 또는 드래그 세션이 바뀌었습니다.");
                CrossPaneCopyTarget target;
                if (mode == "Local")
                {
                    string folder = localFolder ?? throw new InvalidOperationException("드롭할 로컬 폴더가 없습니다.");
                    if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("대상 로컬 폴더가 없습니다.");
                    target = new CrossPaneCopyTarget(folder, null, null, null);
                }
                else
                {
                    var remote = _cloudSlots[destinationIndex]?.SnapshotCopyTarget() ??
                        throw new InvalidOperationException("대상 클라우드 폴더 또는 계정이 변경됐습니다.");
                    if (remoteFolderId is null) throw new InvalidOperationException("드롭할 클라우드 폴더가 없습니다.");
                    target = remote with { RemoteFolderId = remoteFolderId };
                }
                _crossPaneCopyBusy = true;
                try
                {
                    var report = await CrossPaneBatchCopyService.CopyAsync(sources, target, progress, token);
                    try
                    {
                        if (mode == "Local") _panes[destinationIndex].RefreshCurrent();
                        else if (_cloudSlots[destinationIndex] is { } cloud) await cloud.RefreshAsync();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        InvalidOperationException or System.Net.Http.HttpRequestException or OperationCanceledException)
                    {
                        refreshWarning = true;
                    }
                    return report;
                }
                finally { _crossPaneCopyBusy = false; }
            }) { Owner = this };
        dialog.ShowDialog();
        if (dialog.CompletionMessage is { } message)
            JobStatus.Text = "드래그 복사 · " + message +
                (refreshWarning ? " · 대상 목록 갱신 실패(수동 새로고침 필요)" : "");
    }
}
