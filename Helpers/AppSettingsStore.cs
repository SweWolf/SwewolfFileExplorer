using System.IO;
using System.Text.Json;

namespace SweWolfFileExplorer.Helpers;

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

    /// <summary>The window color scheme: "Light", "Dark", "System" (follow Windows) or "Cyber" (light blue on black; working name).</summary>
    public string Style { get; set; } = "System";
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
            if (settings.Style is not ("Light" or "Dark" or "System" or "Cyber")) settings.Style = "System";
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
