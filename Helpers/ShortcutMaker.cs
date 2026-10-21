using System.IO;
using System.Runtime.InteropServices;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// What a shortcut is to be: its target, optional arguments for a program, an own name, and a description. A plain item (file, folder, drive)
/// only has a target. A shortcut to a workspace or a session is a shortcut to this program with the workspace file as its argument.
/// </summary>
public sealed record ShortcutSpec(string Target, string? Arguments = null, string? Name = null, string? Description = null);

/// <summary>
/// Makes a Windows shortcut (<c>.lnk</c>) to a file, a folder or a drive in a folder the user chose ("Create Shortcut..." in the right-click
/// menus), or to this program with a workspace or session file ("Create Shortcut to Workspace / Session" in their menus). Named simply: the
/// name of the item with <c>.lnk</c> added (<c>Report.txt.lnk</c>, <c>My Photos.lnk</c>; the name as the list shows it, so <c>Report.lnk</c> when the
/// user hides the extensions of known file types), except for a program: the name it calls itself (its product
/// name: <c>File Info Viewer.lnk</c>; <see cref="ExeInfo.FriendlyName"/>), else the file name without <c>.exe</c>; a drive is <c>Drive D.lnk</c> (the user's
/// choice: Explorer's "- Shortcut" is not wanted); a spec with its own name uses that. The caller asks before an existing shortcut is replaced and
/// offers the Desktop when a folder cannot be written to. The shortcut points at the real item (not inside a compressed file: a shortcut cannot).
/// </summary>
public static class ShortcutMaker
{
    /// <summary>The name of the shortcut to <paramref name="target"/>, without the ".lnk": the item's own name; a program by its product name; a drive "Drive D".</summary>
    public static string NameFor(string target) => NameFor(new ShortcutSpec(target));

    /// <summary>The name of the shortcut for <paramref name="spec"/>, without the ".lnk" (cleaned of what a file name cannot hold).</summary>
    public static string NameFor(ShortcutSpec spec)
    {
        string name;
        if (!string.IsNullOrWhiteSpace(spec.Name)) name = spec.Name;
        else
        {
            var target = spec.Target;
            var trimmed = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) name = target.Length > 0 ? "Drive " + char.ToUpperInvariant(target[0]) : "Shortcut";
            else if (File.Exists(target) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = ExeInfo.FriendlyName(target) ?? name[..^4];   // a program: the name it calls itself, else the file name without ".exe"
            else if (File.Exists(target))
                name = ShellExtensions.DisplayName(name);            // a file: named as the list shows it, so without the extension when the user hides it
        }

        var bad = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return name.Length == 0 ? "Shortcut" : name;
    }

    /// <summary>What <see cref="CreateAll(IReadOnlyList{ShortcutSpec}, string, string, Func{string, bool}, Func{string, bool})"/> did: the shortcuts made, and what could not be made (name: reason).</summary>
    public sealed record BatchResult(List<string> Made, List<string> Errors);

    /// <summary>Makes a shortcut to each item (see the other overload).</summary>
    public static BatchResult CreateAll(IReadOnlyList<string> targets, string folder, string desktop,
                                        Func<string, bool> confirmOverwrite, Func<string, bool> useDesktop) =>
        CreateAll(targets.Select(t => new ShortcutSpec(t)).ToList(), folder, desktop, confirmOverwrite, useDesktop);

    /// <summary>
    /// Makes each shortcut in <paramref name="folder"/>. <paramref name="confirmOverwrite"/> is asked (with the path) before a shortcut that
    /// is there already is replaced: false skips that one. When the folder cannot be written to, <paramref name="useDesktop"/> is asked once (with
    /// the folder) whether to make the shortcuts on the Desktop instead; the answer holds for the other shortcuts, and a "no" is not reported as an error.
    /// </summary>
    public static BatchResult CreateAll(IReadOnlyList<ShortcutSpec> specs, string folder, string desktop,
                                        Func<string, bool> confirmOverwrite, Func<string, bool> useDesktop)
    {
        var made = new List<string>();
        var errors = new List<string>();
        bool? desktopInstead = null;

        string NameOf(ShortcutSpec s) => s.Name ?? (Path.GetFileName(s.Target.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : s.Target);
        bool Same(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        foreach (var spec in specs)
        {
            var into = desktopInstead == true ? desktop : folder;
            var path = PathFor(spec, into);
            if (File.Exists(path) && !confirmOverwrite(path)) continue;

            if (Create(spec, path, out var error, out var noPermission)) { made.Add(path); continue; }

            // No permission to write there: the Desktop is offered instead, once.
            if (noPermission && desktopInstead == null && !Same(into, desktop))
            {
                desktopInstead = useDesktop(into);
                if (desktopInstead == true)
                {
                    path = PathFor(spec, desktop);
                    if (File.Exists(path) && !confirmOverwrite(path)) continue;
                    if (Create(spec, path, out error, out _)) { made.Add(path); continue; }
                }
            }
            if (noPermission && desktopInstead == false) continue;   // the user declined the Desktop: not an error to list

            errors.Add($"{NameOf(spec)}: {error}");
        }
        return new BatchResult(made, errors);
    }

    /// <summary>The full path the shortcut to <paramref name="target"/> gets in <paramref name="folder"/>.</summary>
    public static string PathFor(string target, string folder) => PathFor(new ShortcutSpec(target), folder);

    public static string PathFor(ShortcutSpec spec, string folder) => Path.Combine(folder, NameFor(spec) + ".lnk");

    /// <summary>
    /// Writes the shortcut at <paramref name="lnkPath"/> (see <see cref="PathFor(ShortcutSpec, string)"/>), replacing a shortcut that is there: the caller has
    /// asked. A folder that is not there yet is made. Returns false with <paramref name="error"/> (fit to show) when the target is gone or the
    /// shortcut could not be saved; <paramref name="noPermission"/> says the folder cannot be written to (the caller offers the Desktop).
    /// </summary>
    public static bool Create(string target, string lnkPath, out string? error, out bool noPermission) =>
        Create(new ShortcutSpec(target), lnkPath, out error, out noPermission);

    public static bool Create(ShortcutSpec spec, string lnkPath, out string? error, out bool noPermission)
    {
        error = null;
        noPermission = false;
        try
        {
            var target = spec.Target;
            bool isFolder = Directory.Exists(target);
            if (!isFolder && !File.Exists(target)) { error = $"\"{target}\" was not found."; return false; }

            var folder = Path.GetDirectoryName(lnkPath)!;
            Directory.CreateDirectory(folder);
            if (Directory.Exists(lnkPath)) { error = "A folder with that name exists."; return false; }

            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link  = shell.CreateShortcut(lnkPath);
            link.TargetPath       = target;
            link.WorkingDirectory = isFolder ? target : Path.GetDirectoryName(target) ?? "";
            if (!string.IsNullOrEmpty(spec.Arguments))   link.Arguments   = spec.Arguments;
            if (!string.IsNullOrEmpty(spec.Description)) link.Description = spec.Description;
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
