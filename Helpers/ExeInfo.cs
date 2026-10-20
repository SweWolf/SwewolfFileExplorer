using System.Diagnostics;
using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// What a program (an .exe) calls itself, read from its version information (the Details tab of its Properties window): used to name a
/// shortcut to it ("Create Shortcut..."), and meant for the file list too (a column, one day).
/// </summary>
public static class ExeInfo
{
    /// <summary>The "Product name" of the file, or null if it has none or cannot be read (not a program, no access).</summary>
    public static string? ProductName(string path) => Read(path, v => v.ProductName);

    /// <summary>The "File description" of the file (Notepad: "Notepad"), or null.</summary>
    public static string? Description(string path) => Read(path, v => v.FileDescription);

    /// <summary>
    /// The name a program is known by: its product name ("File Info Viewer"). Windows' own programs all have the product name
    /// "Microsoft Windows Operating System", which names nothing, so for those the file description is used ("Notepad", "Windows Calculator").
    /// Null when the file has neither (the caller then uses the file name).
    /// </summary>
    public static string? FriendlyName(string path)
    {
        var product = ProductName(path);
        if (product != null && !IsGeneric(product)) return product;
        return Description(path) is { } description && !IsGeneric(description) ? description : null;
    }

    private static bool IsGeneric(string name) =>
        name.Contains("Operating System", StringComparison.OrdinalIgnoreCase);

    private static string? Read(string path, Func<FileVersionInfo, string?> pick)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = pick(FileVersionInfo.GetVersionInfo(path))?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch { return null; }
    }
}
