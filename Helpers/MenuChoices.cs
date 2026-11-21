using System.Windows;
using System.Windows.Controls;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Which items of the right-click menus of the file list and of the folder tree the user wants to see (Tools > Settings > Menus).
/// Everything is shown unless the user turned it off; what is stored is the list of items that are hidden (<see cref="AppSettings.HiddenListMenuItems"/>
/// / <see cref="AppSettings.HiddenTreeMenuItems"/>, the <see cref="Choice.Key"/>s joined with ";").
///
/// The items that make a menu usable are not in the lists and are always shown: Open, Cut, Copy, Paste, Delete, Rename, Properties and
/// More Options. The three terminal items have their own group in Settings (Terminal Menu). Items that belong together are one choice
/// (Set as Tree Root and Reset Tree Root; Mark as Read and Mark as Unread; Rating and Expectation). Items that only show for a CD
/// drive (Eject, Look Up This Disc Again) are not offered either.
/// Items are found by their header text, so the lists below must follow the headers of MainWindow.xaml.
/// </summary>
public static class MenuChoices
{
    public sealed record Choice(string Key, params string[] Headers);

    public static readonly Choice[] List =
    {
        new("Edit", "Edit"),
        new("Open With...", "Open With..."),
        new("Open in New Tab", "Open in New Tab"),
        new("Open in Windows Explorer", "Open in Windows Explorer"),
        new("Send to", "Send to"),
        new("Mount Disc Image / Eject Disc Image", "Mount Disc Image", "Eject Disc Image"),
        new("Add to Bookmarks", "Add to Bookmarks"),
        new("Send to VirusTotal", "Send to VirusTotal"),
        new("Copy as Path", "Copy as Path"),
        new("Move/Copy to Folder...", "Move/Copy to Folder..."),
        new("Create Shortcut...", "Create Shortcut..."),
        new("Create Playlist...", "Create Playlist..."),
        new("Rename All Selected Music Files and Video Files Using Smart Rename", "Rename All Selected Music Files and Video Files Using Smart Rename"),
        new("Hide / Unhide", "Hide", "Unhide"),
        new("Mark as Read / Mark as Unread", "Mark as Read", "Mark as Unread"),
        new("Jump to Last Opened File", "Jump to Last Opened File"),
        new("Rating... / Expectation...", "Rating...", "Expectation..."),
        new("New Folder", "New Folder"),
        new("New Text Document...", "New Text Document..."),
        new("New File from Clipboard", "New File from Clipboard"),
        new("File Info Viewer", "File Info Viewer"),
    };

    public static readonly Choice[] Tree =
    {
        new("Open in New Tab", "Open in New Tab"),
        new("Open in Windows Explorer", "Open in Windows Explorer"),
        new("Send to", "Send to"),
        new("Add to Bookmarks", "Add to Bookmarks"),
        new("Set as Tree Root / Reset Tree Root", "Set as Tree Root", "Reset Tree Root"),
        new("Send to VirusTotal", "Send to VirusTotal"),
        new("Copy as Path", "Copy as Path"),
        new("Move/Copy to Folder...", "Move/Copy to Folder..."),
        new("Create Shortcut...", "Create Shortcut..."),
        new("Create Playlist...", "Create Playlist..."),
        new("Mark as Read / Mark as Unread", "Mark as Read", "Mark as Unread"),
        new("Jump to Last Opened File", "Jump to Last Opened File"),
        new("Rating... / Expectation...", "Rating...", "Expectation..."),
        new("Set Tags...", "Set Tags..."),
        new("New Folder", "New Folder"),
        new("New Text Document...", "New Text Document..."),
        new("New File from Clipboard", "New File from Clipboard"),
        new("File Info Viewer", "File Info Viewer"),
    };

    public static HashSet<string> Parse(string? stored) =>
        new((stored ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    public static string Join(IEnumerable<string> keys) => string.Join(";", keys);

    /// <summary>Gives back the items that <see cref="Apply"/> hid last time (call when the menu opens, before the code that sets each item's visibility).</summary>
    public static void Restore(HashSet<UIElement> hiddenBefore)
    {
        foreach (var item in hiddenBefore) item.Visibility = Visibility.Visible;
        hiddenBefore.Clear();
    }

    /// <summary>Hides the items the user turned off. Call after the code that decides what fits this time, then tidy the separators.</summary>
    public static void Apply(ContextMenu menu, Choice[] choices, string? hiddenSetting, HashSet<UIElement> hiddenNow)
    {
        var hidden = Parse(hiddenSetting);
        if (hidden.Count == 0) return;

        var headers = new HashSet<string>(choices.Where(c => hidden.Contains(c.Key)).SelectMany(c => c.Headers), StringComparer.Ordinal);
        foreach (var mi in menu.Items.OfType<MenuItem>())
        {
            if (mi.Header is string h && headers.Contains(h) && mi.Visibility == Visibility.Visible)
            {
                mi.Visibility = Visibility.Collapsed;
                hiddenNow.Add(mi);
            }
        }
    }
}
