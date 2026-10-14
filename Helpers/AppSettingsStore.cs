using System.IO;
using System.Text.Json;

namespace MyFileExplorer.Helpers;

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
}

/// <summary>Saves <see cref="AppSettings"/> in %AppData%\SweWolfSoftware\MyFileExplorer\settings.json.</summary>
public static class AppSettingsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SweWolfSoftware", "MyFileExplorer", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();

            // A hand-edited file could hold any number.
            settings.ListFontSize = ClampFontSize(settings.ListFontSize);
            settings.TreeFontSize = ClampFontSize(settings.TreeFontSize);
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
