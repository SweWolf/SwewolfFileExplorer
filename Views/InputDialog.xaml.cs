using System.Windows;

namespace MyFileExplorer.Views;

public partial class InputDialog : Window
{
    public string InputText { get; private set; } = "";

    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        InitializeComponent();
        Title           = title;
        PromptText.Text = prompt;
        InputTextBox.Text = defaultValue;
        Loaded += (_, _) => { InputTextBox.SelectAll(); InputTextBox.Focus(); };
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        InputText    = InputTextBox.Text;
        DialogResult = true;
    }
}
