using System.Windows;
using SwewolfFileExplorer.Helpers;
using SwewolfFileExplorer.Views;

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

        // What the command line names, in this order: a workspace / session file, a folder, a picture (shown first, alone), any other
        // file (its folder opens with the file selected). The sibling programs send a path, usually in quotes; quotes are not required.
        var target = StartupTarget.Parse(e.Args);
        StartupFile = target.TabSetFile;
        StartupFolder = target.Folder;
        StartupImage = target.Image;
        StartupSelectFile = target.SelectFile;

        // Started again while the program runs (Settings > General > "When the Program Is Started Again"): the first instance owns the
        // name. A later start hands what it was asked to do to that window and ends, unless the setting says otherwise or the running
        // window cannot take it now (a dialog is open, it is starting, it does not answer): then this is a normal new window.
        // (The splash screen is shown by WPF before this code runs, so a handed-over start flashes it briefly.)
        bool owner = SingleInstance.TryBecomeOwner();
        int startAgain = int.TryParse(Environment.GetEnvironmentVariable("SWEWOLF_START_AGAIN"), out var testMode)   // a test chooses without touching settings.json
            ? testMode
            : AppSettingsStore.Current.WhenStartedAgain;
        // A session file on the command line (the user's rules, 2026-11-14): if a window of this program has that very session on, that
        // window comes to the front, with no question and whatever the setting says (two windows must never record into one session
        // file); if none has it, the session opens in a new window (never in another window, which has its own tabs or its own session).
        // Any number of windows can be open: each window with a session on answers on a pipe named after its session file.
        bool sessionFileArg = false;
        if (target.TabSetFile != null && WorkspaceStore.TryGetKind(target.TabSetFile, out var kind) && kind == StoreKind.Session)
        {
            sessionFileArg = true;
            var found = SingleInstance.TryHandOverToSession(target.TabSetFile);
            if (found == SingleInstance.SessionHandOver.HandedOver)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown();
                return;
            }
            if (found == SingleInstance.SessionHandOver.Busy)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ThemeService.Apply(AppSettingsStore.Current.Style);
                MessageBox.Show($"The session \"{System.IO.Path.GetFileNameWithoutExtension(target.TabSetFile)}\" is already open in another window, which cannot come to the front right now (a dialog may be open there). Close the dialog and try again.",
                    AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            // NotRunning: this start is the new window that opens the session.
        }

        if (!owner && startAgain != SingleInstance.ModeNewWindow && !sessionFileArg)
        {
            // Asked once the running window is known to be ready; true = use it, false = open a new window, null = cancel.
            // A session that is on in the running window is always asked about (the user's rule, 2026-11-14: never go into a session
            // without asking, also when nothing is added to it): the session records every change to its tabs.
            // Without a session only mode Ask asks: always, with or without arguments (the user's wish 2026-11-14; first it was only without).
            bool askAnyway = startAgain == SingleInstance.ModeAsk;
            // The question belongs to a window that is not ours (this start has none): it must be on top of everything, in front, and active
            // (it could end up behind the windows that were open, found by the user 2026-11-14).
            static int AskOnTop(string message, string[] buttons)
            {
                var dialog = new ChoiceDialog(message, buttons, 0, buttons.Length - 1) { Topmost = true, ShowInTaskbar = true };
                dialog.Loaded += (_, _) => { dialog.Activate(); dialog.Focus(); };
                dialog.ShowDialog();
                return dialog.Result;
            }

            Func<string?, bool?> ask = session =>
            {
                if (session == null && !askAnyway) return true;

                ThemeService.Apply(AppSettingsStore.Current.Style);   // the question follows the style
                if (session != null)
                {
                    int sessionChoice = AskOnTop(
                        $"{AppInfo.Name} is already running, and the session \"{session}\" is on in that window.\nA session records every change to the tabs, so using that window changes the session.\n\nWhat do you want to do?",
                        new[] { "Open a New Window", "Use the Running Window", "Cancel" });
                    return sessionChoice switch { 0 => false, 1 => true, _ => null };
                }

                int choice = AskOnTop(
                    $"{AppInfo.Name} is already running.\n\nWhat do you want to do?",
                    new[] { "Use the Running Window", "Open a New Window", "Cancel" });
                return choice switch { 0 => true, 1 => false, _ => null };
            };

            // A question with no window of ours open must not end the program when it closes.
            var modeBefore = ShutdownMode;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = SingleInstance.TryHandOver(target, ask);
            if (result != SingleInstance.HandOverResult.NotHandedOver)
            {
                Shutdown();
                return;
            }
            ShutdownMode = modeBefore;

            // Said in the log (Help > Open Log File), so "it opened a new window" can be explained.
            if (result == SingleInstance.HandOverResult.NotHandedOver && SingleInstance.FailureReason != null)
                LogFiles.AppendError($"Started again: a new window was opened because {SingleInstance.FailureReason}.");
        }

        // For testing: SWEWOLF_CD_LOOKUP=0 switches the online lookup of audio CDs off (a disc then stays "not known").
        if (Environment.GetEnvironmentVariable("SWEWOLF_CD_LOOKUP") == "0") CdDiscInfo.Enabled = false;

        // The web bookmarks' icons moved from %LocalAppData% to %AppData% (2026-11-10): once, and the shortcuts that name them follow.
        SwewolfFileExplorer.Helpers.BookmarkStore.MigrateFavicons();

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

        // Later starts of the program may hand their work to this window (only the first instance answers).
        SingleInstance.StartServer(main, () => main.ActiveSessionName, main.HandleExternalStart);

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
