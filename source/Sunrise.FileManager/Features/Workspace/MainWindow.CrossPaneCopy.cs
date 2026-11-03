using System.IO;
using System.Windows;
using Sunrise.FileManager.Controls;
using Sunrise.FileManager.Services.CloudDrive;

namespace Sunrise.FileManager;

/// <summary>
/// R21 explicit split-to-split file/folder tree copy. Local clipboard/undo is unchanged.
/// Bounded recursion, never implicit overwrite, and no source deletion.
/// </summary>
public partial class MainWindow
{
    private void CrossPaneCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_visiblePanels < 2 || _activePane < 0 || _activePane >= _visiblePanels ||
            _working || _aiBusy || _activeRecycleJobs > 0 || AnyCloudOperationBusy)
        {
            JobStatus.Text = "분할창이 2개 이상이고 진행 중인 작업이 없어야 복사할 수 있습니다.";
            return;
        }
        int sourceIndex = _activePane;
        bool sourceLocal = _paneLocationModes[sourceIndex] == "Local";
        IReadOnlyList<CrossPaneCopySource> sources;
        string label;
        if (sourceLocal)
        {
            var paths = _panes[sourceIndex].SelectedPaths.ToArray();
            if (paths.Length == 0 || paths.Length > CrossPaneBatchCopyService.MaximumFiles ||
                paths.Any(path => !File.Exists(path) && !Directory.Exists(path)))
            {
                JobStatus.Text = $"복사할 파일·폴더를 1~{CrossPaneBatchCopyService.MaximumFiles}개 선택해 주세요.";
                return;
            }
            sources = paths.Select(path => new CrossPaneCopySource(path, null, null)).ToArray();
            label = $"내 PC · 선택 항목 {sources.Count}개";
        }
        else
        {
            sources = _cloudSlots[sourceIndex]?.SnapshotCopySources() ?? [];
            if (sources.Count == 0)
            {
                JobStatus.Text = "클라우드 파일·폴더를 선택해 주세요.";
                return;
            }
            label = $"{_paneLocationModes[sourceIndex]} · 선택 항목 {sources.Count}개";
        }

        var destinations = Enumerable.Range(0, _visiblePanels)
            .Where(index => index != sourceIndex)
            .Select(index => new CrossPaneCopyWindow.DestinationOption(index,
                $"창 {index + 1} · " + (_paneLocationModes[index] == "Local" ? "내 PC" : _paneLocationModes[index])))
            .ToArray();
        if (destinations.Length == 0)
        {
            JobStatus.Text = "복사할 대상 분할창을 선택해 주세요.";
            return;
        }

        bool refreshWarning = false;
        var dialog = new CrossPaneCopyWindow(label, sources.Count, destinations, async (destinationIndex, progress, token) =>
        {
            if (_crossPaneCopyBusy) throw new InvalidOperationException("이미 복사 중입니다.");
            CrossPaneCopyTarget target;
            if (_paneLocationModes[destinationIndex] == "Local")
            {
                string? folder = _panes[destinationIndex].ActiveFolder;
                if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("대상 로컬 폴더를 확인하세요.");
                target = new CrossPaneCopyTarget(folder, null, null, null);
            }
            else
            {
                target = _cloudSlots[destinationIndex]?.SnapshotCopyTarget() ??
                    throw new InvalidOperationException("대상 클라우드 계정을 연결하고 폴더를 연 뒤 다시 시도하세요.");
            }
            _crossPaneCopyBusy = true;
            try
            {
                var report = await CrossPaneBatchCopyService.CopyAsync(sources, target, progress, token);
                try
                {
                    if (_paneLocationModes[destinationIndex] == "Local") _panes[destinationIndex].RefreshCurrent();
                    else if (_cloudSlots[destinationIndex] is { } cloud) await cloud.RefreshAsync();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    InvalidOperationException or System.Net.Http.HttpRequestException or OperationCanceledException)
                {
                    // UI listing refresh failed; retain the authoritative copy outcomes.
                    refreshWarning = true;
                }
                return report;
            }
            finally { _crossPaneCopyBusy = false; }
        }) { Owner = this };
        dialog.ShowDialog();
        if (dialog.CompletionMessage is { } message)
            JobStatus.Text = "분할창 간 복사 · " + message +
                (refreshWarning ? " · 대상 목록 갱신 실패(수동 새로고침 필요)" : "");
    }
}
