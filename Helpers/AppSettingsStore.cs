using System.IO;
using System.Text.Json;

namespace MyFileExplorer.Helpers;

/// <summary>The few settings the user can change in the window, remembered between runs.</summary>
public class AppSettings
{
    /// <summary>Show the folders in the file list. Off by default: they are in the tree already.</summary>
    public bool ShowFolders { get; set; }
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
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

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
