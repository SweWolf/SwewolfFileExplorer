using System.Windows;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer;

public partial class App : Application
{
    /// <summary>A workspace or session file (.json) given on the command line; the main window opens it.</summary>
    public static string? StartupFile { get; private set; }

    /// <summary>A picture given on the command line: it is shown at once in the viewer, with no Explorer window until the user asks for it.</summary>
    public static string? StartupImage { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        foreach (var arg in e.Args)
        {
            try
            {
                var full = System.IO.Path.GetFullPath(arg.Trim('"'));
                if (full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(full))
                {
                    StartupFile = full;
                    break;
                }
                if (System.IO.File.Exists(full) && ThumbnailLoader.IsImage(full))
                {
                    StartupImage = full;
                    break;
                }
            }
            catch { }   // not a path: ignored
        }

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

        // The main window is made here (not by StartupUri): with a picture on the command line it is not shown at all.
        var main = new MainWindow();
        MainWindow = main;
        if (StartupImage != null)
        {
            // Only the viewer is on screen: closing it must not end the program by itself (the right-click menu's Close goes on to the Explorer).
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Started from the message loop once it is running (not from inside OnStartup).
            Dispatcher.BeginInvoke(new Action(() => main.RunPictureOnly(StartupImage)), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
            main.Show();

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
