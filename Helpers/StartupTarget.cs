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

        if (!args.Any(TryArg) && args.Count > 1)
            TryArg(string.Join(" ", args));

        return new StartupTarget(file, folder, image, select);
    }
}
