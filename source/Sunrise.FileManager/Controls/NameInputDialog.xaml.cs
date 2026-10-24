using System.Windows;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

public partial class NameInputDialog : Window
{
    private readonly Func<string, string> _validate;
    public string ResultName { get; private set; } = string.Empty;

    public NameInputDialog(string title, string prompt, string initialValue, Func<string, string>? validate = null)
    {
        InitializeComponent();
        _validate = validate ?? FileActionsService.ValidateName;
        Title = title;
        PromptText.Text = prompt;
        NameBox.Text = initialValue;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ResultName = _validate(NameBox.Text);
            DialogResult = true;
        }
        catch (ArgumentException error)
        {
            MessageBox.Show(this, error.Message, "이름 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
        }
    }
}
