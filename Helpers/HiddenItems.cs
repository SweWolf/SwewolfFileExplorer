using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// What counts as a hidden file: the Hidden or System attribute, or an extension from the user's list
/// (Settings > Hidden Extensions; as in TeraTool, e.g. bup;ifo;db;!ut). One place, so the list, the
/// "N hidden items" line and the read-state of a folder (hidden files are not counted) all agree.
/// </summary>
public static class HiddenItems
{
    public const FileAttributes HiddenAttributes = FileAttributes.Hidden | FileAttributes.System;

    /// <summary>The list TeraTool started with.</summary>
    public const string DefaultExtensions = "bup;ifo;db;!ut";

    private static string _parsedFrom = "";
    private static HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsHiddenByAttributes(FileAttributes attributes) => (attributes & HiddenAttributes) != 0;

    /// <summary>A file is hidden by its attributes or by its extension. (Folders only have the attributes.)</summary>
    public static bool IsHiddenFile(FileAttributes attributes, string name) =>
        IsHiddenByAttributes(attributes) || IsHiddenExtension(name);

    public static bool IsHiddenExtension(string name)
    {
        var ext = Path.GetExtension(name);
        if (ext.Length <= 1) return false;
        return Extensions().Contains(ext.Substring(1));
    }

    private static HashSet<string> Extensions()
    {
        var text = AppSettingsStore.Current.HiddenExtensions ?? "";
        if (!string.Equals(text, _parsedFrom, StringComparison.Ordinal))
        {
            _extensions = Parse(text);
            _parsedFrom = text;
        }
        return _extensions;
    }

    /// <summary>"bup; .IFO, db" gives bup, ifo, db: separators are ; , or a space, a leading dot is allowed.</summary>
    public static HashSet<string> Parse(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var ext = part.Trim().TrimStart('.');
            if (ext.Length > 0) set.Add(ext);
        }
        return set;
    }
}
