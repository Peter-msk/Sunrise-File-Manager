using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>Only an actual collision opens this dialog; closing it safely skips that item.</summary>
public sealed class TransferConflictWindow : Window
{
    public CollisionDecision Decision { get; private set; } = new(CollisionChoice.Skip);
    public TransferConflictWindow(TransferConflict conflict)
    {
        Title = "같은 이름의 항목이 있습니다";
        Width = 640; MinWidth = 420; MaxHeight = 650; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "원본과 기존 항목을 비교한 후 처리 방법을 선택하세요.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Margin = new Thickness(0, 12, 0, 12), TextWrapping = TextWrapping.Wrap,
            Text = $"원본: {conflict.Source}\n크기 {FileEntry.HumanSize(conflict.SourceSize)} · 수정 {conflict.SourceModified:yyyy-MM-dd HH:mm:ss}\n\n기존: {conflict.Destination}\n크기 {FileEntry.HumanSize(conflict.DestinationSize)} · 수정 {conflict.DestinationModified:yyyy-MM-dd HH:mm:ss}" });
        panel.Children.Add(new TextBlock { Text = "바꾸기를 선택하면 기존 파일은 같은 폴더에 .sunrise-before-… 이름으로 보존됩니다. 메모가 있는 기존 파일은 바꾸지 않습니다.", TextWrapping = TextWrapping.Wrap });
        var all = new CheckBox { Content = "남은 파일 충돌에도 적용", Margin = new Thickness(0, 12, 0, 12) };
        panel.Children.Add(all);
        var buttons = new WrapPanel();
        foreach (var item in new[] { ("건너뛰기", CollisionChoice.Skip), ("새 이름으로 보관", CollisionChoice.Rename), ("파일 바꾸기", CollisionChoice.Replace), ("작업 중단", CollisionChoice.Cancel) })
        {
            var button = new Button { Content = item.Item1, Margin = new Thickness(3), Padding = new Thickness(12, 6, 12, 6),
                IsDefault = item.Item2 == CollisionChoice.Skip,
                IsEnabled = item.Item2 != CollisionChoice.Replace || (!conflict.IsDirectory && File.Exists(conflict.Destination)) };
            button.Click += (_, _) => { Decision = new(item.Item2, all.IsChecked == true); DialogResult = true; };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
