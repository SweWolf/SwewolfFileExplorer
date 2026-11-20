using System.Windows;
using System.Windows.Controls;

namespace SwewolfFileExplorer;

/// <summary>
/// The "..." button at the end of the toolbar (2026-11-19, asked by the user: Windows Explorer has it): a menu with Select All, Select None and
/// Invert Selection for the file list. More items may follow. The menu is made in code (event handlers inside XAML styles broke the window once).
/// </summary>
public partial class MainWindow
{
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        bool usable = FileListView.IsVisible && FileListView.Items.Count > 0;

        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MakeSelectItem("Select All", "Ctrl+A", usable, SelectAllInList));
        menu.Items.Add(MakeSelectItem("Select None", "", usable && FileListView.SelectedItems.Count > 0, SelectNoneInList));
        menu.Items.Add(MakeSelectItem("Invert Selection", "", usable, InvertSelectionInList));
        menu.IsOpen = true;
    }

    private static MenuItem MakeSelectItem(string header, string gesture, bool enabled, Action action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private void SelectAllInList()
    {
        FileListView.Focus();
        FileListView.SelectAll();
    }

    private void SelectNoneInList()
    {
        FileListView.UnselectAll();
        FileListView.Focus();
    }

    private void InvertSelectionInList()
    {
        var selected = new HashSet<object>(FileListView.SelectedItems.Cast<object>());
        var others = FileListView.Items.Cast<object>().Where(i => !selected.Contains(i)).ToList();
        FileListView.UnselectAll();
        foreach (var item in others) FileListView.SelectedItems.Add(item);
        FileListView.Focus();
    }
}
