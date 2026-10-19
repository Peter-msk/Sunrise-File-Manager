using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>
/// Core FolderTree control: lazy asynchronous directory expansion and selection synchronization.
/// Quick Access pinning, drive gauges, special folders, and folder drop targets are modularized in
/// Features/Navigation/FolderTree.QuickAccessAndDrives.cs.
/// </summary>
public partial class FolderTree : UserControl
{
    private readonly HashSet<TreeViewItem> _loaded = [];
    private readonly Dictionary<TreeViewItem, Task> _loading = [];
    private readonly List<string> _pinnedFolders = [];
    private CancellationTokenSource _lifetime = new();
    private bool _suppressSelection;
    private int _navigationVersion;

    public event Action<string>? PathChosen;
    public event Action<IReadOnlyList<string>>? PinnedFoldersChanged;
    public event Action<IReadOnlyList<string>, string, TransferKind>? FolderDropRequested;
    public Func<string?>? GetActiveFolder { get; set; }

    public FolderTree()
    {
        InitializeComponent();
        ReloadRoots();
        RenderPinnedFolders();
    }

    // The TreeView is hosted inside one outer ScrollViewer, while its own scrollbars
    // are hidden. Handle tunneling wheel events here so nested TreeViewItem controls
    // cannot consume them before the navigation viewport scrolls.
    private void NavigationScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (NavigationScroll.ScrollableHeight <= 0 || e.Delta == 0) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return;

        double next = NavigationScroll.VerticalOffset - e.Delta / 120.0 * 48.0;
        NavigationScroll.ScrollToVerticalOffset(Math.Clamp(next, 0, NavigationScroll.ScrollableHeight));
        e.Handled = true;
    }

    private void PinCurrent_Click(object sender, RoutedEventArgs e)
    {
        string? active = GetActiveFolder?.Invoke();
        if (!string.IsNullOrWhiteSpace(active)) PinFolder(active);
    }

    public void CancelPendingOperations()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new CancellationTokenSource();
    }

    private void KnownFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        NavigateKnownFolder(key);
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => ReloadRoots();

    private async void FolderItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item || item.Tag is not string) return;
        try { await EnsureLoadedAsync(item); }
        catch (OperationCanceledException) { }
    }

    private Task EnsureLoadedAsync(TreeViewItem parent)
    {
        if (_loaded.Contains(parent)) return Task.CompletedTask;
        if (_loading.TryGetValue(parent, out Task? active)) return active;
        Task task = LoadChildrenAsync(parent, _lifetime.Token);
        _loading[parent] = task;
        return task;
    }

    private async Task LoadChildrenAsync(TreeViewItem parent, CancellationToken token)
    {
        try
        {
            if (parent.Tag is not string path) return;
            FolderChildren result = await FolderTreeScanner.ReadAsync(path, token);
            token.ThrowIfCancellationRequested();
            parent.Items.Clear();
            foreach (string child in result.Paths) parent.Items.Add(MakeNode(child));
            if (result.Truncated)
                parent.Items.Add(new TreeViewItem { Header = $"처음 {FolderTreeScanner.MaxChildFolders:N0}개만 표시", IsEnabled = false });
            if (result.Warning is not null)
                parent.Items.Add(new TreeViewItem { Header = result.Warning, IsEnabled = false });
            _loaded.Add(parent);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _loading.Remove(parent);
        }
    }

    private void FolderItems_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressSelection) return;
        if (FolderItems.SelectedItem is TreeViewItem { Tag: string path }) PathChosen?.Invoke(path);
    }

    public async Task HighlightPathAsync(string path)
    {
        int version = ++_navigationVersion;
        CancellationToken token = _lifetime.Token;
        try
        {
            string full = Path.GetFullPath(path);
            string? rootPath = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(rootPath)) return;
            TreeViewItem? node = FolderItems.Items.OfType<TreeViewItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, rootPath, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                node = MakeNode(rootPath);
                FolderItems.Items.Add(node);
            }
            string current = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string remainder = full[rootPath.Length..];
            foreach (string part in remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (version != _navigationVersion || token.IsCancellationRequested) return;
                await EnsureLoadedAsync(node);
                node.IsExpanded = true;
                current = current.EndsWith(Path.DirectorySeparatorChar) ? current + part : current + Path.DirectorySeparatorChar + part;
                TreeViewItem? next = node.Items.OfType<TreeViewItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, current, StringComparison.OrdinalIgnoreCase));
                if (next is null) break;
                node = next;
            }
            if (version != _navigationVersion || token.IsCancellationRequested) return;
            _suppressSelection = true;
            node.IsSelected = true;
            node.BringIntoView();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
        finally
        {
            _suppressSelection = false;
        }
    }
}
