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
        HideKnownExtensionsBox.IsChecked   = current.HideKnownExtensions;
        RegisteredTypeNamesBox.IsChecked   = current.UseRegisteredTypeNames;
        SuggestMoveBox.IsChecked           = current.SuggestMoveAfterSmartRename;
        RightClickSelectsNodeBox.IsChecked = current.RightClickSelectsTreeNode;
        DisableDragDropBox.IsChecked       = current.DisableInternalDragDrop;
        PreloadPlayerBox.IsChecked         = current.PreloadMediaPlayer;
        VirusTotalKeyBox.Password          = VirusTotal.GetApiKey() ?? "";
        MediaPlayerGroup.Visibility        = MediaPlayerLauncher.FindPlayer() != null ? Visibility.Visible : Visibility.Collapsed;   // no player, nothing to preload
        WarnHiddenNotSelectedBox.IsChecked = current.WarnHiddenNotSelected;
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

    private void VirusTotalKeyLink_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = VirusTotal.KeyPage, UseShellExecute = true }); }
        catch { }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var key = VirusTotalKeyBox.Password.Trim();
        if (key.Length > 0 && !VirusTotal.IsValidKeyFormat(key))
        {
            MessageBox.Show(this, "A VirusTotal API key has 64 letters and digits. Copy it again from VirusTotal's website, or leave the box empty.",
                            AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            VirusTotalKeyBox.Focus();
            return;
        }

        var settings = AppSettingsStore.Current;
        VirusTotal.SetApiKey(key);
        settings.Style = Styles[StyleBox.SelectedIndex];
        settings.UnreadBold   = UnreadBoldBox.IsChecked == true;
        settings.UnreadItalic = UnreadItalicBox.IsChecked == true;
        settings.HiddenExtensions = string.Join(";", HiddenItems.Parse(HiddenExtensionsBox.Text));   // tidied: no dots or blanks
        settings.HiddenExtensionsInArchives = string.Join(";", HiddenItems.Parse(HiddenExtensionsInArchivesBox.Text));
        settings.HideShellExtensions = HideShellExtensionsBox.IsChecked == true;
        settings.HideKnownExtensions = HideKnownExtensionsBox.IsChecked == true;
        settings.UseRegisteredTypeNames = RegisteredTypeNamesBox.IsChecked == true;
        IconHelper.UseRegisteredTypeNames = settings.UseRegisteredTypeNames;   // forgets the type names found so far
        settings.SuggestMoveAfterSmartRename = SuggestMoveBox.IsChecked == true;
        settings.RightClickSelectsTreeNode = RightClickSelectsNodeBox.IsChecked == true;
        settings.DisableInternalDragDrop = DisableDragDropBox.IsChecked == true;
        settings.PreloadMediaPlayer      = PreloadPlayerBox.IsChecked == true;
        settings.WarnHiddenNotSelected   = WarnHiddenNotSelectedBox.IsChecked == true;
        settings.ShowOpenInTerminal      = TerminalBox.IsChecked == true;
        settings.ShowOpenInPowerShell    = PowerShellBox.IsChecked == true;
        settings.ShowOpenInCommandPrompt = CommandPromptBox.IsChecked == true;
        AppSettingsStore.Save(settings);

        ThemeService.Apply(settings.Style);
        UnreadStyle.Instance.Refresh();
        DialogResult = true;
    }
}
