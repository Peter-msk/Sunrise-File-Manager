namespace Sunrise.FileManager.Services;
public static class UiScalePolicy
{
    public const int DefaultPercent = 80;
    public static readonly int[] Choices = [60, 70, 80, 90, 100, 110, 125];
    public static int Normalize(int value) => Choices.Contains(value) ? value : DefaultPercent;
}
