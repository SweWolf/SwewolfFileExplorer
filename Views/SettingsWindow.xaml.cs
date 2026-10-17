using System.Windows;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

public partial class SettingsWindow : Window
{
    private static readonly string[] Styles = { "Light", "Dark", "System", "Cyber" };

    public SettingsWindow()
    {
        InitializeComponent();
        var current = AppSettingsStore.Current;
        StyleBox.SelectedIndex = Math.Max(0, Array.IndexOf(Styles, current.Style));
        UnreadBoldBox.IsChecked   = current.UnreadBold;
        UnreadItalicBox.IsChecked = current.UnreadItalic;
        HiddenExtensionsBox.Text  = current.HiddenExtensions;
        HiddenExtensionsInArchivesBox.Text = current.HiddenExtensionsInArchives;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettingsStore.Current;
        settings.Style = Styles[StyleBox.SelectedIndex];
        settings.UnreadBold   = UnreadBoldBox.IsChecked == true;
        settings.UnreadItalic = UnreadItalicBox.IsChecked == true;
        settings.HiddenExtensions = string.Join(";", HiddenItems.Parse(HiddenExtensionsBox.Text));   // tidied: no dots or blanks
        settings.HiddenExtensionsInArchives = string.Join(";", HiddenItems.Parse(HiddenExtensionsInArchivesBox.Text));
        AppSettingsStore.Save(settings);

        ThemeService.Apply(settings.Style);
        UnreadStyle.Instance.Refresh();
        DialogResult = true;
    }
}
