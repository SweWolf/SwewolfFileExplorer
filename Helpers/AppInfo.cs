using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>What the app calls itself: the title of its windows and the caption of its message boxes, and the name of its data folders.</summary>
public static class AppInfo
{
    /// <summary>The name shown to the user.</summary>
    public const string Name = "SweWolf File Explorer";

    /// <summary>
    /// The name of the app's folders under <c>%AppData%\SweWolfSoftware</c> and <c>%LocalAppData%\SweWolfSoftware</c>: no spaces, as in the
    /// other apps of the family (FileInfoViewer, MP3toMP4, SwewolfMediaPlayer ...). Never use <see cref="Name"/> in a path.
    /// </summary>
    public const string FolderName = "SwewolfFileExplorer";

    // The folder of the first versions, with spaces in it (renamed 2026-10-20).
    private const string OldFolderName = "SweWolf File Explorer";

    /// <summary>
    /// Moves the data folders of the first versions (<c>...\SweWolfSoftware\SweWolf File Explorer</c>) to the new name, in
    /// %AppData% and %LocalAppData%: settings, column layout, workspaces, sessions, logs and the preview cache come along. Done at
    /// the start of the program, before anything is read; it does nothing when there is nothing to move.
    /// </summary>
    public static void MigrateFolders()
    {
        MigrateFolder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        MigrateFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    /// <summary>The same for one root (for tests too): the old folder is renamed; if the new one exists already, what is not in it yet is moved over.</summary>
    public static void MigrateFolder(string root)
    {
        try
        {
            var parent = Path.Combine(root, "SweWolfSoftware");
            var old    = Path.Combine(parent, OldFolderName);
            var now    = Path.Combine(parent, FolderName);
            if (!Directory.Exists(old)) return;

            if (!Directory.Exists(now))
            {
                Directory.Move(old, now);
                return;
            }

            MergeInto(old, now);   // both exist (a new folder was made before the old one was moved): nothing is lost, the new one wins
            try { Directory.Delete(old); } catch { /* not empty: what could not be moved stays */ }
        }
        catch
        {
            // Not being able to move the folder is not worth stopping the program: it starts with the new, empty folder.
        }
    }

    private static void MergeInto(string from, string to)
    {
        foreach (var entry in Directory.GetFileSystemEntries(from))
        {
            var target = Path.Combine(to, Path.GetFileName(entry));
            try
            {
                if (Directory.Exists(entry))
                {
                    if (!Directory.Exists(target)) Directory.Move(entry, target);
                    else { MergeInto(entry, target); try { Directory.Delete(entry); } catch { } }
                }
                else if (!File.Exists(target))
                {
                    File.Move(entry, target);
                }
            }
            catch { /* the next one */ }
        }
    }
}
