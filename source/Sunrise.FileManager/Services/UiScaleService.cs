using System.Windows;
using System.Windows.Media;
namespace Sunrise.FileManager.Services;
public static class UiScaleService
{
    public static int Percent { get; private set; } = UiScalePolicy.DefaultPercent;
    public static void Set(int percent)
    {
        Percent = UiScalePolicy.Normalize(percent);
        Application.Current.Resources["UiScaleFactor"] = Percent / 100d;
        foreach (Window window in Application.Current.Windows) Apply(window);
    }
    public static void Apply(Window window)
    {
        if (window.Content is FrameworkElement root)
            root.LayoutTransform = new ScaleTransform(Percent / 100d, Percent / 100d);
    }
}
