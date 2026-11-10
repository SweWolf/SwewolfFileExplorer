using System.Windows;
using SwewolfFileExplorer.Helpers;
using SwewolfFileExplorer.ViewModels;
using SwewolfFileExplorer.Views;

namespace SwewolfFileExplorer;

/// <summary>
/// A workspace or session file in the list (a .json file that says so, wherever it lies, for example on a USB drive after
/// "Move to Volume..."): Windows would open it in a JSON editor or the browser, and a plain shortcut to it would do the same.
/// So a double-click / Enter asks whether to open it as a workspace or session, and Create Shortcut asks whether to make a shortcut
/// that starts this program with the file (see <see cref="TabSetShortcutSpec"/>).
/// </summary>
public partial class MainWindow
{
    private static string KindWord(StoreKind kind) => kind == StoreKind.Workspace ? "workspace" : "session";

    private static string KindTitle(StoreKind kind) => kind == StoreKind.Workspace ? "Workspace" : "Session";

    /// <summary>The shortcut that starts this program with the file as its argument (named "Name (Session)"); null if the program's own path is not known.</summary>
    private static ShortcutSpec? TabSetShortcutSpec(string file, StoreKind kind)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return null;

        var name = System.IO.Path.GetFileNameWithoutExtension(file);
        return new ShortcutSpec(exe, Arguments: $"\"{file}\"", Name: $"{name} ({KindTitle(kind)})",
                                Description: $"Opens the {KindWord(kind)} \"{name}\" in {AppInfo.Name}");
    }

    /// <summary>
    /// Double-click / Enter on a workspace or session file. True when it was dealt with (opened as a workspace or session, or the
    /// user canceled); false when the file should be opened the usual way (the user chose "Open the File").
    /// </summary>
    private bool TryOpenAsTabSet(FileItemViewModel item)
    {
        if (item.IsDirectory || item.IsInArchive || !WorkspaceStore.TryGetKind(item.FullPath, out var kind)) return false;

        string word = KindWord(kind), title = KindTitle(kind);
        var dialog = new ChoiceDialog(
            $"\"{item.Name}\" is a {word} of {AppInfo.Name}.\n\nDo you want to open this {word} now?\n\n" +
            $"Open the {title} opens its tabs here. Open the File opens it in the program Windows uses for .json files.",
            new[] { $"Open the {title}", "Open the File", "Cancel" }, 0, 2)
        {
            Title    = $"Open {title} - {System.IO.Path.GetFileNameWithoutExtension(item.Name)}",
            FontSize = 16,
            MinWidth = 700,
            Owner    = this,
        };
        dialog.ShowDialog();

        switch (dialog.Result)
        {
            case 0:  OpenTabSet(item.FullPath); return true;
            case 1:  return false;   // the usual way
            default: return true;    // Cancel (or Esc)
        }
    }

    /// <summary>
    /// Create Shortcut on files of which some are workspace or session files: asks once whether those get a shortcut that opens them in
    /// this program (the default) or a plain shortcut to the file. Returns the shortcut to make for each target, or null if the user canceled.
    /// <paramref name="what"/> names what the folder question is about when it is one such file.
    /// </summary>
    private List<ShortcutSpec>? ChooseShortcutSpecs(IReadOnlyList<string> targets, ref string what)
    {
        var sets = new Dictionary<string, StoreKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
            if (System.IO.File.Exists(t) && WorkspaceStore.TryGetKind(t, out var kind)) sets[t] = kind;

        bool program = false;
        if (sets.Count > 0)
        {
            var first = sets.First();
            bool one = sets.Count == 1;
            string word = one ? KindWord(first.Value) : "workspace or session";

            // A shortcut can only hold the drive letter, not a path relative to the drive: it works as long as the drive keeps that letter.
            var letters = sets.Keys.Where(f => !WorkspaceStore.IsInAppFolders(f)).Select(f => WorkspaceStore.DriveRootOf(System.IO.Path.GetFullPath(f))).Where(r => r != null).Distinct().ToList();
            string driveNote = letters.Count == 0 ? "" :
                $"\n\nThe shortcut contains the drive letter of the file ({string.Join(", ", letters.Select(l => l!.TrimEnd('\\')))}), so the drive must always have that letter.";

            var dialog = new ChoiceDialog(
                (one ? $"\"{System.IO.Path.GetFileName(first.Key)}\" is a {word} of {AppInfo.Name}."
                     : $"{sets.Count} of the files are workspaces or sessions of {AppInfo.Name}.") +
                $"\n\nWhat kind of shortcut do you want?\n\nA shortcut that opens it starts {AppInfo.Name} with the {word}.\nA plain shortcut only points to the file itself." + driveNote,
                new[] { "Shortcut That Opens It", "Plain Shortcut to the File", "Cancel" }, 0, 2)
            {
                Title    = "Create Shortcut",
                FontSize = 16,
                MinWidth = 760,
                Owner    = this,
            };
            dialog.ShowDialog();
            if (dialog.Result == 2 || dialog.Result < 0) return null;
            program = dialog.Result == 0;

            if (program && one && targets.Count == 1)
                what = $"the {KindWord(first.Value)} \"{System.IO.Path.GetFileNameWithoutExtension(first.Key)}\"";
        }

        return targets.Select(t => program && sets.TryGetValue(t, out var k) ? TabSetShortcutSpec(t, k) ?? new ShortcutSpec(t) : new ShortcutSpec(t)).ToList();
    }
}
