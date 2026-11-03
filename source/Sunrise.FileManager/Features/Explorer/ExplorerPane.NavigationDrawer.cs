using System.Windows;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Sunrise.FileManager.Services;
namespace Sunrise.FileManager.Controls;
public partial class ExplorerPane
{
    private bool _treeAutoHide;
    private bool _navigationRevealed;
    private bool _navigationMenuOpen;
    private bool _navigationResizing;
    private DispatcherTimer? _navigationHideTimer;
    private void ApplyNavigationState()
    {
        if (NavigationDrawer is null) return;
        bool pinned = _treePreference && !_treeAutoHide;
        double drawerWidth = NavigationPanelPolicy.FitWidth(_navigationWidth, ActualWidth, _treeAutoHide);
        TreeColumn.Width = new GridLength(_treePreference && !_treeAutoHide ? drawerWidth : 0);
        TreeDividerColumn.Width = new GridLength(NavigationPanelPolicy.RailWidth);
        TreeDivider.Visibility = Visibility.Visible;
        NavigationDrawer.Width = drawerWidth;
        System.Windows.Controls.Grid.SetColumnSpan(NavigationDrawer, pinned ? 1 : 3);
        NavigationDrawer.Margin = pinned ? new Thickness(0) : new Thickness(NavigationPanelPolicy.RailWidth, 0, 0, 0);
        NavigationToggleButton.Content = pinned ? "‹" : "›";
        NavigationAutoHideButton.Content = _treeAutoHide ? "◆" : "◇";
        NavigationAutoHideMenu.IsChecked = _treeAutoHide;
        NavigationToggleButton.ToolTip = pinned ? "탐색 창 닫기" : "탐색 창 고정하여 열기";
        TreeToggle.Content = pinned ? "탐색 창 닫기" : "탐색 창";
        if (!_treePreference || pinned)
        {
            _navigationRevealed = pinned;
            NavigationTranslation.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            NavigationTranslation.X = 0;
            NavigationDrawer.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (!_navigationRevealed)
        {
            NavigationTranslation.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            NavigationTranslation.X = -drawerWidth;
            NavigationDrawer.Visibility = Visibility.Collapsed;
        }
        // Do not reload the tree when the user only resizes the drawer.
        // Folder navigation already synchronizes the active path on actual changes.
    }
    private void RevealNavigation(bool reveal)
    {
        if (!_treePreference || !_treeAutoHide || _navigationRevealed == reveal) return;
        _navigationRevealed = reveal;
        double from = NavigationTranslation.X;
        double target = reveal ? 0 : -NavigationPanelPolicy.FitWidth(_navigationWidth, ActualWidth, true);
        NavigationDrawer.Visibility = Visibility.Visible;
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) =>
        {
            if (_navigationRevealed != reveal || !_treeAutoHide) return;
            NavigationTranslation.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            NavigationTranslation.X = target;
            if (!reveal) NavigationDrawer.Visibility = Visibility.Collapsed;
        };
        NavigationTranslation.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private void NavigationDrawer_MouseEnter(object sender, MouseEventArgs e)
    {
        _navigationHideTimer?.Stop();
        RevealNavigation(true);
    }
    private void NavigationDrawer_MouseLeave(object sender, MouseEventArgs e) => ScheduleNavigationHide();
    private void ScheduleNavigationHide()
    {
        _navigationHideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        _navigationHideTimer.Tick -= NavigationHideTick;
        _navigationHideTimer.Tick += NavigationHideTick;
        _navigationHideTimer.Start();
    }
    private void NavigationHideTick(object? sender, EventArgs e)
    {
        if (NavigationDrawer.IsMouseOver || TreeDivider.IsMouseOver || _navigationMenuOpen || _navigationResizing || NavigationDrawer.IsMouseCaptureWithin) return;
        _navigationHideTimer?.Stop();
        RevealNavigation(false);
    }
    private void NavigationDrawer_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e) => _navigationMenuOpen = true;
    private void NavigationDrawer_ContextMenuClosing(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    { _navigationMenuOpen = false; ScheduleNavigationHide(); }
    private void NavigationAutoHide_Click(object sender, RoutedEventArgs e)
    {
        _treeAutoHide = !_treeAutoHide;
        _treePreference = true;
        _navigationRevealed = false;
        ApplyNavigationState();
        SettingsChanged?.Invoke();
    }
    private void NavigationResize_Started(object sender, DragStartedEventArgs e)
    {
        _navigationResizing = true;
        _navigationHideTimer?.Stop();
        NavigationTranslation.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        NavigationTranslation.X = 0;
    }

    private void NavigationResize_Delta(object sender, DragDeltaEventArgs e)
    {
        if (!_treePreference || !double.IsFinite(e.HorizontalChange)) return;
        double limit = NavigationPanelPolicy.FitWidth(NavigationPanelPolicy.MaximumWidth, ActualWidth, _treeAutoHide);
        _navigationWidth = Math.Clamp(_navigationWidth + e.HorizontalChange, NavigationPanelPolicy.MinimumWidth, limit);
        ApplyNavigationState();
    }

    private void NavigationResize_Completed(object sender, DragCompletedEventArgs e)
    {
        _navigationResizing = false;
        SettingsChanged?.Invoke();
        if (_treeAutoHide && !NavigationDrawer.IsMouseOver && !TreeDivider.IsMouseOver)
            ScheduleNavigationHide();
    }
}

