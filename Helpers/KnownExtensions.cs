using Microsoft.Win32;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Is an extension a "known file type" in the sense of Windows' Folder Options > "Hide extensions for known file types": one that Windows has
/// a type for, so that it is registered (<c>HKEY_CLASSES_ROOT\.ext</c> has a program type as its value, or a list of programs that open it). An
/// extension that nothing is registered for (".xyz": "XYZ File") is not known and is always shown. Asked once per extension and kept.
/// </summary>
public static class KnownExtensions
{
    private static readonly Dictionary<string, bool> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="extension"/> (with its dot) is registered in Windows.</summary>
    public static bool IsKnown(string extension)
    {
        if (string.IsNullOrEmpty(extension) || extension[0] != '.') return false;
        lock (Known)
        {
            if (Known.TryGetValue(extension, out var known)) return known;
            return Known[extension] = Look(extension);
        }
    }

    private static bool Look(string extension)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(extension);
            if (key == null) return false;
            if (key.GetValue(null) is string type && type.Length > 0) return true;   // a program type: "txtfile", "jpegfile" ...
            using var openWith = key.OpenSubKey("OpenWithProgids");                    // or only a list of programs that open it
            return openWith != null && openWith.GetValueNames().Length > 0;
        }
        catch { return false; }
    }
}
