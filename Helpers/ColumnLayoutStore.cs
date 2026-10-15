using System.IO;
using System.Text.Json;

namespace SweWolfFileExplorer.Helpers;

/// <summary>One column of the file list as remembered between runs: which one, shown or not, how wide.</summary>
public class ColumnState
{
    public string Key { get; set; } = "";
    public bool Visible { get; set; } = true;
    public double Width { get; set; }
}

/// <summary>Saves the column layout (order, visibility, width) in %AppData%\SweWolfSoftware\SweWolf File Explorer.</summary>
public static class ColumnLayoutStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SweWolfSoftware", AppInfo.Name, "columns.json");

    /// <summary>The saved layout, or null if there is none (first run) or it cannot be read.</summary>
    public static List<ColumnState>? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<ColumnState>>(File.ReadAllText(FilePath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(List<ColumnState> layout)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(layout));
        }
        catch
        {
            // Not being able to remember the layout is not worth an error box.
        }
    }
}
