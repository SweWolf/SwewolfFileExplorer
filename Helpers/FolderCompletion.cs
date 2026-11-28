using System.IO;

namespace SwewolfFileExplorer.Helpers;

// FolderCompletion: the folders the Tab key suggests in the address bar, as in the Command Prompt (2026-11-27).
// History:
//   2026-11-27  First version.

/// <summary>
/// What the Tab key does in the address bar: the folders (real folders only: no files, packed files or playlists) whose names start with the
/// letters typed after the last backslash, in name order. The caller shows the first, and the next one at every Tab, as the Command Prompt does.
/// </summary>
public static class FolderCompletion
{
    /// <summary>
    /// The suggestions for the text typed, each the whole text to put in the box (what was typed up to the last backslash, then the folder's name).
    /// A text with no backslash is looked up in <paramref name="currentFolder"/> (a drive such as "D:" in its root). Hidden and system folders only
    /// come when some letters are typed (so "D:\" + Tab does not start with $RECYCLE.BIN). Nothing for variables, shell: names, web addresses,
    /// wildcards, a folder that is not there or cannot be read. Reads the disk: call it off the UI thread.
    /// </summary>
    public static List<string> Suggest(string? typed, string? currentFolder)
    {
        var result = new List<string>();
        try
        {
            var text = SwewolfFileExplorer.ViewModels.FolderTabViewModel.CleanTypedPath(typed);
            if (text.Length == 0 || text.IndexOfAny(new[] { '%', '*', '?' }) >= 0
                || text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || text.Contains("://")) return result;

            string dirText, prefix;
            int cut = text.LastIndexOfAny(new[] { '\\', '/' });
            if (cut >= 0)
            {
                dirText = text[..(cut + 1)];
                prefix = text[(cut + 1)..];
            }
            else if (text.Length == 2 && text[1] == ':' && char.IsLetter(text[0]))
            {
                dirText = text + "\\";   // "D:" is the root of the drive
                prefix = "";
            }
            else if (!string.IsNullOrEmpty(currentFolder))
            {
                dirText = currentFolder.TrimEnd('\\', '/') + "\\";   // letters alone: the folders of the folder shown
                prefix = text;
            }
            else return result;

            if (!Directory.Exists(dirText)) return result;

            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
            foreach (var dir in new DirectoryInfo(dirText).EnumerateDirectories("*", options))
            {
                if (!dir.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (prefix.Length == 0 && (dir.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                result.Add(dirText + dir.Name);
            }
            result.Sort((a, b) => string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase));
        }
        catch
        {
            result.Clear();   // not readable: no suggestion
        }
        return result;
    }
}
