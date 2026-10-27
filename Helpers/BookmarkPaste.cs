using System.Windows;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// What Ctrl+V means in the Bookmarks folder: the Bookmarks folder only holds bookmarks, so nothing is copied into it.
/// Files and folders on the clipboard, or paths in its text, become shortcuts to them; a web address becomes an Internet
/// shortcut; anything else is refused with an explanation.
/// </summary>
public static class BookmarkPaste
{
    public enum Kind { Nothing, Paths, WebAddress }

    /// <param name="Cut">The paths are files cut on the clipboard (not paths typed as text): a shortcut file among them is moved, not copied.</param>
    public sealed record Input(Kind Kind, List<string> Paths, string? Url, bool Cut = false);

    /// <summary>A shortcut file itself (<c>.lnk</c> or <c>.url</c>): pasting it is a copy of the shortcut, not a bookmark of the shortcut.</summary>
    public static bool IsShortcutFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
               && PathExists(path) && System.IO.File.Exists(path);
    }

    /// <summary>What the clipboard holds, as far as a bookmark is concerned. Call it on the UI thread.</summary>
    public static Input Read()
    {
        var none = new Input(Kind.Nothing, new(), null);

        // Files or folders copied in Explorer (or here): bookmarks of them. A cut counts as a copy: nothing is moved.
        if (FileClipboard.Get() is { } files && files.Paths.Count > 0)
            return new Input(Kind.Paths, files.Paths, null, files.IsCut);

        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : ""; }
        catch { return none; }

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim().Trim('"', '“', '”', '„').Trim())   // a path copied as a path comes in quotes
                        .Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return none;

        // One web address.
        if (lines.Count == 1 && BookmarkStore.AsWebAddress(lines[0]) is { } url && !PathExists(lines[0]))
            return new Input(Kind.WebAddress, new(), url);

        // Paths of things that exist (one or several lines).
        if (lines.All(PathExists)) return new Input(Kind.Paths, lines, null);

        return none;
    }

    private static bool PathExists(string path)
    {
        try { return System.IO.File.Exists(path) || System.IO.Directory.Exists(path); }
        catch { return false; }
    }

    /// <summary>Why nothing can be pasted here (shown when the clipboard holds nothing that makes a bookmark).</summary>
    public const string RefusedText =
        "The Bookmarks folder only holds bookmarks, so the clipboard cannot be pasted here as it is.\n\n" +
        "Copy the path of a file or folder, a web address, or some files or folders, and paste again: " +
        "a bookmark is made of it.";
}
