using System.Windows;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

public partial class InputDialog : Window
{
    public string InputText { get; private set; } = "";

    private readonly Func<string, string?>? _validate;

    /// <param name="validate">
    /// Checks the text when OK is clicked: returns what is wrong with it, or null if it is fine.
    /// If something is wrong the message is shown and the dialog stays open, until OK works or Cancel is clicked.
    /// </param>
    public InputDialog(string title, string prompt, string defaultValue = "", Func<string, string?>? validate = null)
    {
        _validate = validate;
        InitializeComponent();
        Title           = title;
        PromptText.Text = prompt;
        InputTextBox.Text = defaultValue;
        Loaded += (_, _) => { InputTextBox.SelectAll(); InputTextBox.Focus(); };
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var error = _validate?.Invoke(InputTextBox.Text);
        if (error != null)
        {
            MessageBox.Show(this, error, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            InputTextBox.Focus();
            InputTextBox.SelectAll();
            return;
        }

        InputText    = InputTextBox.Text;
        DialogResult = true;
    }
}
