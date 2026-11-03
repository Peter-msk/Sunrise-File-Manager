using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

public sealed class WindowsLocationsWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ListBox _list = new() { DisplayMemberPath = "Display" };
    private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly Stack<string> _history = new();
    private int _generation;
    public string? SelectedPath { get; private set; }
    public WindowsLocationsWindow(string title, string location)
    {
        Title = title; Width = 780; Height = 600; MinWidth = 420; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextPrimary");
        var panel = new DockPanel { Margin = new Thickness(12) };
        var toolbar = new WrapPanel();
        var back = new Button { Content = "이전 위치", Margin = new Thickness(4) };
        back.Click += async (_, _) => { if (_history.Count > 1) { _history.Pop(); await LoadAsync(_history.Peek(), false); } };
        var open = new Button { Content = "선택 항목 열기", Margin = new Thickness(4) };
        open.Click += async (_, _) => await ChooseAsync();
        toolbar.Children.Add(back); toolbar.Children.Add(open);
        DockPanel.SetDock(toolbar, Dock.Top); panel.Children.Add(toolbar);
        DockPanel.SetDock(_status, Dock.Bottom); panel.Children.Add(_status); panel.Children.Add(_list);
        Content = panel;
        _list.MouseDoubleClick += async (_, _) => await ChooseAsync();
        Loaded += async (_, _) => await LoadAsync(location, true);
        Closed += (_, _) => _lifetime.Cancel();
    }
    private async Task ChooseAsync()
    {
        if (_list.SelectedItem is not ShellLocation item) return;
        if (item.ShellOnly) await LoadAsync(item.Path, true);
        else { SelectedPath = item.Path; DialogResult = true; }
    }
    private async Task LoadAsync(string location, bool remember)
    {
        int generation = ++_generation;
        _status.Text = "Windows 목록을 읽고 있습니다…";
        try
        {
            var rows = await WindowsCollectionsService.ReadAsync(location, _lifetime.Token);
            if (_lifetime.IsCancellationRequested || generation != _generation) return;
            if (remember) _history.Push(location);
            _list.ItemsSource = rows;
            _status.Text = $"{rows.Count:N0}개 · {location} · 위치당 최대 2,000개. 네트워크 검색은 Windows 공유·검색 설정에 따릅니다.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) _status.Text = ex.Message; }
    }
}
