using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// What a command line names. Used by the first start (App.OnStartup) and by a second start that is handed over to the
/// running window (SingleInstance). Each argument is tried in this order: a workspace / session .json file, a folder, a
/// picture, any other file. Quotes are not required: a path with spaces that arrived as several arguments is tried as one
/// path (joined with spaces) when no single argument is a path.
/// </summary>
public sealed record StartupTarget(string? TabSetFile, string? Folder, string? Image, string? SelectFile)
{
    public bool IsEmpty => TabSetFile == null && Folder == null && Image == null && SelectFile == null;

    public static StartupTarget Parse(IReadOnlyList<string> args)
    {
        string? file = null, folder = null, image = null, select = null;

        bool TryArg(string arg)
        {
            try
            {
                var full = Path.GetFullPath(arg.Trim().Trim('"'));
                if (full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    file = full;
                    return true;
                }
                if (Directory.Exists(full))
                {
                    folder = full;
                    return true;
                }
                if (File.Exists(full))
                {
                    if (ThumbnailLoader.IsImage(full)) image = full;
                    else select = full;
                    return true;
                }
            }
            catch { }   // not a path: ignored
            return false;
        }

        // Windows Explorer's switches (2026-11-17): /select,<item> selects it instead of opening it (a picture too); /root,<folder>
        // is a folder as the root of the tree; /e, /n and /separate (alone or glued with commas, as in "/e,/select,x") have no
        // meaning here and are ignored. A /select, item that is gone falls back to its folder, as Explorer does.
        var rest = new List<string>();
        string? selectItem = null, rootFolder = null;
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i].Trim();
            bool isSwitch = false;
            while (a.StartsWith('/'))
            {
                if (a.StartsWith("/select,", StringComparison.OrdinalIgnoreCase) || a.StartsWith("/root,", StringComparison.OrdinalIgnoreCase))
                {
                    bool isSelect = a.StartsWith("/select,", StringComparison.OrdinalIgnoreCase);
                    var value = a[(a.IndexOf(',') + 1)..].Trim().Trim('"');
                    if (value.Length == 0 && i + 1 < args.Count) value = args[++i].Trim().Trim('"');   // "/select, "path"" arrives as two arguments
                    if (isSelect) selectItem = value; else rootFolder = value;
                    a = "";
                    isSwitch = true;
                    break;
                }
                var comma = a.IndexOf(',');
                var name = (comma < 0 ? a : a[..comma]).ToLowerInvariant();
                if (name is "/e" or "/n" or "/separate")
                {
                    a = comma < 0 ? "" : a[(comma + 1)..].TrimStart();
                    isSwitch = true;
                    continue;
                }
                break;
            }
            if (!isSwitch) rest.Add(args[i]);
            else if (a.Length > 0) rest.Add(a);
        }

        if (selectItem != null)
        {
            try
            {
                var full = Path.GetFullPath(selectItem);
                if (File.Exists(full) || Directory.Exists(full)) return new StartupTarget(null, null, null, full);
                var parent = Path.GetDirectoryName(full);
                if (parent != null && Directory.Exists(parent)) return new StartupTarget(null, parent, null, null);
            }
            catch { }   // not a path: ignored
        }
        if (rootFolder != null)
        {
            try
            {
                var full = Path.GetFullPath(rootFolder);
                if (Directory.Exists(full)) return new StartupTarget(null, full, null, null);
            }
            catch { }
        }

        if (!rest.Any(TryArg) && rest.Count > 1)
            TryArg(string.Join(" ", rest));

        return new StartupTarget(file, folder, image, select);
    }
}
