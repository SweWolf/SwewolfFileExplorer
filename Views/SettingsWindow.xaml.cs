using System.Windows;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

public partial class SettingsWindow : Window
{
    private static readonly string[] Styles = { "Light", "Dark", "System", "Cyber Cyan" };

    public SettingsWindow()
    {
        InitializeComponent();
        var current = AppSettingsStore.Current;
        StyleBox.SelectedIndex = Math.Max(0, Array.IndexOf(Styles, current.Style));
        UnreadBoldBox.IsChecked   = current.UnreadBold;
        UnreadItalicBox.IsChecked = current.UnreadItalic;
        HiddenExtensionsBox.Text  = current.HiddenExtensions;
        HiddenExtensionsInArchivesBox.Text = current.HiddenExtensionsInArchives;
        HideShellExtensionsBox.IsChecked   = current.HideShellExtensions;
        SuggestMoveBox.IsChecked           = current.SuggestMoveAfterSmartRename;
        TerminalBox.IsChecked              = current.ShowOpenInTerminal;
        PowerShellBox.IsChecked            = current.ShowOpenInPowerShell;
        CommandPromptBox.IsChecked         = current.ShowOpenInCommandPrompt;

        // A program that is not installed is never offered in the menu, so its box cannot do anything.
        foreach (var (box, kind, name) in new[]
                 { (TerminalBox, TerminalKind.WindowsTerminal, "Windows Terminal"), (PowerShellBox, TerminalKind.PowerShell, "Windows PowerShell"),
                   (CommandPromptBox, TerminalKind.CommandPrompt, "The Command Prompt") })
        {
            if (TerminalLauncher.IsInstalled(kind)) continue;
            box.IsEnabled = false;
            box.ToolTip   = $"{name} is not installed on this computer, so it is not shown in the menu.";
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettingsStore.Current;
        settings.Style = Styles[StyleBox.SelectedIndex];
        settings.UnreadBold   = UnreadBoldBox.IsChecked == true;
        settings.UnreadItalic = UnreadItalicBox.IsChecked == true;
        settings.HiddenExtensions = string.Join(";", HiddenItems.Parse(HiddenExtensionsBox.Text));   // tidied: no dots or blanks
        settings.HiddenExtensionsInArchives = string.Join(";", HiddenItems.Parse(HiddenExtensionsInArchivesBox.Text));
        settings.HideShellExtensions = HideShellExtensionsBox.IsChecked == true;
        settings.SuggestMoveAfterSmartRename = SuggestMoveBox.IsChecked == true;
        settings.ShowOpenInTerminal      = TerminalBox.IsChecked == true;
        settings.ShowOpenInPowerShell    = PowerShellBox.IsChecked == true;
        settings.ShowOpenInCommandPrompt = CommandPromptBox.IsChecked == true;
        AppSettingsStore.Save(settings);

        ThemeService.Apply(settings.Style);
        UnreadStyle.Instance.Refresh();
        DialogResult = true;
    }
}
