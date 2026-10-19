using System.IO;
using System.Text.Json;

namespace SwewolfFileExplorer.Helpers;

/// <summary>The few settings the user can change in the window, remembered between runs.</summary>
public class AppSettings
{
    /// <summary>Show the folders in the file list. Off by default: they are in the tree already.</summary>
    public bool ShowFolders { get; set; }

    public const double DefaultFontSize = 13;
    public const double MinFontSize = 8;
    public const double MaxFontSize = 40;

    /// <summary>Font size of the file list and of the tree: set separately with Ctrl + mouse wheel.</summary>
    public double ListFontSize { get; set; } = DefaultFontSize;
    public double TreeFontSize { get; set; } = DefaultFontSize;

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

    /// <summary>Where SweWolf Media Player is, when it is not next to this EXE or in ..\SwewolfMediaPlayer (asked once, then remembered).</summary>
    public string MediaPlayerPath { get; set; } = "";

    /// <summary>Files with these extensions count as hidden (shown only with "click to show"), like the hidden attribute does.</summary>
    public string HiddenExtensions { get; set; } = HiddenItems.DefaultExtensions;

    /// <summary>Extensions hidden only inside compressed files, on top of <see cref="HiddenExtensions"/> (none to begin with).</summary>
    public string HiddenExtensionsInArchives { get; set; } = "";

    /// <summary>Do not show the extension of special shell and system file types (.lnk, .url ...), as Windows does: "Photos", not "Photos.lnk".</summary>
    public bool HideShellExtensions { get; set; } = true;

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

/// <summary>Saves <see cref="AppSettings"/> in %AppData%\SweWolfSoftware\SweWolf File Explorer\settings.json.</summary>
public static class AppSettingsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SweWolfSoftware", AppInfo.Name, "settings.json");

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
