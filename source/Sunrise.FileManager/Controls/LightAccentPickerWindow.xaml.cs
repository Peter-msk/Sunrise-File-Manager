using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>
/// R35, phase 4: light-only preview transaction. Preview callbacks do not write settings;
/// Apply is the only commit point. Closing by X, Escape or Cancel restores the old accent.
/// No files, network or protected company content are consulted here.
/// </summary>
public partial class LightAccentPickerWindow : Window
{
    private readonly string? _committedOnOpen;
    private readonly Action<string?> _preview;
    private readonly Func<string?, bool> _commit;
    private bool _ready;
    private bool _syncing;
    private bool _applied;
    private string? _candidate;

    public LightAccentPickerWindow(string? current, Action<string?> preview, Func<string?, bool> commit)
    {
        _committedOnOpen = current;
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        InitializeComponent();
        _ready = true;
        _candidate = current;
        SelectionStatus.Text = current is null ? "현재 설정: Sunrise 기본값" : $"현재 설정: {current}";
        UpdateControls(current ?? "#096B94", resetChoice: current is null);
    }

    private void UpdateControls(string hex, bool resetChoice = false)
    {
        string normalized = LightAccentPaletteEngine.NormalizeHex(hex);
        _syncing = true;
        try
        {
            HexBox.Text = normalized;
            RedSlider.Value = byte.Parse(normalized.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            GreenSlider.Value = byte.Parse(normalized.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            BlueSlider.Value = byte.Parse(normalized.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            RedValue.Text = ((int)RedSlider.Value).ToString(CultureInfo.InvariantCulture);
            GreenValue.Text = ((int)GreenSlider.Value).ToString(CultureInfo.InvariantCulture);
            BlueValue.Text = ((int)BlueSlider.Value).ToString(CultureInfo.InvariantCulture);
            ColorSwatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(normalized)!);
            HexError.Text = "";
            if (resetChoice) _candidate = null;
        }
        finally { _syncing = false; }
    }

    private void PreviewCandidate(string? hex)
    {
        if (!_ready) return;
        try
        {
            _preview(hex);
            _candidate = hex;
            SelectionStatus.Text = hex is null ? "기본 Sunrise 라이트 색상 미리보기" : $"선택 색상: {hex} (미리보기)";
            HexError.Text = "";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            HexError.Text = "색상을 표시하지 못했습니다. " + ex.Message;
        }
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex }) return;
        UpdateControls(hex);
        PreviewCandidate(LightAccentPaletteEngine.NormalizeHex(hex));
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _syncing) return;
        try
        {
            string hex = LightAccentPaletteEngine.NormalizeHex(HexBox.Text);
            UpdateControls(hex);
            PreviewCandidate(hex);
        }
        catch (ArgumentException)
        {
            HexError.Text = "6자리 HEX 색상을 입력하세요. 예: #1476B8";
        }
    }

    private void RgbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        string hex = $"#{(int)RedSlider.Value:X2}{(int)GreenSlider.Value:X2}{(int)BlueSlider.Value:X2}";
        UpdateControls(hex);
        PreviewCandidate(hex);
    }

    private void RestoreDefault_Click(object sender, RoutedEventArgs e)
    {
        UpdateControls("#096B94", resetChoice: true);
        PreviewCandidate(null);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        // An invalid, unfinished HEX must never silently commit a previous color.
        if (!string.IsNullOrEmpty(HexError.Text)) return;
        if (!_commit(_candidate)) return;
        _applied = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Picker_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    private void Picker_Closing(object? sender, CancelEventArgs e)
    {
        if (!_applied) _preview(_committedOnOpen);
    }
}
