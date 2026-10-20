using System.IO;
using System.Runtime.InteropServices;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Makes a Windows shortcut (<c>.lnk</c>) to a file, a folder or a drive in a folder the user chose ("Create Shortcut..." in the right-click
/// menus). Named simply: the name of the item with <c>.lnk</c> added (<c>Report.txt.lnk</c>, <c>My Photos.lnk</c>), except for a program: the
/// name it calls itself (its product name: <c>File Info Viewer.lnk</c>; <see cref="ExeInfo.FriendlyName"/>), else the file name without <c>.exe</c>;
/// a drive is <c>Drive D.lnk</c> (the user's choice: Explorer's "- Shortcut" is not wanted). The caller
/// asks before an existing shortcut is replaced and offers the Desktop when a folder cannot be written to.
/// The shortcut points at the real item (not inside a compressed file: a shortcut cannot).
/// </summary>
public static class ShortcutMaker
{
    /// <summary>The name of the shortcut to <paramref name="target"/>, without the ".lnk": the item's own name; a program by its product name; a drive "Drive D".</summary>
    public static string NameFor(string target)
    {
        var trimmed = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name)) name = target.Length > 0 ? "Drive " + char.ToUpperInvariant(target[0]) : "Shortcut";
        else if (File.Exists(target) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = ExeInfo.FriendlyName(target) ?? name[..^4];   // a program: the name it calls itself, else the file name without ".exe"

        var bad = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return name.Length == 0 ? "Shortcut" : name;
    }

    /// <summary>What <see cref="CreateAll"/> did: the shortcuts made, and what could not be made (name: reason).</summary>
    public sealed record BatchResult(List<string> Made, List<string> Errors);

    /// <summary>
    /// Makes a shortcut to each target in <paramref name="folder"/>. <paramref name="confirmOverwrite"/> is asked (with the path) before a shortcut that
    /// is there already is replaced: false skips that one. When the folder cannot be written to, <paramref name="useDesktop"/> is asked once (with
    /// the folder) whether to make the shortcuts on the Desktop instead; the answer holds for the other targets, and a "no" is not reported as an error.
    /// </summary>
    public static BatchResult CreateAll(IReadOnlyList<string> targets, string folder, string desktop,
                                        Func<string, bool> confirmOverwrite, Func<string, bool> useDesktop)
    {
        var made = new List<string>();
        var errors = new List<string>();
        bool? desktopInstead = null;

        string NameOf(string t) => Path.GetFileName(t.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : t;
        bool Same(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            var into = desktopInstead == true ? desktop : folder;
            var path = PathFor(target, into);
            if (File.Exists(path) && !confirmOverwrite(path)) continue;

            if (Create(target, path, out var error, out var noPermission)) { made.Add(path); continue; }

            // No permission to write there: the Desktop is offered instead, once.
            if (noPermission && desktopInstead == null && !Same(into, desktop))
            {
                desktopInstead = useDesktop(into);
                if (desktopInstead == true)
                {
                    path = PathFor(target, desktop);
                    if (File.Exists(path) && !confirmOverwrite(path)) continue;
                    if (Create(target, path, out error, out _)) { made.Add(path); continue; }
                }
            }
            if (noPermission && desktopInstead == false) continue;   // the user declined the Desktop: not an error to list

            errors.Add($"{NameOf(target)}: {error}");
        }
        return new BatchResult(made, errors);
    }

    /// <summary>The full path the shortcut to <paramref name="target"/> gets in <paramref name="folder"/>.</summary>
    public static string PathFor(string target, string folder) => Path.Combine(folder, NameFor(target) + ".lnk");

    /// <summary>
    /// Writes the shortcut at <paramref name="lnkPath"/> (see <see cref="PathFor"/>), replacing a shortcut that is there: the caller has
    /// asked. A folder that is not there yet is made. Returns false with <paramref name="error"/> (fit to show) when the target is gone or the
    /// shortcut could not be saved; <paramref name="noPermission"/> says the folder cannot be written to (the caller offers the Desktop).
    /// </summary>
    public static bool Create(string target, string lnkPath, out string? error, out bool noPermission)
    {
        error = null;
        noPermission = false;
        try
        {
            bool isFolder = Directory.Exists(target);
            if (!isFolder && !File.Exists(target)) { error = $"\"{target}\" was not found."; return false; }

            var folder = Path.GetDirectoryName(lnkPath)!;
            Directory.CreateDirectory(folder);
            if (Directory.Exists(lnkPath)) { error = "A folder with that name exists."; return false; }

            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link  = shell.CreateShortcut(lnkPath);
            link.TargetPath       = target;
            link.WorkingDirectory = isFolder ? target : Path.GetDirectoryName(target) ?? "";
            link.Save();

            if (!File.Exists(lnkPath)) { error = "The shortcut could not be saved."; return false; }
            return true;
        }
        catch (UnauthorizedAccessException) { noPermission = true; error = "No permission to write in that folder."; return false; }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070005))   // E_ACCESSDENIED from the shell's Save
        {
            noPermission = true;
            error = "No permission to write in that folder.";
            return false;
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070005))
        {
            noPermission = true;
            error = "No permission to write in that folder.";
            return false;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}
