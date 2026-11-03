using System.IO;
using System.Windows;
using Sunrise.FileManager.Controls;
using Sunrise.FileManager.Services.CloudDrive;

namespace Sunrise.FileManager;

/// <summary>R23: route cloud clipboard copies through the verified cross-pane copy engine.
/// No remote IDs are ever passed to the Windows shell, and no cut/move is inferred.
/// </summary>
public partial class MainWindow
{
    private void Pane_CloudPasteRequested(ExplorerPane.CloudClipboardPasteRequest request)
    {
        int index = Array.IndexOf(_panes, request.Pane);
        if (index < 0 || index >= _visiblePanels || _paneLocationModes[index] != "Local") return;
        try
        {
            if (!WorkspaceCloudClipboard.TryReadCloudCopy(out var sources))
            {
                JobStatus.Text = "클라우드 복사 정보가 만료됐습니다. 원본에서 다시 복사하세요.";
                return;
            }
            OpenCloudClipboardCopy(index, sources, request.TargetFolder);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            JobStatus.Text = "클립보드를 읽지 못했습니다. 다시 시도하세요.";
        }
    }

    private void CloudPane_ClipboardPasteRequested(CloudWorkspacePane pane, IReadOnlyList<CrossPaneCopySource> sources)
    {
        int index = Array.IndexOf(_cloudSlots, pane);
        if (index < 0 || index >= _visiblePanels || _paneLocationModes[index] == "Local") return;
        OpenCloudClipboardCopy(index, sources, null);
    }

    private void OpenCloudClipboardCopy(int destinationIndex, IReadOnlyList<CrossPaneCopySource> sources, string? explicitLocalFolder)
    {
        if (_working || _aiBusy || _activeRecycleJobs > 0 || _crossPaneCopyBusy || AnyCloudOperationBusy)
        {
            JobStatus.Text = "다른 파일 작업이 끝난 후 붙여넣으세요.";
            return;
        }
        if (sources.Count == 0 || sources.Count > CrossPaneBatchCopyService.MaximumFiles) return;
        if (destinationIndex < 0 || destinationIndex >= _visiblePanels) return;
        string mode = _paneLocationModes[destinationIndex];
        var destination = new CrossPaneCopyWindow.DestinationOption(destinationIndex,
            $"창 {destinationIndex + 1} · " + (mode == "Local" ? "내 PC · 네트워크" : mode));
        bool refreshWarning = false;
        var dialog = new CrossPaneCopyWindow("클립보드 복사 · 원본은 보존됩니다.", sources.Count,
            [destination], async (_, progress, cancellationToken) =>
            {
                if (_crossPaneCopyBusy) throw new InvalidOperationException("복사 작업 진행 중");
                if (_paneLocationModes[destinationIndex] != mode)
                    throw new InvalidOperationException("복사 대상이 변경됨");
                if (sources.Any(source => source.RemoteFile is not null) &&
                    (!WorkspaceCloudClipboard.TryReadCloudCopy(out var current) || !ReferenceEquals(current, sources)))
                    throw new InvalidOperationException("클라우드 계정 또는 원본 복사 정보가 변경됐습니다. 다시 복사하세요.");
                CrossPaneCopyTarget target;
                if (mode == "Local")
                {
                    string folder = explicitLocalFolder ?? _panes[destinationIndex].ActiveFolder ?? "";
                    if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("대상 폴더가 없습니다.");
                    target = new CrossPaneCopyTarget(folder, null, null, null);
                }
                else
                {
                    target = _cloudSlots[destinationIndex]?.SnapshotCopyTarget() ??
                        throw new InvalidOperationException("대상 클라우드 계정 또는 폴더가 변경됐습니다.");
                }
                _crossPaneCopyBusy = true;
                try
                {
                    var result = await CrossPaneBatchCopyService.CopyAsync(sources, target, progress, cancellationToken);
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
                    return result;
                }
                finally { _crossPaneCopyBusy = false; }
            }) { Owner = this };
        dialog.ShowDialog();
        if (dialog.CompletionMessage is { } message)
            JobStatus.Text = "클립보드 복사 · " + message +
                (refreshWarning ? " · 대상 목록 갱신 실패(수동 새로고침 필요)" : "");
    }
}
