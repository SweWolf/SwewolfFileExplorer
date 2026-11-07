using System.Windows;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

public partial class InputDialog : Window
{
    public string InputText { get; private set; } = "";

    /// <summary>"Use Smart Name" was shown and checked when OK was clicked.</summary>
    public bool SmartNameChecked { get; private set; }

    private readonly Func<string, string?>? _validate;

    /// <param name="validate">
    /// Checks the text when OK is clicked: returns what is wrong with it, or null if it is fine.
    /// If something is wrong the message is shown and the dialog stays open, until OK works or Cancel is clicked.
    /// </param>
    private readonly string _original;
    private readonly string? _smartName;
    private readonly bool _splitExtension;

    /// <param name="caseButtons">Shows Upper / Lower / Proper Case buttons (Rename).</param>
    /// <param name="keepExtension">
    /// A file whose name is shown with its extension: the extension gets a box of its own after a dot (when the name has one),
    /// so it is not changed by accident, and the case buttons leave it alone.
    /// </param>
    /// <param name="extensionReadOnly">The extension box can be read but not changed (a playlist keeps its .m3u8).</param>
    /// <param name="hint">A few words of explanation under the box (grey).</param>
    /// <param name="currentName">Rename: the present name, shown above in a read-only box.</param>
    /// <param name="smartApplies">The file is a music or video file: "Use Smart Name" is shown (always checked at the start; with no <paramref name="smartName"/> the present name is the smart name).</param>
    /// <param name="smartName">
    /// A suggested name (SmartName.Suggest). When given, a "Use Smart Name" box is shown, checked: the text starts as the
    /// suggestion; unchecking it brings the original name back.
    /// </param>
    public InputDialog(string title, string prompt, string defaultValue = "", Func<string, string?>? validate = null,
                       bool caseButtons = false, bool keepExtension = false, string? smartName = null, string? currentName = null,
                       bool smartApplies = false, string? hint = null, bool extensionReadOnly = false)
    {
        _validate       = validate;
        _original       = defaultValue;
        _smartName      = smartName;
        _splitExtension = keepExtension && defaultValue.LastIndexOf('.') > 0;
        InitializeComponent();
        Title           = title;
        PromptText.Text = prompt;
        if (!string.IsNullOrEmpty(hint))
        {
            HintText.Text       = hint;
            HintText.Visibility = Visibility.Visible;
        }
        if (currentName != null)
        {
            CurrentNameBox.Text    = currentName;
            CurrentPanel.Visibility = Visibility.Visible;
        }
        if (_splitExtension)
        {
            DotText.Visibility = ExtTextBox.Visibility = Visibility.Visible;
            ExtTextBox.MaxLength = 20;
            if (extensionReadOnly)
            {
                ExtTextBox.IsReadOnly = true;
                ExtTextBox.IsTabStop  = false;
            }
        }
        SetName(defaultValue);
        TextBoxMenu.Attach(InputTextBox);   // Tab selects all, right-click has Clear
        TextBoxMenu.Attach(ExtTextBox);
        Width = 456;   // the name box 20 % wider than before (380): file names can be long

        if (caseButtons || smartApplies)
        {
            ExtrasPanel.Visibility = Visibility.Visible;
            Width = 624;   // file names can be long (420, then 520, now 20 % wider again: the user's request)
            if (smartApplies)
            {
                SmartCheckBox.Visibility = Visibility.Visible;
                SmartCheckBox.IsChecked = true;   // sets the text through SmartCheckBox_Changed
            }
        }
        Loaded += (_, _) => { InputTextBox.SelectAll(); InputTextBox.Focus(); };
    }

    /// <summary>Puts a whole file name in the box(es): split at the last dot when the extension has a box of its own.</summary>
    private void SetName(string name)
    {
        var dot = _splitExtension ? name.LastIndexOf('.') : -1;
        if (dot > 0) { InputTextBox.Text = name[..dot]; ExtTextBox.Text = name[(dot + 1)..]; }
        else         { InputTextBox.Text = name;        ExtTextBox.Text = ""; }
    }

    /// <summary>The whole name: the name, a dot and the extension (no dot when the extension box is empty).</summary>
    private string FullName() =>
        _splitExtension && ExtTextBox.Text.Length > 0 ? InputTextBox.Text + "." + ExtTextBox.Text : InputTextBox.Text;

    private void SmartCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_smartName == null || InputTextBox == null) return;
        SetName(SmartCheckBox.IsChecked == true ? _smartName : _original);
        InputTextBox.SelectAll();
        InputTextBox.Focus();
    }

    /// <summary>Applies a case change to the name (never to the extension box).</summary>
    private void ChangeCase(Func<string, string> change)
    {
        InputTextBox.Text = change(InputTextBox.Text);
        InputTextBox.Focus();
        InputTextBox.CaretIndex = InputTextBox.Text.Length;
    }

    private void UpperButton_Click(object sender, RoutedEventArgs e)  => ChangeCase(SmartName.ToUpper);
    private void LowerButton_Click(object sender, RoutedEventArgs e)  => ChangeCase(SmartName.ToLower);
    private void ProperButton_Click(object sender, RoutedEventArgs e) => ChangeCase(SmartName.ToProper);

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var full  = FullName();
        var error = _splitExtension && string.IsNullOrWhiteSpace(InputTextBox.Text) ? "The name cannot be empty." : _validate?.Invoke(full);
        if (error != null)
        {
            MessageBox.Show(this, error, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            InputTextBox.Focus();
            InputTextBox.SelectAll();
            return;
        }

        InputText    = full;
        SmartNameChecked = SmartCheckBox.Visibility == Visibility.Visible && SmartCheckBox.IsChecked == true;
        DialogResult = true;
    }
}
