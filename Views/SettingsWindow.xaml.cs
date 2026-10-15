using System.Windows;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

public partial class SettingsWindow : Window
{
    private static readonly string[] Styles = { "Light", "Dark", "System", "Cyber" };

    public SettingsWindow()
    {
        InitializeComponent();
        StyleBox.SelectedIndex = Math.Max(0, Array.IndexOf(Styles, AppSettingsStore.Current.Style));
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettingsStore.Current;
        settings.Style = Styles[StyleBox.SelectedIndex];
        AppSettingsStore.Save(settings);

        ThemeService.Apply(settings.Style);
        DialogResult = true;
    }
}
