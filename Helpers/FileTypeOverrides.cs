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

    /// <summary>
    /// "win11:file.dll,-number" in an icon: an icon of Windows 11's own DLLs (their numbers are those of Windows 11 and differ on Windows 10).
    /// Used only on Windows 11 or later (build 22000 and up); on an older Windows the entry gives no icon, so the type keeps the icon Windows has for it.
    /// Nothing of Microsoft's is copied into the program: the icon is read from the user's own Windows.
    /// </summary>
    public const string Win11Prefix = "win11:";

    /// <summary>True on Windows 11 or later (build 22000 and up).</summary>
    public static bool IsWindows11 { get; } = Environment.OSVersion.Version.Build >= 22000;

    /// <summary>True when the icon of this extension is a "win11:" one (a failure to read it is then not logged: it is the program's own default).</summary>
    public static bool IsWin11Icon(string extension)
    {
        var map = Load();
        return map.TryGetValue(Normalize(extension), out var e) && e.Icon != null
               && e.Icon.Trim().Trim('"').StartsWith(Win11Prefix, StringComparison.OrdinalIgnoreCase);
    }

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
        if (raw.StartsWith(Win11Prefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsWindows11) return null;                       // an older Windows: the icon Windows has for the type is used
            raw = raw[Win11Prefix.Length..].Trim();
        }
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

    // What the user's file looked like when it was read (time and size, or "none"), so a refresh can tell if it was edited since.
    private static string _stamp = "";

    private static string CurrentStamp()
    {
        try
        {
            var info = new FileInfo(FilePath);
            return info.Exists ? info.LastWriteTimeUtc.Ticks + ":" + info.Length : "none";
        }
        catch { return "none"; }
    }

    /// <summary>
    /// For F5 and the Refresh button: if the user's FileTypes.json was added, changed or removed since it was read, the lists and the names
    /// and icons found so far are forgotten (they are read again at the next lookup) and true is returned. Cheap: it only looks at the file's
    /// time and size, and reads nothing when the file is as it was.
    /// </summary>
    public static bool ReloadIfChanged()
    {
        lock (Gate)
        {
            if (_entries == null || CurrentStamp() == _stamp) return false;
            _entries = null;
        }
        IconHelper.ResetFileTypeCaches();
        return true;
    }

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
            _stamp = CurrentStamp();

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
