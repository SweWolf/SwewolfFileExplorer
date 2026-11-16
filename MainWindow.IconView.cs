using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SwewolfFileExplorer.Helpers;
using SwewolfFileExplorer.ViewModels;
using SwewolfFileExplorer.Views;

namespace SwewolfFileExplorer;

/// <summary>
/// The Icons view (2026-11-15, asked by the user): the same file list, laid out as tiles with a thumbnail and the name under it, instead of
/// the Details columns. One size only (no Small / Medium / Large): Ctrl + mouse wheel or Ctrl + plus / minus over the list changes it, as
/// they change the font size in Details. The list control, the selection, the sort, the filter, the menus and the drag and drop are the
/// very same ones: only the layout (<c>View</c>, item template, items panel) is swapped, so nothing else had to learn about it.
/// The panel only makes the tiles on screen (<see cref="VirtualizingWrapPanel"/>) and <see cref="ThumbnailService"/> reads only their
/// pictures, the newest request first, so a folder of thousands of files stays fast.
/// </summary>
public partial class MainWindow
{
    private GridView? _detailsGrid;
    private Style? _detailsItemStyle;
    private bool _iconMode;
    private int _iconAnchor;

    /// <summary>The panel of the Icons view (found when needed), for the number of tiles in a row.</summary>
    private VirtualizingWrapPanel? IconPanel() => FindChild<VirtualizingWrapPanel>(FileListView);

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>Called once from the constructor, and whenever the Icons button is pressed or released.</summary>
    private void ApplyViewMode()
    {
        bool icons = VM.IsIconView;
        if (icons == _iconMode) return;
        _iconMode = icons;

        if (icons)
        {
            _detailsGrid ??= (GridView)FileListView.View;
            _detailsItemStyle ??= FileListView.ItemContainerStyle;

            ThumbnailService.SetTileSize(VM.IconViewSize, VisualTreeHelper.GetDpi(this).DpiScaleX);
            FileListView.View = null;
            FileListView.ItemContainerStyle = (Style)FileListView.FindResource("IconItemStyle");
            FileListView.ItemTemplate = (DataTemplate)FileListView.FindResource("IconItemTemplate");
            FileListView.ItemsPanel = (ItemsPanelTemplate)FileListView.FindResource("IconPanelTemplate");
            ScrollViewer.SetHorizontalScrollBarVisibility(FileListView, ScrollBarVisibility.Disabled);
            ScrollViewer.SetCanContentScroll(FileListView, true);
        }
        else
        {
            FileListView.ClearValue(ItemsControl.ItemTemplateProperty);
            FileListView.ClearValue(ItemsControl.ItemsPanelProperty);
            FileListView.ItemContainerStyle = _detailsItemStyle;
            FileListView.ClearValue(ScrollViewer.HorizontalScrollBarVisibilityProperty);
            FileListView.ClearValue(ScrollViewer.CanContentScrollProperty);
            FileListView.View = _detailsGrid;

            // The tiles that were on screen let go of their pictures (the panel is gone without telling them).
            foreach (var item in VM.ActiveTab.CurrentItems.Where(i => i.ThumbWanted)) ThumbnailService.Unwanted(item);
            QueueAutoFit();
        }
    }

    /// <summary>The tile size changed: the layout follows by itself; the pictures are made again only when the size crosses a step of 64 pixels.</summary>
    private void IconSizeChanged()
    {
        if (!_iconMode) return;
        if (!ThumbnailService.SetTileSize(VM.IconViewSize, VisualTreeHelper.GetDpi(this).DpiScaleX)) return;
        foreach (var item in VM.ActiveTab.CurrentItems.Where(i => i.ThumbWanted))
        {
            item.ThumbDoneAt = 0;
            ThumbnailService.Wanted(item);
        }
    }

    /// <summary>Ctrl + wheel over the list in the Icons view: the tiles get bigger or smaller by 16 pixels a notch.</summary>
    private bool ChangeIconSizeByWheel(MouseWheelEventArgs e)
    {
        if (!_iconMode || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return false;
        e.Handled = true;

        _wheelDelta += e.Delta;
        int steps = _wheelDelta / 120;
        if (steps == 0) return true;
        _wheelDelta -= steps * 120;

        ChangeIconSize(VM.IconViewSize + steps * 16);
        return true;
    }

    private void ChangeIconSize(int size)
    {
        VM.IconViewSize = size;
        FlashTitle($"{BaseTitle} - Icon size: {VM.IconViewSize} px");
    }

    /// <summary>Ctrl + plus / minus / 0 for the Icons view (the same keys as the font size in Details). True if it was one of them.</summary>
    private bool ChangeIconSizeByKey(KeyEventArgs e)
    {
        if (!_iconMode || NavigationTree.IsKeyboardFocusWithin) return false;
        if (e.Key is not (Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract or Key.D0 or Key.NumPad0)) return false;

        int size = e.Key is Key.D0 or Key.NumPad0 ? AppSettings.DefaultIconViewSize
            : VM.IconViewSize + (e.Key is Key.OemPlus or Key.Add ? 16 : -16);
        ChangeIconSize(size);
        return true;
    }

    // ── Keys: the arrows move along the tiles (the list's own keys know only one column) ──

    private void IconView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_iconMode || FileListView.Items.Count == 0) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (e.OriginalSource is TextBoxBase) return;

        int count = FileListView.Items.Count;
        int columns = IconPanel()?.Columns ?? 1;
        int rowsOnScreen = Math.Max(1, (int)(FileListView.ActualHeight / Math.Max(1, VM.IconCellHeight)));
        int current = FileListView.SelectedIndex;
        if (current < 0 && FileListView.SelectedItems.Count > 0) current = FileListView.Items.IndexOf(FileListView.SelectedItems[0]);

        int target;
        switch (e.Key)
        {
            case Key.Left:     target = current < 0 ? 0 : Math.Max(0, current - 1); break;
            case Key.Right:    target = current < 0 ? 0 : Math.Min(count - 1, current + 1); break;
            case Key.Up:       target = current < 0 ? 0 : Math.Max(0, current - columns); break;
            case Key.Down:     target = current < 0 ? 0 : Math.Min(count - 1, current + columns); break;
            case Key.PageUp:   target = current < 0 ? 0 : Math.Max(0, current - columns * rowsOnScreen); break;
            case Key.PageDown: target = current < 0 ? 0 : Math.Min(count - 1, current + columns * rowsOnScreen); break;
            case Key.Home:     target = 0; break;
            case Key.End:      target = count - 1; break;
            default: return;
        }
        e.Handled = true;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            // Extend from the item the selection started at.
            int from = Math.Max(0, Math.Min(count - 1, _iconAnchor));
            FileListView.SelectedItems.Clear();
            for (int i = Math.Min(from, target); i <= Math.Max(from, target); i++) FileListView.SelectedItems.Add(FileListView.Items[i]);
        }
        else
        {
            _iconAnchor = target;
            FileListView.SelectedItems.Clear();
            FileListView.SelectedItems.Add(FileListView.Items[target]);
        }

        FileListView.ScrollIntoView(FileListView.Items[target]);
        Dispatcher.BeginInvoke(() =>
        {
            if (FileListView.ItemContainerGenerator.ContainerFromIndex(target) is ListViewItem container) container.Focus();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ── Sorting: no column headers here, so the list's right-click menu gets a Sort By submenu ──

    private const string IconSortTag = "IconSort";

    /// <summary>Puts "Sort By" at the top of the list's menu in the Icons view and takes it out again in Details.</summary>
    private void UpdateIconSortMenu()
    {
        for (int i = FileContextMenu.Items.Count - 1; i >= 0; i--)
            if (FileContextMenu.Items[i] is FrameworkElement { Tag: IconSortTag }) FileContextMenu.Items.RemoveAt(i);
        if (!_iconMode) return;

        var sort = new MenuItem { Header = "Sort By", Tag = IconSortTag };
        (string Name, SortColumn Column)[] columns =
        {
            ("Name", SortColumn.Name), ("Date Modified", SortColumn.DateModified), ("Date Created", SortColumn.DateCreated),
            ("Type", SortColumn.Type), ("Size", SortColumn.Size), ("Date Taken", SortColumn.DateTaken), ("Length", SortColumn.Length),
            ("Dimensions", SortColumn.Dimensions), ("Rating", SortColumn.Rating),
        };
        foreach (var (name, column) in columns)
        {
            var item = new MenuItem { Header = name, IsCheckable = false, IsChecked = _sortColumn == column };
            item.Click += (_, _) => VM.SetSort(column, _sortColumn == column ? _sortAscending : column is not SortColumn.Rating);
            sort.Items.Add(item);
        }
        sort.Items.Add(new Separator());
        var up = new MenuItem { Header = "Ascending", IsChecked = _sortAscending };
        up.Click += (_, _) => VM.SetSort(_sortColumn == SortColumn.None ? SortColumn.Name : _sortColumn, true);
        var down = new MenuItem { Header = "Descending", IsChecked = !_sortAscending };
        down.Click += (_, _) => VM.SetSort(_sortColumn == SortColumn.None ? SortColumn.Name : _sortColumn, false);
        sort.Items.Add(up);
        sort.Items.Add(down);

        FileContextMenu.Items.Insert(0, sort);
        FileContextMenu.Items.Insert(1, new Separator { Tag = IconSortTag });
    }
}
