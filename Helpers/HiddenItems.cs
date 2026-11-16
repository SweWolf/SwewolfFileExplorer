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

    /// <summary>Sets or clears the Hidden attribute of a file or folder and keeps its other attributes (System is left alone).</summary>
    public static void SetHiddenAttribute(string path, bool hidden)
    {
        var a = File.GetAttributes(path);
        a = hidden ? (a | FileAttributes.Hidden) & ~FileAttributes.Normal : a & ~FileAttributes.Hidden;
        if (a == 0) a = FileAttributes.Normal;
        File.SetAttributes(path, a);
    }

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

    /// <summary>
    /// A file inside a compressed file (zip, RAR ...) is hidden by the general list of extensions <b>or</b> by the
    /// extra list for compressed files (Settings > Hidden Files), e.g. the nfo / txt files that come with a comic.
    /// </summary>
    public static bool IsHiddenInArchive(string name)
    {
        if (IsHiddenExtension(name)) return true;

        var ext = Path.GetExtension(name);
        if (ext.Length <= 1) return false;
        return ArchiveExtensions().Contains(ext.Substring(1));
    }

    private static string _archiveParsedFrom = "";
    private static HashSet<string> _archiveExtensions = new(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> ArchiveExtensions()
    {
        var text = AppSettingsStore.Current.HiddenExtensionsInArchives ?? "";
        if (!string.Equals(text, _archiveParsedFrom, StringComparison.Ordinal))
        {
            _archiveExtensions = Parse(text);
            _archiveParsedFrom = text;
        }
        return _archiveExtensions;
    }

    /// <summary>
    /// "bup; .IFO, db" gives bup, ifo, db: separators are ; , a space, a tab or a line break, and a typed extension may come with a dot, a "*." (as in
    /// *.sh), quotes, or as a double extension (tar.gz gives gz: only the last one counts). Nothing but the plain extension is kept.
    /// </summary>
    public static HashSet<string> Parse(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(new[] { ';', ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var ext = part.Trim().Trim('"', '\'', '“', '”').TrimStart('*', '.');
            int dot = ext.LastIndexOf('.');
            if (dot >= 0) ext = ext[(dot + 1)..];
            ext = ext.Trim();
            if (ext.Length > 0) set.Add(ext);
        }
        return set;
    }
}
