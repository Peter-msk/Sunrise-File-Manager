using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>Multi-folder photo collection with bounded pages and existing bounded thumbnail decoder.</summary>
public sealed class PhotoGalleryWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _pageCancellation;
    private readonly WrapPanel _cards = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private List<FileEntry> _photos = [];
    private int _page, _generation, _photoTotal, _photoUnreadable;
    private CancellationTokenSource? _scanCancellation;
    private const int PageSize = 80;
    public PhotoGalleryWindow(string? folder)
    {
        Title = "사진 모아보기"; Width = 1000; Height = 720; MinWidth = 450; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextPrimary");
        if (folder is not null) _folders.Add(folder);
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (Directory.Exists(pictures)) _folders.Add(pictures);
        var panel = new DockPanel { Margin = new Thickness(12) };
        var toolbar = new WrapPanel();
        AddButton(toolbar, "폴더 추가", async () =>
        {
            var dialog = new OpenFolderDialog { Multiselect = true, Title = "사진을 모아볼 폴더 선택" };
            if (dialog.ShowDialog(this) != true) return;
            foreach (string path in dialog.FolderNames) _folders.Add(path);
            await ReloadAsync();
        });
        AddButton(toolbar, "이전", async () => { if (_page > 0) { _page--; await ShowPageAsync(); } });
        AddButton(toolbar, "다음", async () => { if ((_page + 1) * PageSize < _photos.Count) { _page++; await ShowPageAsync(); } });
        DockPanel.SetDock(toolbar, Dock.Top); panel.Children.Add(toolbar);
        DockPanel.SetDock(_status, Dock.Bottom); panel.Children.Add(_status);
        panel.Children.Add(new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = panel;
        Loaded += async (_, _) => await ReloadAsync();
        Closed += (_, _) => { _lifetime.Cancel(); _pageCancellation?.Cancel(); _scanCancellation?.Cancel(); };
    }
    private static void AddButton(Panel parent, string text, Func<Task> action)
    {
        var button = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(10, 4, 10, 4) };
        button.Click += async (_, _) => await action(); parent.Children.Add(button);
    }
    private async Task ReloadAsync()
    {
        _scanCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _scanCancellation = cancellation;
        int generation = ++_generation;
        string[] roots = _folders.ToArray();
        _status.Text = "사진을 모으고 있습니다…";
        try
        {
            var photos = await PhotoCollectionScanner.ReadAsync(roots, ImageThumbnailService.CanPreview, cancellation.Token);
            if (cancellation.IsCancellationRequested || generation != _generation) return;
            _photos = photos.Entries.ToList(); _photoTotal = photos.Total; _photoUnreadable = photos.Unreadable; _page = 0;
            await ShowPageAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) _status.Text = ex.Message; }
        finally { if (ReferenceEquals(_scanCancellation, cancellation)) _scanCancellation = null; }
    }
    private async Task ShowPageAsync()
    {
        _pageCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _pageCancellation = cancellation;
        _cards.Children.Clear();
        _status.Text = $"조회한 전체 사진 {_photoTotal:N0}개 중 최신 {_photos.Count:N0}개 · {_page + 1}/{Math.Max(1, (_photos.Count + PageSize - 1) / PageSize)}쪽 · 폴더 {_folders.Count}개 · 쪽당 80개 · 읽기 불가·차단 링크 {_photoUnreadable:N0}개";
        try
        {
            foreach (FileEntry photo in _photos.Skip(_page * PageSize).Take(PageSize))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var content = new StackPanel { Width = 160 };
                var image = new Image { Height = 112, Stretch = Stretch.Uniform };
                content.Children.Add(image);
                content.Children.Add(new TextBlock { Text = photo.Name, TextTrimming = TextTrimming.CharacterEllipsis });
                content.Children.Add(new TextBlock { Text = photo.ModifiedGroup, FontSize = 11 });
                var button = new Button { Content = content, ToolTip = photo.FullPath, Margin = new Thickness(5), Padding = new Thickness(5) };
                button.Click += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo(photo.FullPath) { UseShellExecute = true }); }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "사진 열기"); }
                };
                _cards.Children.Add(button);
                try { image.Source = await ImageThumbnailService.LoadAsync(photo.FullPath, cancellation.Token); }
                catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException) { button.ToolTip = photo.FullPath + "\n미리보기 불가"; }
            }
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_pageCancellation, cancellation)) _pageCancellation = null; }
    }
}
