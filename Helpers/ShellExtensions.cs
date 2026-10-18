using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The special shell and system file types whose extension Windows never shows (the registry value <c>NeverShowExt</c> of their type):
/// a shortcut is "Photos", not "Photos.lnk". The list is the one Windows has out of the box; the setting
/// <see cref="AppSettings.HideShellExtensions"/> turns the hiding on and off here.
/// </summary>
public static class ShellExtensions
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".pif", ".url", ".scf", ".desklink", ".mapimail", ".mydocs", ".library-ms", ".searchconnector-ms",
    };

    /// <summary>The extension of <paramref name="fileName"/> that is not shown (with its dot), or null if it is shown.</summary>
    public static string? HiddenExtensionOf(string fileName)
    {
        if (!AppSettingsStore.Current.HideShellExtensions) return null;
        var extension = Path.GetExtension(fileName);
        return extension.Length > 0 && Extensions.Contains(extension) ? extension : null;
    }

    /// <summary>What the list shows for a file: its name, without the extension when that is one of the hidden ones.</summary>
    public static string DisplayName(string fileName)
    {
        var hidden = HiddenExtensionOf(fileName);
        return hidden == null ? fileName : fileName[..^hidden.Length];
    }

    /// <summary>
    /// The real file name for a name typed in the Rename box, which showed the name without the hidden extension: the extension
    /// is put back (as Explorer does), unless the user typed it too.
    /// </summary>
    public static string RestoreExtension(string originalFileName, string typedName)
    {
        var hidden = HiddenExtensionOf(originalFileName);
        if (hidden == null || typedName.EndsWith(hidden, StringComparison.OrdinalIgnoreCase)) return typedName;
        return typedName + hidden;
    }
}
