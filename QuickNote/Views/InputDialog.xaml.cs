using System.Windows;
using System.Windows.Input;

namespace QuickNote.Views;

public partial class InputDialog : Window
{
    public string Result { get; private set; } = string.Empty;

    public InputDialog(string prompt, string defaultValue = "")
    {
        InitializeComponent();
        PromptText.Text = prompt;
        InputBox.Text = defaultValue;
        InputBox.SelectAll();
        InputBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = InputBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Ok_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
            e.Handled = true;
        }
    }
}
