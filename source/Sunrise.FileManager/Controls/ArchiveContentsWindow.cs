using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

public sealed class ArchiveContentsWindow : Window
{
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, EnableRowVirtualization = true };
    private readonly TextBlock _status = new() { Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap };
    private readonly string _path;
    private readonly CancellationTokenSource _cancel = new();
    public string? Password { get; private set; }
    public IReadOnlySet<string>? SelectedKeys { get; private set; }
    public bool WasModified { get; private set; }
    public ArchiveContentsWindow(string path)
    {
        _path = path; Title = "압축 파일 내용"; Width = 760; Height = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextPrimary");
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(_status, Dock.Top); panel.Children.Add(_status);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        void Button(string label, Action click) { var b = new Button { Content = label, Margin = new Thickness(3), Padding = new Thickness(8, 4, 8, 4) }; b.Click += (_, _) => { if (!_editing) click(); }; buttons.Children.Add(b); }
        Button("암호 입력·다시 읽기", () => { var prompt = new ArchivePasswordWindow { Owner = this }; if (prompt.ShowDialog() == true) { Password = prompt.Password; _ = Load(); } });
        Button("선택한 파일만 풀기", () => { SelectedKeys = _grid.SelectedItems.OfType<ArchiveService.Item>().Where(i => !i.Directory).Select(i => i.Key).ToHashSet(StringComparer.Ordinal); if (SelectedKeys.Count > 0) DialogResult = true; });
        Button("전체 압축 풀기", () => { SelectedKeys = null; DialogResult = true; });
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            Button("파일 추가…", () => _ = EditZip(ZipEditService.Operation.Add));
            Button("선택 항목 교체…", () => _ = EditZip(ZipEditService.Operation.Replace));
            Button("선택 항목 삭제…", () => _ = EditZip(ZipEditService.Operation.Remove));
        }
        Button("닫기", Close); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        _grid.Columns.Add(new DataGridTextColumn { Header = "이름", Binding = new Binding("Key"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "크기 (바이트)", Binding = new Binding("Bytes"), Width = 120 });
        _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "폴더", Binding = new Binding("Directory"), Width = 60 });
        _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "암호", Binding = new Binding("Encrypted"), Width = 60 });
        panel.Children.Add(_grid); Content = panel;
        Loaded += async (_, _) => await Load(); Closing += (_, e) =>
        {
            if (_editing)
            {
                _cancel.Cancel();
                _status.Text = "ZIP 작업 취소 요청됨. 파일 보호 단계가 완료된 뒤 창을 닫으세요.";
                e.Cancel = true;
                return;
            }
            _cancel.Cancel();
        };
    }
    private bool _loading;
    private async Task Load()
    {
        if (_loading) return;
        _loading = true; _status.Text = "압축 항목 읽기 중…";
        try { var rows = await Task.Run(() => ArchiveService.List(_path, _cancel.Token, Password)); _grid.ItemsSource = rows; _status.Text = $"{rows.Count:N0}개 항목 · 목록 보기 및 선택 해제" + (_path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? " · ZIP 추가/교체/삭제 가능" : " · 압축 내부 직접 수정은 지원하지 않음"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "목록을 읽지 못했습니다. 암호·파일 형식을 확인하세요. " + ex.GetType().Name; }
        finally { _loading = false; }
    }
    private bool _editing;
    private async Task EditZip(ZipEditService.Operation operation)
    {
        if (_editing || _loading || _cancel.IsCancellationRequested) return;
        string? local = null;
        string? entry = null;
        if (operation != ZipEditService.Operation.Add)
        {
            if (_grid.SelectedItem is not ArchiveService.Item row || row.Directory)
            { MessageBox.Show(this, "압축 안의 파일 한 개를 선택하세요.", "ZIP 직접 수정"); return; }
            entry = row.Key;
        }
        if (operation != ZipEditService.Operation.Remove)
        {
            var choose = new Microsoft.Win32.OpenFileDialog { Title = "ZIP에 넣을 로컬 파일 선택", CheckFileExists = true, Multiselect = false };
            if (choose.ShowDialog(this) != true) return;
            local = choose.FileName;
            entry ??= System.IO.Path.GetFileName(local);
        }
        if (MessageBox.Show(this, $"ZIP 내부 항목 '{entry}'을(를) {(operation == ZipEditService.Operation.Remove ? "삭제" : operation == ZipEditService.Operation.Replace ? "교체" : "추가")}할까요?\n\n원본 ZIP은 안전한 임시 사본으로 먼저 수정한 후 검증하고 교체합니다. 실패 시 원본을 보존합니다.",
            "ZIP 변경 승인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _editing = true;
        _status.Text = "ZIP 작업 중… 원본을 보호하고 있습니다.";
        try
        {
            await Task.Run(() => ZipEditService.Apply(_path, operation, entry!, local, _cancel.Token));
            WasModified = true;
            _status.Text = "ZIP 변경 및 재열기 확인 완료";
            await Load();
        }
        catch (OperationCanceledException) { _status.Text = "ZIP 수정을 취소했습니다."; }
        catch (Exception ex) { _status.Text = "ZIP 수정 실패: " + ex.Message; MessageBox.Show(this, ex.Message, "ZIP 직접 수정", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _editing = false; }
    }
}
