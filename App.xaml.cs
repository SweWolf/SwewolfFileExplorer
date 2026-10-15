using System.Windows;
using MyFileExplorer.Helpers;

namespace MyFileExplorer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Before the main window is created (StartupUri), so it opens in the right colors.
        ThemeService.Initialize();
        ThemeService.Apply(AppSettingsStore.Current.Style);
        base.OnStartup(e);
    }
}
