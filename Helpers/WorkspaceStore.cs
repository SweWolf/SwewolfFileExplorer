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
            "SweWolfSoftware", AppInfo.FolderName, kind == StoreKind.Workspace ? "Workspaces" : "Sessions");
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

    // ── Paths relative to the volume ──────────────────────────────────────────
    // A file that lies outside the program's own two folders (moved to a USB drive with "Move to Volume...", or opened from
    // somewhere else) keeps the paths on its own drive without the drive letter: \Music\Suno. Read puts the letter of the drive the
    // file is on right now in front again, so the drive may be O: on one computer and R: on another. Paths on other drives, and
    // network paths, are kept as they are.

    /// <summary>The drive root ("M:\") of a path, or null for a network path or anything with no drive letter.</summary>
    public static string? DriveRootOf(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 3) return null;
        return char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')
            ? char.ToUpperInvariant(path[0]) + @":\"
            : null;
    }

    /// <summary>True when the file is in the program's own Workspaces or Sessions folder (those keep full paths).</summary>
    public static bool IsInAppFolders(string file)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(file))?.TrimEnd('\\');
            return string.Equals(dir, Folder(StoreKind.Workspace).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                || string.Equals(dir, Folder(StoreKind.Session).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>M:\Music\Suno gives \Music\Suno when <paramref name="volumeRoot"/> is M:\; any other path is returned as it is.</summary>
    public static string? ToVolumeRelative(string? path, string volumeRoot)
    {
        if (string.IsNullOrEmpty(path) || DriveRootOf(path) is not { } root
            || !string.Equals(root, volumeRoot, StringComparison.OrdinalIgnoreCase)) return path;
        return @"\" + path.Substring(3).Replace('/', '\\');
    }

    /// <summary>\Music\Suno gives M:\Music\Suno for the root M:\; a path with a drive letter or a network path is returned as it is.</summary>
    public static string? FromVolumeRelative(string? path, string volumeRoot)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '\\' || path.StartsWith(@"\\")) return path;
        return volumeRoot.TrimEnd('\\') + path;
    }

    /// <summary>How many tabs are on the drive of <paramref name="volumeRoot"/>, and how many are on other drives or on the network (a tab with no folder, This PC, counts for neither).</summary>
    public static (int OnVolume, int Elsewhere) CountTabs(TabSetFile data, string volumeRoot)
    {
        int on = 0, elsewhere = 0;
        foreach (var tab in data.Tabs)
        {
            if (string.IsNullOrWhiteSpace(tab.Path)) continue;
            var root = DriveRootOf(tab.Path);
            if (root != null && string.Equals(root, volumeRoot, StringComparison.OrdinalIgnoreCase)) on++;
            else elsewhere++;
        }
        return (on, elsewhere);
    }

    /// <summary>The drive root where most of the tabs are (the first one when it is a tie), or null if none is on a drive with a letter.</summary>
    public static string? MostUsedDrive(TabSetFile data)
    {
        return data.Tabs
            .Select(t => DriveRootOf(t.Path))
            .Where(r => r != null)
            .GroupBy(r => r!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
    }

    /// <summary>
    /// Writes the file the safe way: to a temporary file next to it first, then swapped in, so a power failure in the middle
    /// leaves the old file whole. Returns false if it could not be written (full disk, drive gone ...).
    /// A file outside the program's own folders gets the paths on its own drive without the drive letter (see above).
    /// </summary>
    public static bool Write(string file, StoreKind kind, TabSetFile data)
    {
        try
        {
            data.Format = FormatOf(kind);
            if (kind == StoreKind.Workspace)
                foreach (var tab in data.Tabs) tab.SelectedFile = null;   // a workspace is folders only

            var toWrite = data;
            var volume = IsInAppFolders(file) ? null : DriveRootOf(Path.GetFullPath(file));
            if (volume != null)
            {
                // A copy, so what the caller holds keeps its full paths.
                toWrite = new TabSetFile
                {
                    Format = data.Format, Version = data.Version, ActiveTab = data.ActiveTab,
                    Tabs = data.Tabs.Select(t => new TabState
                    {
                        Path = ToVolumeRelative(t.Path, volume) ?? "",
                        Root = ToVolumeRelative(t.Root, volume),
                        Name = t.Name,
                        SelectedFile = ToVolumeRelative(t.SelectedFile, volume),
                    }).ToList(),
                };
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(toWrite, Options));
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

            // Paths with no drive letter (\Music\Suno) belong to the drive the file is on right now.
            if (DriveRootOf(Path.GetFullPath(file)) is { } volume)
                foreach (var tab in data.Tabs)
                {
                    tab.Path         = FromVolumeRelative(tab.Path, volume) ?? "";
                    tab.Root         = FromVolumeRelative(tab.Root, volume);
                    tab.SelectedFile = FromVolumeRelative(tab.SelectedFile, volume);
                }
            return data;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = ex is JsonException ? "The file is damaged or is not a workspace or session file." : ex.Message;
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="file"/> is a workspace or session file of this program, wherever it lies (a .json file that is not
    /// too big, whose Format says so and that has tabs in it). Used to give such a file its own questions in the list.
    /// </summary>
    public static bool TryGetKind(string file, out StoreKind kind)
    {
        kind = StoreKind.Workspace;
        try
        {
            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
            if (new FileInfo(file).Length > 2_000_000) return false;   // a real one is a few lines
        }
        catch { return false; }
        return Read(file, out kind, out _) != null;
    }

    /// <summary>Renames a saved one (never replaces another). Returns false if that did not work.</summary>
    public static bool Rename(StoreKind kind, string name, string newName)
    {
        try
        {
            var from = PathFor(kind, name);
            var to   = PathFor(kind, newName);
            // A change of case only is the same file to Windows: allowed. Anything else that exists already is refused.
            if (File.Exists(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return false;
            File.Move(from, to);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Deletes a saved one. Returns false if that did not work.</summary>
    public static bool Delete(StoreKind kind, string name)
    {
        try { File.Delete(PathFor(kind, name)); return true; }
        catch { return false; }
    }
}
