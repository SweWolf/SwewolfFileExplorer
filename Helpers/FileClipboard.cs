using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Cut, Copy and Paste of files through the Windows clipboard, the way Explorer does it: a list of files
/// plus a "Preferred DropEffect" that says Copy or Move (Cut). So files can go to and come from
/// Explorer and other programs, and from one instance of this app to another.
/// </summary>
public static class FileClipboard
{
    private const string DropEffectFormat = "Preferred DropEffect";
    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;

    /// <summary>Puts the files on the clipboard, to be copied (or moved, if <paramref name="cut"/>) by the one who pastes them.</summary>
    public static void Set(IEnumerable<string> paths, bool cut)
    {
        var list = new StringCollection();
        list.AddRange(paths.ToArray());

        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData(DropEffectFormat, new MemoryStream(BitConverter.GetBytes(cut ? DropEffectMove : DropEffectCopy)));

        // copy: true keeps the files on the clipboard when this app ends.
        Retry(() => { Clipboard.SetDataObject(data, copy: true); return 0; });
    }

    /// <summary>Puts text on the clipboard (Copy as Path).</summary>
    public static void SetText(string text) =>
        Retry(() => { Clipboard.SetDataObject(text, copy: true); return 0; });

    /// <summary>The files on the clipboard and whether they are to be moved; null if there are no files on it.</summary>
    public static (List<string> Paths, bool IsCut)? Get()
    {
        try
        {
            if (!Retry(Clipboard.ContainsFileDropList)) return null;

            var files = Retry(Clipboard.GetFileDropList).Cast<string>().ToList();
            if (files.Count == 0) return null;

            bool cut = false;
            if (Retry(() => Clipboard.GetData(DropEffectFormat)) is MemoryStream stream && stream.Length >= 4)
            {
                var bytes = new byte[4];
                stream.Position = 0;
                stream.ReadExactly(bytes);
                int effect = BitConverter.ToInt32(bytes);
                cut = (effect & DropEffectMove) != 0 && (effect & DropEffectCopy) == 0;
            }

            return (files, cut);
        }
        catch (COMException) { return null; }   // the clipboard is in use by another program
    }

    /// <summary>True if the clipboard holds files (from this app, Explorer or another program).</summary>
    public static bool HasFiles()
    {
        try { return Clipboard.ContainsFileDropList(); }
        catch (COMException) { return false; }
    }

    /// <summary>Empties the clipboard, as Explorer does after pasting what was cut.</summary>
    public static void Clear()
    {
        try { Clipboard.Clear(); }
        catch (COMException) { /* in use: leave it */ }
    }

    // The clipboard can be locked for a moment by another program (CLIPBRD_E_CANT_OPEN): try again a few times.
    private static T Retry<T>(Func<T> action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return action(); }
            catch (COMException) when (attempt < 5) { Thread.Sleep(40); }
        }
    }
}
