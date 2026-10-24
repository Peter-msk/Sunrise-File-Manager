using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

public partial class ExplorerPane
{
    private async void SelectAllResults_Click(object sender, RoutedEventArgs e)
    {
        if (!_actionsEnabled || ActiveFolder is not string folder || _selectAllCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _selectAllCancellation = cancellation;
        int generation = _listingGeneration;
        string query = SearchBox.Text.Trim();
        string category = _categoryFilter;
        try
        {
            PaneStatus.Text = "전체 결과 선택 중…";
            IReadOnlyList<string> paths;
            if (UsesRecursiveResults)
            {
                // Reject an incomplete/in-progress tree or Windows-index result instead of selecting an undisclosed subset.
                if (_searchCache is null || _searchCache.Warning?.Contains("중단") == true ||
                    _searchCache.Warning?.Contains("한도") == true)
                    throw new InvalidOperationException("검색을 완료한 후 전체 선택을 다시 실행하세요. 결과 한도에 도달했다면 검색 범위를 좁혀주세요.");
                var matches = await Task.Run(() => FileListView.Apply(_searchCache.Entries, "", _sortField,
                    _sortDescending, cancellation.Token, category, _showHidden), cancellation.Token);
                paths = matches.Select(x => x.FullPath).ToArray();
            }
            else paths = await DirectoryScanner.ReadMatchingPathsAsync(folder, query, category, _showHidden,
                    PagedSelectionSet.MaxSelection, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (paths.Count > PagedSelectionSet.MaxSelection)
                throw new InvalidOperationException("전체 선택 개수가 상한을 초과했습니다. 검색 범위를 좁혀주세요.");
            if (generation != _listingGeneration || !string.Equals(folder, ActiveFolder, StringComparison.OrdinalIgnoreCase)
                || query != SearchBox.Text.Trim() || category != _categoryFilter) return;
            if (!_pagedSelection.ReplaceAll(paths)) throw new InvalidOperationException("전체 선택을 적용할 수 없습니다.");
            _reloadingSelection = true;
            try
            {
                FilesGrid.UnselectAll();
                foreach (FileEntry row in FilesGrid.Items.OfType<FileEntry>())
                {
                    row.IsChecked = _pagedSelection.Contains(row.FullPath);
                    if (row.IsChecked) FilesGrid.SelectedItems.Add(row);
                }
            }
            finally { _reloadingSelection = false; }
            PaneStatus.Text = $"{paths.Count:N0}개 전체 결과 선택 완료";
            UpdateFooter(); UpdateDetails(); SelectionContextChanged?.Invoke(this);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "전체 결과 선택", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { if (ReferenceEquals(_selectAllCancellation, cancellation)) _selectAllCancellation = null; }
    }
}
