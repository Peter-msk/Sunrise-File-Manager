using System.Windows;
using System.Windows.Controls;

namespace Sunrise.FileManager.Controls;

/// <summary>Passwords remain in memory for one extraction attempt and are never persisted.</summary>
public sealed class ArchivePasswordWindow : Window
{
    private readonly PasswordBox _password = new() { MinWidth = 240, Margin = new Thickness(0, 10, 0, 10) };
    public string Password => _password.Password;
    public ArchivePasswordWindow()
    {
        Title = "압축 파일 암호"; SizeToContent = SizeToContent.WidthAndHeight;
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextPrimary");
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 420 };
        panel.Children.Add(new TextBlock { Text = "암호가 필요한 경우 입력하세요. 빈 값은 암호 없이 해제를 시도합니다.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_password);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 80, Margin = new Thickness(4) };
        var ok = new Button { Content = "압축 풀기", IsDefault = true, MinWidth = 90, Margin = new Thickness(4) };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(ok); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => _password.Focus();
    }
}
