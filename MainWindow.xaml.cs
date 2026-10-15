using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SwewolfFileExplorer.Converters;
using SwewolfFileExplorer.Helpers;
using SwewolfFileExplorer.ViewModels;
using SwewolfFileExplorer.Views;

namespace SwewolfFileExplorer;

public partial class MainWindow : Window
{
    private MainViewModel VM => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        TreeColumn.Width = _treeWidth;
        DataContext = new MainViewModel();
        VM.FolderCreated += OnFolderCreated;
        VM.SortChanged   += () => ApplySort(VM.SortColumn, VM.SortAscending);
        VM.LastTabClosed += Close;

        // Swapping the tab's tree can select its nodes, which must not navigate the tab.
        VM.ActiveTabChanging += () => _switchingTab = true;
        VM.ActiveTabChanged  += OnActiveTabChanged;

        _layout.AddRange(new[]
        {
            new ColumnInfo(SortColumn.Name,         "Name",          NameColumn),
            new ColumnInfo(SortColumn.DateModified, "Date Modified", DateColumn),
            new ColumnInfo(SortColumn.DateCreated,  "Date Created",  CreatedColumn),
            new ColumnInfo(SortColumn.Type,         "Type",          TypeColumn),
            new ColumnInfo(SortColumn.Size,         "Size",          SizeColumn)
        });

        VM.PropertyChanged += (_, e) =>
        {
            // An empty name means "everything changed" (another tab became active).
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainViewModel.ShowTree))
                UpdateTreePane();
        };

        // The info panel under the list follows the items (a folder was loaded, a sort order was applied).
        ((INotifyCollectionChanged)FileListView.Items).CollectionChanged += (_, _) => QueueInfoUpdate();

        LoadColumnLayout();
        ApplySort(VM.SortColumn, VM.SortAscending);

        Closing += (_, _) => SaveColumnLayout();

        _titleTimer.Tick += (_, _) =>
        {
            _titleTimer.Stop();
            Title = BaseTitle;
        };
    }

    private void OnFolderCreated(FileItemViewModel folder)
    {
        if (VM.CurrentItems.Contains(folder))   // not there when the folders are hidden
        {
            FileListView.SelectedItem = folder;
            FileListView.ScrollIntoView(folder);
        }
        ShowRenameDialog(folder);
    }

    // ── Sorting ───────────────────────────────────────────────────────────────

    private SortColumn _sortColumn;
    private bool _sortAscending;

    private void FileListView_HeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column }) return;
        var info = _layout.FirstOrDefault(c => c.Column == column);
        if (info == null) return;

        // A second click on the same column reverses the order.
        bool ascending = info.Key != _sortColumn || !_sortAscending;
        VM.SetSort(info.Key, ascending);   // remembered for this folder; comes back as SortChanged
    }

    private void ApplySort(SortColumn key, bool ascending)
    {
        _sortColumn    = key;
        _sortAscending = ascending;

        // The view's sort survives navigation and refresh, and keeps the selection.
        // No sort (None): the view shows the items as they were read, which for an archive is the order stored in it.
        if (CollectionViewSource.GetDefaultView(FileListView.ItemsSource) is ListCollectionView view)
            view.CustomSort = key == SortColumn.None ? null : new FileItemComparer(key, ascending);

        foreach (var info in _layout)
        {
            info.Column.Header = info.Key == key
                ? $"{info.Title} {(ascending ? "▲" : "▼")}"
                : info.Title;
        }

        QueueInfoUpdate();   // the position of the selected item changed
    }

    // ── Column layout (which columns are shown, in what order) ────────────────

    private class ColumnInfo
    {
        public SortColumn Key { get; }
        public string Title { get; }
        public GridViewColumn Column { get; }
        public bool Visible { get; set; } = true;
        public double Width { get; set; }

        public ColumnInfo(SortColumn key, string title, GridViewColumn column)
        {
            Key    = key;
            Title  = title;
            Column = column;
            Width  = column.Width;
        }
    }

    /// <summary>All columns in display order; hidden ones are kept here but are not in the GridView.</summary>
    private readonly List<ColumnInfo> _layout = new();

    private GridView ListGrid => (GridView)FileListView.View;

    private void LoadColumnLayout()
    {
        var saved = ColumnLayoutStore.Load();
        if (saved != null)
        {
            // Saved order first; a column the file does not know (a newer version) goes last.
            var ordered = new List<ColumnInfo>();
            foreach (var state in saved)
            {
                var info = _layout.FirstOrDefault(c => c.Key.ToString() == state.Key);
                if (info == null || ordered.Contains(info)) continue;

                info.Visible = state.Visible || info.Key == SortColumn.Name;
                if (state.Width >= 20) info.Width = state.Width;
                ordered.Add(info);
            }
            ordered.AddRange(_layout.Except(ordered));

            _layout.Clear();
            _layout.AddRange(ordered);
        }

        ApplyColumns();
    }

    private void SaveColumnLayout()
    {
        SyncLayoutFromGrid();
        ColumnLayoutStore.Save(_layout
            .Select(c => new ColumnState { Key = c.Key.ToString(), Visible = c.Visible, Width = c.Width })
            .ToList());
    }

    /// <summary>Takes over what the user did in the list itself: dragged a header, resized a column.</summary>
    private void SyncLayoutFromGrid()
    {
        var shown = new List<ColumnInfo>();
        foreach (var column in ListGrid.Columns)
        {
            var info = _layout.FirstOrDefault(c => c.Column == column);
            if (info == null) continue;

            double width = double.IsNaN(column.Width) ? column.ActualWidth : column.Width;
            if (width >= 20) info.Width = width;
            shown.Add(info);
        }

        var hidden = _layout.Where(c => !shown.Contains(c)).ToList();
        _layout.Clear();
        _layout.AddRange(shown);
        _layout.AddRange(hidden);
    }

    /// <summary>Rebuilds the GridView's columns from the layout.</summary>
    private void ApplyColumns()
    {
        ListGrid.Columns.Clear();
        foreach (var info in _layout.Where(c => c.Visible))
        {
            info.Column.Width = info.Width;
            ListGrid.Columns.Add(info.Column);
        }
        ApplySort(_sortColumn, _sortAscending);
    }

    /// <summary>A right-click on a column header shows the column menu, not the file menu.</summary>
    private void FileListView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.CursorLeft < 0) return;   // opened with the keyboard

        var hit = VisualTreeHelper.HitTest(FileListView, Mouse.GetPosition(FileListView));
        for (DependencyObject? d = hit?.VisualHit; d != null && d != FileListView; d = VisualTreeHelper.GetParent(d))
        {
            if (d is not GridViewColumnHeader) continue;

            e.Handled = true;
            ShowColumnMenu();
            return;
        }
    }

    private void ShowColumnMenu()
    {
        SyncLayoutFromGrid();

        var menu = new ContextMenu();
        foreach (var info in _layout)
        {
            var item = new MenuItem
            {
                Header      = info.Title,
                IsCheckable = true,
                IsChecked   = info.Visible,
                IsEnabled   = info.Key != SortColumn.Name   // Name is always shown
            };
            item.Click += (_, _) =>
            {
                info.Visible = item.IsChecked;
                ApplyColumns();
                SaveColumnLayout();
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var more = new MenuItem { Header = "More..." };
        more.Click += (_, _) => ShowChooseDetails();
        menu.Items.Add(more);

        var fit = new MenuItem { Header = "Size All Columns to Fit", InputGestureText = "Ctrl+Num +" };
        fit.Click += (_, _) => AutoFitColumns();
        menu.Items.Add(fit);

        menu.PlacementTarget = FileListView;
        menu.IsOpen = true;
    }

    private void ShowChooseDetails()
    {
        SyncLayoutFromGrid();

        var dlg = new ChooseDetailsDialog(_layout.Select(c => new ColumnChoice
        {
            Key       = c.Key.ToString(),
            Title     = c.Title,
            IsVisible = c.Visible,
            Width     = Math.Round(c.Width),
            CanHide   = c.Key != SortColumn.Name
        })) { Owner = this };

        if (dlg.ShowDialog() != true) return;

        var ordered = new List<ColumnInfo>();
        foreach (var choice in dlg.Result)
        {
            var info = _layout.First(c => c.Key.ToString() == choice.Key);
            info.Visible = choice.IsVisible || !choice.CanHide;
            info.Width   = choice.Width;
            ordered.Add(info);
        }

        _layout.Clear();
        _layout.AddRange(ordered);
        ApplyColumns();
        SaveColumnLayout();
    }

    // ── Navigation tree ───────────────────────────────────────────────────────

    // ── Font size (Ctrl + mouse wheel, separate for the list and the tree) ────

    private const string BaseTitle = AppInfo.Name;
    private readonly System.Windows.Threading.DispatcherTimer _titleTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private int _wheelDelta;

    private void List_PreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        ChangeFontSize(e, () => VM.ListFontSize, v => VM.ListFontSize = v, "List");

    private void Tree_PreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        ChangeFontSize(e, () => VM.TreeFontSize, v => VM.TreeFontSize = v, "Tree");

    private void ChangeFontSize(MouseWheelEventArgs e, Func<double> get, Action<double> set, string what)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        e.Handled = true;   // no scrolling while Ctrl is held

        // One step per wheel notch (120); a touchpad sends many small amounts, so add them up.
        _wheelDelta += e.Delta;
        int steps = _wheelDelta / 120;
        if (steps == 0) return;
        _wheelDelta -= steps * 120;

        set(get() + steps);
        FlashTitle($"{BaseTitle} - {what} font size: {get():0.#} pt");
    }

    /// <summary>Shows a short message in the title bar for a couple of seconds.</summary>
    private void FlashTitle(string text)
    {
        Title = text;
        _titleTimer.Stop();
        _titleTimer.Start();
    }

    // ── Tabs ──────────────────────────────────────────────────────────────────

    private bool _switchingTab;

    private void OnActiveTabChanged()
    {
        // The list now shows the new tab's items, in a new view: give it the tab's sort order.
        ApplySort(VM.SortColumn, VM.SortAscending);

        // The tree creates its nodes later; keep ignoring their selection until it is idle.
        Dispatcher.BeginInvoke(() => _switchingTab = false, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void TabStrip_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Middle-click closes a tab, as in a browser.
        if (e.ChangedButton != MouseButton.Middle) return;
        if (FindItemUnder<FolderTabViewModel>(e.OriginalSource) is { } tab)
        {
            VM.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }

    // Drag a tab sideways to reorder; it moves along with the mouse.
    private FolderTabViewModel? _dragTab;
    private Point _dragStart;

    private void TabStrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragTab = null;
        if (IsOverButton(e.OriginalSource)) return;   // the close button is a click, not a drag

        _dragTab   = FindItemUnder<FolderTabViewModel>(e.OriginalSource);
        _dragStart = e.GetPosition(TabStrip);
    }

    private void TabStrip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab == null || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(TabStrip);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var tab = _dragTab;
        _dragTab = null;
        DragDrop.DoDragDrop(TabStrip, new DataObject(typeof(FolderTabViewModel), tab), DragDropEffects.Move);
    }

    private void TabStrip_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (e.Data.GetData(typeof(FolderTabViewModel)) is not FolderTabViewModel dragged) return;
        e.Effects = DragDropEffects.Move;

        if (FindItemUnder<FolderTabViewModel>(e.OriginalSource) is not { } target || target == dragged) return;
        if (TabStrip.ItemContainerGenerator.ContainerFromItem(target) is not FrameworkElement targetBox ||
            TabStrip.ItemContainerGenerator.ContainerFromItem(dragged) is not FrameworkElement draggedBox) return;

        // Swap only when the mouse is over the spot the dragged tab would then occupy.
        // Otherwise it would jump back and forth when the tabs differ in width.
        double x      = e.GetPosition(TabStrip).X;
        double left   = targetBox.TranslatePoint(new Point(0, 0), TabStrip).X;
        double right  = left + targetBox.ActualWidth;
        int    from   = VM.Tabs.IndexOf(dragged);
        int    to     = VM.Tabs.IndexOf(target);
        bool   movesRight = to > from;

        double newLeft  = movesRight ? right - draggedBox.ActualWidth : left;
        double newRight = newLeft + draggedBox.ActualWidth;
        if (x < newLeft || x > newRight) return;

        VM.Tabs.Move(from, to);
    }

    private static bool IsOverButton(object source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
            if (d is ListBoxItem) break;
        }
        return false;
    }

    private void FileListView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Middle-click on a folder opens it in a new tab, behind the current one.
        if (e.ChangedButton != MouseButton.Middle) return;
        if (FindItemUnder<FileItemViewModel>(e.OriginalSource) is { IsDirectory: true } folder)
        {
            VM.OpenNewTab(folder.FullPath, activate: false);
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Tab: the list would take it for moving the focus out of it.
        if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? VM.PreviousTabCommand : VM.NextTabCommand).Execute(null);
            e.Handled = true;
        }
        // Ctrl + numeric keypad plus: size every column to its widest text, as in Explorer.
        else if (e.Key == Key.Add && Keyboard.Modifiers == ModifierKeys.Control)
        {
            AutoFitColumns();
            e.Handled = true;
        }
    }

    /// <summary>Gives each shown column the width of its widest text (header included), growing or shrinking it.</summary>
    private void AutoFitColumns()
    {
        SyncLayoutFromGrid();

        var typeface = new Typeface(FileListView.FontFamily, FileListView.FontStyle,
                                    FileListView.FontWeight, FileListView.FontStretch);
        double dpi = VisualTreeHelper.GetDpi(FileListView).PixelsPerDip;

        double TextWidth(string text) => string.IsNullOrEmpty(text) ? 0 :
            new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
                              FileListView.FontSize, Brushes.Black, new NumberSubstitution(),
                              TextFormattingMode.Display, dpi).WidthIncludingTrailingWhitespace;

        var sizeConverter = new FileSizeConverter();
        string Cell(ColumnInfo col, FileItemViewModel item) => col.Key switch
        {
            SortColumn.Name         => item.Name,
            SortColumn.DateModified => item.DateModified.ToString("MM/dd/yyyy  HH:mm", CultureInfo.InvariantCulture),
            SortColumn.DateCreated  => item.DateCreated.ToString("MM/dd/yyyy  HH:mm", CultureInfo.InvariantCulture),
            SortColumn.Type         => item.TypeName,
            SortColumn.Size         => sizeConverter.Convert(item.Size, typeof(string), null!, CultureInfo.CurrentCulture) as string ?? "",
            _                       => ""
        };

        foreach (var col in _layout.Where(c => c.Visible))
        {
            // The header gets room for the sort arrow too, so sorting does not clip it.
            double width = TextWidth(col.Title + " ▲") + 16;

            foreach (var item in VM.CurrentItems)
            {
                // Cell padding; the Name cell also holds the 16 px icon and its 5 px margin.
                double w = TextWidth(Cell(col, item)) + 14 + (col.Key == SortColumn.Name ? 21 : 0);
                if (w > width) width = w;
            }

            col.Width = Math.Ceiling(width);
        }

        ApplyColumns();
        SaveColumnLayout();
    }

    /// <summary>The data item of the list or tab row the mouse is over, or null.</summary>
    private static T? FindItemUnder<T>(object source) where T : class
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem { DataContext: T item }) return item;   // ListViewItem is a ListBoxItem
        }
        return null;
    }

    // ── Menu ──────────────────────────────────────────────────────────────────

    private void MenuCreateShortcut_Click(object sender, RoutedEventArgs e) =>
        new CreateShortcutWindow { Owner = this }.ShowDialog();

    private void MenuSettings_Click(object sender, RoutedEventArgs e) =>
        new SettingsWindow { Owner = this }.ShowDialog();

    private void MenuAbout_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    // ── Navigation tree ───────────────────────────────────────────────────────

    // Until the user can set the width of the tree pane: a fifth of the screen (the width it comes back to after being hidden too).
    private const double TreeWidthFraction = 0.20;
    private GridLength _treeWidth = new(Math.Round(SystemParameters.WorkArea.Width * TreeWidthFraction));

    /// <summary>Shows or hides the tree pane and its splitter; the width comes back as it was.</summary>
    private void UpdateTreePane()
    {
        bool show = VM.ShowTree;
        if (show == (TreePane.Visibility == Visibility.Visible)) return;

        if (show)
        {
            TreeColumn.MinWidth = 80;
            TreeColumn.Width    = _treeWidth;
        }
        else
        {
            _treeWidth = TreeColumn.Width;
            TreeColumn.MinWidth = 0;
            TreeColumn.Width    = new GridLength(0);
            if (NavigationTree.IsKeyboardFocusWithin) FileListView.Focus();
        }

        SplitterColumn.Width = new GridLength(show ? 4 : 0);
        TreePane.Visibility  = TreeSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NavigationTree_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (_switchingTab) return;
        if (e.NewValue is not TreeItemViewModel item) return;

        // The tab selected this node itself, to follow a navigation: nothing to navigate to.
        if (VM.ConsumeProgrammaticTreeSelection(item)) return;

        if (!string.IsNullOrEmpty(item.FullPath))
            VM.Navigate(item.FullPath);
    }

    private void TreeItem_Selected(object sender, RoutedEventArgs e)
    {
        // Scroll the selected node into view (it bubbles up through the parent nodes: handle it once).
        if (e.OriginalSource == sender)
            ((TreeViewItem)sender).BringIntoView();
    }

    // ── File list ─────────────────────────────────────────────────────────────

    private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = FileListView.SelectedItems
            .Cast<FileItemViewModel>()
            .ToList();

        VM.UpdateSelectedItems(selected);
        QueueInfoUpdate();
    }

    // ── Info panel (under the list) ───────────────────────────────────────────

    private bool _infoUpdateQueued;
    private int _thumbnailVersion;

    /// <summary>Updates the panel once, after the current burst of changes (a folder loads item by item).</summary>
    private void QueueInfoUpdate()
    {
        if (_infoUpdateQueued) return;
        _infoUpdateQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _infoUpdateQueued = false;
            UpdateInfoPanel();
        }, DispatcherPriority.Background);
    }

    /// <summary>"File 14/17" (the place of the selected item in the list as shown) and the preview of a picture.</summary>
    private void UpdateInfoPanel()
    {
        var selected = FileListView.SelectedItems;

        if (selected.Count == 1 && selected[0] is FileItemViewModel item)
        {
            int position = FileListView.Items.IndexOf(item) + 1;   // in the order the list shows
            PositionText.Text = $"{(item.IsDirectory ? "Folder" : "File")} {position}/{FileListView.Items.Count}";
            ShowThumbnail(item);
        }
        else
        {
            PositionText.Text = selected.Count > 1 ? $"{selected.Count} items selected" : "";
            ShowThumbnail(null);
        }
    }

    private const int ThumbnailDecodeWidth = 320;   // twice the size shown, so it stays sharp on a high-DPI screen

    private async void ShowThumbnail(FileItemViewModel? item)
    {
        int version = ++_thumbnailVersion;

        if (item == null || item.IsDirectory || !ThumbnailLoader.IsImage(item.FullPath))
        {
            ThumbnailImage.Source = null;
            return;
        }

        // Off the UI thread: a large photo takes a moment to decode, and the arrow keys must stay responsive.
        string path = item.FullPath;
        var bitmap = await Task.Run(() => ThumbnailLoader.Load(path, ThumbnailDecodeWidth));

        if (version == _thumbnailVersion)   // not if another item was selected meanwhile
            ThumbnailImage.Source = bitmap;
    }

    private void FileListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileListView.SelectedItem is FileItemViewModel item)
            OpenItem(item);
    }

    /// <summary>A picture opens in the picture viewer of this app (not in the program Windows has for it); anything else as usual.</summary>
    private void OpenItem(FileItemViewModel item)
    {
        if (!item.IsDirectory && ThumbnailLoader.IsImage(item.FullPath))
            OpenInViewer(item);
        else
            VM.OpenItem(item);
    }

    private void OpenInViewer(FileItemViewModel item)
    {
        // The pictures of the list, in the order it shows them: the viewer goes through them.
        var pictures = FileListView.Items.OfType<FileItemViewModel>()
            .Where(i => !i.IsDirectory && ThumbnailLoader.IsImage(i.FullPath))
            .Select(i => i.FullPath)
            .ToList();

        var viewer = new PictureViewerWindow(pictures, item.FullPath) { Owner = this };
        viewer.PictureChanged += SelectInList;   // the list follows the viewer
        viewer.ShowDialog();

        if (viewer.AnyDeleted)
            VM.RefreshCommand.Execute(null);   // pictures went to the Recycle Bin
        SelectInList(viewer.CurrentPath);
        FileListView.Focus();
    }

    private void SelectInList(string path)
    {
        var item = FileListView.Items.OfType<FileItemViewModel>()
            .FirstOrDefault(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;

        FileListView.SelectedItem = item;
        FileListView.ScrollIntoView(item);
    }

    private void FileListView_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && FileListView.SelectedItem is FileItemViewModel openItem)
        {
            OpenItem(openItem);
            e.Handled = true;
        }
        else if (e.Key == Key.System && e.SystemKey == Key.Return)   // Alt+Enter
        {
            ShowProperties();
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && !VM.InArchive && FileListView.SelectedItem is FileItemViewModel renameItem)
        {
            ShowRenameDialog(renameItem);
            e.Handled = true;
        }
    }

    // ── Context menu ──────────────────────────────────────────────────────────

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool hasSelection = FileListView.SelectedItem != null;
        bool canPaste     = VM.PasteCommand.CanExecute(null);
        bool inArchive    = VM.InArchive;   // read-only: files can be opened and copied out, nothing else

        // Read the SendTo folder each time, so new shortcuts show up at once.
        FillSendToMenu(SendToMenu, hasSelection && !inArchive ? SendToHelper.GetEntries() : new());

        var menu = (ContextMenu)sender;
        foreach (var mi in menu.Items.OfType<MenuItem>())
        {
            mi.IsEnabled = mi.Header switch
            {
                "Open"       => hasSelection,
                "Open With..." => FileListView.SelectedItems.Count == 1 && FileListView.SelectedItem is FileItemViewModel { IsDirectory: false },
                "Send to"    => hasSelection && !inArchive,
                "Open in New Tab" => FileListView.SelectedItems.Count == 1 && FileListView.SelectedItem is FileItemViewModel { IsDirectory: true },
                "Cut"        => hasSelection && !inArchive,
                "Copy"       => hasSelection,
                "Paste"      => canPaste,
                "Delete"     => hasSelection && !inArchive,
                "Rename"     => hasSelection && !inArchive,
                "New Folder" => !inArchive,
                "Properties" => !inArchive,
                _            => true
            };
        }
    }

    private void FillSendToMenu(MenuItem parent, List<SendToEntry> entries)
    {
        parent.Items.Clear();
        foreach (var entry in entries)
        {
            var item = new MenuItem
            {
                Header = entry.Name.Replace("_", "__"),   // "_" would become an access key
                Icon   = entry.Icon == null ? null : new Image { Source = entry.Icon, Width = 16, Height = 16 },
                Tag    = entry.Path
            };

            if (entry.Children != null)
                FillSendToMenu(item, entry.Children);
            else
                item.Click += SendToItem_Click;

            parent.Items.Add(item);
        }

        // An empty submenu would otherwise open as a blank box.
        if (parent.Items.Count == 0)
            parent.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
    }

    private void SendToItem_Click(object sender, RoutedEventArgs e)
    {
        var target = (string)((MenuItem)sender).Tag;
        var files  = FileListView.SelectedItems.Cast<FileItemViewModel>().Select(i => i.FullPath).ToList();

        try
        {
            SendToHelper.SendTo(target, files);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot send to '{System.IO.Path.GetFileNameWithoutExtension(target)}': {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        VM.RefreshCommand.Execute(null);
    }

    /// <summary>Properties of the selected items, or of the current folder if nothing is selected.</summary>
    private void ShowProperties()
    {
        if (VM.InArchive) return;   // what is in an archive is not on the disk: Windows has no properties for it

        var paths = FileListView.SelectedItems.Cast<FileItemViewModel>().Select(i => i.FullPath).ToList();
        if (paths.Count == 0 && !string.IsNullOrEmpty(VM.CurrentPath))
            paths.Add(VM.CurrentPath);

        try
        {
            PropertiesHelper.Show(paths);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot show properties: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CtxProperties_Click(object sender, RoutedEventArgs e) => ShowProperties();

    private void CtxOpen_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is FileItemViewModel item)
            OpenItem(item);
    }

    /// <summary>Opens the file in a program of the user's choice (the Windows "Open with" dialog).</summary>
    private void CtxOpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is not FileItemViewModel { IsDirectory: false } file) return;

        // A file in an archive is first unpacked to the temporary folder: the dialog needs a file on the disk.
        var path = FolderTabViewModel.RealPathOf(file);
        if (path == null) return;

        try
        {
            OpenWithHelper.Show(path, new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot open: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CtxOpenInNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is FileItemViewModel { IsDirectory: true } folder)
            VM.OpenNewTab(folder.FullPath, activate: false);
    }

    private void CtxCut_Click(object sender, RoutedEventArgs e)     => VM.CutCommand.Execute(null);
    private void CtxCopy_Click(object sender, RoutedEventArgs e)    => VM.CopyCommand.Execute(null);
    private void CtxPaste_Click(object sender, RoutedEventArgs e)   => VM.PasteCommand.Execute(null);
    private void CtxDelete_Click(object sender, RoutedEventArgs e)  => VM.DeleteCommand.Execute(null);
    private void CtxNewFolder_Click(object sender, RoutedEventArgs e) => VM.NewFolderCommand.Execute(null);

    private void CtxRename_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is FileItemViewModel item)
            ShowRenameDialog(item);
    }

    // ── Rename dialog ─────────────────────────────────────────────────────────

    private void ShowRenameDialog(FileItemViewModel item)
    {
        var dlg = new InputDialog("Rename", "Enter new name:", item.Name) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.InputText))
            VM.RenameItem(item, dlg.InputText);
    }
}
