using System.Windows;
using System.Windows.Input;

namespace EverLinkHost;

/// <summary>
/// Simple modal text-entry dialog, used for the device rename flow. Returns the entered
/// text via Result after ShowDialog() if OK was clicked, null if cancelled, or empty string
/// if "Reset to Default" was clicked (caller interprets empty string as "clear the nickname").
/// </summary>
public partial class InputDialog : Window
{
    public string? Result { get; private set; }

    public InputDialog(string prompt, string currentValue)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        InputTextBox.Text = currentValue;
        InputTextBox.Focus();
        InputTextBox.SelectAll();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Result = InputTextBox.Text;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        Result = string.Empty; // caller treats empty as "clear nickname / use default"
        DialogResult = true;
    }

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OkButton_Click(sender, e);
        else if (e.Key == Key.Escape) CancelButton_Click(sender, e);
    }
}
