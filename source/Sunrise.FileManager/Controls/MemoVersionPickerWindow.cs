using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>Explicit local history choice, with current/historical text shown side by side.</summary>
internal sealed class MemoVersionPickerWindow : Window
{
    private readonly string _memoPath;
    private readonly string _currentText;
    private readonly ListBox _versions;
    private readonly TextBox _historicalText;
    private readonly TextBlock _difference;
    internal MemoLocalSnapshots.SnapshotInfo? SelectedVersion { get; private set; }

    internal MemoVersionPickerWindow(string memoPath, string currentText, IReadOnlyList<MemoLocalSnapshots.SnapshotInfo> versions)
    {
        _memoPath = memoPath;
        _currentText = currentText;
        Title = "Sunrise 메모 · 변경 이력 비교 및 복원";
        Width = 850; Height = 535;
        MinWidth = 600; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "ThemeSurface");
        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(TitleLine("로컬 암호화 이력 중 복원할 버전을 선택하세요. 내용·작성자 비교 후 복원할 수 있습니다.", 0));
        var columns = new Grid { Margin = new Thickness(0, 10, 0, 9) };
        Grid.SetRow(columns, 1);
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _versions = new ListBox { ItemsSource = versions, DisplayMemberPath = "Label", Margin = new Thickness(0, 0, 8, 0) };
        _versions.SelectionChanged += (_, _) => ShowSelectedHistory();
        columns.Children.Add(_versions);
        var currentPane = MakeTextPane("현재 공유 메모", currentText);
        Grid.SetColumn(currentPane, 1);
        columns.Children.Add(currentPane);
        var previousPane = MakeTextPane("선택한 로컬 이력", "");
        Grid.SetColumn(previousPane, 2);
        columns.Children.Add(previousPane);
        _historicalText = (TextBox)previousPane.Children[1];
        _difference = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _difference.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_difference);
        var restore = new Button { Content = "선택한 이력 복원", Padding = new Thickness(11, 5, 11, 5), MinHeight = 30, Margin = new Thickness(8, 0, 7, 0) };
        restore.Click += (_, _) =>
        {
            if (_versions.SelectedItem is not MemoLocalSnapshots.SnapshotInfo snapshot) return;
            SelectedVersion = snapshot;
            DialogResult = true;
        };
        Grid.SetColumn(restore, 1);
        footer.Children.Add(restore);
        var close = new Button { Content = "취소", IsCancel = true, Padding = new Thickness(15, 5, 15, 5), MinHeight = 30 };
        Grid.SetColumn(close, 2);
        footer.Children.Add(close);
        Grid.SetRow(footer, 2);
        layout.Children.Add(columns);
        layout.Children.Add(footer);
        Content = layout;
        _versions.SelectedIndex = versions.Count > 1 ? 1 : 0; // Prefer previous over identical current snapshot.
    }

    private static TextBlock TitleLine(string text, int row)
    {
        var control = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        control.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        Grid.SetRow(control, row);
        return control;
    }

    private static Grid MakeTextPane(string title, string content)
    {
        var pane = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = TitleLine(title, 0);
        header.Margin = new Thickness(0, 0, 0, 6);
        pane.Children.Add(header);
        var editor = new TextBox
        {
            Text = content, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8)
        };
        Grid.SetRow(editor, 1);
        pane.Children.Add(editor);
        return pane;
    }

    private void ShowSelectedHistory()
    {
        if (_versions.SelectedItem is not MemoLocalSnapshots.SnapshotInfo entry) return;
        try
        {
            string historical = MemoLocalSnapshots.PreviewText(_memoPath, entry);
            _historicalText.Text = historical;
            _difference.Text = historical == _currentText
                ? "본문 동일 · 작성자 기록: " + entry.User
                : $"본문 다름 · 현재 {_currentText.Length:N0}자 / 선택 기록 {historical.Length:N0}자 · 기록 작성자: {entry.User}";
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            _historicalText.Text = "이 복원 이력은 읽을 수 없습니다.";
            _difference.Text = "검증 실패: " + ex.Message;
        }
    }
}
