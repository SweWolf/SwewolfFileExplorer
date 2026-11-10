using System.Windows;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer;

public partial class App : Application
{
    /// <summary>A workspace or session file (.json) given on the command line; the main window opens it.</summary>
    public static string? StartupFile { get; private set; }

    /// <summary>A picture given on the command line: it is shown at once in the viewer, with no Explorer window until the user asks for it.</summary>
    public static string? StartupImage { get; private set; }

    /// <summary>A folder given on the command line: the Explorer opens it as the root of its tree.</summary>
    public static string? StartupFolder { get; private set; }

    /// <summary>Any other file on the command line (a video, a song ...): the Explorer goes to its folder and selects it. Nothing is played or opened.</summary>
    public static string? StartupSelectFile { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // The data folders of the first versions had spaces in their name: moved to the new name before anything is read.
        SwewolfFileExplorer.Helpers.AppInfo.MigrateFolders();

        // The web bookmarks' icons moved from %LocalAppData% to %AppData% (2026-11-10): once, and the shortcuts that name them follow.
        SwewolfFileExplorer.Helpers.BookmarkStore.MigrateFavicons();

        // What the command line names, in this order: a workspace / session file, a folder, a picture (shown first, alone), any other
        // file (its folder opens with the file selected). The sibling programs send a path, usually in quotes; quotes are not required:
        // a path with spaces that was not quoted arrives as several arguments, so all of them together are tried as one path last.
        bool TryArg(string arg)
        {
            try
            {
                var full = System.IO.Path.GetFullPath(arg.Trim().Trim('"'));
                if (full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(full))
                {
                    StartupFile = full;
                    return true;
                }
                if (System.IO.Directory.Exists(full))
                {
                    StartupFolder = full;
                    return true;
                }
                if (System.IO.File.Exists(full))
                {
                    if (ThumbnailLoader.IsImage(full)) StartupImage = full;
                    else StartupSelectFile = full;
                    return true;
                }
            }
            catch { }   // not a path: ignored
            return false;
        }

        if (!e.Args.Any(TryArg) && e.Args.Length > 1)
            TryArg(string.Join(" ", e.Args));

        // An unexpected error must leave a trace (Help > Open Log File) instead of the program just disappearing.
        DispatcherUnhandledException += (_, args) =>
        {
            LogFiles.AppendError($"Unexpected error: {args.Exception}");
            args.Handled = true;

            // An error while the program is closing (found 2026-10-28 on the work laptop: WPF's own telemetry could not
            // load System.Diagnostics.Tracing in CriticalShutdown) must never show a box: the window is gone, the box
            // came back after every OK and the program could only be ended with Ctrl+Alt+Delete. Log once, then leave.
            if (args.Exception.ToString().Contains("Application.CriticalShutdown", StringComparison.Ordinal))
            {
                try { LibraryService.Flush(); ArchiveSupport.CleanUpTemp(); } catch { }
                Environment.Exit(0);
            }
            MessageBox.Show($"An unexpected error occurred: {args.Exception.Message}\n\nThe details were saved in the log file (Help > Open Log File).",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogFiles.AppendError($"Fatal error: {args.ExceptionObject}");

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
        IconHelper.UseRegisteredTypeNames = AppSettingsStore.Current.UseRegisteredTypeNames;
        FileTypeOverrides.SetBuiltIn(AppSettingsStore.Current.UseOwnFileTypeNames, AppSettingsStore.Current.UseOwnFileTypeIcons);
        MenuSpacing.Install();   // the right-click menus a little lower per item
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
