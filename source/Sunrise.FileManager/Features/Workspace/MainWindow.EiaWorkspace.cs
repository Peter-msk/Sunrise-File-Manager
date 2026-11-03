using System.Windows;
using Sunrise.FileManager.Controls;

namespace Sunrise.FileManager;

public partial class MainWindow
{
    private EiaWorkspacePlanningWindow? _eiaWorkspacePlanningWindow;

    private void EiaWorkspacePlanning_Click(object sender, RoutedEventArgs e)
    {
        if (_eiaWorkspacePlanningWindow is { IsVisible: true } window)
        {
            window.Activate();
            return;
        }
        var dialog = new EiaWorkspacePlanningWindow { Owner = this };
        _eiaWorkspacePlanningWindow = dialog;
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_eiaWorkspacePlanningWindow, dialog))
                _eiaWorkspacePlanningWindow = null;
        };
        dialog.Show();
    }
}
