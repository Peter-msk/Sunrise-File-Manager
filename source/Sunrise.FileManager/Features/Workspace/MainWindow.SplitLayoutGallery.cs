using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Media;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager;

/// <summary>
/// R01: selected existing layouts only beneath 2/3/4 buttons; single applies instantly.
/// Existing canonical production layouts only; opening the list never applies a layout.
/// Workspace fit details are available as item tooltips, not crowded into the list.
/// </summary>
public partial class MainWindow
{
    private bool CanChangeSplitLayout => !_working && !_aiBusy && _activeRecycleJobs == 0;
    private ContextMenu? _openSplitGalleryMenu;

    private void ApplyLastLayoutForCount(int count)
    {
        if (!CanChangeSplitLayout) return;
        int normalized = WorkspaceSplitLayoutEngine.ClampVisiblePanels(count);
        ArrangePanels(normalized, _lastPresetByCount[normalized]);
    }

    private void CurrentSplitLayoutGallery_Click(object sender, RoutedEventArgs e)
    {
        Button anchor = _visiblePanels switch
        {
            2 => TwoButton,
            3 => ThreeButton,
            4 => FourButton,
            _ => OneButton
        };
        if (_visiblePanels == 1) ApplyLastLayoutForCount(1);
        else ShowSplitLayoutGallery(_visiblePanels, anchor);
    }

    private void ShowSplitLayoutGallery(int count, Button anchor)
    {
        // A click on another count button must replace—not stack—its popup.
        if (_openSplitGalleryMenu is { IsOpen: true }) _openSplitGalleryMenu.IsOpen = false;
        int normalized = WorkspaceSplitLayoutEngine.ClampVisiblePanels(count);
        var choices = WorkspaceSplitLayoutEngine.GetPresetsForCount(normalized);
        bool canApply = CanChangeSplitLayout;
        // Viewport measurements reflect the space left after right-hand utility panes;
        // the full window width would mislead the user in 2–4 pane mode.
        double availableWidth = WorkspaceViewport.ViewportWidth > 1
            ? WorkspaceViewport.ViewportWidth : WorkspaceViewport.ActualWidth;
        double availableHeight = WorkspaceViewport.ViewportHeight > 1
            ? WorkspaceViewport.ViewportHeight : WorkspaceViewport.ActualHeight;
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = 0,
            VerticalOffset = 0,
            MinWidth = 212,
            MaxHeight = Math.Max(250, SystemParameters.WorkArea.Height - 100),
            Tag = normalized
        };
        menu.SetResourceReference(Control.BackgroundProperty, "ThemeSurface");
        menu.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        foreach (var choice in choices)
        {
            bool selected = normalized == _visiblePanels && choice.Preset == _splitPreset;
            string conciseLabel = choice.Preset switch
            {
                SplitLayoutPreset.TwoColumns => "좌우 2분할",
                SplitLayoutPreset.TwoRows => "상하 2분할",
                SplitLayoutPreset.ThreeTop1Bottom2 => "상단 1 · 하단 2",
                SplitLayoutPreset.ThreeBottom1Top2 => "상단 2 · 하단 1",
                SplitLayoutPreset.ThreeLeft1Right2 => "좌측 1 · 우측 2",
                SplitLayoutPreset.ThreeRight1Left2 => "좌측 2 · 우측 1",
                SplitLayoutPreset.ThreeColumns => "가로 3열",
                SplitLayoutPreset.ThreeRows => "세로 3행",
                SplitLayoutPreset.FourGrid2x2 => "2×2 격자",
                SplitLayoutPreset.FourColumns => "가로 4열",
                SplitLayoutPreset.FourRows => "세로 4행",
                SplitLayoutPreset.FourTop1Bottom3 => "상단 1 · 하단 3",
                SplitLayoutPreset.FourLeft1Right3 => "좌측 1 · 우측 3",
                _ => "단일 화면"
            };
            var title = new TextBlock
            {
                Text = conciseLabel,
                FontSize = 12.5,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 1, 4, 1) };
            content.Children.Add(CreateSplitLayoutThumbnail(choice.Preset, selected));
            content.Children.Add(title);
            // R06 fitting hints remain nonintrusive (tooltip / accessibility only).
            SplitLayoutFitAssessment fit = WorkspaceSplitLayoutEngine.AssessViewportFit(
                choice.Preset, availableWidth, availableHeight);
            var item = new MenuItem
            {
                Header = content,
                Tag = choice.Preset,
                IsCheckable = false,
                IsEnabled = canApply,
                ToolTip = $"{choice.Description}\n{fit.DisplayText}",
                MinHeight = 44,
                Padding = new Thickness(4, 2, 6, 2)
            };
            if (selected) item.SetResourceReference(Control.BackgroundProperty, "Selected");
            AutomationProperties.SetName(item,
                $"{normalized}분할. {conciseLabel}. {choice.Description}. {fit.DisplayText}" );
            SplitLayoutPreset requestedPreset = choice.Preset;
            item.Click += (_, _) =>
            {
                if (CanChangeSplitLayout) ArrangePanels(normalized, requestedPreset);
            };
            menu.Items.Add(item);
        }
        anchor.ContextMenu = menu;
        _openSplitGalleryMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openSplitGalleryMenu, menu)) _openSplitGalleryMenu = null;
            if (ReferenceEquals(anchor.ContextMenu, menu)) anchor.ContextMenu = null;
        };
        menu.Opened += (_, _) =>
        {
            // Keyboard users enter the actual list, without selecting a layout.
            var focus = menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Tag is SplitLayoutPreset p && p == _splitPreset && normalized == _visiblePanels && item.IsEnabled)
                ?? menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.IsEnabled);
            focus?.Focus();
        };
        menu.IsOpen = true;
    }

    private FrameworkElement CreateSplitLayoutThumbnail(SplitLayoutPreset preset, bool selected)
    {
        var blueprint = WorkspaceSplitLayoutEngine.BuildBlueprint(preset);
        var thumbnail = new Grid
        {
            Width = 56,
            Height = 32,
            Margin = new Thickness(0, 0, 9, 0),
            SnapsToDevicePixels = true,
        };
        thumbnail.SetResourceReference(Panel.BackgroundProperty, "ThemeSoftAlt");
        for (int c = 0; c < blueprint.StarColumns * 2 - 1; c++)
            thumbnail.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = c % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(3)
            });
        for (int r = 0; r < blueprint.StarRows * 2 - 1; r++)
            thumbnail.RowDefinitions.Add(new RowDefinition
            {
                Height = r % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(3)
            });
        foreach (PaneGridPlacement placement in blueprint.Panes)
        {
            var pane = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(1),
                Child = new TextBlock
                {
                    Text = (placement.PaneIndex + 1).ToString(),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            pane.SetResourceReference(Border.BackgroundProperty, selected ? "ThemeSplitActive" : "ThemeSplitInactive");
            pane.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
            ((TextBlock)pane.Child).SetResourceReference(TextBlock.ForegroundProperty, selected ? "ThemeSplitActiveText" : "ThemeSplitText");
            Grid.SetRow(pane, placement.Row);
            Grid.SetColumn(pane, placement.Column);
            Grid.SetRowSpan(pane, placement.RowSpan);
            Grid.SetColumnSpan(pane, placement.ColumnSpan);
            thumbnail.Children.Add(pane);
        }
        return thumbnail;
    }
}
