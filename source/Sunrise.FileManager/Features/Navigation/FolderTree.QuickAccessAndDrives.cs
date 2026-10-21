using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>
/// Feature module for FolderTree: Quick Access pinned folders, drive capacity gauges,
/// Windows special folder navigation, and drag-and-drop onto tree folders.
/// </summary>
public partial class FolderTree
{
    public IReadOnlyList<string> PinnedFolders => _pinnedFolders;

    public void SetPinnedFolders(IEnumerable<string>? paths)
    {
        _pinnedFolders.Clear();
        if (paths is not null)
        {
            foreach (string p in paths.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(20))
                _pinnedFolders.Add(p);
        }
        RenderPinnedFolders();
    }

    public void PinFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        string full = Path.GetFullPath(path);
        if (!_pinnedFolders.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            _pinnedFolders.Add(full);
            RenderPinnedFolders();
            PinnedFoldersChanged?.Invoke(_pinnedFolders);
        }
    }

    private void RenderPinnedFolders()
    {
        if (PinnedPanel is null) return;
        PinnedPanel.Children.Clear();
        if (_pinnedFolders.Count == 0)
        {
            PinnedPanel.Children.Add(new TextBlock
            {
                Text = "고정된 폴더 없음 ([+ 고정] 클릭)",
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
                Margin = new Thickness(8, 2, 4, 2)
            });
            return;
        }
        foreach (string folder in _pinnedFolders.ToArray())
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string label = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            if (string.IsNullOrWhiteSpace(label)) label = folder;

            var navBtn = new Button
            {
                Content = "📌 " + label,
                ToolTip = folder,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 3, 4, 3),
                MinHeight = 26,
                AllowDrop = true
            };
            navBtn.Click += (_, _) =>
            {
                if (Directory.Exists(folder)) PathChosen?.Invoke(folder);
                else MessageBox.Show("고정된 폴더를 찾을 수 없습니다.", "즐겨찾기", MessageBoxButton.OK, MessageBoxImage.Information);
            };
            AttachFolderDropTarget(navBtn, folder);

            var removeBtn = new Button
            {
                Content = "×",
                ToolTip = "즐겨찾기 고정 해제",
                Padding = new Thickness(5, 1, 5, 1),
                MinHeight = 24,
                Width = 24
            };
            removeBtn.Click += (_, _) =>
            {
                _pinnedFolders.RemoveAll(x => string.Equals(x, folder, StringComparison.OrdinalIgnoreCase));
                RenderPinnedFolders();
                PinnedFoldersChanged?.Invoke(_pinnedFolders);
            };
            Grid.SetColumn(navBtn, 0);
            Grid.SetColumn(removeBtn, 1);
            row.Children.Add(navBtn);
            row.Children.Add(removeBtn);
            PinnedPanel.Children.Add(row);
        }
    }

    private TreeViewItem MakeNode(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string title = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(title)) title = path;
        var item = new TreeViewItem { Header = MakeHeader(title, false), Tag = path, AllowDrop = true };
        item.Items.Add(new TreeViewItem { Header = "읽는 중...", IsEnabled = false });
        AttachFolderDropTarget(item, path);
        return item;
    }

    private void AttachFolderDropTarget(FrameworkElement element, string targetFolder)
    {
        element.PreviewDragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] files &&
                files.Length > 0 && Directory.Exists(targetFolder))
            {
                bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 || (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 || (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                TransferKind kind = ExplorerCommandRouter.ResolveDragTransferKind(files, targetFolder, ctrl, shift);
                e.Effects = kind == TransferKind.Move ? DragDropEffects.Move : DragDropEffects.Copy;
                e.Handled = true;
            }
        };
        element.PreviewDrop += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] files &&
                files.Length > 0 && Directory.Exists(targetFolder))
            {
                e.Handled = true;
                bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 || (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 || (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                TransferKind kind = ExplorerCommandRouter.ResolveDragTransferKind(files, targetFolder, ctrl, shift);
                FolderDropRequested?.Invoke(files, targetFolder, kind);
            }
        };
    }

    private static FrameworkElement MakeHeader(string title, bool drive, string? subtitle = null, double? usagePercent = null)
    {
        var outer = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 1, 0, 1) };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new System.Windows.Shapes.Path
        {
            Width = 14,
            Height = 12,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Fill = new System.Windows.Media.SolidColorBrush(drive ? System.Windows.Media.Color.FromRgb(9, 107, 148) : System.Windows.Media.Color.FromRgb(211, 158, 49)),
            Data = System.Windows.Media.Geometry.Parse(drive ? "M0,1 L18,1 18,14 0,14 Z M3,10 L5,10 5,12 3,12 Z" : "M0,2 L6,2 9,5 20,5 20,17 0,17 Z")
        });
        header.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontWeight = drive ? FontWeights.SemiBold : FontWeights.Normal });
        outer.Children.Add(header);

        if (drive && !string.IsNullOrWhiteSpace(subtitle))
        {
            var capacityLabel = new TextBlock
            {
                Text = subtitle,
                FontSize = 10.5,
                Margin = new Thickness(22, 1, 0, 2)
            };
            capacityLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            outer.Children.Add(capacityLabel);
            if (usagePercent is double pct)
            {
                var bar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = Math.Clamp(pct, 0, 100),
                    Height = 4,
                    Width = 135,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(22, 0, 0, 2)
                };
                outer.Children.Add(bar);
            }
        }
        return outer;
    }

    private void ReloadRoots()
    {
        CancelPendingOperations();
        _loaded.Clear();
        _loading.Clear();
        FolderItems.Items.Clear();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                var root = MakeNode(drive.RootDirectory.FullName);
                string title = drive.Name;
                string? subtitle = null;
                double? pct = null;
                try
                {
                    if (drive.IsReady)
                    {
                        string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "로컬 디스크" : drive.VolumeLabel;
                        title = $"{label} ({drive.Name.TrimEnd('\\')})";
                        long free = drive.AvailableFreeSpace;
                        long total = drive.TotalSize;
                        subtitle = $"여유 {FileEntry.HumanSize(free)} / {FileEntry.HumanSize(total)}";
                        if (total > 0) pct = 100.0 * (total - free) / total;
                    }
                }
                catch { }
                root.Header = MakeHeader(title, true, subtitle, pct);
                FolderItems.Items.Add(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string ResolveKnownFolderPath(string key) => WindowsKnownLocations.Resolve(key);

    private void NavigateKnownFolder(string key)
    {
        if (key == "RecycleBin")
        {
            PathChosen?.Invoke(WindowsKnownLocations.RecycleBin);
            return;
        }
        string path = ResolveKnownFolderPath(key);
        if (Directory.Exists(path)) PathChosen?.Invoke(path);
        else MessageBox.Show("폴더를 찾을 수 없습니다.", "폴더 이동", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
