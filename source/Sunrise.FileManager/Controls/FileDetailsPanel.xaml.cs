using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>Independent file metadata/attributes panel. Never touches #memo storage or editor state.</summary>
public partial class FileDetailsPanel : UserControl
{
    private FileEntry? _selected;
    private bool _updatingAttributes;
    private CancellationTokenSource? _detailsCts;
    private CancellationTokenSource? _hashCts;
    public event Action? CloseRequested;
    public event Action? AttributesChanged;
    public string? CurrentFilePath => _selected?.FullPath;

    // A non-mutating focus target; opening the details panel must not toggle attributes.
    public bool FocusDetailsControl() => HashButton.IsEnabled && HashButton.Focus() || FolderSizeButton.Focus();

    public FileDetailsPanel()
    {
        InitializeComponent();
        Unloaded += (_, _) => { _detailsCts?.Cancel(); _hashCts?.Cancel(); };
    }

    public void SelectFile(FileEntry? entry)
    {
        // Results from the previous selection (including failures) must never overwrite
        // the new file details. Cancelling hash suppresses UI publication even if the
        // underlying non-cancellable disk hash is still finishing.
        if (!string.Equals(_selected?.FullPath, entry?.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            _detailsCts?.Cancel();
            _hashCts?.Cancel();
        }
        _selected = entry;
        UpdateDetails(entry);
    }
    private void CloseDetails_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void UpdateDetails(FileEntry? entry)
    {
        DetailName.Text = entry?.Name ?? "파일을 선택하세요";
        DetailType.Text = entry is null ? "—" : $"{entry.TypeName} ({entry.AttributesDisplay})";
        DetailSize.Text = entry is null ? "—" : (entry.IsDirectory ? "폴더 ([용량계산] 클릭)" : $"{entry.SizeDisplay} ({entry.SizeBytes:N0} 바이트)");
        DetailModified.Text = entry?.ModifiedDisplay ?? "—";
        if (DetailCreated is not null) DetailCreated.Text = entry?.CreatedDisplay ?? "—";
        DetailLocation.Text = entry is null ? "—" : Path.GetDirectoryName(entry.FullPath) ?? entry.FullPath;
        if (DetailHash is not null) DetailHash.Text = string.Empty;
        if (HashButton is not null) HashButton.IsEnabled = entry is { IsDirectory: false };
        if (FolderSizeButton is not null) FolderSizeButton.IsEnabled = entry is { IsDirectory: true };

        _updatingAttributes = true;
        try
        {
            if (ReadOnlyCheck is not null)
            {
                ReadOnlyCheck.IsEnabled = entry is not null;
                ReadOnlyCheck.IsChecked = entry?.IsReadOnly == true;
            }
            if (HiddenCheck is not null)
            {
                HiddenCheck.IsEnabled = entry is not null;
                HiddenCheck.IsChecked = entry?.IsHidden == true;
            }
        }
        finally { _updatingAttributes = false; }
    }

    private void AttributeCheck_Click(object sender, RoutedEventArgs e) => ApplySelectedFileAttributes();
    private async void FolderSizeButton_Click(object sender, RoutedEventArgs e) => await RunFolderSizeCalculationAsync();
    private async void HashButton_Click(object sender, RoutedEventArgs e) => await RunFileHashCalculationAsync();

    private void ApplySelectedFileAttributes()
    {
        if (_updatingAttributes || _selected is null) return;
        try
        {
            bool readOnly = ReadOnlyCheck.IsChecked == true;
            bool hidden = HiddenCheck.IsChecked == true;
            FileActionsService.SetFileAttributes(_selected.FullPath, readOnly, hidden);
            AttributesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "파일 속성을 변경하지 못했습니다.\n" + ex.Message,
                "속성 변경 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RunFolderSizeCalculationAsync()
    {
        if (_selected is not { IsDirectory: true } entry || !Directory.Exists(entry.FullPath)) return;
        _detailsCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _detailsCts = cts;
        try
        {
            FolderSizeButton.IsEnabled = false;
            DetailSize.Text = "하위 폴더 용량 계산 중...";
            string path = entry.FullPath;
            FolderSizeSummary summary = await Task.Run(() => FileActionsService.CalculateFolderSize(path, cts.Token), cts.Token);
            if (ReferenceEquals(_detailsCts, cts) && !cts.IsCancellationRequested &&
                string.Equals(_selected?.FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                DetailSize.Text = $"{FileEntry.HumanSize(summary.TotalBytes)} ({summary.TotalBytes:N0} B) · 파일 {summary.FileCount:N0}개 · 폴더 {summary.FolderCount:N0}개"
                    + (summary.Truncated ? " (상한 도달)" : "");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_detailsCts, cts) && !cts.IsCancellationRequested &&
                string.Equals(_selected?.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase))
                DetailSize.Text = "용량 계산 실패: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_detailsCts, cts))
            {
                _detailsCts = null;
                FolderSizeButton.IsEnabled = _selected is { IsDirectory: true };
            }
        }
    }

    private async Task RunFileHashCalculationAsync()
    {
        if (_selected is not { IsDirectory: false } entry || !File.Exists(entry.FullPath)) return;
        _hashCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _hashCts = cts;
        string path = entry.FullPath;
        try
        {
            HashButton.IsEnabled = false;
            DetailHash.Text = "SHA-256 계산 중...";
            // Existing hash helper has no cancellation parameter. Cancel may stop a
            // queued task; an already-running hash is allowed to finish silently.
            string hex = await Task.Run(() => FileActionsService.ComputeSha256Hex(path), cts.Token);
            if (ReferenceEquals(_hashCts, cts) && !cts.IsCancellationRequested &&
                string.Equals(_selected?.FullPath, path, StringComparison.OrdinalIgnoreCase))
                DetailHash.Text = $"SHA-256: {hex}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_hashCts, cts) && !cts.IsCancellationRequested &&
                string.Equals(_selected?.FullPath, path, StringComparison.OrdinalIgnoreCase))
                DetailHash.Text = "해시 계산 실패: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_hashCts, cts))
            {
                _hashCts = null;
                HashButton.IsEnabled = _selected is { IsDirectory: false };
            }
        }
    }

}
