using System.IO;
using System.Text.Json;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The descriptions and icons of file types: the program's own list (embedded, Resourses\FileTypes.default.json) and the user's own
/// %AppData%\SweWolfSoftware\SwewolfFileExplorer\FileTypes.json, which wins where it names an extension.
/// Only a lookup: when the file or the extension is not there, nothing is overridden and the Type column
/// works exactly as before (Windows' name, or the registered name when that setting is on).
///
/// The file is a JSON object whose keys are extensions (with or without the dot, any case):
///   { "jpg": { "description": "JPEG Image" }, ".mkv": { "description": "Matroska Video" } }
/// (comments and trailing commas are allowed). "icon" is optional too: a picture file (.ico, .png ...), "file.dll,index", or "builtin:name" (an icon embedded in the program).
/// The file is read once per run (and again when <see cref="Reload"/> is called).
/// </summary>
internal static class FileTypeOverrides
{
    /// <summary>"builtin:name" in an icon means the icon Resourses\FileTypeIcons\name.ico that is embedded in the program.</summary>
    public const string BuiltInPrefix = "builtin:";

    /// <summary>The program's own list, embedded in the EXE (Resourses\FileTypes.default.json).</summary>
    private const string BuiltInResource = "FileTypes.default.json";

    private static bool _useBuiltIn = true;

    /// <summary>
    /// Settings > "Use SweWolf File Explorer's Own Definition of File Type Names and Icons": the embedded list is used (on by default).
    /// Off, only the user's own FileTypes.json counts. Changing it forgets the lists and the icons and names found so far.
    /// </summary>
    public static bool UseBuiltIn
    {
        get => _useBuiltIn;
        set
        {
            if (_useBuiltIn == value) return;
            _useBuiltIn = value;
            Reload();
            IconHelper.ResetFileTypeCaches();
        }
    }

    private sealed record Entry(string? Description, string? Icon);

    private static readonly object Gate = new();
    private static Dictionary<string, Entry>? _entries;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SweWolfSoftware", AppInfo.FolderName, "FileTypes.json");

    /// <summary>The user's description for an extension ("jpg" or ".jpg"), or null when there is none.</summary>
    public static string? GetDescription(string extension)
    {
        var map = Load();
        if (map.Count == 0) return null;
        return map.TryGetValue(Normalize(extension), out var e) ? e.Description : null;
    }

    /// <summary>
    /// The user's icon for an extension as (file, index): "icon" is a path (a relative one starts in the folder of FileTypes.json;
    /// %VARIABLES% are expanded), or "file,index" for an icon inside a DLL / EXE. Index is null for a plain picture file.
    /// Null when the extension has no icon in the file.
    /// </summary>
    public static (string File, int? Index)? GetIcon(string extension)
    {
        var map = Load();
        if (map.Count == 0 || !map.TryGetValue(Normalize(extension), out var e) || string.IsNullOrWhiteSpace(e.Icon)) return null;

        string raw = e.Icon.Trim().Trim('"');
        if (raw.StartsWith(BuiltInPrefix, StringComparison.OrdinalIgnoreCase)) return (raw, null);   // an icon inside the EXE: no path, no index

        string spec = Environment.ExpandEnvironmentVariables(raw);
        int? index = null;
        int comma = spec.LastIndexOf(',');
        if (comma > 0 && int.TryParse(spec[(comma + 1)..].Trim(), out int i))
        {
            index = i;
            spec = spec[..comma].Trim().Trim('"');
        }
        if (!Path.IsPathRooted(spec)) spec = Path.Combine(Path.GetDirectoryName(FilePath)!, spec);
        return (spec, index);
    }

    /// <summary>True when the file gives this extension an icon (so the icon of the type is used, not one read from each file).</summary>
    public static bool HasIcon(string extension) => GetIcon(extension) != null;

    /// <summary>Forget what was read, so the file is read again at the next lookup.</summary>
    public static void Reload()
    {
        lock (Gate) _entries = null;
    }

    private static string Normalize(string extension) => extension.Trim().TrimStart('.').ToLowerInvariant();

    private static Dictionary<string, Entry> Load()
    {
        lock (Gate)
        {
            if (_entries != null) return _entries;

            var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

            // 1. The program's own list, embedded in the EXE (Settings: "Use SweWolf File Explorer's Own Definition ...").
            if (UseBuiltIn)
            {
                try
                {
                    using var res = typeof(FileTypeOverrides).Assembly.GetManifestResourceStream(BuiltInResource);
                    if (res != null)
                    {
                        using var reader = new StreamReader(res, System.Text.Encoding.UTF8);
                        Merge(map, reader.ReadToEnd());
                    }
                }
                catch (Exception ex)
                {
                    try { LogFiles.AppendError("The built-in file type list could not be read: " + ex.Message); } catch { }
                    map.Clear();
                }
            }

            // 2. The user's own FileTypes.json: what it names wins, property by property (it is always used: the user made it).
            try
            {
                if (File.Exists(FilePath)) Merge(map, File.ReadAllText(FilePath));
            }
            catch (Exception ex)
            {
                // A broken file must never break the list: the rest is used, and the log says what happened.
                try { LogFiles.AppendError("FileTypes.json could not be read, so it is ignored: " + ex.Message); } catch { }
            }

            _entries = map;
            return map;
        }
    }

    /// <summary>Reads a list (JSON text) into the map: an extension that is already there keeps what the new text does not say.</summary>
    private static void Merge(Dictionary<string, Entry> map, string json)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var doc = JsonDocument.Parse(json, options);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            string? text = entry.Value.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()?.Trim() : null;
            string? icon = entry.Value.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString()?.Trim() : null;
            string key = Normalize(entry.Name);
            if (key.Length == 0 || (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(icon))) continue;

            map.TryGetValue(key, out var old);
            map[key] = new Entry(string.IsNullOrEmpty(text) ? old?.Description : text, string.IsNullOrEmpty(icon) ? old?.Icon : icon);
        }
    }
}
