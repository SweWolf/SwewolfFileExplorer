using System.IO;
namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Which files count as "programs" for the question before the explorer starts one (Settings > Security). The list is the user's own
/// (<see cref="AppSettings.ProgramExtensions"/>); this is only its default and the check. Plan: D:\Dev\dev-notes\FileExplorer-ask-before-starting-programs-plan.md
/// </summary>
public static class ProgramTypes
{
    /// <summary>Types Windows starts when they are opened, and scripts that run when a script program is installed. No .ps1 (Windows opens it
    /// in Notepad), no .dll (never started by a double-click), no .lnk.</summary>
    public const string DefaultExtensions = "exe;com;bat;cmd;scr;msi;vbs;vbe;js;jse;wsf;wsh;hta;jar;py;pyw";

    /// <summary>The default of the first version (2026-11-07, before JavaScript and VBScript's .vbe were added): a saved list equal to it is upgraded.</summary>
    public const string FirstDefaultExtensions = "exe;com;bat;cmd;scr;msi;vbs;wsf;hta;jar;py;pyw";

    /// <summary>The kinds shown as check boxes in Settings > Security. Together with the box for "other file types" they make the list; a kind is checked
    /// when any of its extensions is in the list (the kind stands for all of them: a list saved before .vbe or .js were added still shows VBScript checked),
    /// and extensions that belong to no checked kind go to the "other" box, so nothing is ever lost.</summary>
    public static readonly (string Label, string[] Extensions)[] Groups =
    {
        ("Programs (.exe, .com, .scr)",            new[] { "exe", "com", "scr" }),
        ("Installers (.msi)",                      new[] { "msi" }),
        ("Batch Files (.bat, .cmd)",               new[] { "bat", "cmd" }),
        ("VBScript Files (.vbs, .vbe)",            new[] { "vbs", "vbe" }),
        ("JavaScript Files (.js, .jse)",           new[] { "js", "jse" }),
        ("Windows Script Host Files (.wsf, .wsh)", new[] { "wsf", "wsh" }),
        ("HTML Applications (.hta)",               new[] { "hta" }),
        ("PowerShell Scripts (.ps1)",              new[] { "ps1" }),
        ("Python Scripts (.py, .pyw)",             new[] { "py", "pyw" }),
        ("Java Programs (.jar)",                   new[] { "jar" }),
    };

    /// <summary>The kind a file belongs to by its last extension: the label and the extensions of the check box in Settings, or for an extension of the user's
    /// own (the "other" box) the extension itself as ".sh Files". Null for a file with no extension.</summary>
    public static (string Label, string[] Extensions)? KindOf(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) return null;
        foreach (var group in Groups)
            if (group.Extensions.Contains(ext)) return group;
        return ("." + ext + " Files", new[] { ext });
    }

    /// <summary>The list without the kind of this file ("Don't ask again for this file type": those extensions stop counting as programs).</summary>
    public static string RemoveKind(string list, string path)
    {
        if (KindOf(path) is not { } kind) return list;
        var set = HiddenItems.Parse(list);
        foreach (var e in kind.Extensions) set.Remove(e);
        return string.Join(";", set);
    }

    /// <summary>Which kinds a list contains, and the extensions left over (separated by semicolons).</summary>
    public static (bool[] Checked, string Other) Split(string list)
    {
        var all = HiddenItems.Parse(list);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checks = new bool[Groups.Length];
        for (int i = 0; i < Groups.Length; i++)
        {
            checks[i] = Groups[i].Extensions.Any(all.Contains);
            if (checks[i]) foreach (var e in Groups[i].Extensions) used.Add(e);
        }
        return (checks, string.Join(";", all.Where(e => !used.Contains(e))));
    }

    /// <summary>The list for the kinds that are checked plus the other extensions (tidied: no dots, blanks or doubles).</summary>
    public static string Join(IReadOnlyList<bool> checks, string other)
    {
        var set = new List<string>();
        for (int i = 0; i < Groups.Length; i++)
            if (checks[i]) set.AddRange(Groups[i].Extensions);
        set.AddRange(HiddenItems.Parse(other));
        return string.Join(";", set.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Plain-text scripts that can be read in an editor. Not .vbe and .jse (encoded), not .jar or the programs (binary).</summary>
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "bat", "cmd", "vbs", "wsf", "wsh", "hta", "js", "ps1", "py", "pyw" };

    /// <summary>Types that are known not to be text (no need to look inside them).</summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "exe", "com", "scr", "msi", "jar", "vbe", "jse", "dll" };

    public static bool IsBinaryType(string path) => BinaryExtensions.Contains(Path.GetExtension(path).TrimStart('.'));

    /// <summary>
    /// A quick look at the first 8 KB: does the file look like text (for a file type of the user's own, to offer it in the text editor)? A byte order mark
    /// of UTF-8 or UTF-16 says yes; a zero byte says no; otherwise at most 1 % control characters (tab, line breaks, form feed and escape are fine).
    /// An empty or unreadable file is not text.
    /// </summary>
    public static bool LooksLikeText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[8192];
            int n = stream.Read(buffer, 0, buffer.Length);
            if (n == 0) return false;
            if (n >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) return true;
            if (n >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF))) return true;

            int control = 0;
            for (int i = 0; i < n; i++)
            {
                byte b = buffer[i];
                if (b == 0) return false;
                if (b < 32 && b != 9 && b != 10 && b != 12 && b != 13 && b != 27) control++;
            }
            return control * 100 <= n;
        }
        catch { return false; }
    }

    /// <summary>Is this a script that is worth opening in a text editor (by its last extension)?</summary>
    public static bool IsScript(string path) => ScriptExtensions.Contains(Path.GetExtension(path).TrimStart('.'));

    /// <summary>Is this file a program by its extension (the last one only), as the user's list says?</summary>
    public static bool IsProgram(string path) => IsProgram(path, AppSettingsStore.Current.ProgramExtensions);

    public static bool IsProgram(string path, string extensionList)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        return ext.Length > 0 && HiddenItems.Parse(extensionList).Contains(ext);
    }
}
