using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Services;
using System.Linq;

namespace Sunrise.FileManager.Controls;

public partial class ExplorerPane
{
    private ExplorerSearchTarget _searchTarget = ExplorerSearchTarget.All;
    private string _searchFileType = "All";
    private string _searchCustomExtensions = "";
    private ExplorerSearchFilter CurrentSearchFilter => new(_searchTarget, _searchFileType, _searchCustomExtensions);
    private bool HasSearchTypeSelection => _searchTarget != ExplorerSearchTarget.All || _searchFileType != "All";

    private void RestoreSearchOptions(PanelSession? session)
    {
        if (Enum.TryParse<ExplorerSearchTarget>(session?.SearchTarget, out var target) && Enum.IsDefined(target))
            _searchTarget = target;
        if (session?.SearchFileType is string preset && ExplorerSearchFilter.Presets.ContainsKey(preset))
            _searchFileType = preset;
        _searchCustomExtensions = ExplorerSearchFilter.NormalizeExtensions(session?.SearchCustomExtensions);
        if (_searchFileType == "Custom" && _searchCustomExtensions.Length == 0) _searchFileType = "All";
        UpdateSearchOptionsLabel();
    }

    private void UpdateSearchOptionsLabel()
    {
        if (SearchOptionsButton is null) return;
        var filter = CurrentSearchFilter;
        SearchOptionsButton.Content = _searchTarget == ExplorerSearchTarget.Folders ? "폴더 ▾" :
            _searchFileType == "All" ? (_searchTarget == ExplorerSearchTarget.Files ? "파일 ▾" : "필터 ▾") : "형식 ▾";
        SearchOptionsButton.ToolTip = "검색 조건: " + filter.Summary + " · 클릭해 변경";
        SearchModeLabel.Text = _searchTarget == ExplorerSearchTarget.Folders
            ? (_searchScope == ExplorerSearchScope.Current ? "현재 폴더 이름" : "하위 폴더 이름")
            : _searchScope switch
            {
                ExplorerSearchScope.Descendants => "하위 폴더 검색",
                ExplorerSearchScope.Contents => "이름·본문 검색",
                ExplorerSearchScope.Index => "인덱스 검색",
                _ => "현재 폴더에서 검색"
            };
        if (SearchOptionsMenu is null) return;
        foreach (var item in SearchOptionsMenu.Items.OfType<MenuItem>())
        {
            if (item.Header?.ToString() == "검색 대상")
                foreach (var child in item.Items.OfType<MenuItem>()) child.IsChecked = (string?)child.Tag == _searchTarget.ToString();
            if (item == SearchScopeOptionsMenu)
                foreach (var child in item.Items.OfType<MenuItem>()) child.IsChecked = (string?)child.Tag == _searchScope.ToString();
            if (item == SearchFileTypeMenu)
                foreach (var child in item.Items.OfType<MenuItem>()) child.IsChecked = (string?)child.Tag == _searchFileType;
        }
    }

    private void ApplySearchOptions()
    {
        // A file format always means files; the only explicit folder mode ignores formats.
        if (_searchTarget == ExplorerSearchTarget.Folders) _searchFileType = "All";
        UpdateSearchOptionsLabel();
        _searchCache = null; _searchCacheKey = null;
        _ = ApplyFilterAndSortAsync();
        SettingsChanged?.Invoke();
    }

    private void SearchOptions_Click(object sender, RoutedEventArgs e)
    {
        if (SearchOptionsButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = SearchOptionsButton;
        menu.IsOpen = true;
    }

    private void SearchTarget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<ExplorerSearchTarget>(tag, out var target)) return;
        _searchTarget = target;
        ApplySearchOptions();
    }

    private void SearchFileType_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string preset } || !ExplorerSearchFilter.Presets.ContainsKey(preset)) return;
        _searchFileType = preset;
        if (preset != "All") _searchTarget = ExplorerSearchTarget.Files;
        ApplySearchOptions();
    }

    private void SearchCustomExtensions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NameInputDialog("검색할 확장자 입력", "파일 확장자만 입력하세요 (쉼표/세미콜론/공백 구분)\n예: .hwp, .dwg, pdf", _searchCustomExtensions,
            ExplorerSearchFilter.NormalizeExtensions) { Owner = Window.GetWindow(this), Width = 550 };
        if (dialog.ShowDialog() != true) return;
        string normalized = ExplorerSearchFilter.NormalizeExtensions(dialog.ResultName);
        if (normalized.Length == 0) { MessageBox.Show(Window.GetWindow(this), "확장자를 하나 이상 입력하세요.", "검색 형식"); return; }
        _searchCustomExtensions = normalized;
        _searchFileType = "Custom";
        _searchTarget = ExplorerSearchTarget.Files;
        ApplySearchOptions();
    }

    private void SearchOptionsReset_Click(object sender, RoutedEventArgs e)
    {
        _searchTarget = ExplorerSearchTarget.All;
        _searchFileType = "All";
        ApplySearchOptions();
    }
}
