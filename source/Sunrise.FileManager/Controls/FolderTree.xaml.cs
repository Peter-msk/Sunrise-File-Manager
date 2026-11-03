using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
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
    // Per-expanded-node cursor; only one page of UI containers is appended at a time.
    private readonly Dictionary<TreeViewItem, string> _pageCursors = [];
    private readonly List<string> _pinnedFolders = [];
    private CancellationTokenSource _lifetime = new();
    private bool _suppressSelection;
    private int _navigationVersion;
    private bool _manualExpansion;
    private int _manualExpansionVersion;

    public event Action<string>? PathChosen;
    public event Action<string>? QuickPathChosen;
    public event Action<IReadOnlyList<string>>? PinnedFoldersChanged;
    public event Action<IReadOnlyList<string>, string, TransferKind>? FolderDropRequested;
    public Func<string?>? GetActiveFolder { get; set; }

    public FolderTree()
    {
        InitializeComponent();
        // Expansion must load real child nodes, including when the custom themed expander is clicked.
        FolderItems.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(FolderItem_Expanded));
        // TreeViewItem.IsExpanded owns the state; the themed arrow only displays it.
        // Do not let native toggle state and a second double-click handler compete.
        FolderItems.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(FolderExpander_Click), true);
        FolderItems.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(FolderItems_PreviewMouseLeftButtonDown), true);
        FolderItems.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler(FolderItems_RequestBringIntoView), true);
        ReloadRoots();
        RenderPinnedFolders();
    }

    // The TreeView is hosted inside one outer ScrollViewer, while its own scrollbars
    // are hidden. Handle tunneling wheel events here so nested TreeViewItem controls
    // cannot consume them before the navigation viewport scrolls.
    private void NavigationScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            if (NavigationScroll.ScrollableWidth > 0)
                NavigationScroll.ScrollToHorizontalOffset(Math.Clamp(
                    NavigationScroll.HorizontalOffset - e.Delta / 120.0 * 48.0, 0, NavigationScroll.ScrollableWidth));
            e.Handled = true;
            return;
        }
        if (NavigationScroll.ScrollableHeight <= 0) return;
        double next = NavigationScroll.VerticalOffset - e.Delta / 120.0 * 48.0;
        NavigationScroll.ScrollToVerticalOffset(Math.Clamp(next, 0, NavigationScroll.ScrollableHeight));
        e.Handled = true;
    }

    private void PinCurrent_Click(object sender, RoutedEventArgs e)
    {
        string? active = GetActiveFolder?.Invoke();
        if (!string.IsNullOrWhiteSpace(active)) PinFolder(active);
    }

    public List<string> GetExpandedPaths()
    {
        var result = new List<string>();
        void Visit(TreeViewItem item)
        {
            if (result.Count >= 200 || !item.IsExpanded) return;
            if (item.Tag is string path) result.Add(path);
            foreach (var child in item.Items.OfType<TreeViewItem>()) Visit(child);
        }
        foreach (var root in FolderItems.Items.OfType<TreeViewItem>()) Visit(root);
        return result;
    }
    public double ScrollOffset => NavigationScroll.VerticalOffset;
    public async Task RestoreExpandedPathsAsync(IReadOnlyList<string> paths, double offset, string? activePath = null)
    {
        // Legacy sessions may contain hundreds of expanded paths; restore only the active chain.
        if (activePath is not null && !WindowsKnownLocations.IsVirtual(activePath))
        {
            paths = paths.Where(path => IsPathAncestorOf(path, activePath)).ToArray();
        }
        else if (activePath is not null) paths = [];
        var wanted = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int version = _navigationVersion;
        try
        {
            async Task Visit(TreeViewItem node)
            {
                if (version != _navigationVersion || node.Tag is not string path || !wanted.Contains(path)) return;
                node.IsExpanded = true;
                await EnsureLoadedAsync(node);
                if (version != _navigationVersion) return;
                foreach (var child in node.Items.OfType<TreeViewItem>()) await Visit(child);
            }
            foreach (var root in FolderItems.Items.OfType<TreeViewItem>()) await Visit(root);
            if (version == _navigationVersion) NavigationScroll.ScrollToVerticalOffset(offset);
        }
        catch (OperationCanceledException) { }
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

    // A single authoritative expansion state prevents the themed ToggleButton from
    // becoming out of sync with the visible TreeViewItem.  The first click must
    // collapse an already expanded drive, including while an async reveal is pending.
    private void FolderExpander_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not ToggleButton { Name: "ExpandToggle" } toggle ||
            toggle.TemplatedParent is not TreeViewItem item) return;
        ToggleFolderExpansion(item);
        e.Handled = true;
    }

    // The WPF TreeViewItem class handles double clicks internally.  Intercept the
    // second mouse-down on the header so expansion occurs exactly once, not again
    // later in a bubbled MouseDoubleClick event.  An arrow double click is two
    // independent arrow clicks; no additional header handler runs for it.
    private void FolderItems_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source) return;
        // Intercept before TreeViewItem's focus handling can reveal the old selected
        // descendant and move the arrow out from under the first mouse-up.
        if (VisualAncestor<ToggleButton>(source) is { Name: "ExpandToggle", TemplatedParent: TreeViewItem arrowItem })
        {
            ToggleFolderExpansion(arrowItem);
            e.Handled = true;
            return;
        }
        if (e.ClickCount != 2) return;
        var item = VisualAncestor<TreeViewItem>(source);
        if (item?.Tag is not string path ||
            !string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase)) return;
        ToggleFolderExpansion(item);
        e.Handled = true;
    }

    private void ToggleFolderExpansion(TreeViewItem item)
    {
        ++_navigationVersion; // Invalidate pending reveal AND its queued scroll callback.
        int actionVersion = ++_manualExpansionVersion;
        double vertical = NavigationScroll.VerticalOffset, horizontal = NavigationScroll.HorizontalOffset;
        _manualExpansion = true;
        bool suppress = _suppressSelection;
        _suppressSelection = true;
        try { item.IsExpanded = !item.IsExpanded; }
        finally { _suppressSelection = suppress; }
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (actionVersion != _manualExpansionVersion) return;
            try
            {
                NavigationScroll.UpdateLayout();
                NavigationScroll.ScrollToVerticalOffset(Math.Min(vertical, NavigationScroll.ScrollableHeight));
                NavigationScroll.ScrollToHorizontalOffset(Math.Min(horizontal, NavigationScroll.ScrollableWidth));
            }
            finally { _manualExpansion = false; }
        }));
    }

    private void FolderItems_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (_manualExpansion) e.Handled = true;
    }

    private static T? VisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (DependencyObject? node = source; node is not null; node =
            node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is T match) return match;
        return null;
    }

    // Keep only the selected branch expanded. Old visited branches are closed,
    // including their nested items, so reopening a drive never reveals stale branches.
    // Null means quick navigation: close the drive tree without auto-expanding physical paths.
    public void CollapseInactiveBranches(string? activePath)
    {
        ++_navigationVersion; // invalidate any in-flight HighlightPathAsync
        string? active = null;
        if (!string.IsNullOrWhiteSpace(activePath) && !WindowsKnownLocations.IsVirtual(activePath))
        {
            try { active = Path.GetFullPath(activePath); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
        }
        _suppressSelection = true;
        try
        {
            void CloseUnused(TreeViewItem node)
            {
                bool keep = active is not null && node.Tag is string path && IsPathAncestorOf(path, active);
                if (!keep)
                {
                    node.IsExpanded = false;
                    // Release previous pages of unrelated branches. Do not detach
                    // a subtree while its asynchronous enumeration is in flight.
                    if (!HasPendingLoads(node))
                    {
                        ForgetCachedSubtree(node);
                        return;
                    }
                }
                foreach (var child in node.Items.OfType<TreeViewItem>()) CloseUnused(child);
            }
            foreach (var root in FolderItems.Items.OfType<TreeViewItem>()) CloseUnused(root);
        }
        finally { _suppressSelection = false; }
    }

    private bool HasPendingLoads(TreeViewItem node)
    {
        if (_loading.ContainsKey(node)) return true;
        return node.Items.OfType<TreeViewItem>().Any(HasPendingLoads);
    }

    private void ForgetCachedSubtree(TreeViewItem node)
    {
        foreach (TreeViewItem child in node.Items.OfType<TreeViewItem>()) ForgetCachedSubtree(child);
        _loaded.Remove(node);
        _pageCursors.Remove(node);
        if (node.Tag is string)
        {
            node.Items.Clear();
            node.Items.Add(new TreeViewItem { Header = "읽는 중...", IsEnabled = false });
        }
    }

    private static bool IsPathAncestorOf(string ancestor, string child)
    {
        string parent = Path.TrimEndingDirectorySeparator(ancestor);
        string target = Path.TrimEndingDirectorySeparator(child);
        return string.Equals(parent, target, StringComparison.OrdinalIgnoreCase) ||
               target.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public void CancelAutomaticReveal()
    {
        ++_navigationVersion;
        _suppressSelection = true;
        try
        {
            if (FolderItems.SelectedItem is TreeViewItem selected) selected.IsSelected = false;
        }
        finally { _suppressSelection = false; }
    }

    public void FocusDriveList()
    {
        FolderItems.Focus();
        if (FolderItems.Items.OfType<TreeViewItem>().FirstOrDefault() is { } first)
            first.Focus();
    }

    // The single navigation ScrollViewer is preserved. An explicit locate request
    // reveals only the requested folder chain; Quick Access never silently unfolds
    // the physical drive hierarchy when a shortcut is clicked.
    private async void LocateCurrent_Click(object sender, RoutedEventArgs e)
    {
        string? path = GetActiveFolder?.Invoke();
        if (string.IsNullOrWhiteSpace(path) || WindowsKnownLocations.IsVirtual(path))
        {
            FocusDriveList();
            return;
        }
        if (!Directory.Exists(path)) return;
        CollapseInactiveBranches(path);
        await HighlightPathAsync(path);
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
            // HighlightPathAsync resolves children before setting IsExpanded.
            // Loading must therefore also work for a not-yet-expanded parent.
            if (token.IsCancellationRequested) return;
            parent.Items.Clear();
            foreach (string child in result.Paths) parent.Items.Add(MakeNode(child));
            if (result.Paths.Count > 0) _pageCursors[parent] = result.Paths[^1];
            if (result.Truncated) AddLoadMoreItem(parent);
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

    // Keep one navigation ScrollViewer. More pages are requested explicitly, so
    // 1,000+ sibling folders never create 1,000 WPF rows in a single UI pass.
    private void AddLoadMoreItem(TreeViewItem parent)
    {
        var more = new Button
        {
            Content = $"다음 {FolderTreeScanner.MaxChildFolders:N0}개 폴더 보기…",
            MinHeight = 28,
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ToolTip = "나머지 하위 폴더를 계속 표시합니다."
        };
        var holder = new TreeViewItem { Header = more };
        parent.Items.Add(holder);
        more.Click += async (_, e) =>
        {
            e.Handled = true;
            if (more.IsEnabled && _pageCursors.TryGetValue(parent, out string? cursor))
                await AppendNextPageAsync(parent, holder, more, cursor);
        };
    }

    private async Task AppendNextPageAsync(TreeViewItem parent, TreeViewItem holder, Button more, string cursor)
    {
        more.IsEnabled = false;
        CancellationToken token = _lifetime.Token;
        try
        {
            FolderChildren page = await FolderTreeScanner.ReadPageAsync((string)parent.Tag, cursor, token);
            if (token.IsCancellationRequested || !parent.IsExpanded || !parent.Items.Contains(holder)) return;
            parent.Items.Remove(holder);
            // The locate action may have inserted a distant child before its page
            // was reached. Do not create duplicate visual nodes for that folder.
            foreach (string child in page.Paths)
            {
                if (parent.Items.OfType<TreeViewItem>().Any(item =>
                    string.Equals(item.Tag as string, child, StringComparison.OrdinalIgnoreCase))) continue;
                InsertSortedChild(parent, MakeNode(child));
            }
            if (page.Paths.Count > 0) _pageCursors[parent] = page.Paths[^1];
            if (page.Truncated) AddLoadMoreItem(parent);
            if (page.Warning is not null)
                parent.Items.Add(new TreeViewItem { Header = page.Warning, IsEnabled = false });
        }
        catch (OperationCanceledException) { }
        finally { more.IsEnabled = true; }
    }

    private static void InsertSortedChild(TreeViewItem parent, TreeViewItem child)
    {
        string path = (string)child.Tag;
        string name = Path.GetFileName(path);
        int index = 0;
        foreach (TreeViewItem existing in parent.Items.OfType<TreeViewItem>())
        {
            if (existing.Tag is not string existingPath) break;
            int compare = NaturalNameComparer.Instance.Compare(Path.GetFileName(existingPath), name);
            if (compare > 0 || (compare == 0 && StringComparer.OrdinalIgnoreCase.Compare(existingPath, path) > 0)) break;
            index++;
        }
        parent.Items.Insert(index, child);
    }

    private void FolderItems_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressSelection || _manualExpansion) return;
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
                if (version != _navigationVersion || token.IsCancellationRequested) return;
                node.IsExpanded = true;
                current = current.EndsWith(Path.DirectorySeparatorChar) ? current + part : current + Path.DirectorySeparatorChar + part;
                TreeViewItem? next = node.Items.OfType<TreeViewItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, current, StringComparison.OrdinalIgnoreCase));
                if (next is null)
                {
                    // An explicitly requested folder may sort beyond the first
                    // 350 siblings. Locate it without eagerly materializing all
                    // preceding pages or expanding unrelated directories.
                    try
                    {
                        if (!Directory.Exists(current) ||
                            (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) break;
                        next = MakeNode(current);
                        InsertSortedChild(node, next);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { break; }
                }
                node = next;
            }
            if (version != _navigationVersion || token.IsCancellationRequested) return;
            _suppressSelection = true;
            node.IsSelected = true;
            // Scroll this navigation rail vertically only. TreeViewItem.BringIntoView()
            // bubbles to the outer workspace and can move the entire split layout right.
            TreeViewItem selectedNode = node;
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (version != _navigationVersion || token.IsCancellationRequested ||
                    !selectedNode.IsVisible || NavigationScroll.ViewportHeight <= 0) return;
                try
                {
                    // A TreeViewItem's ActualHeight includes its expanded descendants.
                    // Scroll to the 38-DIP header row, not the full subtree height.
                    // This keeps deep paths readable without jumping over the target.
                    selectedNode.ApplyTemplate();
                    FrameworkElement? headerRow = selectedNode.Template.FindName("TreeRow", selectedNode)
                        as FrameworkElement;
                    double top = (headerRow ?? selectedNode).TransformToAncestor(NavigationScroll).Transform(new Point()).Y;
                    double height = headerRow is { ActualHeight: > 0 } ? headerRow.ActualHeight : 38.0;
                    double bottom = top + height;
                    if (top < 0 || bottom > NavigationScroll.ViewportHeight)
                    {
                        double centered = NavigationScroll.VerticalOffset + top
                            - Math.Max(0, NavigationScroll.ViewportHeight - height) * 0.38;
                        NavigationScroll.ScrollToVerticalOffset(Math.Clamp(centered, 0, NavigationScroll.ScrollableHeight));
                    }
                }
                catch (InvalidOperationException) { /* Control was detached during navigation. */ }
            }));
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
