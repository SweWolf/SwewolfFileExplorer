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
        VM.SelectPathRequested += OnSelectPathRequested;
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
            new ColumnInfo(SortColumn.Size,         "Size",          SizeColumn),
            new ColumnInfo(SortColumn.Artist,       "Artist",        ArtistColumn, visible: false),
            new ColumnInfo(SortColumn.Title,        "Title",         TitleColumn, visible: false),
            new ColumnInfo(SortColumn.TrackNumber,  "Track Number",  TrackColumn, visible: false),
            new ColumnInfo(SortColumn.Rating,       "Rating",        RatingColumn, visible: false),
            new ColumnInfo(SortColumn.Expectation,  "Expectation",   ExpectationColumn, visible: false)
        });
        VM.TagsLoaded += () => { if (IsTagColumn(_sortColumn)) ApplySort(_sortColumn, _sortAscending); };

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

        Closing += (_, _) =>
        {
            SaveColumnLayout();
            VM.Session.Flush();   // the last file selected is written before the program ends
        };

        VM.Session.Changed += UpdateSessionUi;
        UpdateSessionUi();

        // A workspace or session file given on the command line is opened (replacing the tab the program starts with).
        Loaded += (_, _) =>
        {
            if (App.StartupFile != null) OpenTabSet(App.StartupFile, atStartup: true);
            else if (App.StartupFolder != null) VM.SetTreeRoot(App.StartupFolder);   // a folder on the command line is the root of the tree
        };

        // The media player is another program and marks files read while it plays: look again when the window comes back.
        Activated += (_, _) =>
        {
            if (_refreshOnActivate) { _refreshOnActivate = false; RefreshKeepingSelection(); }
            VM.RefreshReadState();

            // The player (another process) may have set the preview picture of the selected video while this window was
            // in the background: look for it again now, so it shows without F5 or selecting another file.
            // (The item whose preview is shown is remembered: the list does not report its selection reliably at this moment.)
            if (_mediaPreviewItem is { } shown) ShowThumbnail(shown);
        };

        _titleTimer.Tick += (_, _) =>
        {
            _titleTimer.Stop();
            Title = BaseTitle;
        };
    }

    /// <summary>Selects an item in the list again, e.g. after it was renamed (the list was read again).</summary>
    private void OnSelectPathRequested(string path)
    {
        var item = VM.CurrentItems.FirstOrDefault(i =>
            string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;   // not listed (the folders are hidden)

        FileListView.SelectedItem = item;
        FileListView.ScrollIntoView(item);
    }

    private void OnFolderCreated(FileItemViewModel folder)
    {
        if (VM.CurrentItems.Contains(folder))   // not there when the folders are hidden
        {
            FileListView.SelectedItem = folder;
            FileListView.ScrollIntoView(folder);
        }
        ShowRenameDialog(folder, isNew: true);   // the name of a new folder is asked for, not changed
    }

    // ── Sorting ───────────────────────────────────────────────────────────────

    private SortColumn _sortColumn;
    private bool _sortAscending;

    private void FileListView_HeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column }) return;
        var info = _layout.FirstOrDefault(c => c.Column == column);
        if (info == null) return;

        // A second click on the same column reverses the order. Rating and expectation start with the highest first.
        bool firstAscending = info.Key is not (SortColumn.Rating or SortColumn.Expectation);
        bool ascending = info.Key != _sortColumn ? firstAscending : !_sortAscending;
        VM.SetSort(info.Key, ascending);   // remembered for this folder; comes back as SortChanged
    }

    private static bool IsTagColumn(SortColumn key) => key is SortColumn.Artist or SortColumn.Title or SortColumn.TrackNumber;

    private void ApplySort(SortColumn key, bool ascending)
    {
        _sortColumn    = key;
        _sortAscending = ascending;

        // The tags of the music files are read only if a column that shows them is in use (shown, or sorted by).
        VM.SetTagColumnsVisible(IsTagColumn(key) || _layout.Any(c => c.Visible && IsTagColumn(c.Key)));

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
        public bool Visible { get; set; }
        public double Width { get; set; }

        public ColumnInfo(SortColumn key, string title, GridViewColumn column, bool visible = true)
        {
            Key     = key;
            Title   = title;
            Column  = column;
            Visible = visible;
            Width   = column.Width;
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
        _contextOverEmpty = false;
        if (e.CursorLeft < 0) return;   // opened with the keyboard
        _contextOverEmpty = !IsOverListItem();

        // Shift + right-click: the Windows menu at once, as in Explorer (not on a column header).
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && !IsOverColumnHeader())
        {
            e.Handled = true;
            ShowWindowsMenu();
            return;
        }

        var hit = VisualTreeHelper.HitTest(FileListView, Mouse.GetPosition(FileListView));
        for (DependencyObject? d = hit?.VisualHit; d != null && d != FileListView; d = VisualTreeHelper.GetParent(d))
        {
            if (d is not GridViewColumnHeader) continue;

            e.Handled = true;
            ShowColumnMenu();
            return;
        }
    }

    /// <summary>The right-click that opened the list's menu was on the empty part of the list (the Windows menu is then the folder's).</summary>
    private bool _contextOverEmpty;

    private bool IsOverListItem()
    {
        var hit = VisualTreeHelper.HitTest(FileListView, Mouse.GetPosition(FileListView));
        for (DependencyObject? d = hit?.VisualHit; d != null && d != FileListView; d = VisualTreeHelper.GetParent(d))
            if (d is ListViewItem) return true;
        return false;
    }

    private bool IsOverColumnHeader()
    {
        var hit = VisualTreeHelper.HitTest(FileListView, Mouse.GetPosition(FileListView));
        for (DependencyObject? d = hit?.VisualHit; d != null && d != FileListView; d = VisualTreeHelper.GetParent(d))
            if (d is GridViewColumnHeader) return true;
        return false;
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

    /// <summary>The window title: the program's name, and the session's name while one is on.</summary>
    private string BaseTitle => VM.Session.IsActive ? $"{AppInfo.Name} - Session {VM.Session.Name}" : AppInfo.Name;
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
        // F6: into the address bar with its text selected, as in Explorer.
        else if (e.Key == Key.F6 && Keyboard.Modifiers == ModifierKeys.None)
        {
            AddressBar.Focus();
            AddressBar.SelectAll();
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

    private void MenuSettings_Click(object sender, RoutedEventArgs e)
    {
        // The list of hidden extensions may have changed: read the folder again.
        if (new SettingsWindow { Owner = this }.ShowDialog() == true)
            VM.RefreshCommand.Execute(null);
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    // Errors only are logged (LogFiles): what the program could not do, so the user can look into it afterwards.
    private void MenuOpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        if (!System.IO.File.Exists(LogFiles.ErrorsLogFile))
        {
            MessageBox.Show(this, "There is no log file yet: nothing has gone wrong.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(LogFiles.ErrorsLogFile) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, $"Cannot open the log file: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void MenuOpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogFiles.LogFolder);
            Process.Start("explorer.exe", $"\"{LogFiles.LogFolder}\"");
        }
        catch (Exception ex) { MessageBox.Show(this, $"Cannot open the log folder: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ── Workspaces and sessions ───────────────────────────────────────────────

    private void WorkspaceMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != sender) return;
        bool open = sender == WorkspaceOpenMenu;
        FillNames((MenuItem)sender, StoreKind.Workspace, null,
            name => { if (open) OpenTabSet(WorkspaceStore.PathFor(StoreKind.Workspace, name)); else DeleteSaved(StoreKind.Workspace, name); });
    }

    private void SessionMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != sender) return;
        bool open = sender == SessionOpenMenu;
        FillNames((MenuItem)sender, StoreKind.Session, open ? VM.Session.Name : null,
            name => { if (open) OpenTabSet(WorkspaceStore.PathFor(StoreKind.Session, name)); else DeleteSaved(StoreKind.Session, name); });
    }

    /// <summary>Fills a submenu with the names of the saved workspaces or sessions (the one in use gets a check mark).</summary>
    private static void FillNames(MenuItem menu, StoreKind kind, string? checkedName, Action<string> click)
    {
        menu.Items.Clear();

        var names = WorkspaceStore.Names(kind);
        if (names.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(None)", IsEnabled = false });
            return;
        }

        foreach (var name in names)
        {
            var item = new MenuItem
            {
                Header    = name.Replace("_", "__"),   // a single underscore would be an access key
                IsChecked = string.Equals(name, checkedName, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => click(name);
            menu.Items.Add(item);
        }
    }

    /// <summary>Asks for a name (it becomes a file name). Null if canceled.</summary>
    private string? AskName(string title, string prompt)
    {
        var dialog = new InputDialog(title, prompt, "", WorkspaceStore.CheckName) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.InputText.Trim() : null;
    }

    private bool Confirm(string text) =>
        MessageBox.Show(this, text, AppInfo.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void WorkspaceSave_Click(object sender, RoutedEventArgs e)
    {
        var name = AskName("Save Workspace", "Name of the workspace:");
        if (name == null) return;

        var file = WorkspaceStore.PathFor(StoreKind.Workspace, name);
        if (System.IO.File.Exists(file) && !Confirm($"A workspace named \"{name}\" already exists. Do you want to replace it?"))
            return;

        if (WorkspaceStore.Write(file, StoreKind.Workspace, VM.CaptureState()))
            FlashTitle($"{BaseTitle} - Workspace saved: {name}");
        else
            MessageBox.Show(this, $"The workspace could not be saved to:\n{file}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void SessionStart_Click(object sender, RoutedEventArgs e)
    {
        var name = AskName("Start New Session", "Name of the session:");
        if (name == null) return;

        var file = WorkspaceStore.PathFor(StoreKind.Session, name);
        if (System.IO.File.Exists(file) && !Confirm($"A session named \"{name}\" already exists. Do you want to replace it with the tabs that are open now?"))
            return;

        VM.Session.Start(file);   // writes the open tabs at once, then keeps the file up to date
        if (!System.IO.File.Exists(file))
        {
            VM.Session.Stop();
            MessageBox.Show(this, $"The session could not be saved to:\n{file}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SessionStop_Click(object sender, RoutedEventArgs e) => VM.Session.Stop();

    private void WorkspaceOpenFile_Click(object sender, RoutedEventArgs e) => OpenTabSetFromDialog(StoreKind.Workspace);
    private void SessionOpenFile_Click(object sender, RoutedEventArgs e)   => OpenTabSetFromDialog(StoreKind.Session);

    private void OpenTabSetFromDialog(StoreKind kind)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title            = kind == StoreKind.Workspace ? "Open Workspace" : "Open Session",
            Filter           = "Workspace and Session Files (*.json)|*.json|All Files (*.*)|*.*",
            InitialDirectory = WorkspaceStore.Folder(kind),
        };
        if (dialog.ShowDialog(this) == true) OpenTabSet(dialog.FileName);
    }

    private void WorkspaceShowFolder_Click(object sender, RoutedEventArgs e) => ShowFolder(StoreKind.Workspace);
    private void SessionShowFolder_Click(object sender, RoutedEventArgs e)   => ShowFolder(StoreKind.Session);

    private static void ShowFolder(StoreKind kind)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{WorkspaceStore.Folder(kind)}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void DeleteSaved(StoreKind kind, string name)
    {
        string what = kind == StoreKind.Workspace ? "workspace" : "session";
        if (!Confirm($"Do you want to delete the {what} \"{name}\"?")) return;

        if (kind == StoreKind.Session && string.Equals(VM.Session.Name, name, StringComparison.OrdinalIgnoreCase))
            VM.Session.Stop();

        if (!WorkspaceStore.Delete(kind, name))
            MessageBox.Show(this, $"The {what} could not be deleted.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// Opens a workspace or session file; what it is, is read from the file itself, wherever it lies. If the user has been
    /// clicking around, asks first: for a session "All your current tabs will be closed. Do you want to continue?" (OK / Cancel;
    /// a session always replaces the tabs; not asked if a session is on, as the tabs are saved in it); for a workspace whether to
    /// close the open tabs first (Yes: close, No: keep and add, Cancel).
    /// A session goes on being saved into the same file. The mouse pointer is the wait pointer while the tabs open.
    /// </summary>
    private void OpenTabSet(string file, bool atStartup = false)
    {
        var data = WorkspaceStore.Read(file, out var kind, out var error);
        if (data == null)
        {
            MessageBox.Show(this, $"Cannot open \"{file}\":\n{error}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // If the user has been clicking around, a question first, in the app's own dialog (title "Open Workspace - <name>").
        // A session always replaces the open tabs (it is the whole state, never mixed with them): OK / Cancel; not asked when a
        // session is on already, as the open tabs are saved in it and closing them loses nothing. A workspace can also be added
        // to the open tabs: Yes (close them first) / No (keep them and add the workspace) / Cancel.
        bool replace = true;
        if (!atStartup && VM.HasUserActivity && !(kind == StoreKind.Session && VM.Session.IsActive))
        {
            bool session = kind == StoreKind.Session;
            var dialog = new ChoiceDialog(
                session ? "All your current tabs will be closed.\n\nDo you want to continue?" : "Do you want to close all current tabs first?",
                session ? new[] { "OK", "Cancel" } : new[] { "Yes", "No", "Cancel" },
                0, session ? 1 : 2)
            {
                Title    = $"Open {(session ? "Session" : "Workspace")} - {System.IO.Path.GetFileNameWithoutExtension(file)}",
                FontSize = 16,
                MinWidth = 560,
                Owner    = this,
            };
            dialog.ShowDialog();

            if (session) { if (dialog.Result != 0) return; }
            else
            {
                if (dialog.Result == 2) return;   // Cancel (or Esc)
                replace = dialog.Result == 0;     // Yes: close them first; No: keep them and add
            }
        }

        // Opening the folders takes a while (every tab reads its folder and its tree): the mouse pointer says so.
        var previousCursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            if (kind == StoreKind.Session)
            {
                VM.Session.Flush();   // the old session gets its last changes (a pending folder or selected file) written now...
                VM.Session.Stop();    // ...and does not record the switch itself
            }
            VM.RestoreTabs(data, replace);
            if (kind == StoreKind.Session) VM.Session.Start(file);
        }
        finally
        {
            Mouse.OverrideCursor = previousCursor;
        }
    }

    /// <summary>The window title and the Stop Session item follow whether a session is on.</summary>
    private void UpdateSessionUi()
    {
        SessionStopMenuItem.IsEnabled = VM.Session.IsActive;
        Title = BaseTitle;
    }

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
        else
            VM.ShowNoFolder();   // This PC / Quick Access: not a folder, so the list is emptied
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

    // What the preview shows, so that a click on it can open it in the viewer: the item it is of and, for a video or music
    // file, where its preview picture came from.
    private FileItemViewModel? _previewItem;
    private PreviewPicture? _previewSource;
    private FileItemViewModel? _mediaPreviewItem;   // the video or music file whose preview picture is looked for or shown

    private async void ShowThumbnail(FileItemViewModel? item)
    {
        int version = ++_thumbnailVersion;
        _previewItem      = null;
        _previewSource    = null;
        _mediaPreviewItem = null;

        if (item == null || item.IsDirectory)
        {
            ThumbnailImage.Source = null;
            ShowPreviewFrame(false);
            return;
        }

        string path = item.FullPath;

        // A picture: off the UI thread, as a large photo takes a moment to decode and the arrow keys must stay responsive.
        if (ThumbnailLoader.IsImage(path))
        {
            var bitmap = await Task.Run(() => ThumbnailLoader.Load(path, ThumbnailDecodeWidth));
            if (version != _thumbnailVersion) return;   // another item was selected meanwhile

            ThumbnailImage.Source = bitmap;
            ShowPreviewFrame(false);
            if (bitmap != null) _previewItem = item;
            return;
        }

        // A video or music file: the user's choice, a picture built into the file, a picture file next to it, for a video a
        // frame Windows makes (PreviewPictureFinder). Not inside an archive.
        if (!item.IsInArchive && (MediaPlayerLauncher.IsVideo(path) || AudioTagReader.HasTags(path)))
        {
            _mediaPreviewItem = item;
            var found = await Task.Run(() =>
            {
                LibraryService.WaitForQueued();   // a choice that was just made is written first
                return PreviewPictureFinder.Find(path, ThumbnailDecodeWidth);
            });
            if (version != _thumbnailVersion) return;

            ThumbnailImage.Source = found?.Image;
            ShowPreviewFrame(found == null);   // no picture: a frame and a hint, so the user sees where to right-click
            if (found != null) { _previewItem = item; _previewSource = found; }
            return;
        }

        ThumbnailImage.Source = null;
        ShowPreviewFrame(false);
    }

    /// <summary>A thin frame with the text "No Preview Picture" in the empty preview of a video or music file.</summary>
    private void ShowPreviewFrame(bool show)
    {
        PreviewBox.BorderThickness = new Thickness(show ? 1 : 0);
        PreviewBox.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        PreviewEmptyText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── The right-click menu of the preview square ───────────────────────────

    /// <summary>The file the preview menu is for: one selected video or music file (not a folder, a picture or something in an archive).</summary>
    private FileItemViewModel? PreviewMenuTarget() =>
        FileListView.SelectedItems.Count == 1
        && FileListView.SelectedItems[0] is FileItemViewModel { IsDirectory: false, IsInArchive: false } item
        && (MediaPlayerLauncher.IsVideo(item.FullPath) || AudioTagReader.HasTags(item.FullPath))
            ? item : null;

    private void PreviewBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = PreviewMenuTarget();
        if (item == null) { e.Handled = true; return; }   // no menu where there is nothing to choose

        var choice = LibraryStore.GetPreviewChoice(item.FullPath);
        PreviewChooseItem.Visibility = Visibility.Visible;
        PreviewNoneItem.Visibility   = choice is { None: true } ? Visibility.Collapsed : Visibility.Visible;
        PreviewAutoItem.Visibility   = choice != null ? Visibility.Visible : Visibility.Collapsed;

        // Save As only when there is a picture to save.
        var save = _previewSource != null ? Visibility.Visible : Visibility.Collapsed;
        PreviewSaveItem.Visibility      = save;
        PreviewSaveSeparator.Visibility = save;
    }

    /// <summary>
    /// Save Preview Picture As...: the picture shown can be saved as a file. The suggestion is the same folder and name when the
    /// picture already is a file (a poster beside the video, or the one the user chose); otherwise (a picture inside the file, or a
    /// frame of the video) it is <c>&lt;video name&gt;-poster.png</c> (.jpg for a JPEG cover) in the folder of the video, which is
    /// what the preview picture finder looks for. A frame is made again at full size, not saved at the size of the preview.
    /// </summary>
    private async void PreviewSave_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewMenuTarget() is not { } item || _previewSource is not { } source) return;

        string videoPath = item.FullPath;
        string folder, name;
        if (source.File != null)
        {
            folder = System.IO.Path.GetDirectoryName(source.File)!;
            name   = System.IO.Path.GetFileName(source.File);
        }
        else
        {
            bool jpeg = source.Bytes is { Length: > 2 } b && b[0] == 0xFF && b[1] == 0xD8;
            folder = System.IO.Path.GetDirectoryName(videoPath)!;
            name   = System.IO.Path.GetFileNameWithoutExtension(videoPath) + "-poster" + (jpeg ? ".jpg" : ".png");
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title            = "Save Preview Picture As",
            InitialDirectory = folder,
            FileName         = name,
            Filter           = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All Files|*.*",
            DefaultExt       = System.IO.Path.GetExtension(name),
            OverwritePrompt  = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var target = dialog.FileName;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await Task.Run(() => WritePreviewPicture(source, videoPath, target));

            // The folder shown may be the one saved into, and a new poster file may now be what the preview shows.
            if (IsSameFolder(System.IO.Path.GetDirectoryName(target), VM.CurrentPath))
            {
                var selected = FileListView.SelectedItems.OfType<FileItemViewModel>().Select(i => i.FullPath).ToList();
                VM.RefreshCommand.Execute(null);
                ReselectInList(selected);   // the selection stays, and the preview is made again by it
            }
            ShowThumbnail(item);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cannot save the picture: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private static bool IsSameFolder(string? a, string? b) =>
        a != null && b != null &&
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // Writes the picture in the format of the file name; the original bytes when the format is the same, otherwise it is
    // decoded and encoded again. A frame of the video is made again at full size.
    private static void WritePreviewPicture(PreviewPicture source, string videoPath, string target)
    {
        string targetExt = System.IO.Path.GetExtension(target).ToLowerInvariant();

        if (source.File != null)
        {
            if (SameFormat(System.IO.Path.GetExtension(source.File), targetExt))
            {
                if (!string.Equals(System.IO.Path.GetFullPath(source.File), System.IO.Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    System.IO.File.Copy(source.File, target, overwrite: true);
                return;
            }
            WriteBitmap(DecodeFull(System.IO.File.ReadAllBytes(source.File)), target, targetExt);
            return;
        }

        if (source.Bytes is { } bytes)
        {
            bool jpeg = bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8;
            if (SameFormat(jpeg ? ".jpg" : ".png", targetExt)) System.IO.File.WriteAllBytes(target, bytes);
            else WriteBitmap(DecodeFull(bytes), target, targetExt);
            return;
        }

        // A frame: Windows' thumbnail at a large size, else ffmpeg's frame at the size of the video (as the preview was found).
        var frame = ShellThumbnail.Get(videoPath, 1920)
                    ?? (FfmpegFrame.Get(videoPath, 0) is { } jpegBytes ? DecodeFull(jpegBytes) : null)
                    ?? throw new InvalidOperationException("the frame of the video could not be made again");
        WriteBitmap(frame, target, targetExt);
    }

    private static bool SameFormat(string a, string b)
    {
        static string Norm(string e) => e.ToLowerInvariant() switch { ".jpeg" or ".jpe" or ".jfif" => ".jpg", ".tiff" => ".tif", var x => x };
        return Norm(a) == Norm(b);
    }

    private static System.Windows.Media.Imaging.BitmapSource DecodeFull(byte[] bytes)
    {
        using var stream = new System.IO.MemoryStream(bytes);
        var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    private static void WriteBitmap(System.Windows.Media.Imaging.BitmapSource bitmap, string target, string ext)
    {
        System.Windows.Media.Imaging.BitmapEncoder encoder = ext switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 92 },
            ".bmp"                                  => new System.Windows.Media.Imaging.BmpBitmapEncoder(),
            ".gif"                                  => new System.Windows.Media.Imaging.GifBitmapEncoder(),
            ".tif" or ".tiff"                       => new System.Windows.Media.Imaging.TiffBitmapEncoder(),
            _                                       => new System.Windows.Media.Imaging.PngBitmapEncoder(),
        };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(target);
        encoder.Save(file);
    }

    private void SetPreviewChoice(FileItemViewModel item, LibraryStore.PreviewChoice? choice)
    {
        var path = item.FullPath;
        LibraryService.Queue(() => LibraryStore.SetPreviewChoice(path, choice));
        ShowThumbnail(item);
    }

    private void PreviewChoose_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewMenuTarget() is not { } item) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title            = "Choose Preview Picture",
            Filter           = "Pictures|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.ico|All Files|*.*",
            InitialDirectory = System.IO.Path.GetDirectoryName(item.FullPath),
        };
        if (dialog.ShowDialog(this) != true) return;

        if (ThumbnailLoader.Load(dialog.FileName, ThumbnailDecodeWidth) == null)
        {
            MessageBox.Show(this, "That file is not a picture the program can show.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SetPreviewChoice(item, new LibraryStore.PreviewChoice(false, dialog.FileName));
    }

    private void PreviewNone_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewMenuTarget() is { } item) SetPreviewChoice(item, new LibraryStore.PreviewChoice(true, null));
    }

    private void PreviewAuto_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewMenuTarget() is { } item) SetPreviewChoice(item, null);
    }

    /// <summary>
    /// A click on the preview opens its picture in the picture viewer: a picture of the list as any other (and the viewer goes
    /// on through the pictures of the list); the cover of a music file is saved to the temporary folder first, as the
    /// cover of a folder is, and shown alone.
    /// </summary>
    private void ThumbnailImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || _previewItem == null || ThumbnailImage.Source == null) return;
        e.Handled = true;

        var item   = _previewItem;
        var source = _previewSource;
        if (source == null)
        {
            OpenInViewer(item);   // a picture file shows itself, with the pictures of the list
            return;
        }

        try
        {
            string file;
            if (source.File != null)
            {
                file = source.File;   // a picture file: the one the user chose, or the one next to the video
            }
            else
            {
                // A picture from inside the file, or a frame Windows made: saved to the temporary folder first.
                var bytes = source.Bytes;
                bool png = bytes == null || (bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 'P');
                if (bytes == null)
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source.Image));
                    using var memory = new System.IO.MemoryStream();
                    encoder.Save(memory);
                    bytes = memory.ToArray();
                }
                file = ArchiveSupport.SaveToTemp(System.IO.Path.GetFileNameWithoutExtension(item.Name) + (png ? ".png" : ".jpg"), bytes);
            }
            new PictureViewerWindow(new[] { file }, file) { Owner = this }.ShowDialog();
            FileListView.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cannot show the picture: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void FileListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A double-click on the Rating or Expectation cell edits it (as in TeraTool); anywhere else it opens the item.
        var column = ColumnUnderMouse(e);
        if (column is SortColumn.Rating or SortColumn.Expectation && FileListView.SelectedItem is FileItemViewModel)
        {
            RateSelectedItems(rating: column == SortColumn.Rating);
            e.Handled = true;
            return;
        }

        if (FileListView.SelectedItem is FileItemViewModel item)
            OpenItem(item);
    }

    /// <summary>The column of the cell under the mouse in a list row (null for a header, the empty space, or outside a row).</summary>
    private SortColumn? ColumnUnderMouse(MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is not GridViewRowPresenter presenter) continue;

            // The cells are laid out from the left edge of the row, one column after the other.
            double x = e.GetPosition(presenter).X, left = 0;
            foreach (var column in ListGrid.Columns)
            {
                if (x < left + column.ActualWidth)
                    return _layout.FirstOrDefault(c => c.Column == column)?.Key;
                left += column.ActualWidth;
            }
            return null;
        }
        return null;
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
        var viewer = CreateViewer(PicturesInList(), item.FullPath, owner: this);
        viewer.ShowDialog();
        AfterViewer(viewer);
    }

    /// <summary>
    /// The program was started with a picture: the viewer opens at once, alone, with no Explorer window (the folder is not even
    /// opened in the Explorer yet, which costs time). Esc, X and the window's close button end the program; "Close" in the
    /// viewer's right-click menu goes on to the Explorer, in the folder of the picture.
    /// </summary>
    public void RunPictureOnly(string imagePath)
    {
        var folder = System.IO.Path.GetDirectoryName(imagePath) ?? "";

        var viewer = CreateViewer(QuickPictureList(folder, imagePath), imagePath, owner: null);

        // "Close" in the viewer's menu: the Explorer is made ready while the viewer stays on screen with an hourglass (the tree
        // takes a few seconds), then the viewer closes. Esc and the other ways out just end the program (ExitApplication).
        bool leaving = false;
        viewer.CloseToExplorerHandler = () =>
        {
            if (leaving) return;
            leaving = true;
            Mouse.OverrideCursor = Cursors.Wait;

            // The folder of the picture last shown (the viewer may have gone on to another folder).
            var shownFolder = System.IO.Path.GetDirectoryName(viewer.CurrentPath) ?? folder;
            if (!string.Equals(VM.ActiveTab.CurrentPath?.TrimEnd('\\', '/'), shownFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                VM.Navigate(shownFolder);

            ContentRendered += (_, _) =>
            {
                Mouse.OverrideCursor = null;
                viewer.Close();   // the Explorer is under it and shows now
                Activate();
            };
            Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;   // the Explorer is the program now
            Show();
        };

        viewer.ShowDialog();

        if (viewer.ExitApplication)
        {
            Application.Current.Shutdown();
            return;
        }

        AfterViewer(viewer);   // the Explorer is shown already
    }

    /// <summary>
    /// The pictures of a folder in the order the Explorer's list would show them (the sort the user chose for that folder, else
    /// by name), without opening the folder in the Explorer: hidden files are left out, the picture asked for is always there.
    /// </summary>
    private List<string> QuickPictureList(string folder, string imagePath)
    {
        var items = new List<FileItemViewModel>();
        try
        {
            foreach (var file in new System.IO.DirectoryInfo(folder).EnumerateFiles())
            {
                if (!ThumbnailLoader.IsImage(file.FullName)) continue;
                if (HiddenItems.IsHiddenFile(file.Attributes, file.Name)) continue;

                items.Add(new FileItemViewModel
                {
                    Name = file.Name, FullPath = file.FullName, IsDirectory = false,
                    DateModified = file.LastWriteTime, DateCreated = file.CreationTime, Size = file.Length,
                });
            }
        }
        catch { /* the folder cannot be read: the picture asked for is shown alone */ }

        var (column, ascending) = VM.Shared.TryGetFolderSort(folder, out var saved) ? saved : (SortColumn.Name, true);
        items.Sort((x, y) => new FileItemComparer(column, ascending).Compare(x, y));

        var pictures = items.Select(i => i.FullPath).ToList();
        if (!pictures.Contains(imagePath, StringComparer.OrdinalIgnoreCase)) pictures.Insert(0, imagePath);
        return pictures;
    }

    private PictureViewerWindow CreateViewer(List<string> pictures, string current, Window? owner)
    {
        var viewer = new PictureViewerWindow(pictures, current);
        if (owner != null) viewer.Owner = owner;

        // At the end of the list the viewer can go on to the next folder with pictures (tree order, same drive).
        // The search runs on a background thread, where VM (it reads the DataContext) must not be used: take what it needs here.
        var shared = VM.Shared;
        var root   = VM.TreeRootPath;   // with a root for the tree there is no next folder above it
        viewer.FindNextFolder = (folder, cancel) =>
            NextFolderFinder.Find(folder, shared.GetTreeSort, shared.ShowsHiddenInTree, cancel, root);
        viewer.OpenFolder = folder =>
        {
            VM.Navigate(folder);   // the list, the tree and the address bar follow
            return string.Equals(VM.ActiveTab.CurrentPath?.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                ? PicturesInList()
                : null;
        };
        viewer.PictureChanged += path =>
        {
            SelectInList(path);      // the list follows the viewer
            VM.MarkPathRead(path);   // a picture shown here is a picture opened
        };
        return viewer;
    }

    private void AfterViewer(PictureViewerWindow viewer)
    {
        if (viewer.AnyDeleted)
            VM.RefreshCommand.Execute(null);   // pictures went to the Recycle Bin
        SelectInList(viewer.CurrentPath);
        FileListView.Focus();
    }

    /// <summary>The pictures of the list, in the order it shows them: the viewer goes through them.</summary>
    private List<string> PicturesInList() =>
        FileListView.Items.OfType<FileItemViewModel>()
            .Where(i => !i.IsDirectory && ThumbnailLoader.IsImage(i.FullPath))
            .Select(i => i.FullPath)
            .ToList();

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
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.R or Key.E)   // as in TeraTool
        {
            RateSelectedItems(rating: e.Key == Key.R);
            e.Handled = true;
        }
    }

    // ── Rating and expectation ────────────────────────────────────────────────

    private void CtxRating_Click(object sender, RoutedEventArgs e)      => RateSelectedItems(rating: true);
    private void CtxExpectation_Click(object sender, RoutedEventArgs e) => RateSelectedItems(rating: false);

    private void TreeCtxRating_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) RateFolder(_treeMenuNode.FullPath, rating: true);
    }

    private void TreeCtxExpectation_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) RateFolder(_treeMenuNode.FullPath, rating: false);
    }

    // The two lines under the tree: a click edits the current folder's value.
    private void FolderRating_Click(object sender, MouseButtonEventArgs e)      => RateFolder(VM.ActiveTab.FolderPath, rating: true);
    private void FolderExpectation_Click(object sender, MouseButtonEventArgs e) => RateFolder(VM.ActiveTab.FolderPath, rating: false);

    /// <summary>Ctrl+R / Ctrl+E on the list: every selected item gets the value (the box starts with the first one's).</summary>
    private void RateSelectedItems(bool rating)
    {
        var items = FileListView.SelectedItems.OfType<FileItemViewModel>().ToList();
        if (items.Count == 0) return;

        var first = items[0];
        RateItems(items.Select(i => i.FullPath).ToList(), new LibraryStore.Marks(first.Rating, first.Expectation), rating);
    }

    private void RateFolder(string path, bool rating)
    {
        if (string.IsNullOrEmpty(path)) return;
        RateItems(new[] { path }, VM.GetMarks(path), rating);
    }

    /// <summary>
    /// Asks for a rating or an expectation and sets it. A number from 1 to 10 sets it, anything else removes it
    /// (blank, text, zero, negative, more than 10): see <see cref="RatingRules"/>. Cancel changes nothing.
    /// </summary>
    private void RateItems(IReadOnlyCollection<string> paths, LibraryStore.Marks current, bool rating)
    {
        var dialog = new InputDialog(
            rating ? "Rating" : "Expectation",
            rating ? "Rating from 1 to 10. Leave it empty to remove it:"
                   : "Expectation from 1 to 10 (decimals allowed). Leave it empty to remove it:",
            rating ? RatingRules.Format(current.Rating) : RatingRules.Format(current.Expectation)) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (rating)
        {
            var value = RatingRules.ParseRating(dialog.InputText);
            VM.SetRating(paths, value);
            if (value != null) ShowColumn(SortColumn.Rating);
        }
        else
        {
            var value = RatingRules.ParseExpectation(dialog.InputText);
            VM.SetExpectation(paths, value);
            if (value != null) ShowColumn(SortColumn.Expectation);
        }
    }

    /// <summary>The column of a value that was just set shows itself if it was hidden (TeraTool did the same).</summary>
    private void ShowColumn(SortColumn key)
    {
        var info = _layout.FirstOrDefault(c => c.Key == key);
        if (info == null || info.Visible) return;

        SyncLayoutFromGrid();
        info.Visible = true;
        ApplyColumns();
        SaveColumnLayout();
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
                "Copy as Path" => hasSelection,
                "Mark as Read" or "Mark as Unread" => hasSelection,
                "Rating..." or "Expectation..." => hasSelection,
                "Delete"     => hasSelection && !inArchive,
                "Rename"     => hasSelection && !inArchive,
                "New Folder" => !inArchive,
                "Properties" => !inArchive,
                "More Options" => !inArchive && (hasSelection || System.IO.Directory.Exists(VM.CurrentPath)),
                _            => true
            };

            // Only the change that makes sense: unread files can be marked read, read files unread (both for a mixed selection).
            var files = FileListView.SelectedItems.OfType<FileItemViewModel>()
                .Where(i => !i.IsDirectory && !ArchiveSupport.IsArchiveName(i.Name)).ToList();
            if (mi.Header is "Mark as Read")   mi.Visibility = files.Count == 0 || files.Any(i => i.IsUnread)  ? Visibility.Visible : Visibility.Collapsed;
            if (mi.Header is "Mark as Unread") mi.Visibility = files.Count == 0 || files.Any(i => !i.IsUnread) ? Visibility.Visible : Visibility.Collapsed;
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
            PropertiesHelper.Show(paths, OnPropertiesChanged);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot show properties: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Something was changed from the Properties window (OK or Apply): a new track number, a new name, the Hidden attribute.
    /// The rows of the files that only changed in size, dates or tags are updated in place (and the list is sorted again);
    /// a file that is gone (renamed), a folder, or a changed Hidden attribute needs the folder to be read again.
    /// The selection stays.
    /// </summary>
    private void OnPropertiesChanged(IReadOnlyList<string> paths)
    {
        var current = VM.ActiveTab.FolderPath;
        var inPlace = new List<FileItemViewModel>();
        bool readAgain = false;

        foreach (var path in paths)
        {
            if (string.Equals(path.TrimEnd('\\', '/'), current.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                readAgain = true;   // the folder shown (its Properties were open)
                continue;
            }

            var item = VM.CurrentItems.FirstOrDefault(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
            var parent = System.IO.Path.GetDirectoryName(path.TrimEnd('\\', '/'));
            if (item == null)
            {
                // Not listed: it may have become listed (no longer hidden) or have a new name, if it is in this folder.
                if (string.Equals(parent, current.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) readAgain = true;
                continue;
            }

            bool hiddenNow = false;
            try { hiddenNow = HiddenItems.IsHiddenFile(System.IO.File.GetAttributes(path), item.Name); } catch { readAgain = true; continue; }

            if (item.IsDirectory || hiddenNow != item.IsHidden || !System.IO.File.Exists(path)) readAgain = true;
            else inPlace.Add(item);
        }

        if (readAgain)
        {
            // Read the folder again, as F5 does, and select again what was selected.
            var selected = FileListView.SelectedItems.OfType<FileItemViewModel>().Select(i => i.FullPath).ToList();
            VM.RefreshCommand.Execute(null);
            ReselectInList(selected);
            return;
        }

        if (inPlace.Count == 0) return;

        foreach (var item in inPlace) item.ReloadFileInfo();
        VM.ReloadTags(inPlace);                       // the tags come back by themselves, a sort by them is redone then
        ApplySort(_sortColumn, _sortAscending);       // a sort by size or date is redone now
        QueueInfoUpdate();
    }

    private void ReselectInList(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;

        var items = FileListView.Items.OfType<FileItemViewModel>()
            .Where(i => paths.Contains(i.FullPath, StringComparer.OrdinalIgnoreCase)).ToList();
        if (items.Count == 0) return;

        FileListView.SelectedItems.Clear();
        foreach (var item in items) FileListView.SelectedItems.Add(item);
        FileListView.ScrollIntoView(items[0]);
    }

    private void CtxProperties_Click(object sender, RoutedEventArgs e) => ShowProperties();

    private void CtxOpen_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is FileItemViewModel item)
            OpenItem(item);
    }

    private void CtxMarkRead_Click(object sender, RoutedEventArgs e) =>
        VM.MarkRead(FileListView.SelectedItems.OfType<FileItemViewModel>(), read: true);

    private void CtxMarkUnread_Click(object sender, RoutedEventArgs e) =>
        VM.MarkRead(FileListView.SelectedItems.OfType<FileItemViewModel>(), read: false);

    private void TreeCtxMarkRead_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) VM.MarkFolderTree(_treeMenuNode.FullPath, read: true);
    }

    private void TreeCtxMarkUnread_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) VM.MarkFolderTree(_treeMenuNode.FullPath, read: false);
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

    private void CtxCopyPath_Click(object sender, RoutedEventArgs e) => VM.CopyPathCommand.Execute(null);

    // ── Context menu of the tree ──────────────────────────────────────────────

    /// <summary>The folder the tree's menu was opened on (not necessarily the selected one).</summary>
    private TreeItemViewModel? _treeMenuNode;

    private void NavigationTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _treeMenuNode = null;

        // The node under the mouse (or with the keyboard focus, for the menu key): the nearest TreeViewItem above the source.
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not TreeViewItem)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);

        // Quick Access and This PC are not folders: no menu.
        if ((element as TreeViewItem)?.DataContext is TreeItemViewModel { FullPath.Length: > 0 } node)
        {
            _treeMenuNode = node;

            // Shift + right-click: the Windows menu of the folder at once, as in Explorer.
            if (e.CursorLeft >= 0 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                ShowWindowsMenuForFolder(node.FullPath);
            }
        }
        else
        {
            e.Handled = true;
        }
    }

    /// <summary>A click on the picture of the folder opens it in the picture viewer.</summary>
    private void FolderPicture_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1) return;   // a quick second click must not open a second viewer

        var file = VM.GetFolderPictureFile();
        if (file == null) return;

        new PictureViewerWindow(new[] { file }, file) { Owner = this }.ShowDialog();
        e.Handled = true;
    }

    private void TreeContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode == null) return;

        string path       = _treeMenuNode.FullPath;
        bool realFolder   = System.IO.Directory.Exists(path);
        bool canChange    = CanChangeFolder(path);   // a drive cannot be cut, deleted or renamed

        var menu = (ContextMenu)sender;
        foreach (var mi in menu.Items.OfType<MenuItem>())
        {
            mi.IsEnabled = mi.Header switch
            {
                "Set as Tree Root"   => !(VM.HasTreeRoot && SharedState.Key(path).Equals(SharedState.Key(VM.TreeRootPath!), StringComparison.OrdinalIgnoreCase)),
                "Reset Tree Root"    => VM.HasTreeRoot,
                "Cut"        => canChange,
                "Paste"      => realFolder && FileClipboard.HasFiles(),
                "Delete"     => canChange,
                "Rename"     => canChange,
                "New Folder" => realFolder,
                "Properties" => realFolder || System.IO.File.Exists(path),   // what is in an archive is not on the disk
                "More Options" => realFolder,
                _            => true
            };

            // The state is known for the current folder only: there the useless one is hidden.
            bool isCurrent = string.Equals(SharedState.Key(path), SharedState.Key(VM.CurrentPath), StringComparison.OrdinalIgnoreCase);
            if (mi.Header is "Mark as Read")   mi.Visibility = isCurrent && VM.FolderStatusText == "Status: Read"   ? Visibility.Collapsed : Visibility.Visible;
            if (mi.Header is "Mark as Unread") mi.Visibility = isCurrent && VM.FolderStatusText == "Status: Unread" ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void TreeCtxOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) VM.Navigate(_treeMenuNode.FullPath);
    }

    private void TreeCtxOpenInNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) VM.OpenNewTab(_treeMenuNode.FullPath, activate: false);
    }

    private void TreeCtxSetRoot_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) SetTreeRoot(_treeMenuNode.FullPath);
    }

    private void TreeCtxResetRoot_Click(object sender, RoutedEventArgs e) => VM.ResetTreeRoot();

    // The arrow next to the address bar: right-click for the root of the tree.
    private void GoMenu_Opened(object sender, RoutedEventArgs e)
    {
        GoResetRootMenuItem.IsEnabled = VM.HasTreeRoot;
    }

    private void GoSetRoot_Click(object sender, RoutedEventArgs e) => SetTreeRoot(AddressBar.Text);

    private void GoResetRoot_Click(object sender, RoutedEventArgs e) => VM.ResetTreeRoot();

    private void SetTreeRoot(string path)
    {
        if (!VM.SetTreeRoot(path))
            MessageBox.Show(this, $"The folder \"{path.Trim()}\" does not exist.", AppInfo.Name,
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void TreeCtxCut_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) VM.PutFolderOnClipboard(_treeMenuNode.FullPath, cut: true);
    }

    private void TreeCtxCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) VM.PutFolderOnClipboard(_treeMenuNode.FullPath, cut: false);
    }

    private void TreeCtxPaste_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) VM.PasteInto(_treeMenuNode.FullPath);
    }

    private void TreeCtxCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) VM.CopyPathsAsText(new[] { _treeMenuNode.FullPath });
    }

    private void TreeCtxDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) VM.DeleteFolder(_treeMenuNode.FullPath);
    }

    private void TreeCtxRename_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) RenameTreeFolder(_treeMenuNode);
    }

    /// <summary>True for a folder of the disk that may be renamed or deleted: not a drive, and nothing in an archive.</summary>
    private static bool CanChangeFolder(string path) =>
        System.IO.Directory.Exists(path)
        && !string.Equals(System.IO.Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase);

    private void RenameTreeFolder(TreeItemViewModel node)
    {
        var dlg = new InputDialog("Rename Folder", "Enter new name:", node.Name,
            name => FolderTabViewModel.CheckNewName(node.FullPath, name)) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.InputText))
            VM.RenameFolder(node.FullPath, dlg.InputText);
    }

    /// <summary>F2 in the tree renames the selected folder; in the list it renames the selected item (what has the focus decides).</summary>
    private void NavigationTree_KeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+R / Ctrl+E in the tree rate the selected folder (in the list: the selected files).
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.R or Key.E)
        {
            if (NavigationTree.SelectedItem is TreeItemViewModel { FullPath.Length: > 0 } rated)
                RateFolder(rated.FullPath, rating: e.Key == Key.R);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.F2) return;

        if (NavigationTree.SelectedItem is TreeItemViewModel { FullPath.Length: > 0 } node && CanChangeFolder(node.FullPath))
            RenameTreeFolder(node);

        e.Handled = true;
    }

    private void TreeCtxNewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) VM.CreateFolderIn(_treeMenuNode.FullPath);
    }

    private void TreeCtxRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) VM.RefreshFolder(_treeMenuNode.FullPath);
    }

    private void TreeCtxProperties_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode == null) return;

        try
        {
            PropertiesHelper.Show(new[] { _treeMenuNode.FullPath }, OnPropertiesChanged);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot show properties: {ex.Message}", AppInfo.Name,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── A picture dragged in from a web browser ──────────────────────────────

    /// <summary>The folder a picture can be dropped in: a real folder of the disk (not This PC, not inside a compressed file).</summary>
    private static bool CanSaveIn(string? folder) =>
        !string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder) && !ArchiveSupport.IsInside(folder);

    private void FileListView_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanSaveIn(VM.ActiveTab.FolderPath) && DroppedImage.CanAccept(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void FileListView_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (CanSaveIn(VM.ActiveTab.FolderPath)) SavePicture(e, VM.ActiveTab.FolderPath);
    }

    /// <summary>The folder of the tree under the mouse, if it can take a picture.</summary>
    private string? TreeFolderUnder(DragEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not TreeViewItem)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);

        var path = ((element as TreeViewItem)?.DataContext as TreeItemViewModel)?.FullPath;
        return CanSaveIn(path) ? path : null;
    }

    private void NavigationTree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TreeFolderUnder(e) != null && DroppedImage.CanAccept(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void NavigationTree_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var folder = TreeFolderUnder(e);
        if (folder != null) SavePicture(e, folder);
    }

    /// <summary>
    /// Takes the picture out of the drop (now: the data is only valid in the handler), then saves it in <paramref name="folder"/>
    /// (a download may take a moment). A new file is selected when its folder is the one shown.
    /// </summary>
    private async void SavePicture(DragEventArgs e, string folder)
    {
        DroppedImage.Payload? payload;
        try { payload = DroppedImage.Read(e.Data); }
        catch { payload = null; }
        if (payload == null) return;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var saved = await DroppedImage.SaveAsync(payload, folder);

            bool shown = string.Equals(SharedState.Key(folder), SharedState.Key(VM.ActiveTab.FolderPath), StringComparison.OrdinalIgnoreCase);
            if (shown)
            {
                VM.RefreshCommand.Execute(null);
                SelectInList(saved);
                FileListView.Focus();
            }
            FlashTitle($"{BaseTitle} - Saved {System.IO.Path.GetFileName(saved)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // ── The Windows menu ("More Options") ─────────────────────────────────────

    private void CtxMoreOptions_Click(object sender, RoutedEventArgs e) => ShowWindowsMenu();

    private void TreeCtxMoreOptions_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) ShowWindowsMenuForFolder(_treeMenuNode.FullPath);
    }

    /// <summary>The Windows menu of the selected items of the list, or of the shown folder when nothing is selected.</summary>
    private void ShowWindowsMenu()
    {
        if (VM.InArchive) return;   // not on the disk: Windows has no menu for it

        // On the empty part of the list (or with nothing selected) the menu is the folder's, as in Explorer.
        var paths = FileListView.SelectedItems.OfType<FileItemViewModel>().Select(i => i.FullPath).ToList();
        if (paths.Count > 0 && !_contextOverEmpty)
            ShellContextMenu.ShowFor(paths, AfterWindowsMenu);
        else if (System.IO.Directory.Exists(VM.CurrentPath))
            ShellContextMenu.ShowForBackground(VM.CurrentPath, AfterWindowsMenu);
    }

    private void ShowWindowsMenuForFolder(string folder)
    {
        if (System.IO.Directory.Exists(folder))
            ShellContextMenu.ShowFor(new[] { folder }, AfterWindowsMenu);
    }

    private bool _refreshOnActivate;

    /// <summary>
    /// The chosen command may have made, changed or removed files (7-Zip, Create shortcut, PowerRename ...), some of them a moment later
    /// in another program: the folder is read again now and once more when the window comes back to the front.
    /// </summary>
    private void AfterWindowsMenu(string? error)
    {
        if (error != null)
            MessageBox.Show(this, $"The Windows menu could not be used: {error}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);

        _refreshOnActivate = true;
        RefreshKeepingSelection();
    }

    private void RefreshKeepingSelection()
    {
        var selected = FileListView.SelectedItems.OfType<FileItemViewModel>().Select(i => i.FullPath).ToList();
        VM.RefreshCommand.Execute(null);
        ReselectInList(selected);
    }

    // ── Rename dialog ─────────────────────────────────────────────────────────

    /// <summary>Asks for a new name. The title says what it is for: a new folder (just made, with a default name), a folder or a file.</summary>
    private void ShowRenameDialog(FileItemViewModel item, bool isNew = false)
    {
        var (title, prompt) = isNew        ? ("New Folder",    "Enter a name for the new folder:")
                            : item.IsDirectory ? ("Rename Folder", "Enter new name:")
                            :                    ("Rename File",   "Enter new name:");
        // A file whose extension is not shown is renamed without it: the extension is put back (as Explorer does).
        string Real(string typed) => item.IsDirectory ? typed : ShellExtensions.RestoreExtension(item.Name, typed);
        var dlg = new InputDialog(title, prompt, item.DisplayName,
            name => FolderTabViewModel.CheckNewName(item.FullPath, Real(name))) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.InputText))
            VM.RenameItem(item, Real(dlg.InputText));
    }
}
