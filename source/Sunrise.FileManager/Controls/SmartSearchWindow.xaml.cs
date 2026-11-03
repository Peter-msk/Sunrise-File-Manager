using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>Non-destructive mini explorer. Uses the same R09 filtering/search engine as the main panes.</summary>
public partial class SmartSearchWindow : Window
{
    private const int RenderLimit = 2000;
    private readonly Action<string, bool> _revealInExplorer;
    private CancellationTokenSource? _searchCts;
    private int _generation;
    private bool _initializing = true;
    private static readonly object EmptyNode = new();

    public SmartSearchWindow(string? initialFolder, Action<string, bool> revealInExplorer)
    {
        InitializeComponent();
        _revealInExplorer = revealInExplorer ?? throw new ArgumentNullException(nameof(revealInExplorer));
        foreach (var (key, entry) in ExplorerSearchFilter.Presets)
            TypeCombo.Items.Add(new ComboBoxItem { Content = entry.Label, Tag = key });
        TargetCombo.SelectedIndex = 0;
        TypeCombo.SelectedIndex = 0;
        ScopeCombo.SelectedIndex = 1;
        FolderPathBox.Text = Directory.Exists(initialFolder) ? Path.GetFullPath(initialFolder!) :
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var (name, path) in CloudSearchLocations.GetRoots())
            FolderTree.Items.Add(NewFolderNode(name, path));
        if (Directory.Exists(FolderPathBox.Text) &&
            !CloudSearchLocations.GetRoots().Any(item =>
                string.Equals(item.Path, FolderPathBox.Text, StringComparison.OrdinalIgnoreCase)))
            FolderTree.Items.Insert(0, NewFolderNode("현재 탐색창", FolderPathBox.Text));
        _initializing = false;
        ResultsGrid.SelectionChanged += (_, _) => RevealButton.IsEnabled = ResultsGrid.SelectedItem is FileEntry;
        Closed += (_, _) => CancelSearch();
        Loaded += (_, _) => QueryBox.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (CancelSearchButton.IsEnabled) CancelSearch();
            else Close();
        };
    }

    public void FocusSearchInput()
    {
        if (QueryBox.IsEnabled) QueryBox.Focus();
    }

    private static TreeViewItem NewFolderNode(string display, string path)
    {
        var item = new TreeViewItem { Header = display, Tag = path, ToolTip = path };
        item.Items.Add(EmptyNode);
        return item;
    }

    private void TargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || TargetCombo.SelectedItem is not ComboBoxItem { Tag: string target }) return;
        bool files = target != "Folders";
        TypeCombo.IsEnabled = files;
        CustomExtensionsBox.IsEnabled = files && (TypeCombo.SelectedItem as ComboBoxItem)?.Tag is string selected && selected == "Custom";
        if (!files && ScopeCombo.SelectedItem is ComboBoxItem { Tag: string scope } && (scope == "Contents" || scope == "Index"))
            ScopeCombo.SelectedIndex = 1;
    }

    private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || TypeCombo.SelectedItem is not ComboBoxItem { Tag: string preset }) return;
        if (preset != "All" && TargetCombo.SelectedIndex == 0) TargetCombo.SelectedIndex = 1;
        CustomExtensionsBox.IsEnabled = preset == "Custom" && TargetCombo.SelectedIndex != 2;
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (QueryHint is not null)
            QueryHint.Visibility = string.IsNullOrEmpty(QueryBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = ExecuteSearchAsync(); }
        else if (e.Key == Key.Escape) { CancelSearch(); e.Handled = true; }
    }

    private void FolderPathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = ExecuteSearchAsync(); }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "스마트 검색할 폴더 선택", Multiselect = false };
        if (Directory.Exists(FolderPathBox.Text)) picker.InitialDirectory = FolderPathBox.Text;
        if (picker.ShowDialog(this) != true) return;
        FolderPathBox.Text = picker.FolderName;
        // Add only the user-selected root. Do not persist or change files in the cloud folder.
        FolderTree.Items.Insert(0, NewFolderNode("선택한 폴더", picker.FolderName));
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (FolderTree.SelectedItem is TreeViewItem { Tag: string path }) FolderPathBox.Text = path;
    }

    private async void FolderTree_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem { Tag: string folder } node ||
            node.Items.Count != 1 || !ReferenceEquals(node.Items[0], EmptyNode)) return;
        node.Items.Clear();
        try
        {
            // Directory enumeration runs away from the UI thread; omit junctions to avoid cycles.
            var paths = await Task.Run(() => Directory.EnumerateDirectories(folder)
                .Where(p => !ReparsePointPolicy.IsBlocked(p))
                .Take(501).ToArray());
            if (!IsLoaded) return;
            foreach (var path in paths.Take(500)) node.Items.Add(NewFolderNode(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), path));
            if (paths.Length > 500) node.Items.Add(new TreeViewItem { Header = "폴더가 많습니다. 위의 경로 입력/직접 선택 기능을 이용하세요.", IsEnabled = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { node.Items.Add(new TreeViewItem { Header = "이 폴더를 읽을 수 없습니다.", IsEnabled = false }); }
    }

    private void StartSearch_Click(object sender, RoutedEventArgs e) => _ = ExecuteSearchAsync();
    private void CancelSearch_Click(object sender, RoutedEventArgs e) => CancelSearch();

    private void CancelSearch()
    {
        _searchCts?.Cancel();
        ++_generation;
        StartSearchButton.IsEnabled = true;
        CancelSearchButton.IsEnabled = false;
        StatusText.Text = "검색을 중단했습니다.";
    }

    private async Task ExecuteSearchAsync()
    {
        int runId = _generation;
        string root, query = QueryBox.Text.Trim();
        try
        {
            root = Path.GetFullPath(FolderPathBox.Text.Trim());
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("검색할 폴더가 없습니다. 왼쪽 탐색창 또는 폴더 선택 버튼으로 지정하세요.");
            if (ReparsePointPolicy.IsBlocked(root)) throw new IOException("바로가기나 연결 지점은 검색 시작 위치로 지정할 수 없습니다.");
            if (TargetCombo.SelectedItem is not ComboBoxItem { Tag: string targetName } ||
                TypeCombo.SelectedItem is not ComboBoxItem { Tag: string preset } ||
                ScopeCombo.SelectedItem is not ComboBoxItem { Tag: string scopeName } ||
                !Enum.TryParse(targetName, out ExplorerSearchTarget target) ||
                !Enum.TryParse(scopeName, out ExplorerSearchScope scope))
                throw new ArgumentException("검색 대상·유형·범위를 선택하세요.");
            if (target == ExplorerSearchTarget.Folders) preset = "All";
            if (preset != "All" && target == ExplorerSearchTarget.All) target = ExplorerSearchTarget.Files;
            if (target == ExplorerSearchTarget.Folders && (scope == ExplorerSearchScope.Contents || scope == ExplorerSearchScope.Index))
                scope = ExplorerSearchScope.Descendants;
            if (query.Length == 0 && scope != ExplorerSearchScope.Current)
                throw new ArgumentException("하위 폴더 전체 검색은 검색어를 입력해야 시작합니다. 빈 검색어는 '선택한 폴더 바로 아래'를 이용하세요.");
            var filter = new ExplorerSearchFilter(target, preset, CustomExtensionsBox.Text);
            int id = ++_generation;
            runId = id;
            _searchCts?.Cancel(); _searchCts?.Dispose();
            var cts = new CancellationTokenSource(); _searchCts = cts;
            ResultsGrid.ItemsSource = null; ResultCount.Text = "검색 중";
            StartSearchButton.IsEnabled = false; CancelSearchButton.IsEnabled = true;
            RevealButton.IsEnabled = false;
            StatusText.Text = "검색 중…  " + filter.Summary + " · " + root;
            var found = await ExplorerSearchService.SearchAsync(root, query, scope, ShowHiddenBox.IsChecked == true, cts.Token, filter);
            if (id != _generation || !IsLoaded) return;
            ResultsGrid.ItemsSource = found.Entries.Take(RenderLimit).ToArray();
            ResultCount.Text = $"{found.Entries.Count:N0}개";
            StatusText.Text = found.Entries.Count > RenderLimit
                ? $"검색 결과 {found.Entries.Count:N0}개 중 {RenderLimit:N0}개 표시. 조건을 좁혀주세요. " + found.Warning
                : $"검색 완료 · {filter.Summary} · " + found.Warning;
        }
        catch (OperationCanceledException) { StatusText.Text = "검색을 중단했습니다."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException)
        { MessageBox.Show(this, ex.Message, "Sunrise 스마트 검색", MessageBoxButton.OK, MessageBoxImage.Warning); StatusText.Text = "검색할 폴더와 조건을 확인한 후 다시 검색하세요."; }
        finally
        {
            if (runId == _generation)
            {
                StartSearchButton.IsEnabled = true;
                CancelSearchButton.IsEnabled = false;
            }
        }
    }

    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => RevealSelected();
    private void RevealButton_Click(object sender, RoutedEventArgs e) => RevealSelected();
    private void RevealSelected()
    {
        if (ResultsGrid.SelectedItem is FileEntry entry)
            _revealInExplorer(entry.FullPath, entry.IsDirectory);
    }
}
