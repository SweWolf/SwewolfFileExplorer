using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwewolfFileExplorer.Helpers;

/// <summary>A workspace is a set of tabs the user saved by hand; a session is the open tabs, remembered all the time.</summary>
public enum StoreKind { Workspace, Session }

/// <summary>One tab as it is saved.</summary>
public sealed class TabState
{
    /// <summary>The folder (or packed file, or folder in one) the tab shows. Kept even if it is not there now (a USB drive that is not connected).</summary>
    public string Path { get; set; } = "";

    /// <summary>The root of the tab's tree, if it has one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Root { get; set; }

    /// <summary>The name the user gave the tab (Rename Tab); null for the folder's name. Older files and older versions of the program have none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>The file that was selected (sessions only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedFile { get; set; }
}

/// <summary>The content of a workspace file or a session file.</summary>
public sealed class TabSetFile
{
    /// <summary>
    /// Says what the file is, so the app knows it wherever the file lies (it can be moved, or given on the command line):
    /// <see cref="WorkspaceStore.WorkspaceFormat"/> or <see cref="WorkspaceStore.SessionFormat"/>.
    /// </summary>
    public string Format { get; set; } = "";

    public int Version { get; set; } = 1;

    public List<TabState> Tabs { get; set; } = new();

    /// <summary>The number (from 0) of the tab that was active.</summary>
    public int ActiveTab { get; set; }
}

/// <summary>
/// Workspaces and sessions: one JSON file each, named as the user named it, in two folders next to settings.json:
/// <c>Workspaces</c> and <c>Sessions</c>. The files can be copied to another computer. Nothing in them is ever removed
/// because it does not exist at the moment.
/// </summary>
public static class WorkspaceStore
{
    public const string WorkspaceFormat = "SweWolfFileExplorer.Workspace";
    public const string SessionFormat   = "SweWolfFileExplorer.Session";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,   // reading: a file edited by hand
    };

    private static string FormatOf(StoreKind kind) => kind == StoreKind.Workspace ? WorkspaceFormat : SessionFormat;

    /// <summary>The folder of the files of this kind (made if it is not there).</summary>
    public static string Folder(StoreKind kind)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SweWolfSoftware", AppInfo.Name, kind == StoreKind.Workspace ? "Workspaces" : "Sessions");
        try { Directory.CreateDirectory(folder); } catch { }
        return folder;
    }

    public static string PathFor(StoreKind kind, string name) => Path.Combine(Folder(kind), name + ".json");

    /// <summary>The names (file names without .json) of the saved ones, in alphabetical order.</summary>
    public static List<string> Names(StoreKind kind)
    {
        try
        {
            return Directory.EnumerateFiles(Folder(kind), "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>What is wrong with a name the user typed (it becomes a file name), or null if it is fine.</summary>
    public static string? CheckName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return "Please enter a name.";

        var bad = name.Where(c => Path.GetInvalidFileNameChars().Contains(c)).Distinct().ToArray();
        if (bad.Length > 0)
            return $"The name cannot contain these characters: {string.Join(" ", bad.Where(c => !char.IsControl(c)))}";

        if (name.EndsWith('.')) return "The name cannot end with a period.";
        return null;
    }

    /// <summary>
    /// Writes the file the safe way: to a temporary file next to it first, then swapped in, so a power failure in the middle
    /// leaves the old file whole. Returns false if it could not be written (full disk, drive gone ...).
    /// </summary>
    public static bool Write(string file, StoreKind kind, TabSetFile data)
    {
        try
        {
            data.Format = FormatOf(kind);
            if (kind == StoreKind.Workspace)
                foreach (var tab in data.Tabs) tab.SelectedFile = null;   // a workspace is folders only

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, Options));
            File.Move(temp, file, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a file and says what it is. Null if it is no workspace or session file of this app (or it is damaged);
    /// <paramref name="error"/> then says why.
    /// </summary>
    public static TabSetFile? Read(string file, out StoreKind kind, out string? error)
    {
        kind  = StoreKind.Workspace;
        error = null;

        try
        {
            var data = JsonSerializer.Deserialize<TabSetFile>(File.ReadAllText(file), Options);
            if (data == null) { error = "The file is empty."; return null; }

            if (data.Format == WorkspaceFormat)    kind = StoreKind.Workspace;
            else if (data.Format == SessionFormat) kind = StoreKind.Session;
            else { error = "The file is not a workspace or a session of this program."; return null; }

            data.Tabs ??= new List<TabState>();
            data.Tabs.RemoveAll(t => t == null || string.IsNullOrWhiteSpace(t.Path));
            if (data.Tabs.Count == 0) { error = "The file has no tabs in it."; return null; }

            data.ActiveTab = Math.Clamp(data.ActiveTab, 0, data.Tabs.Count - 1);
            return data;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = ex is JsonException ? "The file is damaged or is not a workspace or session file." : ex.Message;
            return null;
        }
    }

    /// <summary>Deletes a saved one. Returns false if that did not work.</summary>
    public static bool Delete(StoreKind kind, string name)
    {
        try { File.Delete(PathFor(kind, name)); return true; }
        catch { return false; }
    }
}
