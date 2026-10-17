using System.Windows;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // For testing a copy of the app without touching the real library file.
        var testDb = Environment.GetEnvironmentVariable("SWEWOLF_LIBRARY_TEST_DB");
        if (!string.IsNullOrEmpty(testDb)) LibraryService.MainDbFileOverride = testDb;

        // A damaged library file is replaced by LibraryStore (the newest good backup, or a new file): say so.
        LibraryStore.Repaired += (movedTo, whatNow) => Dispatcher.BeginInvoke(() =>
            MessageBox.Show(
                $"The file that remembers which files you have opened was damaged. It was moved to:\n{movedTo}\n\n{whatNow}",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning));

        // One backup of the main library file a day (the first start of the day), the last 7 are kept.
        Task.Run(() => LibraryStore.BackUpMainFile());

        // Before the main window is created (StartupUri), so it opens in the right colors.
        ThemeService.Initialize();
        ThemeService.Apply(AppSettingsStore.Current.Style);
        base.OnStartup(e);

        // Files unpacked from archives to open them elsewhere go to a temporary folder: remove what a crashed run left.
        Task.Run(ArchiveSupport.CleanUpStaleTemp);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LibraryService.Flush();   // the last files opened are written
        ArchiveSupport.CleanUpTemp();
        base.OnExit(e);
    }
}
