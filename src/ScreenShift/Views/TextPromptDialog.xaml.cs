using System.Windows;

namespace ScreenShift.Views;

/// <summary>Small dark-themed input box: one line of text, OK, Cancel. Used for profile names.</summary>
public partial class TextPromptDialog : Window
{
    private TextPromptDialog(string title, string prompt, string initialValue)
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;
        PromptText.Text = prompt;
        Input.Text = initialValue;

        Loaded += (_, _) =>
        {
            Input.SelectAll();
            Input.Focus();
        };
    }

    /// <summary>Returns the trimmed text, or null when cancelled or left empty.</summary>
    public static string? Show(Window? owner, string title, string prompt, string initialValue = "")
    {
        var dialog = new TextPromptDialog(title, prompt, initialValue);

        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var text = dialog.Input.Text.Trim();
        return text.Length == 0 ? null : text;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
