using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

public sealed class MetadataEditorWindow : Window
{
    private readonly Dictionary<string, TextBox> _fields = [];
    private bool _saving;
    public MetadataEditorWindow(string path)
    {
        Title = "파일 메타데이터 편집"; Width = 600; MaxHeight = 720; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextPrimary");
        var initial = new Dictionary<string, string>();
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap });
        var labels = new[] { "제목", "주제", "설명", "태그 (세미콜론 구분)", "작성자 (세미콜론 구분)", "평점 (0~99, 1/25/50/75/99 = 별 1~5개)" };
        for (int i = 0; i < WindowsMetadataService.Names.Length; i++)
        {
            string key = WindowsMetadataService.Names[i];
            panel.Children.Add(new TextBlock { Text = labels[i], Margin = new Thickness(0, 10, 0, 3) });
            var field = new TextBox { Text = "", MaxLength = 4000, MinHeight = 28 };
            _fields.Add(key, field); panel.Children.Add(field);
        }
        panel.Children.Add(new TextBlock { Text = "파일 형식과 설치된 Windows 속성 처리기가 허용하는 항목만 저장됩니다.", Margin = new Thickness(0, 12, 0, 12), TextWrapping = TextWrapping.Wrap });
        var status = new TextBlock { Text = "속성을 읽고 있습니다…", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(status);
        var save = new Button { IsEnabled = false, Content = "변경 내용 저장", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += async (_, _) =>
        {
            var changed = _fields.Where(pair => pair.Value.Text != initial[pair.Key]).ToDictionary(pair => pair.Key, pair => pair.Value.Text);
            if (changed.Count == 0) { DialogResult = true; return; }
            if (changed.TryGetValue("System.Rating", out string? rating) && (!uint.TryParse(rating, out uint score) || score > 99))
            { MessageBox.Show(this, "평점은 0~99로 입력하세요."); return; }
            try
            {
                _saving = true; save.IsEnabled = false;
                foreach (var field in _fields.Values) field.IsEnabled = false;
                status.Text = "속성을 저장하고 있습니다…";
                await WindowsMetadataService.WriteAsync(path, changed); _saving = false; DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(this, "저장하지 못했습니다. Windows 속성창에서도 지원 여부를 확인할 수 있습니다.\n" + ex.Message, "메타데이터 저장", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { _saving = false; save.IsEnabled = true; foreach (var field in _fields.Values) field.IsEnabled = true; status.Text = ""; }
        };
        Loaded += async (_, _) =>
        {
            try
            {
                initial = await WindowsMetadataService.ReadAsync(path);
                if (!IsVisible) return;
                foreach (var field in _fields) field.Value.Text = initial[field.Key];
                status.Text = ""; save.IsEnabled = true;
            }
            catch (Exception ex) { if (IsVisible) status.Text = "속성을 읽지 못했습니다: " + ex.Message; }
        };
        Closing += (_, e) => { if (_saving) { e.Cancel = true; status.Text = "현재 속성 저장이 끝난 후 닫을 수 있습니다."; } };
        panel.Children.Add(save);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
