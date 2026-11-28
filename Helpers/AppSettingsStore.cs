using System.IO;
using System.Text.Json;

namespace SwewolfFileExplorer.Helpers;

/// <summary>The few settings the user can change in the window, remembered between runs.</summary>
public class AppSettings
{
    /// <summary>Show the folders in the file list. Off by default: they are in the tree already.</summary>
    public bool ShowFolders { get; set; }

    public const double DefaultFontSize = 14;
    public const double MinFontSize = 8;
    public const double MaxFontSize = 40;

    /// <summary>Font size of the file list and of the tree: set separately with Ctrl + mouse wheel.</summary>
    public double ListFontSize { get; set; } = DefaultFontSize;
    public double TreeFontSize { get; set; } = DefaultFontSize;

    /// <summary>The file list shows icons (thumbnails) instead of the Details columns: the toolbar's Icons button.</summary>
    public bool IconView { get; set; }

    public const int DefaultIconViewSize = 96;
    public const int MinIconViewSize = 48;
    public const int MaxIconViewSize = 256;

    /// <summary>The size of a thumbnail in the Icons view (device-independent pixels): Ctrl + mouse wheel or Ctrl + plus / minus.</summary>
    public int IconViewSize { get; set; } = DefaultIconViewSize;

    /// <summary>Font of the file list, the drives list and the tree (Tools > Settings > Font). Empty = the program's usual font.</summary>
    public string FontFamily { get; set; } = "";

    /// <summary>
    /// The columns of the list are sized to their widest text after the list is filled, when the font size changes and when a column
    /// is shown (the user: always on for now; a setting may come: settings.json only, no window yet).
    /// </summary>
    public bool AutoFitColumns { get; set; } = true;

    /// <summary>The folder shown is watched and the list follows changes made by other programs (settings.json only, no window yet).</summary>
    public bool WatchFolders { get; set; } = true;

    /// <summary>
    /// The Type column says what the file type itself is called ("JPEG Image"), not what Windows makes of it for the default app of the
    /// user ("JPG File" when that app names no type). Off = as Windows Explorer shows it.
    /// </summary>
    public bool UseRegisteredTypeNames { get; set; }

    /// <summary>The program's own list of file type names and icons (embedded in the EXE) is used. Off = only the user's FileTypes.json and Windows.</summary>
    public bool UseOwnFileTypeNames { get; set; } = true;

    /// <summary>The Title column of a video takes the title from its .nfo file when it has one (else the title inside the video file). On to begin with.</summary>
    public bool TitleFromNfo { get; set; } = true;
    public bool UseOwnFileTypeIcons { get; set; } = true;

    /// <summary>The picture of the current folder is shown under the tree (the "x" on it hides it; a button in the status line brings it back).</summary>
    public bool ShowFolderPicture { get; set; } = true;

    /// <summary>The panel under the info line (the details, the preview picture, the plot ...) is shown; the "x" at its far right hides it, a small button at the right end of the info line brings it back.</summary>
    public bool ShowInfoPanel { get; set; } = true;

    /// <summary>The height of the info panel under the file list (the preview picture, the plot): dragged with the divider above it.</summary>
    public const double DefaultInfoPanelHeight = 141;
    public double InfoPanelHeight { get; set; } = DefaultInfoPanelHeight;

    /// <summary>
    /// The text editor that opens .nfo files (text and XML ones; settings.json only, no window for it). Empty = automatic: Visual
    /// Studio Code if the registry knows it, else Notepad.
    /// </summary>
    public string TextEditorPath { get; set; } = "";

    /// <summary>The window color scheme: "Light", "Dark", "System" (follow Windows) or "Cyber Cyan" (light blue on black).</summary>
    public string Style { get; set; } = "System";

    /// <summary>How an unread file shows in the list: bold and/or italic (as in TeraTool). Both off = no difference.</summary>
    public bool UnreadBold { get; set; } = true;
    public bool UnreadItalic { get; set; }

    /// <summary>Files with these extensions count as hidden (shown only with "click to show"), like the hidden attribute does.</summary>
    public string HiddenExtensions { get; set; } = HiddenItems.DefaultExtensions;

    /// <summary>Extensions hidden only inside compressed files, on top of <see cref="HiddenExtensions"/> (none to begin with).</summary>
    public string HiddenExtensionsInArchives { get; set; } = "";

    /// <summary>Do not show the extension of special shell and system file types (.lnk, .url ...), as Windows does: "Photos", not "Photos.lnk".</summary>
    public bool HideShellExtensions { get; set; } = true;

    /// <summary>
    /// Do not show the extension of known file types (.txt, .jpg ...), as Windows' Folder Options > "Hide extensions for known file types". Off to
    /// begin with: the extensions are shown (the user's wish; Windows hides them by default). The ".ext" button in the toolbar and Settings > File Names.
    /// </summary>
    public bool HideKnownExtensions { get; set; }

    /// <summary>
    /// The folder tree always shows the extension of the files it lists (packed files such as .zip, and .m3u playlists), as before. On by
    /// default. Off, the tree follows the same hiding rules as the list (the special shell types, and the known types when
    /// <see cref="HideKnownExtensions"/> is on), except that a node keeps its extension when another node would get the same name.
    /// </summary>
    public bool AlwaysShowExtensionsInTree { get; set; } = true;

    // The Play bar (slideshow). TeraTool's defaults: every file 10 seconds, at the end go to the next folder, ask first with a 10 second time-out.
    /// <summary>0 = Slide Show / Continues Playing, 1 = Single Play (a picture opens and stays).</summary>
    public int PlayMode { get; set; }
    public int PlaySeconds { get; set; } = 10;
    /// <summary>What a slideshow does after the last picture: <see cref="EndOfListAction"/> (0 next folder, 1 start over, 2 close).</summary>
    public int PlayEndAction { get; set; }
    public bool PlayAskFirst { get; set; } = true;
    public int PlayTimeout { get; set; } = 10;

    /// <summary>
    /// A right-click on a folder of the tree selects it first (and so opens it in the list), as a right-click on a file does in the list
    /// (Settings > Folder Tree). Off: the selection stays where it is and the folder under the mouse gets an outline while its menu is open,
    /// as in Windows Explorer.
    /// </summary>
    public bool RightClickSelectsTreeNode { get; set; } = true;

    /// <summary>Tools > Settings > Folder Tree > "Start with the Full Folder Tree" (on by default): how the switch above the tree starts. Off: it starts in "hide parents" mode.</summary>
    public bool StartWithFullTree { get; set; } = true;

    /// <summary>Tools > Settings > General > Address Bar (on by default): the Tab key in the address bar suggests a folder, as in the Command Prompt. Off: Tab moves to the next control.</summary>
    public bool TabSuggestsFolders { get; set; } = true;

    /// <summary>
    /// Settings > Drag and Drop > "Disable Internal Drag and Drop" (off by default). On: files dropped on the list or the tree are refused
    /// (copy / move by drag, with the left or the right button) and dragging with the right button does not start. A picture dragged in from
    /// a web browser and files dragged out to another program with the left button always work.
    /// </summary>
    public bool DisableInternalDragDrop { get; set; } = false;

    /// <summary>Settings > General > Progress Bars > "Rounded Progress Bars" (off by default = square): the look of the bar in the copy / move /
    /// delete progress window. Frame and fill always have the same corners.</summary>
    public bool RoundedProgressBars { get; set; } = false;

    /// <summary>Settings > Media Player > "Preload SweWolf Media Player in the Background at Startup" (on by default; the box is only shown when the
    /// player is found). Once per run the player is started with --warm-up so its first real start is fast.</summary>
    public bool PreloadMediaPlayer { get; set; } = true;

    /// <summary>Settings > General > Updates > "Check for a New Version at Startup" (on by default). A few seconds after the window is shown
    /// GitHub Releases is asked once; a newer version shows a link at the right end of the menu bar (MainWindow.CheckForUpdateAsync).</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>Settings > General > "When the Program Is Started Again": what a new start does while the program runs already
    /// (<c>SingleInstance</c>): 0 = start a new window, 1 = use the running window (the default, the user's choice 2026-11-14; a folder or
    /// file named on the command line opens there), 2 = ask (always, also when the start names a folder or file).
    /// A running window that is busy (a dialog is open, it does not answer) always means a new window.</summary>
    public int WhenStartedAgain { get; set; } = 1;

    /// <summary>The user's own VirusTotal API key, encrypted with Windows DPAPI for this Windows user and written as base64
    /// (Settings > VirusTotal; <c>VirusTotal.GetApiKey / SetApiKey</c>). Empty = no key: "Send to VirusTotal" only opens the page.</summary>
    public string VirusTotalApiKeyProtected { get; set; } = "";

    /// <summary>The user's own The Movie Database (TMDB) API key or read access token, encrypted like the VirusTotal key
    /// (Settings > Scrapers; <c>Tmdb.GetApiKey / SetApiKey</c>). Empty = "Download NFO File..." asks for it.</summary>
    public string TmdbApiKeyProtected { get; set; } = "";

    /// <summary>Settings > Troubleshooting > "Log All Internet Calls" (off by default): every call to a web service is written to
    /// internet.log (<c>LogFiles.AppendInternet</c>). Errors always go to errors.log, whatever this says.</summary>
    public bool LogInternetCalls { get; set; } = false;

    /// <summary>Settings > Menus: the items of the list's right-click menu that are turned off (<see cref="MenuChoices"/> keys joined with ";"). Empty = everything is shown.</summary>
    public string HiddenListMenuItems { get; set; } = "";

    /// <summary>The same for the right-click menu of the folder tree.</summary>
    public string HiddenTreeMenuItems { get; set; } = "";

    /// <summary>Settings > Troubleshooting > "Log Warnings" (off by default): also write the "Warning:" lines (things that needed a second try, such as
    /// asking for administrator permission) to errors.log. Errors are always written.</summary>
    public bool LogWarnings { get; set; } = false;

    /// <summary>Settings > Troubleshooting > Log Searches: search.log lists every folder of the last search with the time it took (off by default).</summary>
    public bool LogSearches { get; set; } = false;

    /// <summary>Settings > Search: the standard folders to leave out of searches, one per line: a full path (that folder and all below it) or a plain name (any folder with that name).</summary>
    public string SearchExcludedFolders { get; set; } = "";

    /// <summary>The check box "Exclude Folders from the Search" of the Search window: remembered from one search to the next.</summary>
    public bool SearchSkipExcluded { get; set; } = false;

    /// <summary>The check box "Keep This Window Open After Opening a Folder" of the Find Folder window (off by default: the window closes when a folder is opened).</summary>
    public bool FindFolderKeepOpen { get; set; } = false;

    /// <summary>The order of the columns of the Find Folder grid, as column numbers separated by commas (0 Name, 1 Location, 2 Date Created, 3 Date Modified); empty = the default order.</summary>
    public string FindFolderColumns { get; set; } = "";

    /// <summary>Settings > Files > Hidden Files > "Warn When All Files Are Selected but Hidden Items Are Not" (on by default): a red warning in the
    /// info line while everything shown is selected and the folder has hidden items that are not shown (they are not copied or moved with the selection).</summary>
    public bool WarnHiddenNotSelected { get; set; } = true;


    /// <summary>Settings > Security > "Show a Message Box Before Windows Asks for Administrator Permission" (off by default, the user's choice 2026-11-16): on shows
    /// the question "You need administrator permission to ... Do you want to continue?" before Windows' own UAC prompt; off goes straight to Windows' prompt.</summary>
    public bool AskBeforeAdministrator { get; set; } = false;

    /// <summary>Settings > Security > "Ask Before Starting a Program" (on by default: the program asks until the user says it should not).
    /// Not used yet: the question itself is not built (see the plan in dev-notes).</summary>
    public bool AskBeforeStartingPrograms { get; set; } = true;

    /// <summary>Settings > Security > "Ask Before Starting a Program Inside a Compressed File" (on by default): its own setting, apart from the one above.</summary>
    public bool AskBeforeStartingProgramsInArchives { get; set; } = true;

    /// <summary>Settings > Security: the extensions that count as programs for that question, separated by semicolons.</summary>
    public string ProgramExtensions
    {
        get => _programExtensions;
        // A list saved by the first version (exactly its default, in any order) gets the new default, which also has JavaScript, VBScript's .vbe and
        // .wsh; a list the user changed is kept as it is.
        set
        {
            var parsed = HiddenItems.Parse(value ?? "");
            _programExtensions = parsed.SetEquals(HiddenItems.Parse(ProgramTypes.FirstDefaultExtensions)) || parsed.SetEquals(HiddenItems.Parse(ProgramTypes.SecondDefaultExtensions))
                ? ProgramTypes.DefaultExtensions : value ?? "";
        }
    }
    private string _programExtensions = ProgramTypes.DefaultExtensions;

    /// <summary>The user's own list of folders in the "Move/Copy to Folder" window (right-click menus), in the order they chose.</summary>
    public List<string> MoveCopyFolders { get; set; } = new();

    /// <summary>The checkbox "Open This Folder in a New Tab When Finished" of the "Move/Copy to Folder" window: kept from one time to the next (off at first).</summary>
    public bool MoveCopyOpenInNewTab { get; set; } = false;

    /// <summary>The "Advanced" checkbox of the Workspace Manager window (more columns, Open Folder): kept from one time to the next (off at first).</summary>
    public bool WorkspaceManagerAdvanced { get; set; } = false;

    /// <summary>The "Advanced" checkbox of the Session Manager window: kept from one time to the next (off at first).</summary>
    public bool SessionManagerAdvanced { get; set; } = false;

    /// <summary>The Rip Audio CD window (Rip button in the toolbar): the file type ("mp3", "flac", "m4a"), the quality (index in that type's list), whether to verify, and the output folder; kept from one time to the next.</summary>
    public string RipFormat { get; set; } = "mp3";
    public int RipQuality { get; set; } = 0;
    public bool RipVerify { get; set; } = true;
    public string RipFolder { get; set; } = "";

    /// <summary>Show "Open in Terminal" (Windows Terminal) in the right-click menus of folders (Settings > Terminal Menu).</summary>
    public bool ShowOpenInTerminal { get; set; } = true;

    /// <summary>Show "Open in PowerShell" in the right-click menus of folders.</summary>
    public bool ShowOpenInPowerShell { get; set; } = true;

    /// <summary>Show "Open in Command Prompt" in the right-click menus of folders.</summary>
    public bool ShowOpenInCommandPrompt { get; set; } = true;

    /// <summary>
    /// After a video file is renamed with "Use Smart Name", offer to move it into Movies or TV Shows\Show Name in the
    /// user's Videos folder (TeraTool did). Off to begin with (Settings > File Names).
    /// </summary>
    public bool SuggestMoveAfterSmartRename { get; set; } = false;

    /// <summary>The poster named in a video's .nfo file may be downloaded for the preview picture (no window for it: settings.json only).</summary>
    public bool DownloadPosterFromNfo { get; set; } = true;

    /// <summary>
    /// What is left out of the Windows menu ("More Options"): verbs or texts of its lines, separated by semicolons. The app has its own
    /// Cut, Copy, Delete, Rename, Properties and Copy as Path (they keep the library right), and no Favorites. Edit the file to show more.
    /// </summary>
    public string HiddenShellMenuItems { get; set; } = "cut;copy;delete;rename;properties;open;copyaspath;paste;undo;Add to Favorites";
}

/// <summary>Saves <see cref="AppSettings"/> in %AppData%\SweWolfSoftware\SwewolfFileExplorer\settings.json.</summary>
public static class AppSettingsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SweWolfSoftware", AppInfo.FolderName, "settings.json");

    private static AppSettings? _current;

    /// <summary>
    /// The one settings object of the app, loaded on first use. Change it and call <see cref="Save"/>:
    /// a second copy loaded from the file would overwrite the changes of the first.
    /// </summary>
    public static AppSettings Current => _current ??= Load();

    private static AppSettings Load()
    {
        try
        {
            var settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();

            // A hand-edited file could hold any number.
            settings.ListFontSize = ClampFontSize(settings.ListFontSize);
            settings.TreeFontSize = ClampFontSize(settings.TreeFontSize);
            if (settings.Style == "Cyber") settings.Style = "Cyber Cyan";   // its name until 2026-10-18
            if (settings.Style is not ("Light" or "Dark" or "System" or "Cyber Cyan")) settings.Style = "System";
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static double ClampFontSize(double size) =>
        double.IsNaN(size) ? AppSettings.DefaultFontSize
                           : Math.Clamp(size, AppSettings.MinFontSize, AppSettings.MaxFontSize);

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // Not being able to remember a setting is not worth an error box.
        }
    }
}
