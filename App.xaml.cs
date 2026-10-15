using System.Windows;
using SweWolfFileExplorer.Helpers;

namespace SweWolfFileExplorer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Before the main window is created (StartupUri), so it opens in the right colors.
        ThemeService.Initialize();
        ThemeService.Apply(AppSettingsStore.Current.Style);
        base.OnStartup(e);

        // Files unpacked from archives to open them elsewhere go to a temporary folder: remove what a crashed run left.
        Task.Run(ArchiveSupport.CleanUpStaleTemp);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ArchiveSupport.CleanUpTemp();
        base.OnExit(e);
    }
}
