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
        InitPlayBar();
        InitDragOut();
        InputBindings.Add(new KeyBinding(new SwewolfFileExplorer.Commands.RelayCommand(_ => TogglePlayBar()), Key.P, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new SwewolfFileExplorer.Commands.RelayCommand(_ => { if (VM.InPlaylist) VM.ActiveTab.SavePlaylist(); }), Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new SwewolfFileExplorer.Commands.RelayCommand(_ => ToggleFilterBar()), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new SwewolfFileExplorer.Commands.RelayCommand(_ => GoToDefaultFolder()), Key.Home, ModifierKeys.Alt));   // Alt+Home: the default folder
        InputBindings.Add(new KeyBinding(new SwewolfFileExplorer.Commands.RelayCommand(_ => BookmarkSelectionOrFolder()), Key.D, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new SwewolfFileExplorer.Commands.RelayCommand(_ => GoToBookmarksNode()), Key.O, ModifierKeys.Control | ModifierKeys.Shift));   // Ctrl+Shift+O: the focus goes to the Bookmarks node

        // The height of the info panel as the user left it; the preview box follows the panel's size.
        InfoRow.Height = new GridLength(Math.Max(InfoPanelMinHeight, AppSettingsStore.Current.InfoPanelHeight));
        InfoPanel.SizeChanged += (_, _) => FitPreviewBox();
        TreeColumn.Width = _treeWidth;
        DataContext = new MainViewModel();
        VM.FolderCreated += OnFolderCreated;
        VM.SelectPathRequested += OnSelectPathRequested;
        VM.SelectPathsRequested += OnSelectPathsRequested;
        VM.ClipboardFileRequested += NewFileFromClipboard;   // Ctrl+V with a picture or text on the clipboard, and the user said yes
        VM.AddressPasteRequested += text =>   // Ctrl+V on This PC with text on the clipboard: into the address bar, no Enter
        {
            AddressBar.Focus();
            AddressBar.Text = text;
            AddressBar.CaretIndex = text.Length;
        };
        VM.BookmarksPasteRequested += PasteIntoBookmarks;    // Ctrl+V in the Bookmarks folder: bookmarks are made, nothing is copied there
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
            new ColumnInfo(SortColumn.Length,       "Length",        LengthColumn, visible: false),
            new ColumnInfo(SortColumn.Bitrate,      "Bitrate",       BitrateColumn, visible: false),
            new ColumnInfo(SortColumn.Dimensions,   "Dimensions",    DimensionsColumn, visible: false),
            new ColumnInfo(SortColumn.ProductName,  "Product Name",  ProductNameColumn, visible: false),
            new ColumnInfo(SortColumn.ProductVersion, "Product Version", ProductVersionColumn, visible: false),
            new ColumnInfo(SortColumn.Rating,       "Rating",       RatingColumn, visible: false),
            new ColumnInfo(SortColumn.Expectation,  "Expectation",   ExpectationColumn, visible: false)
        });
        VM.DisplayNamesChanged += () => { ApplyFilter(); QueueAutoFit(); };   // the extensions are shown or hidden: the filter works on the names shown, and the Name column fits them
        VM.TagsLoaded += () => { if (IsTagColumn(_sortColumn)) ApplySort(_sortColumn, _sortAscending); QueueAutoFit(); };
        VM.ItemsChangedInPlace += () => { if (_sortColumn != SortColumn.None) ApplySort(_sortColumn, _sortAscending); };   // a file changed size or date: the order may too

        VM.PropertyChanged += (_, e) =>
        {
            // An empty name means "everything changed" (another tab became active).
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainViewModel.ShowTree))
                UpdateTreePane();

            // Entering a playlist opens the Play bar (it is what a playlist is for); leaving it does not close it.
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(MainViewModel.InPlaylist) or nameof(MainViewModel.PlaylistDirty))
                UpdatePlaylistBar();

            if (e.PropertyName == nameof(MainViewModel.InPlaylist))
            {
                if (VM.InPlaylist && PlayBar.Visibility != Visibility.Visible) ShowPlayBar(true, byPlaylist: true);
                else if (!VM.InPlaylist && _playBarOpenedByPlaylist && !_playRunning) ShowPlayBar(false);   // only if the playlist opened it
            }

            // The hidden items were counted, shown or hidden: the warning about them may change.
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(MainViewModel.HiddenText) or nameof(MainViewModel.HasHiddenItems))
                QueueInfoUpdate();

            // Another folder (or another tab): the filter goes away, so a folder never looks empty because of an old filter.
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainViewModel.CurrentPath))
            {
                CloseFilterOnFolderChange();
                ApplyFolderColumns();   // the folder's own columns, or the default ones
                QueueInfoUpdate();   // what the info panel says about an archive as the folder shown follows
            }

            // The panel under the info line is hidden (the "x" far right) or shown again (the button at the right end of the info line).
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainViewModel.ShowInfoPanel))
                UpdateInfoPanelVisibility();

            // This PC shows the drives list instead of the file list (an empty name: another tab became active).
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainViewModel.IsThisPcShown))
                UpdateDrivesList();

            // The text of the list got bigger or smaller (Ctrl + wheel, or another tab with another size): so do the columns.
            if (e.PropertyName == nameof(MainViewModel.ListFontSize)) QueueAutoFit();
        };

        VM.Tabs.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Add) ResetPlayMode(); };   // a new tab: Slide Show again

        // The info panel under the list follows the items (a folder was loaded, a sort order was applied).
        ((INotifyCollectionChanged)FileListView.Items).CollectionChanged += (_, _) => { QueueInfoUpdate(); QueueAutoFit(); };

        UpdateDrivesList();   // the program starts on This PC
        UpdateInfoPanelVisibility();   // the panel was hidden in an earlier run
        LoadColumnLayout();
        ApplySort(VM.SortColumn, VM.SortAscending);

        Closing += (_, e) =>
        {
            if (VM.Tabs.Any(t => !t.ConfirmLeavePlaylist())) { e.Cancel = true; return; }   // a playlist with changes that are not saved
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
            else if (App.StartupSelectFile != null) GoToFile(App.StartupSelectFile);
        };

        // The media player is another program and marks files read while it plays: look again when the window comes back.
        Activated += (_, _) =>
        {
            if (_refreshOnActivate) { _refreshOnActivate = false; RefreshKeepingSelection(); }
            VM.RefreshReadState();
            VM.ReloadBookmarksIfChanged();   // a bookmark added, renamed or removed in Windows Explorer
            VM.SyncDrivesNow();              // a drive mounted or removed meanwhile

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

    /// <summary>The files just pasted: all selected, the last one scrolled into view, the list has the focus (so they stand out).</summary>
    private void OnSelectPathsRequested(List<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var items = VM.CurrentItems.Where(i => set.Contains(i.FullPath)).ToList();
        if (items.Count == 0) return;   // not listed (a folder with the folders hidden, a filter)

        FileListView.SelectedItems.Clear();
        foreach (var i in items) FileListView.SelectedItems.Add(i);
        FileListView.ScrollIntoView(items[0]);
        FileListView.Focus();
        if (FileListView.ItemContainerGenerator.ContainerFromItem(items[0]) is ListViewItem c) c.Focus();
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

    // ── Filter bar (the list shows only the names that contain what is typed) ──

    private string[] _filterTerms = Array.Empty<string>();

    private void FilterToggle_Click(object sender, RoutedEventArgs e) => ShowFilterBar(FilterToggle.IsChecked == true);

    private void ToggleFilterBar() => ShowFilterBar(FilterBar.Visibility != Visibility.Visible);

    /// <summary>Shows or hides the bar. Hiding it clears the filter, so no file stays hidden without the bar saying so.</summary>
    private void ShowFilterBar(bool show, bool focusList = true)
    {
        FilterToggle.IsChecked = show;
        FilterBar.Visibility   = show ? Visibility.Visible : Visibility.Collapsed;

        if (show)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
        }
        else
        {
            FilterBox.Text = "";   // clears the filter (TextChanged)
            RatingModeBox.SelectedIndex = RatingAny;   // and the rating filter
            if (focusList) FileListView.Focus();
        }
    }

    /// <summary>
    /// Alt+Home. When the folder tree has the keyboard focus its focus frame must move with the selection: it stayed on the old folder
    /// ("CD") while This PC was selected, which looked wrong.
    /// </summary>
    private void GoToDefaultFolder()
    {
        bool treeHadFocus = NavigationTree.IsKeyboardFocusWithin;
        VM.GoToDefaultFolder();
        if (!treeHadFocus) return;

        Dispatcher.BeginInvoke(() =>
        {
            var node = NavigationTree.Items.OfType<TreeItemViewModel>().FirstOrDefault(n => n.IsSelected);
            if (node != null && NavigationTree.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem item)
                item.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private string _lastFolderKey = "";

    /// <summary>
    /// The filter bar is closed (and the filter cleared) whenever another folder is shown: the user's rule, 2026-10-22, so an old filter is not
    /// forgotten and a folder does not look empty. A setting for it may come later. A re-read of the same folder (F5) keeps the filter.
    /// </summary>
    private void CloseFilterOnFolderChange()
    {
        var path = VM.CurrentPath;
        string key;
        try { key = string.IsNullOrEmpty(path) ? "" : SharedState.Key(path); }   // This PC has no path (Key throws for an empty one: Alt+Home crashed)
        catch { key = path ?? ""; }
        if (key == _lastFolderKey) return;
        _lastFolderKey = key;
        if (FilterBar.Visibility == Visibility.Visible) ShowFilterBar(false, focusList: false);
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Several words: every one of them must be somewhere in the name.
        _filterTerms = FilterBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ApplyFilter();
    }

    private void FilterClear_Click(object sender, RoutedEventArgs e)
    {
        FilterBox.Text = "";
        RatingModeBox.SelectedIndex = RatingAny;
        FilterBox.Focus();
    }

    /// <summary>
    /// Esc in the address bar: what was typed is dropped and the folder shown is back in the box, and the focus goes to the list (so the
    /// next Esc can close the filter bar). Enter navigates, as before (a key binding).
    /// </summary>
    private void AddressBar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None) return;

        VM.AddressText = VM.CurrentPath;
        FileListView.Focus();
        e.Handled = true;
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // The first Esc clears the text, the next closes the bar.
            if (FilterBox.Text.Length > 0) FilterBox.Text = "";
            else ShowFilterBar(false);
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Down)
        {
            // On to the list (the first item gets selected if nothing is).
            FileListView.Focus();
            if (FileListView.SelectedItem == null && FileListView.Items.Count > 0) FileListView.SelectedIndex = 0;
            (FileListView.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, FileListView.SelectedIndex)) as ListViewItem)?.Focus();
            e.Handled = true;
        }
    }

    /// <summary>Puts the filter on the list's view (a new view for every tab: also called when the sort is applied).</summary>
    private void ApplyFilter()
    {
        if (CollectionViewSource.GetDefaultView(FileListView.ItemsSource) is ListCollectionView view)
        {
            var terms = _filterTerms;
            int mode  = _ratingMode;
            int value = _ratingValue;

            if (terms.Length == 0 && mode == RatingAny)
            {
                view.IsLiveFiltering = false;
                view.Filter = null;
            }
            else
            {
                view.Filter = o => o is FileItemViewModel item
                                   && terms.All(t => item.DisplayName.Contains(t, StringComparison.CurrentCultureIgnoreCase))
                                   && RatingMatches(item.Rating, mode, value);

                // An item rated while the filter is on (here, in the picture viewer) comes or goes at once.
                if (mode != RatingAny)
                {
                    if (!view.LiveFilteringProperties.Contains(nameof(FileItemViewModel.Rating)))
                        view.LiveFilteringProperties.Add(nameof(FileItemViewModel.Rating));
                    view.IsLiveFiltering = true;
                }
                else
                {
                    view.IsLiveFiltering = false;
                }
            }
        }
        UpdateFilterCount();
        QueueInfoUpdate();   // "File 3/12" and the count of items follow
    }

    // The rating filter: modes are the order of the choices in RatingModeBox.
    private const int RatingAny = 0, RatingAtLeast = 1, RatingExactly = 2, RatingAtMost = 3, RatingUnrated = 4;
    private int _ratingMode = RatingAny;
    private int _ratingValue = 5;

    private static bool RatingMatches(int? rating, int mode, int value) => mode switch
    {
        RatingAtLeast => rating is { } r && r >= value,
        RatingExactly => rating is { } r && r == value,
        RatingAtMost  => rating is { } r && r <= value,   // an item with no rating is not "at most": it has none
        RatingUnrated => rating == null,
        _             => true,
    };

    private void RatingFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RatingModeBox == null || RatingValueBox == null || FileListView == null) return;   // raised while the window is being built (the list comes later in the XAML)

        _ratingMode  = Math.Max(0, RatingModeBox.SelectedIndex);
        _ratingValue = RatingValueBox.SelectedIndex + 1;
        RatingValueBox.IsEnabled = _ratingMode is RatingAtLeast or RatingExactly or RatingAtMost;
        ApplyFilter();
    }

    /// <summary>"12 of 125" while a filter (name or rating) is on.</summary>
    private void UpdateFilterCount()
    {
        bool on = _filterTerms.Length > 0 || _ratingMode != RatingAny;
        FilterCountText.Text = on ? $"{FileListView.Items.Count} of {VM.CurrentItems.Count}" : "";
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

    // The columns that need the files to be read (tags, length, bitrate, size in pixels).
    private static bool IsTagColumn(SortColumn key) =>
        key is SortColumn.Artist or SortColumn.Title or SortColumn.TrackNumber or SortColumn.Length or SortColumn.Bitrate or SortColumn.Dimensions
            or SortColumn.ProductName or SortColumn.ProductVersion;

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

        ApplyFilter();       // a new view (another tab) has no filter yet
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

        // What columns.json says is the default: the layout of every folder that has none of its own (see ApplyFolderColumns).
        _defaultColumns = _layout.Select(c => (c.Key, c.Visible)).ToList();

        // A change in the list itself (a header dragged to another place) is a change of this folder's layout.
        ((INotifyCollectionChanged)ListGrid.Columns).CollectionChanged += (_, e) =>
        {
            if (!_applyingColumns && e.Action == NotifyCollectionChangedAction.Move) SaveFolderColumns();
        };

        ApplyColumns();
    }

    /// <summary>The layout of columns.json (order and visibility); the layout of a folder without a layout of its own. Widths are fitted anyway.</summary>
    private List<(SortColumn Key, bool Visible)>? _defaultColumns;

    private bool _applyingColumns;

    /// <summary>
    /// Every folder can have its own columns (which are shown, in what order; kept in the library database). A folder without a layout of
    /// its own shows the default one. Called when another folder or tab is shown; nothing is rebuilt when the columns are the right ones.
    /// </summary>
    private void ApplyFolderColumns()
    {
        if (!VM.HasFolder || _defaultColumns == null) return;   // This PC and the like: the file list is not shown, keep what is there

        SyncLayoutFromGrid();
        var wantedShown = VM.Shared.TryGetFolderColumnsOrInherited(VM.CurrentPath, out var saved)   // its own, else the nearest parent's
            ? saved.ToList()
            : _defaultColumns.Where(c => c.Visible).Select(c => c.Key).ToList();
        if (!wantedShown.Contains(SortColumn.Name)) wantedShown.Insert(0, SortColumn.Name);   // Name is always shown

        var current = _layout.Where(c => c.Visible).Select(c => c.Key).ToList();
        if (current.SequenceEqual(wantedShown.Where(k => _layout.Any(c => c.Key == k)))) return;

        // The shown columns in the wanted order, then the others in the default order.
        var ordered = new List<ColumnInfo>();
        foreach (var key in wantedShown)
            if (_layout.FirstOrDefault(c => c.Key == key) is { } info && !ordered.Contains(info)) ordered.Add(info);
        foreach (var info in ordered) info.Visible = true;
        var rest = _layout.Where(c => !ordered.Contains(c)).ToList();
        foreach (var info in rest) info.Visible = false;
        rest = rest.OrderBy(c => _defaultColumns.FindIndex(d => d.Key == c.Key) is var i && i < 0 ? int.MaxValue : i).ToList();

        _layout.Clear();
        _layout.AddRange(ordered);
        _layout.AddRange(rest);

        _applyingColumns = true;
        try { ApplyColumns(); }
        finally { _applyingColumns = false; }
        QueueAutoFit();
    }

    /// <summary>The user changed the columns here: they are kept for this folder (and only this one).</summary>
    private void SaveFolderColumns()
    {
        if (!VM.HasFolder) return;
        SyncLayoutFromGrid();
        VM.Shared.SetFolderColumns(VM.CurrentPath, _layout.Where(c => c.Visible).Select(c => c.Key).ToList());
    }

    /// <summary>The columns the folder shown has now become the default one (columns.json), for folders with no layout of their own.</summary>
    private void SetDefaultColumns()
    {
        SyncLayoutFromGrid();
        _defaultColumns = _layout.Select(c => (c.Key, c.Visible)).ToList();
        SaveColumnLayout();
    }

    /// <summary>
    /// The columns and the sort order of the folder shown are kept for it and become those of all the folders below it: they forget their
    /// own, and (because a folder without its own takes its parent's) so do folders made later. Asks first: it replaces what they have.
    /// </summary>
    private void UseColumnsInSubfolders()
    {
        if (!VM.HasFolder || VM.InArchive || VM.InPlaylist) return;

        var answer = MessageBox.Show(this,
            "Do you want all folders below this one to show the columns and the sort order of this folder?\n\n" +
            "The columns and sort orders they have chosen for themselves will be replaced.",
            AppInfo.Name, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        SaveFolderColumns();                            // this folder keeps these columns for itself ...
        VM.SetSort(VM.SortColumn, VM.SortAscending);    // ... and this sort order
        VM.Shared.ClearSubfolderLayouts(VM.CurrentPath);   // the folders below take them from it
        FlashTitle($"{BaseTitle} - Columns and sort order set for all subfolders");
    }

    /// <summary>The folder shown goes back to the default columns.</summary>
    private void ResetFolderColumns()
    {
        if (!VM.HasFolder) return;
        VM.Shared.SetFolderColumns(VM.CurrentPath, null);
        ApplyFolderColumns();
    }

    private void SaveColumnLayout()
    {
        SyncLayoutFromGrid();

        // columns.json holds the default layout (order and visibility, until "Use These Columns as the Default" changes it) and the widths.
        var states = new List<ColumnState>();
        foreach (var (key, visible) in _defaultColumns ?? _layout.Select(c => (c.Key, c.Visible)).ToList())
            if (_layout.FirstOrDefault(c => c.Key == key) is { } info)
                states.Add(new ColumnState { Key = key.ToString(), Visible = visible, Width = info.Width });
        foreach (var info in _layout.Where(c => !states.Any(s => s.Key == c.Key.ToString())))
            states.Add(new ColumnState { Key = info.Key.ToString(), Visible = info.Visible, Width = info.Width });
        ColumnLayoutStore.Save(states);
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
                SaveFolderColumns();
                QueueAutoFit();   // a column that was just shown gets the width of its text
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

        // Each folder remembers its own columns; these two decide what a folder without columns of its own shows.
        menu.Items.Add(new Separator());
        var useDefault = new MenuItem
        {
            Header = "Forget the Columns of This Folder",
            IsEnabled = VM.HasFolder && VM.Shared.TryGetFolderColumns(VM.CurrentPath, out _),
            ToolTip = "Removes the columns this folder remembers; it then shows the columns of the nearest parent folder that has some, else the default columns"
        };
        useDefault.Click += (_, _) => ResetFolderColumns();
        menu.Items.Add(useDefault);

        var makeDefault = new MenuItem
        {
            Header = "Use These Columns as the Default",
            ToolTip = "Folders that have no columns of their own will show these columns"
        };
        makeDefault.Click += (_, _) => SetDefaultColumns();
        menu.Items.Add(makeDefault);

        var toSubfolders = new MenuItem
        {
            Header = "Use These Columns and Sort Order in All Subfolders",
            IsEnabled = VM.HasFolder && !VM.InArchive && !VM.InPlaylist,
            ToolTip = "Every folder below this one forgets its own columns and sort order and shows the ones of this folder"
        };
        toSubfolders.Click += (_, _) => UseColumnsInSubfolders();
        menu.Items.Add(toSubfolders);

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
        SaveFolderColumns();
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

    // ── Rename Tab (the text on the tab; the folder keeps its name) ───────────

    /// <summary>
    /// A right-click on a tab: a small menu made here, in code (a menu with handlers inside the tab strip's item style made the
    /// window fail to load: "Set connectionId threw an exception").
    /// </summary>
    private void TabStrip_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindItemUnder<FolderTabViewModel>(e.OriginalSource) is not { } tab) return;
        e.Handled = true;

        var rename = new MenuItem { Header = "Rename Tab...", ToolTip = "Change the text on this tab. The name of the folder stays as it is." };
        rename.Click += (_, _) => RenameTab(tab);

        var reset = new MenuItem { Header = "Reset Tab Name", ToolTip = "Show the name of the folder on this tab again", IsEnabled = tab.CustomTitle != null };
        reset.Click += (_, _) => tab.SetCustomTitle(null);

        new ContextMenu { Items = { rename, reset }, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, IsOpen = true };
    }

    /// <summary>F2 with a tab clicked (the tab strip has the focus) renames that tab, as F2 renames a file in the list.</summary>
    private void TabStrip_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2) return;
        e.Handled = true;
        RenameTab(VM.ActiveTab);
    }

    private void RenameTab(FolderTabViewModel tab)
    {
        var dlg = new InputDialog("Rename Tab", "Name of the tab:", tab.TitleForEditing,
            hint: "This only changes the text on the tab, not the name of the folder. Leave it empty to show the name of the folder again.")
            { Owner = this, Width = 520 };
        if (dlg.ShowDialog() == true) tab.SetCustomTitle(dlg.InputText);   // saved in a workspace or session with the rest
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
        if (e.Data.GetData(typeof(FolderTabViewModel)) is not FolderTabViewModel dragged)
        {
            HoverSwitchTab(e);   // files dragged over another tab: it becomes the active one, so they can be dropped there
            return;
        }
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

    private FolderTabViewModel? _hoverTab;
    private System.Windows.Threading.DispatcherTimer? _hoverTabTimer;

    /// <summary>
    /// Files or folders dragged (left or right button) over a tab that is not the active one: after a short wait that tab is made the
    /// active one. Off with Settings > Drag and Drop > Disable Internal Drag and Drop.
    /// </summary>
    private void HoverSwitchTab(DragEventArgs e)
    {
        var tab = !Helpers.AppSettingsStore.Current.DisableInternalDragDrop && e.Data.GetDataPresent(DataFormats.FileDrop)
                  ? FindItemUnder<FolderTabViewModel>(e.OriginalSource) : null;
        if (tab == null || tab == VM.ActiveTab) { StopHoverTab(); return; }
        if (tab == _hoverTab) return;

        StopHoverTab();
        _hoverTab = tab;
        _hoverTabTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _hoverTabTimer.Tick += (_, _) =>
        {
            var target = _hoverTab;
            StopHoverTab();
            if (target != null && VM.Tabs.Contains(target)) VM.ActiveTab = target;
        };
        _hoverTabTimer.Start();
    }

    private void TabStrip_DragLeave(object sender, DragEventArgs e) => StopHoverTab();

    private void StopHoverTab()
    {
        _hoverTabTimer?.Stop();
        _hoverTabTimer = null;
        _hoverTab = null;
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
        // A click on the empty part of the list gives the list the keyboard focus (it stayed in the tree after a click there, so Ctrl+V,
        // Ctrl+C, Delete ... did nothing). Not for the header or a scroll bar, which have their own handling.
        if (e.ChangedButton is MouseButton.Left or MouseButton.Right && !FileListView.IsKeyboardFocusWithin
            && FindItemUnder<FileItemViewModel>(e.OriginalSource) == null && !IsHeaderOrScrollBar(e.OriginalSource))
            FileListView.Focus();

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
        // Esc closes the filter bar (and so clears the filter) from anywhere: the file list, the tree, the tabs ... Not from the filter box,
        // whose first Esc only clears its text, and not from the address bar, which keeps its own Esc. The focus stays where it is.
        else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && FilterBar.Visibility == Visibility.Visible
                 && !FilterBox.IsKeyboardFocusWithin && !AddressBar.IsKeyboardFocusWithin)
        {
            ShowFilterBar(false, focusList: false);
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

    private bool _autoFitQueued;

    /// <summary>
    /// Sizes the columns to their text once the current burst of changes is over (a folder fills the list item by item, the tags
    /// come later). Switched on and off by <see cref="AppSettings.AutoFitColumns"/>.
    /// </summary>
    private void QueueAutoFit()
    {
        if (_autoFitQueued || !AppSettingsStore.Current.AutoFitColumns) return;
        _autoFitQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _autoFitQueued = false;
            AutoFitColumns(automatic: true);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Gives each shown column the width of its widest text (header included), growing or shrinking it. Ctrl + numeric plus does it
    /// by hand (and saves the layout); <paramref name="automatic"/> is the same after a folder was read or the font size changed:
    /// it only sets the widths (the layout is saved with the next change and when the window closes).
    /// </summary>
    private void AutoFitColumns(bool automatic = false)
    {
        if (!automatic) SyncLayoutFromGrid();
        if (!FileListView.IsLoaded) return;

        // Measured in the style of the unread files (bold and/or italic, as set in Settings): they are the widest.
        var unread = UnreadStyle.Instance;
        var typeface = new Typeface(FileListView.FontFamily,
                                    unread.Style == FontStyles.Italic ? FontStyles.Italic : FileListView.FontStyle,
                                    unread.Weight == FontWeights.Bold ? FontWeights.Bold : FileListView.FontWeight, FileListView.FontStretch);
        double dpi = VisualTreeHelper.GetDpi(FileListView).PixelsPerDip;

        double TextWidth(string text) => string.IsNullOrEmpty(text) ? 0 :
            new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
                              FileListView.FontSize, Brushes.Black, new NumberSubstitution(),
                              TextFormattingMode.Display, dpi).WidthIncludingTrailingWhitespace;

        var sizeConverter = new FileSizeConverter();
        string Cell(ColumnInfo col, FileItemViewModel item) => col.Key switch
        {
            SortColumn.Name         => item.DisplayName,
            SortColumn.DateModified => Converters.DateTimeConverter.Format(item.DateModified),
            SortColumn.DateCreated  => Converters.DateTimeConverter.Format(item.DateCreated),
            SortColumn.Type         => item.TypeName,
            SortColumn.Size         => sizeConverter.Convert(item.Size, typeof(string), null!, CultureInfo.CurrentCulture) as string ?? "",
            SortColumn.Length       => item.LengthText,
            SortColumn.Bitrate      => item.BitrateText,
            SortColumn.Dimensions   => item.DimensionsText,
            SortColumn.ProductName  => item.ProductName,
            SortColumn.ProductVersion => item.ProductVersion,
            SortColumn.Artist      => item.Artist,
            SortColumn.Title        => item.Title,
            SortColumn.TrackNumber  => item.TrackText,
            SortColumn.Rating       => item.RatingText,
            SortColumn.Expectation  => item.ExpectationText,
            _                       => ""
        };

        foreach (var col in _layout.Where(c => c.Visible))
        {
            // The header gets room for the sort arrow too, so sorting does not clip it.
            double width = TextWidth(col.Title + " ▲") + 16;

            // Only the longest texts are measured (by number of characters): a folder of thousands of files would take too long
            // otherwise, and the widest text is among the longest.
            var longest = VM.CurrentItems.Select(item => Cell(col, item)).Where(t => t.Length > 0).Distinct()
                              .OrderByDescending(t => t.Length).Take(30);
            foreach (var text in longest)
            {
                // Cell padding (a little more for the slant of italic letters); the Name cell also holds the 16 px icon and its 5 px margin,
                // the Size cell has the 11 px right margin of its template (it was cut off: "940.6 KI").
                double w = TextWidth(text) + 16 + (col.Key == SortColumn.Name ? 21 : 0) + (col.Key == SortColumn.Size ? 11 : 0);
                if (w > width) width = w;
            }

            // A very long name must not push the other columns out of the window.
            if (col.Key == SortColumn.Name) width = Math.Min(width, Math.Max(300, FileListView.ActualWidth * 0.6));

            col.Width = Math.Ceiling(width);
        }

        if (automatic)
        {
            foreach (var col in _layout.Where(c => c.Visible)) col.Column.Width = col.Width;   // no rebuild of the columns, no save
            return;
        }

        ApplyColumns();
        SaveColumnLayout();
    }

    /// <summary>The data item of the list or tab row the mouse is over, or null.</summary>
    private static bool IsHeaderOrScrollBar(object source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is GridViewColumnHeader or System.Windows.Controls.Primitives.ScrollBar) return true;
        return false;
    }

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
        {
            VM.RefreshCommand.Execute(null);
            VM.ExtensionSettingChanged();   // "Hide Extensions for Known File Types" may have changed: every tab and the button follow
            QueueInfoUpdate();              // so may the warning about hidden items that are not selected
        }
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

    /// <summary>Asks for a name (it becomes a file name), starting with <paramref name="suggestion"/>. Null if canceled.</summary>
    private string? AskName(string title, string prompt, string suggestion = "")
    {
        var dialog = new InputDialog(title, prompt, suggestion, WorkspaceStore.CheckName) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.InputText.Trim() : null;
    }

    private bool Confirm(string text) =>
        MessageBox.Show(this, text, AppInfo.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    // The workspace opened or saved last in this run: Save Workspace suggests its name again (a workspace that exists asks before it is replaced).
    private string? _lastWorkspaceName;
    private string? _lastWorkspaceFile;   // its file (also one opened from the command line, wherever it lies): "Create Shortcut to Workspace" needs it

    /// <summary>
    /// The name Save Workspace starts with: the workspace opened or saved last, else (none yet in this run) the name of the tab
    /// that is open, without what a file name cannot hold.
    /// </summary>
    private string SuggestedWorkspaceName()
    {
        var name = _lastWorkspaceName ?? VM.ActiveTab.Title ?? "";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return new string(name.Where(c => Array.IndexOf(invalid, c) < 0).ToArray()).Trim();
    }

    private void WorkspaceSave_Click(object sender, RoutedEventArgs e)
    {
        var name = AskName("Save Workspace", "Name of the workspace:", SuggestedWorkspaceName());
        if (name == null) return;

        var file = WorkspaceStore.PathFor(StoreKind.Workspace, name);
        if (System.IO.File.Exists(file) && !Confirm($"A workspace named \"{name}\" already exists. Do you want to replace it?"))
            return;

        if (WorkspaceStore.Write(file, StoreKind.Workspace, VM.CaptureState()))
        {
            _lastWorkspaceName = name;
            _lastWorkspaceFile = file;
            FlashTitle($"{BaseTitle} - Workspace saved: {name}");
        }
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
            else
            {
                _lastWorkspaceName = System.IO.Path.GetFileNameWithoutExtension(file);   // Save Workspace suggests it
                _lastWorkspaceFile = file;
            }
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
        HideBookmarkNote();   // another node: the note about the new bookmark has done its work

        // The tab selected this node itself, to follow a navigation: nothing to navigate to.
        if (VM.ConsumeProgrammaticTreeSelection(item)) return;

        // A bookmark of a file: its folder is opened and the file selected.
        if (item.Bookmark is { IsFile: true } && !string.IsNullOrEmpty(item.FullPath))
        {
            GoToFile(item.FullPath);
            return;
        }

        if (!string.IsNullOrEmpty(item.FullPath))
            VM.NavigateAsync(item.FullPath);   // read in the background: a slow folder must not freeze the window
        else
            VM.ShowNoFolder(item.IconShape);   // This PC / Quick Access: not a folder, so the list is emptied (and they have a picture of their own)
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
        if (FileListView.SelectedItems.Count > 0) HideBookmarkNote();   // another file was clicked (an emptied list, as after a navigation, is not a click)
        var selected = FileListView.SelectedItems
            .Cast<FileItemViewModel>()
            .ToList();

        VM.UpdateSelectedItems(selected);
        UpdatePlaylistMoveButtons();
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
    /// <summary>
    /// "Warning: 2 hidden items not selected" (red, next to the hidden-items link) while everything shown in the list is selected, the folder has
    /// hidden items and they are not shown: a copy or move of the selection leaves them out. Settings can turn it off.
    /// </summary>
    private void UpdateHiddenWarning()
    {
        var tab = VM.ActiveTab;
        int shown = FileListView.Items.Count;
        bool warn = AppSettingsStore.Current.WarnHiddenNotSelected
                    && tab.HiddenItemCount > 0 && !tab.HiddenItemsShown
                    && shown > 0 && FileListView.SelectedItems.Count == shown;

        HiddenWarning.Visibility = warn ? Visibility.Visible : Visibility.Collapsed;
        if (warn) HiddenWarning.Text = $"Warning: {tab.HiddenItemCount} hidden {(tab.HiddenItemCount == 1 ? "item" : "items")} not selected";
    }

    private void UpdateInfoPanel()
    {
        UpdateFilterCount();
        UpdateHiddenWarning();
        var selected = FileListView.SelectedItems;

        if (selected.Count == 1 && selected[0] is FileItemViewModel item)
        {
            int position = FileListView.Items.IndexOf(item) + 1;   // in the order the list shows
            PositionText.Text = $"{(item.IsDirectory ? "Folder" : "File")} {position}/{FileListView.Items.Count}";
            if (VM.ShowInfoPanel)   // the panel is hidden: nothing under the info line is read (the web origin is also kept in the library, so that goes on)
            {
                ShowThumbnail(item);
                ShowPlot(item);
                ShowAiInfo(item);
            }
            ShowWebOrigin(item);
            if (VM.ShowInfoPanel)
            {
                ShowGps(item);
                ShowDetails(new List<FileItemViewModel> { item });
            }
        }
        else
        {
            ShowDetails(selected.OfType<FileItemViewModel>().ToList());
            // Nothing selected: how many items the list has; several: how many are selected (the status bar no longer says it).
            var count = FileListView.Items.Count;
            // "files" while the list holds only files (the usual case); "items" as soon as a folder is in it (the Folders button, archives).
            bool onlyFiles = FileListView.Items.OfType<FileItemViewModel>().All(i => !i.IsDirectory);
            string Word(int n, bool files) => files ? (n == 1 ? "file" : "files") : (n == 1 ? "item" : "items");
            PositionText.Text = selected.Count > 1 ? $"{selected.Count} {Word(selected.Count, selected.OfType<FileItemViewModel>().All(i => !i.IsDirectory))} selected"
                              : !VM.HasFolder      ? ""   // This PC, Quick Access: nothing to count
                              :                      $"{count} {Word(count, onlyFiles)}";   // also "0 files"
            ShowThumbnail(null);
            ShowPlot(null);
            ShowAiInfo(null);
            ShowWebOrigin(null);
            ShowGps(null);
        }
    }

    private int _aiInfoVersion;

    /// <summary>
    /// The prompt, negative prompt and seed that an AI program wrote into the selected PNG (ComfyUI, Automatic1111) or MP4 (Wan2GP), to the
    /// right of the preview picture and the plot; nothing (and no space) for any other file. Read in the background; a late answer for a
    /// file no longer selected is ignored.
    /// </summary>
    private async void ShowAiInfo(FileItemViewModel? item)
    {
        int version = ++_aiInfoVersion;

        if (item == null || item.IsDirectory || item.IsInArchive || !AiInfoReader.CanHave(item.FullPath))
        {
            AiInfoArea.Visibility = Visibility.Collapsed;
            SetInfoRow(AiSeedLabel, AiSeedBox, null);
            LayoutInfoColumns();
            return;
        }

        string path = item.FullPath;
        var info = await Task.Run(() => AiInfoReader.Read(path));
        if (version != _aiInfoVersion) return;

        if (info == null)
        {
            AiInfoArea.Visibility = Visibility.Collapsed;   // a file from somewhere else: no area, no space
            SetInfoRow(AiSeedLabel, AiSeedBox, null);
            LayoutInfoColumns();
            return;
        }

        AiPromptLabel.Text = info.PromptLabel;
        AiPromptBox.Text   = info.Prompt   ?? "";
        AiNegativeBox.Text = info.Negative ?? "";
        SetInfoRow(AiSeedLabel, AiSeedBox, info.Seed);
        AiPromptBox.ScrollToHome();
        AiNegativeBox.ScrollToHome();
        AiInfoArea.Visibility = Visibility.Visible;
        LayoutInfoColumns();
    }

    // ── The map symbol (where a picture or video was taken) ───────────────────

    private int _gpsVersion;
    private GpsPosition? _gps;

    /// <summary>
    /// A picture or video that says where it was taken gets a small map symbol in the lower right corner of the info panel. Looked for in
    /// the background when the file is selected (a file that cannot hold a position is not even opened); a late answer for a file no
    /// longer selected is ignored.
    /// </summary>
    private async void ShowGps(FileItemViewModel? item)
    {
        int version = ++_gpsVersion;
        _gps = null;
        GpsButton.Visibility = Visibility.Collapsed;
        InfoRightArea.Margin = new Thickness(16, 0, 0, 0);

        if (item == null || item.IsDirectory || item.IsInArchive || !GpsReader.CanHave(item.FullPath)) return;

        string path = item.FullPath;
        var position = await Task.Run(() => GpsReader.Read(path));
        if (version != _gpsVersion || position is not { } found) return;

        _gps = found;
        InfoRightArea.Margin = new Thickness(16, 0, 40, 0);   // the map symbol sits in the lower right corner
        GpsButton.ToolTip = $"GPS coordinates: {found.Text}{"\n"}Click to show the place on Google Maps.";
        GpsButton.Visibility = Visibility.Visible;
    }

    private void GpsButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenGps();

    private void GpsOpen_Click(object sender, RoutedEventArgs e) => OpenGps();

    private void GpsCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_gps is { } position) CopyText(position.Text);
    }

    /// <summary>A map site chosen in the symbol's right-click menu (its key is the item's Tag: see <see cref="GpsPosition.UrlFor"/>).</summary>
    private void GpsSite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string site }) OpenGps(site);
    }

    private void OpenGps(string site = "google")
    {
        if (_gps is not { } position) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(position.UrlFor(site)) { UseShellExecute = true }); }
        catch { /* no browser: nothing to show */ }
    }

    // ── Right-click menu of the AI information boxes ──────────────────────────

    private static TextBox? AiBoxOf(object sender) =>
        sender is MenuItem { Parent: ContextMenu { PlacementTarget: TextBox box } } ? box : null;

    /// <summary>Copy Selected Text: what is selected, or all the text when nothing is.</summary>
    private void AiCopySelected_Click(object sender, RoutedEventArgs e)
    {
        if (AiBoxOf(sender) is not { } box) return;
        if (box.SelectionLength > 0) box.Copy();
        else CopyText(box.Text);
    }

    /// <summary>Ctrl+C in a prompt box with nothing selected copies all the text (with a selection the box does its usual copy).</summary>
    private void AiTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && sender is TextBox { SelectionLength: 0 } box)
        {
            CopyText(box.Text);
            e.Handled = true;
        }
    }

    /// <summary>Double-click on a web address of the right-hand part (Host URL, Referrer URL): open it in the default browser.</summary>
    private void WebUrlBox_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox box) OpenWebAddress(box.Text, null);
        e.Handled = true;
    }

    /// <summary>Right-click on such an address: Open in the default browser, in any installed browser, or Copy. Built in code each time (the installed browsers are looked up then).</summary>
    private void WebUrlBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TextBox box) return;
        string url = box.Text;
        bool isWeb = WebBrowsers.IsWebAddress(url);

        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open in Default Browser", IsEnabled = isWeb, ToolTip = "Double-click does the same" };
        open.Click += (_, _) => OpenWebAddress(url, null);
        menu.Items.Add(open);

        if (isWeb)
        {
            var browsers = WebBrowsers.Installed();
            if (browsers.Count > 0) menu.Items.Add(new Separator());
            foreach (var browser in browsers)
            {
                var item = new MenuItem { Header = "Open in " + browser.Name.Replace("_", "__") };
                if (IconHelper.GetIcon(browser.ExePath) is { } icon)
                    item.Icon = new Image { Source = icon, Width = 16, Height = 16 };
                var chosen = browser;
                item.Click += (_, _) => OpenWebAddress(url, chosen);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
        }

        var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C", ToolTip = "Copies the whole text" };
        copy.Click += (_, _) => CopyText(url);
        menu.Items.Add(copy);

        box.ContextMenu = menu;
    }

    private void OpenWebAddress(string url, WebBrowsers.Browser? browser)
    {
        if (!WebBrowsers.IsWebAddress(url)) return;   // only real web addresses are opened
        try
        {
            if (browser == null) WebBrowsers.OpenInDefault(url); else WebBrowsers.Open(browser, url);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The address could not be opened: " + ex.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Only the letters of a prompt, in lower case: spaces, commas, dots, dashes, digits and so on do not count.</summary>
    private static string SimplifiedPrompt(string? prompt) =>
        prompt == null ? "" : new string(prompt.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// "Select All Files with This Prompt": the files of the list (the folder shown) with the same extension as the selected file whose
    /// simplified prompt equals this one's; negative prompt and seed are not compared.
    /// </summary>
    private async void AiSelectSamePrompt_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItems.Count != 1 || FileListView.SelectedItems[0] is not FileItemViewModel current) return;

        string wanted = SimplifiedPrompt(AiPromptBox.Text);
        if (wanted.Length == 0)
        {
            MessageBox.Show(this, "This file has no prompt to compare.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string extension = System.IO.Path.GetExtension(current.Name);
        var candidates = FileListView.Items.OfType<FileItemViewModel>()
            .Where(i => !i.IsDirectory && !i.IsInArchive
                        && string.Equals(System.IO.Path.GetExtension(i.Name), extension, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Mouse.OverrideCursor = Cursors.Wait;
        List<FileItemViewModel> matches;
        try
        {
            matches = await Task.Run(() =>
            {
                var found = new List<FileItemViewModel>();
                foreach (var item in candidates)
                {
                    if (ReferenceEquals(item, current)) { found.Add(item); continue; }
                    try
                    {
                        if (SimplifiedPrompt(AiInfoReader.Read(item.FullPath)?.Prompt) == wanted) found.Add(item);
                    }
                    catch { /* an unreadable file simply does not match */ }
                }
                return found;
            });
        }
        finally { Mouse.OverrideCursor = null; }

        // The selection may have changed while the files were read: then the answer is no longer wanted.
        if (FileListView.SelectedItems.Count != 1 || !ReferenceEquals(FileListView.SelectedItems[0], current)) return;

        FileListView.SelectedItems.Clear();
        foreach (var item in matches) FileListView.SelectedItems.Add(item);
        FileListView.Focus();
    }

    private void AiCopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (AiBoxOf(sender) is { } box) CopyText(box.Text);
    }

    /// <summary>The seed is always copied whole: a part of a number is of no use, so Ctrl+C in that box takes all of it.</summary>
    private void AiSeedBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && sender is TextBox box)
        {
            CopyText(box.Text);
            e.Handled = true;
        }
    }

    private static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { /* another program holds the clipboard right now: nothing is copied */ }
    }

    /// <summary>
    /// The plot and the AI information (a Wan2GP video has both) share the room right of the picture; a column the area of which is
    /// not shown gets no width at all. Called whenever one of the two is shown or hidden.
    /// </summary>
    private void LayoutInfoColumns()
    {
        bool plot = PlotArea.Visibility == Visibility.Visible, ai = AiInfoArea.Visibility == Visibility.Visible;
        bool right = AiSeedBox.Visibility == Visibility.Visible || WebZoneBox.Visibility == Visibility.Visible
                  || WebHostBox.Visibility == Visibility.Visible || WebReferrerBox.Visibility == Visibility.Visible;

        InfoRightArea.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        PlotColumn.Width   = new GridLength(plot || (!ai && !right) ? 1 : 0, GridUnitType.Star);   // free room goes to the plot column when nothing else is shown
        AiInfoColumn.Width = new GridLength(ai ? 3 : 0, GridUnitType.Star);

        // The right-hand part: a share of the room next to the AI boxes (1.5 parts to their 3: the seed has its label above it now, so it needs
        // less width), but never narrower than 170 px or wider than 420 px.
        // (A column of width 0 must have no minimum either, or it would still take room.)
        InfoRightColumn.MinWidth = right ? 170 : 0;
        // With nothing else shown it takes all the room (long web addresses show in full; the line above it runs to the right edge).
        InfoRightColumn.MaxWidth = right && (ai || plot) ? 420 : double.PositiveInfinity;
        InfoRightColumn.Width    = new GridLength(right ? (ai ? 1.5 : (plot ? 1.5 : 1)) : 0, GridUnitType.Star);
    }

    /// <summary>A row of the right-hand part: the value in its text box (and as its tooltip), or the row hidden when there is none.</summary>
    private static void SetInfoRow(TextBlock label, TextBox box, string? value, bool openable = false)
    {
        bool has = !string.IsNullOrEmpty(value);
        label.Visibility = box.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        box.Text    = has ? value! : "";
        box.ToolTip = !has ? null
                    : openable && WebBrowsers.IsWebAddress(value) ? "Double-click or right-click to open the URL:" + Environment.NewLine + value
                    : value;
        if (has) box.ScrollToHome();
    }

    private int _webVersion;

    /// <summary>
    /// What the web says about the selected file: the zone, the address it was downloaded from and the page that linked to it, from the
    /// Mark of the Web that browsers attach to a download (<see cref="MarkOfTheWeb"/>). Read in the background when the file is selected;
    /// a file without it (most files) or a folder on the disk shows nothing. Inside an archive (or with nothing selected while an archive
    /// is the folder shown) it is the archive file's.
    /// </summary>
    private async void ShowWebOrigin(FileItemViewModel? item)
    {
        int version = ++_webVersion;

        // What to look at: the selected file itself; for a file or folder inside an archive, and when nothing (or several items) is
        // selected while the folder shown is an archive or inside one, the archive file: everything in it came with that download.
        bool wantArchive;
        string? path;
        if (item != null)
        {
            if (item.IsDirectory && !item.IsInArchive) { ClearWebRows(); return; }   // a folder on the disk: nothing to read
            path = item.FullPath;
            wantArchive = item.IsInArchive;
        }
        else
        {
            path = VM.HasFolder ? VM.CurrentPath : null;
            wantArchive = true;
        }

        if (string.IsNullOrEmpty(path)) { ClearWebRows(); return; }

        var origin = await Task.Run(() =>
        {
            if (wantArchive)
            {
                var archive = ArchiveSupport.DiskArchiveOf(path);
                return archive == null ? null : MarkOfTheWeb.Read(archive);
            }

            // The file's own Mark of the Web wins; what it does not say (or all of it, after the file lost it) comes from the library.
            // What it says is copied to the library, so it is still known when the mark is lost (a USB stick, a zip, a re-saved file).
            var web = MarkOfTheWeb.Read(path);

            // A mark that only says "about:internet" or names this computer's own server (a local program) says nothing true about the
            // internet: it is left out, Zone ID included, as if the file had no mark.
            if (web != null && (web.HostUrl != null || web.ReferrerUrl != null))
            {
                var hostOk = WebAddresses.IsUseful(web.HostUrl);
                var refOk  = WebAddresses.IsUseful(web.ReferrerUrl);
                if (!hostOk && !refOk) web = null;
                else if (!hostOk || !refOk) web = new WebOrigin(web.ZoneId, hostOk ? web.HostUrl : null, refOk ? web.ReferrerUrl : null);
            }
            if (web is { HostUrl: not null } or { ReferrerUrl: not null }) LibraryService.SetItemInfo(path, web.HostUrl, web.ReferrerUrl);

            var saved = LibraryStore.GetItemInfo(path);
            var host = web?.HostUrl ?? saved?.HostUrl;
            var referrer = web?.ReferrerUrl ?? saved?.ReferrerUrl;
            return host == web?.HostUrl && referrer == web?.ReferrerUrl ? web : new WebOrigin(web?.ZoneId, host, referrer);
        });
        if (version != _webVersion) return;

        SetInfoRow(WebZoneLabel,     WebZoneBox,     origin?.ZoneText);
        SetInfoRow(WebHostLabel,     WebHostBox,     origin?.HostUrl, openable: true);
        SetInfoRow(WebReferrerLabel, WebReferrerBox, origin?.ReferrerUrl, openable: true);
        LayoutInfoColumns();
    }

    private void ClearWebRows()
    {
        SetInfoRow(WebZoneLabel,     WebZoneBox,     null);
        SetInfoRow(WebHostLabel,     WebHostBox,     null);
        SetInfoRow(WebReferrerLabel, WebReferrerBox, null);
        LayoutInfoColumns();
    }

    private int _plotVersion;

    /// <summary>
    /// The plot from the .nfo file of the selected video or music file, to the right of the preview picture; nothing (and no
    /// space) when there is none. Read in the background; a late answer for a file no longer selected is ignored.
    /// </summary>
    private async void ShowPlot(FileItemViewModel? item)
    {
        int version = ++_plotVersion;

        if (item == null || item.IsDirectory || item.IsInArchive
            || !(MediaPlayerLauncher.IsVideo(item.FullPath) || AudioTagReader.HasTags(item.FullPath)))
        {
            _plotItem = null;
            PlotArea.Visibility = Visibility.Collapsed;   // not a file that has a plot: no area, no hint, no space
            LayoutInfoColumns();
            return;
        }

        string path = item.FullPath;
        var plot = await Task.Run(() => NfoPoster.Plot(path));
        if (version != _plotVersion) return;

        _plotItem = item;
        PlotArea.Visibility = Visibility.Visible;
        LayoutInfoColumns();

        if (plot == null)
        {
            // No plot: only a faint hint (the area is where a double-click adds one).
            PlotScroll.Visibility = Visibility.Collapsed;
            PlotHint.Visibility   = Visibility.Visible;
            return;
        }

        PlotHint.Visibility = Visibility.Collapsed;
        PlotText.Text = plot;
        PlotText.ToolTip = new ToolTip { Content = new TextBlock { Text = plot, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 } };
        PlotScroll.ScrollToTop();
        PlotScroll.Visibility = Visibility.Visible;
    }

    private FileItemViewModel? _plotItem;   // the file whose plot (or hint) is shown

    // A double-click in the plot area (also where there is only the hint) opens Edit Plot.
    private void PlotArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        e.Handled = true;
        EditPlot();
    }

    private void PlotEdit_Click(object sender, RoutedEventArgs e) => EditPlot();

    private void EditPlot()
    {
        if (_plotItem == null) return;
        var item = _plotItem;

        var window = new PlotEditWindow(item.FullPath) { Owner = this };
        if (window.ShowDialog() != true || !window.Saved) return;

        // A .nfo file may have been made: the folder is read again (the selection stays), and the plot is read again.
        var selected = FileListView.SelectedItems.OfType<FileItemViewModel>().Select(i => i.FullPath).ToList();
        VM.RefreshCommand.Execute(null);
        ReselectInList(selected);
        ShowPlot(item);
        ShowThumbnail(item);   // a poster may be named in the changed file
    }

    // Twice the width of the preview box (so it stays sharp on a high-DPI screen); the box is 160 px wide until the panel is dragged taller.
    private int ThumbnailDecodeWidth = 320;

    // ── The height of the info panel (a divider above it) ────────────────────

    private const double InfoPanelMinHeight = 141;   // 1 (top line) + 8 + 24 (the info line) + 100 (the preview box) + 8

    // ── Hiding the panel under the info line ─────────────────────────────────

    private void InfoClose_Click(object sender, RoutedEventArgs e) => VM.ShowInfoPanel = false;

    private void InfoPanelShow_Click(object sender, RoutedEventArgs e) => VM.ShowInfoPanel = true;

    /// <summary>
    /// The "x" at the far right of the panel hides everything under the info line (the details, the preview picture, the plot ...): the row
    /// is then only as high as the info line (and the Play bar when it is open), the dividers are not needed, and a small button at the
    /// right end of the info line brings the panel back with the height it had.
    /// </summary>
    private void UpdateInfoPanelVisibility()
    {
        bool on = VM.ShowInfoPanel;
        InfoBody.Visibility            = on ? Visibility.Visible : Visibility.Collapsed;
        InfoPanelShowButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        InfoDivider.Visibility         = on ? Visibility.Visible : Visibility.Collapsed;
        InfoLineDivider.Visibility     = on ? Visibility.Visible : Visibility.Collapsed;

        if (on)
        {
            InfoRow.MinHeight = InfoPanelMinHeight + _playBarHeight;
            InfoRow.Height    = new GridLength(Math.Max(InfoRow.MinHeight, AppSettingsStore.Current.InfoPanelHeight + _playBarHeight));
            FitPreviewBox();
            QueueInfoUpdate();   // what was skipped while it was hidden is read now
        }
        else
        {
            InfoRow.MinHeight = 0;
            InfoRow.Height    = GridLength.Auto;
        }
    }

    /// <summary>The preview box is as high as the panel allows and 1.6 times as wide (160 x 100 at the smallest panel).</summary>
    private void FitPreviewBox()
    {
        if (!VM.ShowInfoPanel) return;   // hidden: no picture to size
        double height = Math.Max(100, InfoPanel.ActualHeight - 17 - 24 - _playBarHeight);   // minus the line, the padding, the info line and the Play bar
        PreviewBox.Height = height;
        PreviewBox.Width  = Math.Round(height * 1.6);
        ThumbnailDecodeWidth = (int)Math.Max(320, PreviewBox.Width * 2);
    }

    private void InfoDivider_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var grid = (Grid)InfoPanel.Parent;
        double room = grid.ActualHeight - grid.RowDefinitions[0].MinHeight - grid.RowDefinitions[1].ActualHeight - grid.RowDefinitions[3].ActualHeight;
        double min = InfoPanelMinHeight + _playBarHeight;   // the Play bar, when shown, is room of its own
        double height = Math.Clamp(InfoRow.ActualHeight - e.VerticalChange, min, Math.Max(min, room));
        InfoRow.Height = new GridLength(height);
    }

    // The new height is remembered, and the preview is decoded again at the new size (it was made for the old one).
    private void InfoDivider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        FitPreviewBox();
        var settings = AppSettingsStore.Current;
        settings.InfoPanelHeight = InfoRow.Height.Value - _playBarHeight;   // without the Play bar: it is hidden when the program starts
        AppSettingsStore.Save(settings);
        UpdateInfoPanel();
    }

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

        // A Windows theme pack (a CAB with the wallpapers in it): its first picture says what the theme looks like.
        if (!item.IsInArchive && ThemePackPreview.Applies(path))
        {
            var found = await Task.Run(() => ThemePackPreview.Find(path, ThumbnailDecodeWidth));
            if (version != _thumbnailVersion) return;

            ThumbnailImage.Source = found?.Image;
            ShowPreviewFrame(false);
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
        var item      = PreviewMenuTarget();               // a video or music file: its own preview picture
        var candidate = FolderPictureCandidate();          // a picture file in this folder that the preview shows
        bool hasFolderChoice = !string.IsNullOrEmpty(VM.CurrentPath) && LibraryStore.GetFolderPicture(VM.CurrentPath) != null;

        if (item == null && candidate == null && !hasFolderChoice) { e.Handled = true; return; }   // no menu where there is nothing to choose

        Visibility V(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

        // The preview picture of a video or music file.
        var choice = item == null ? null : LibraryStore.GetPreviewChoice(item.FullPath);
        PreviewChooseItem.Visibility = V(item != null);
        PreviewNoneItem.Visibility   = V(item != null && choice is not { None: true });
        PreviewAutoItem.Visibility   = V(item != null && choice != null);

        // Save As only when there is a picture to save.
        bool save = item != null && _previewSource != null;
        PreviewSaveItem.Visibility      = V(save);
        PreviewSaveSeparator.Visibility = V(save);

        // The picture of the folder (the one under the tree).
        FolderPictureSeparator.Visibility = V(item != null && (candidate != null || hasFolderChoice));
        FolderPictureSetItem.Visibility   = V(candidate != null);
        FolderPictureAutoItem.Visibility  = V(hasFolderChoice);
    }

    /// <summary>
    /// The picture file that Set as Folder Picture would choose: the picture file selected (it shows itself), or the picture file
    /// beside a video that its preview shows (a poster next to it) when that file is in the same folder. Its name alone is kept,
    /// so a picture from inside the file, a frame of a video, or a file elsewhere cannot be chosen (no file would be made for it).
    /// Null in an archive (read-only) and when nothing like that is shown.
    /// </summary>
    private string? FolderPictureCandidate()
    {
        if (_previewItem is not { IsDirectory: false, IsInArchive: false } item || string.IsNullOrEmpty(VM.CurrentPath)) return null;

        string? file = _previewSource == null
            ? (ThumbnailLoader.IsImage(item.FullPath) ? item.FullPath : null)
            : _previewSource.File;
        if (file == null) return null;

        var folder = System.IO.Path.GetDirectoryName(file);
        return string.Equals(folder?.TrimEnd('\\', '/'), VM.CurrentPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ? file : null;
    }

    private void FolderPictureSet_Click(object sender, RoutedEventArgs e)
    {
        if (FolderPictureCandidate() is not { } file) return;

        var name = System.IO.Path.GetFileName(file);
        var folder = VM.CurrentPath;
        LibraryService.Queue(() => LibraryStore.SetFolderPicture(folder, name));
        RefreshFolderPictureSoon();
    }

    // Right-click on the folder picture under the tree: choose a picture file, none, or the automatic one again.
    private void FolderPictureMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool real   = VM.HasFolder && !VM.InArchive && System.IO.Directory.Exists(VM.CurrentPath);
        var  chosen = real ? LibraryStore.GetFolderPicture(VM.CurrentPath) : null;
        FolderPicChooseItem.IsEnabled = real;
        FolderPicNoneItem.IsEnabled   = real;
        FolderPicNoneItem.Visibility  = chosen == "" ? Visibility.Collapsed : Visibility.Visible;   // already none
        FolderPicAutoItem.Visibility  = chosen != null ? Visibility.Visible : Visibility.Collapsed;   // only when there is a choice to forget
    }

    private void FolderPictureChoose_Click(object sender, RoutedEventArgs e)
    {
        var folder = VM.CurrentPath;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title            = "Choose Folder Picture",
            Filter           = "Pictures|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.ico|All Files|*.*",
            InitialDirectory = folder,
        };
        if (dialog.ShowDialog(this) != true) return;

        if (ThumbnailLoader.Load(dialog.FileName, 300) == null)
        {
            MessageBox.Show(this, "That file is not a picture the program can show.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The name only when the picture lies in the folder itself (it then follows a rename of the folder), else its full path.
        var picture = string.Equals(System.IO.Path.GetDirectoryName(dialog.FileName)?.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetFileName(dialog.FileName)
            : dialog.FileName;
        LibraryService.Queue(() => LibraryStore.SetFolderPicture(folder, picture));
        RefreshFolderPictureSoon();
    }

    private void FolderPictureNone_Click(object sender, RoutedEventArgs e)
    {
        var folder = VM.CurrentPath;
        LibraryService.Queue(() => LibraryStore.SetFolderPicture(folder, ""));   // an empty name: no picture at all
        RefreshFolderPictureSoon();
    }

    private void FolderPictureAuto_Click(object sender, RoutedEventArgs e)
    {
        var folder = VM.CurrentPath;
        LibraryService.Queue(() => LibraryStore.SetFolderPicture(folder, null));
        RefreshFolderPictureSoon();
    }

    // The folder picture is looked for again once the choice is written (the library writes in the background, in order).
    private void RefreshFolderPictureSoon()
    {
        Task.Run(() => LibraryService.WaitForQueued()).ContinueWith(_ => Dispatcher.InvokeAsync(() => VM.RefreshFolderPicture()));
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

    // ── The drives list of This PC ────────────────────────────────────────────

    /// <summary>On This PC the drives list takes the place of the file list (they are two grids with different columns).</summary>
    private void UpdateDrivesList()
    {
        bool drives = VM.IsThisPcShown;
        DrivesListView.Visibility = drives ? Visibility.Visible : Visibility.Collapsed;
        FileListView.Visibility   = drives ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenDrive(DriveRow? drive)
    {
        if (drive != null) VM.Navigate(drive.Root);
    }

    private void DrivesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on a row opens it (not on a header or a scroll bar).
        if (ItemUnder(e.OriginalSource as DependencyObject) != null) OpenDrive(DrivesListView.SelectedItem as DriveRow);
    }

    private void DrivesListView_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DrivesListView.SelectedItem is DriveRow drive)
        {
            OpenDrive(drive);
            e.Handled = true;
        }
    }

    private static ListViewItem? ItemUnder(DependencyObject? d)
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ListViewItem item) return item;
        return null;
    }

    /// <summary>A right-click selects the drive under the mouse, as in the file list.</summary>
    private void DrivesListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource as DependencyObject) is { } item) item.IsSelected = true;
    }

    /// <summary>The menu of a drive, built in code (a menu with handlers inside a style once stopped the window from loading).</summary>
    private void DrivesListView_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource as DependencyObject)?.DataContext is not DriveRow drive) return;

        var menu = new ContextMenu();
        MenuItem Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        Add("Open", () => OpenDrive(drive)).FontWeight = FontWeights.Bold;
        Add("Open in New Tab", () => VM.OpenNewTab(drive.Root, activate: true));
        Add("Open in Windows Explorer", () => OpenInWindowsExplorer(drive.Root));
        menu.Items.Add(new Separator());
        Add("Properties", () => PropertiesHelper.Show(new[] { drive.Root }));
        Add("More Options", () => Dispatcher.BeginInvoke(() => ShowWindowsMenuForFolder(drive.Root), DispatcherPriority.Background));

        menu.PlacementTarget = DrivesListView;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void FileListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Quick clicks on a scroll bar (to page down) must not open the selected item.
        for (var d = e.OriginalSource as DependencyObject; d != null && d != FileListView; d = d is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ScrollBar)
            {
                e.Handled = true;
                return;
            }
        }

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
        // A bookmark (a shortcut in the Bookmarks folder) opens what it points to, the way this program opens it: a folder in a
        // new tab, video and music in SweWolf Media Player, a picture in the viewer.
        if (!item.IsDirectory && !item.IsInArchive && BookmarkStore.TargetOfBookmarkFile(item.FullPath, out var startMs) is { } target)
        {
            OpenBookmarkTarget(target, startMs);
            return;
        }

        if (!item.IsDirectory && ThumbnailLoader.IsImage(item.FullPath))
            OpenInViewer(item);
        else if (!TryPlayFromItem(item))
            VM.OpenItem(item);
    }

    /// <summary>Opens what a bookmark points to; <paramref name="startMs"/> is the point in a video or music file where it starts (null: the usual way).</summary>
    private void OpenBookmarkTarget(string target, long? startMs = null)
    {
        if (System.IO.Directory.Exists(target))
        {
            VM.OpenNewTab(target, activate: true);
            return;
        }

        if (!System.IO.File.Exists(target))
        {
            MessageBox.Show(this, $"\"{target}\" is not there any more. The drive may not be connected.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = System.IO.Path.GetFileName(target);
        if (ThumbnailLoader.IsImage(target))
        {
            var viewer = CreateViewer(QuickPictureList(System.IO.Path.GetDirectoryName(target) ?? "", target), target, owner: this);
            viewer.ShowDialog();
            AfterViewer(viewer);
        }
        else if (MediaPlayerLauncher.IsMedia(name))
        {
            // With a start point the saved resume point is not used or asked about.
            if (MediaPlayerLauncher.Open(target, null, startMs ?? 0, noResume: startMs.HasValue) == LaunchResult.UseDefaultProgram) FolderTabViewModel.OpenFile(target);
        }
        else
            FolderTabViewModel.OpenFile(target);
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

        var (column, ascending) = VM.Shared.TryGetFolderSortOrInherited(folder, out var saved) ? saved : (SortColumn.Name, true);
        items.Sort((x, y) => new FileItemComparer(column, ascending).Compare(x, y));

        var pictures = items.Select(i => i.FullPath).ToList();
        if (!pictures.Contains(imagePath, StringComparer.OrdinalIgnoreCase)) pictures.Insert(0, imagePath);
        return pictures;
    }

    private PictureViewerWindow CreateViewer(List<string> pictures, string current, Window? owner)
    {
        var viewer = new PictureViewerWindow(pictures, current) { IsPlaylist = VM.InPlaylist };
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
        // Pictures another program added while the viewer is open: the folder is read again at the end of the list. With a filter
        // on in the Explorer the list as filtered is kept (the viewer goes through what the list shows).
        viewer.RefreshPictures = (folder, current) =>
            FilterBar.Visibility == Visibility.Visible && FilterBox.Text.Length > 0 && !VM.InPlaylist
                ? PicturesInList()
                : QuickPictureList(folder, current);
        viewer.GetMarks  = path => VM.GetMarks(path);
        viewer.SetRating = (path, value) =>
        {
            VM.SetRating(new[] { path }, value);   // the list and the label under the tree follow
            if (value != null) ShowColumn(SortColumn.Rating);
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

    /// <summary>
    /// A file named on the command line (by SweWolf Media Player and the other sibling programs), and the bookmark of a file: its
    /// folder is opened and the file is selected and has the focus. Nothing is played or opened. A hidden file is shown (the folder's hidden items are turned on).
    /// </summary>
    private void GoToFile(string file)
    {
        var folder = System.IO.Path.GetDirectoryName(file);
        if (string.IsNullOrEmpty(folder)) return;

        VM.Navigate(folder);

        FileItemViewModel? Find() => VM.CurrentItems.FirstOrDefault(i => string.Equals(i.FullPath, file, StringComparison.OrdinalIgnoreCase));

        var item = Find();
        if (item == null)
        {
            VM.ActiveTab.ShowHiddenItems();   // a hidden file would not be listed
            item = Find();
        }
        if (item == null) return;

        FileListView.SelectedItem = item;
        FileListView.ScrollIntoView(item);
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

    private readonly TypeAhead _typeAhead = new();

    /// <summary>
    /// A letter typed in the list goes to the next item whose name starts with it (again: the one after that; several letters quickly:
    /// the first name that starts with all of them), as in Windows Explorer. The name is the one the list shows.
    /// </summary>
    private void FileListView_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var typed = e.Text;
        if (string.IsNullOrEmpty(typed) || typed.Any(char.IsControl)) return;

        var items = FileListView.Items.OfType<FileItemViewModel>().ToList();
        if (items.Count == 0) return;

        int current = FileListView.SelectedItem is FileItemViewModel sel ? items.IndexOf(sel) : -1;
        int found = _typeAhead.Next(items.Select(i => i.DisplayName).ToList(), current, typed, DateTime.UtcNow);
        e.Handled = true;
        if (found < 0) return;

        var item = items[found];
        FileListView.SelectedItem = item;   // only this one (any other selected items are let go, as in Explorer)
        FileListView.ScrollIntoView(item);
        FileListView.UpdateLayout();
        (FileListView.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem)?.Focus();   // the arrow keys go on from here
    }

    private readonly TypeAhead _treeTypeAhead = new();

    /// <summary>
    /// A letter typed in the tree goes to the next visible node whose name starts with it, as in Windows Explorer: only nodes that are
    /// already shown count (nothing is expanded by typing), children of expanded nodes included, in the order they appear.
    /// </summary>
    private void NavigationTree_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var typed = e.Text;
        if (string.IsNullOrEmpty(typed) || typed.Any(char.IsControl)) return;

        var nodes = new List<List<TreeItemViewModel>>();   // each entry: the path from a top node down to the visible node
        void Walk(IEnumerable<TreeItemViewModel> level, List<TreeItemViewModel> parents)
        {
            foreach (var node in level)
            {
                var path = new List<TreeItemViewModel>(parents) { node };
                nodes.Add(path);
                if (node.IsExpanded) Walk(node.Children, path);
            }
        }
        Walk(VM.TreeItems, new List<TreeItemViewModel>());
        if (nodes.Count == 0) return;

        int current = NavigationTree.SelectedItem is TreeItemViewModel sel ? nodes.FindIndex(p => p[^1] == sel) : -1;
        int found = _treeTypeAhead.Next(nodes.Select(p => p[^1].Name).ToList(), current, typed, DateTime.UtcNow);
        e.Handled = true;
        if (found < 0) return;

        var target = nodes[found];
        target[^1].IsSelected = true;

        // Give the node the keyboard focus (the arrow keys go on from here): walk down through the containers.
        NavigationTree.UpdateLayout();
        ItemsControl level2 = NavigationTree;
        foreach (var node in target)
        {
            if (level2.ItemContainerGenerator.ContainerFromItem(node) is not TreeViewItem container) return;
            level2 = container;
        }
        ((TreeViewItem)level2).BringIntoView();
        ((TreeViewItem)level2).Focus();
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

    // ── Tags of a folder (its desktop.ini, as in TeraTool's Set Tags) ─────────

    private void TreeCtxTags_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) EditFolderTags(_treeMenuNode.FullPath);
    }

    /// <summary>A double-click on the "Tags: ..." line under the tree edits the current folder's tags.</summary>
    private void FolderTags_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        e.Handled = true;
        EditFolderTags(VM.ActiveTab.FolderPath);
    }

    /// <summary>Asks for the tags of a folder (the box starts with the present ones; empty removes them) and writes them to its desktop.ini.</summary>
    private void EditFolderTags(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder)) return;   // This PC, a folder in an archive

        var name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (name.Length == 0) name = folder;

        var dlg = new InputDialog("Set Tags", $"Tags of \"{name}\" (separated by semicolons):", FolderTagReader.Read(folder) ?? "") { Owner = this, Width = 560 };
        if (dlg.ShowDialog() != true) return;

        if (!FolderTagWriter.TrySet(folder, dlg.InputText, out var error))
        {
            MessageBox.Show(this, $"The tags could not be saved:\n{error}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The line under the tree is for the current folder only.
        if (string.Equals(SharedState.Key(folder), SharedState.Key(VM.CurrentPath), StringComparison.OrdinalIgnoreCase))
            VM.ActiveTab.ReloadFolderTags();
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
        SaveFolderColumns();
        QueueAutoFit();
    }

    // ── Context menu ──────────────────────────────────────────────────────────

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool hasSelection = FileListView.SelectedItem != null;
        bool canPaste     = VM.PasteCommand.CanExecute(null);
        bool inArchive    = VM.InArchive;   // read-only: files can be opened and copied out, nothing else
        bool hasFolder    = VM.HasFolder;   // false on This PC and Quick Access: the list is empty and there is no folder to act on

        // Read the SendTo folder each time, so new shortcuts show up at once.
        FillSendToMenu(SendToMenu, hasSelection && !inArchive ? SendToHelper.GetEntries() : new(), SendToItem_Click);

        ShowTerminalItems(ListTerminalSeparator, TerminalItems(ListTerminalItem, ListPowerShellItem, ListCommandItem), ListTerminalFolder());
        ShowFileInfoItem(ListFileInfoItem, FileListView.SelectedItems.Count == 1 && FileListView.SelectedItem is FileItemViewModel { IsDirectory: false });

        var menu = (ContextMenu)sender;
        foreach (var mi in menu.Items.OfType<MenuItem>())
        {
            // In a playlist "Delete" takes the entries out of the list (the files stay).
            if (mi.Header is "Delete" or "Remove from Playlist") mi.Header = VM.InPlaylist ? "Remove from Playlist" : "Delete";

            // "Edit" (right under Open, as in Windows Explorer) only for one file whose type has an edit command (.bat, .cmd, pictures ...).
            if (mi.Header is "Edit")
                mi.Visibility = FileListView.SelectedItems.Count == 1 && FileListView.SelectedItem is FileItemViewModel { IsDirectory: false } editItem
                                && EditVerb.Exists(editItem.Name) ? Visibility.Visible : Visibility.Collapsed;

            // As in Windows Explorer, programs and scripts have no "Open with": it is not shown for them (it did nothing).
            if (mi.Header is "Open With...")
                mi.Visibility = FileListView.SelectedItem is FileItemViewModel { IsDirectory: false } openWithItem && !OpenWithHelper.IsNotApplicable(openWithItem.Name)
                    ? Visibility.Visible : Visibility.Collapsed;

            mi.IsEnabled = mi.Header switch
            {
                "Open"       => hasSelection,
                "Open With..." => FileListView.SelectedItems.Count == 1 && FileListView.SelectedItem is FileItemViewModel { IsDirectory: false },
                "Send to"    => hasSelection && !inArchive,
                "Add to Bookmarks" => !inArchive && (hasSelection || (hasFolder && System.IO.Directory.Exists(VM.CurrentPath))),
                "Open in Windows Explorer" => ListExplorerTarget() is { } explorerTarget
                                              && (System.IO.File.Exists(explorerTarget) || System.IO.Directory.Exists(explorerTarget)),
                "Open in New Tab" => FileListView.SelectedItems.Count == 1 && FileListView.SelectedItem is FileItemViewModel { IsDirectory: true },
                "Cut"        => hasSelection && !inArchive,
                "Copy"       => hasSelection,
                "Paste"      => canPaste,
                "Copy as Path" => hasSelection,
                "Create Shortcut..." => hasSelection && !inArchive,   // a shortcut cannot point inside a compressed file
                "Create Playlist..." => !inArchive && hasFolder && System.IO.Directory.Exists(VM.CurrentPath),
                "Mark as Read" or "Mark as Unread" => hasSelection,
                "Rating..." or "Expectation..." => hasSelection,
                "Delete" or "Remove from Playlist" => hasSelection && (!inArchive || VM.InPlaylist),
                "Rename..."  => hasSelection && !inArchive,
                "Rename All Selected Music Files and Video Files Using Smart Rename" =>
                    !inArchive && FileListView.SelectedItems.OfType<FileItemViewModel>().Any(i => !i.IsDirectory && !i.IsInArchive && SmartName.Applies(i.Name)),
                "New Folder" => !inArchive && hasFolder,
                "New Text Document..." => !inArchive && hasFolder && System.IO.Directory.Exists(VM.CurrentPath)
                                          && !FolderTabViewModel.IsUnderBookmarks(VM.CurrentPath),
                "New File from Clipboard" => !inArchive && hasFolder && System.IO.Directory.Exists(VM.CurrentPath)
                                             && !FolderTabViewModel.IsUnderBookmarks(VM.CurrentPath) && ClipboardFile.Detect() != ClipboardFile.Kind.None,
                "Properties" => !inArchive && (hasSelection || hasFolder),
                "Jump to Last Opened File" => hasFolder,
                "More Options" => !inArchive && (hasSelection || (hasFolder && System.IO.Directory.Exists(VM.CurrentPath))),
                _            => true
            };

            // Only the change that makes sense: unread files can be marked read, read files unread (both for a mixed selection).
            var files = FileListView.SelectedItems.OfType<FileItemViewModel>()
                .Where(i => !i.IsDirectory && !ArchiveSupport.IsArchiveName(i.Name)).ToList();
            if (mi.Header is "Mark as Read")   mi.Visibility = files.Count == 0 || files.Any(i => i.IsUnread)  ? Visibility.Visible : Visibility.Collapsed;
            if (mi.Header is "Mark as Unread") mi.Visibility = files.Count == 0 || files.Any(i => !i.IsUnread) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// A right-click on "Send to" offers "Edit Send To Menu": the SendTo folder (what <c>shell:sendto</c> is) opens in a new tab, where
    /// shortcuts can be added and removed. WPF does nothing with a right-click on a menu item, so this one makes the small menu itself,
    /// where the mouse is, after the big one has closed.
    /// </summary>
    private void SendToMenu_RightClick(object sender, MouseButtonEventArgs e) => EditSendToMenu(e, FileContextMenu);

    /// <summary>The same for the "Send to" of the tree's menu.</summary>
    private void TreeSendToMenu_RightClick(object sender, MouseButtonEventArgs e) => EditSendToMenu(e, TreeContextMenu);

    private void EditSendToMenu(MouseButtonEventArgs e, ContextMenu bigMenu)
    {
        e.Handled = true;
        bigMenu.IsOpen = false;

        var edit = new MenuItem { Header = "Edit Send To Menu" };
        edit.Click += (_, _) =>
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
            if (System.IO.Directory.Exists(folder)) VM.OpenNewTab(folder, activate: true);
            else MessageBox.Show(this, $"The Send To folder was not found:\n{folder}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
        };

        new ContextMenu { Items = { edit }, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, IsOpen = true };
    }

    private void FillSendToMenu(MenuItem parent, List<SendToEntry> entries, RoutedEventHandler click)
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
                FillSendToMenu(item, entry.Children, click);
            else
                item.Click += click;

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
        ReselectInList(files);   // the files that were sent stay selected
    }

    /// <summary>Send to from the tree's menu: the folder under the mouse is what is sent.</summary>
    private void TreeSendToItem_Click(object sender, RoutedEventArgs e)
    {
        var target = (string)((MenuItem)sender).Tag;
        var folder = _treeMenuNode?.FullPath;
        if (folder == null) return;

        var selected = FileListView.SelectedItems.OfType<FileItemViewModel>().Select(i => i.FullPath).ToList();

        try
        {
            SendToHelper.SendTo(target, new[] { folder });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot send to '{System.IO.Path.GetFileNameWithoutExtension(target)}': {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        VM.RefreshCommand.Execute(null);
        ReselectInList(selected);
    }

    /// <summary>Properties of the selected items, or of the current folder if nothing is selected.</summary>
    private void ShowProperties()
    {
        if (VM.InArchive) return;   // what is in an archive is not on the disk: Windows has no properties for it

        var paths = FileListView.SelectedItems.Cast<FileItemViewModel>().Select(i => i.FullPath).ToList();
        if (paths.Count == 0 && VM.HasFolder && !string.IsNullOrEmpty(VM.CurrentPath))
            paths.Add(VM.CurrentPath);
        if (paths.Count == 0) return;   // This PC: no folder, nothing to show

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

    private void CtxEdit_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is not FileItemViewModel { IsDirectory: false } file) return;

        // A file in an archive is unpacked first (an edit of the copy is lost, as for Open).
        var path = FolderTabViewModel.RealPathOf(file);
        if (path == null) return;

        try { EditVerb.Run(path); }
        catch (Exception ex)
        {
            var reason = ex is System.ComponentModel.Win32Exception w ? new System.ComponentModel.Win32Exception(w.NativeErrorCode).Message : ex.Message;
            MessageBox.Show(this, $"Cannot edit \"{file.Name}\": {reason}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
    private async void CtxOpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is not FileItemViewModel { IsDirectory: false } file) return;

        // A file in an archive is first unpacked to the temporary folder: the dialog needs a file on the disk.
        var path = FolderTabViewModel.RealPathOf(file);
        if (path == null) return;

        // The dialog is modal and would keep the menu on screen: close the menu first. The menu fades out (the first
        // version showed the dialog at once, and the half-faded menu stayed behind it), so wait until it is gone.
        if (sender is MenuItem { Parent: ContextMenu menu }) menu.IsOpen = false;
        await Task.Delay(300);

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

    /// <summary>The nearest tree node (TreeViewItem) at or above an element.</summary>
    private static TreeViewItem? TreeViewItemAbove(DependencyObject? element)
    {
        while (element != null && element is not TreeViewItem)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        return element as TreeViewItem;
    }

    /// <summary>
    /// Settings > Folder Tree: a right-click on a folder selects it first (the list opens it), as a right-click on a file selects the file.
    /// Quick Access and This PC have no menu and are left alone. With the setting off nothing happens here: the selection stays, and
    /// <see cref="NavigationTree_ContextMenuOpening"/> outlines the folder the menu is for.
    /// </summary>
    private void NavigationTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!AppSettingsStore.Current.RightClickSelectsTreeNode) return;

        var item = TreeViewItemAbove(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not TreeItemViewModel { FullPath.Length: > 0 }) return;

        if (!item.IsSelected) item.IsSelected = true;
        item.Focus();
    }

    private TreeItemViewModel? _outlinedTreeNode;

    /// <summary>Outlines the folder a menu is open on (only when a right-click does not select it); the outline goes when the menu closes.</summary>
    private void OutlineTreeNode(TreeItemViewModel? node)
    {
        ClearTreeOutline();
        if (node == null || AppSettingsStore.Current.RightClickSelectsTreeNode) return;
        node.IsContextTarget = true;
        _outlinedTreeNode = node;
    }

    private void ClearTreeOutline()
    {
        if (_outlinedTreeNode != null) _outlinedTreeNode.IsContextTarget = false;
        _outlinedTreeNode = null;
    }

    private void TreeContextMenu_Closed(object sender, RoutedEventArgs e) => ClearTreeOutline();

    private void NavigationTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _treeMenuNode = null;
        ClearTreeOutline();

        // The node under the mouse (or with the keyboard focus, for the menu key): the nearest TreeViewItem above the source.
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not TreeViewItem)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);

        // The Bookmarks part of the tree has a menu of its own (a bookmark is a shortcut: Delete and Cut must not touch the folder it points to).
        if ((element as TreeViewItem)?.DataContext is TreeItemViewModel { IsBookmarksRoot: true } or TreeItemViewModel { Bookmark: not null })
        {
            e.Handled = true;
            ShowBookmarkMenu((TreeItemViewModel)((TreeViewItem)element!).DataContext);
            return;
        }

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
            else
            {
                OutlineTreeNode(node);   // closed again by TreeContextMenu_Closed
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

    // ── Bookmarks (shortcuts in the Bookmarks folder, under the "Bookmarks" node of the tree) ──

    /// <summary>Bookmarks these folders and files (Ctrl+D: the folder shown). Says what happened in the title bar.</summary>
    private void AddBookmarks(IEnumerable<string> paths, string? inFolder = null)
    {
        var list = paths.Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (list.Count == 0)
        {
            FlashTitle($"{BaseTitle} - Open a folder first, then bookmark it");
            return;
        }

        int added = 0;
        string? lastAdded = null, lastAlready = null, lastPath = null, lastAlreadyPath = null;
        var problems = new List<string>();
        foreach (var path in list)
        {
            var entry = BookmarkStore.Add(path, out var already, out var error, inFolder);
            if (entry == null) problems.Add(error);
            else if (already)  { lastAlready = entry.Name; lastAlreadyPath = entry.Path; }
            else               { added++; lastAdded = entry.Name; lastPath = entry.Path; }
        }

        if (added > 0) VM.ReloadBookmarks();
        if (added > 0 && inFolder != null)
        {
            VM.RefreshFolder(inFolder);   // pasted into a group: the list shows the new shortcuts, the last one selected
            if (lastPath != null) OnSelectPathRequested(lastPath);
        }

        if (problems.Count > 0)
            MessageBox.Show(this, $"The bookmark could not be made:\n{problems[0]}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        else if (added == 1)
            ShowBookmarkNote($"Bookmark added: {Shorten(lastAdded!)}", lastPath, withLink: inFolder == null);
        else if (added > 1)
            ShowBookmarkNote($"{added} bookmarks added", lastPath, withLink: inFolder == null);
        else if (lastAlready != null && inFolder != null)   // pasted: a message box, a paste that seems to do nothing looks like a fault
            MessageBox.Show(this, $"\"{lastAlready}\" is bookmarked in this folder already.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
        else if (lastAlready != null)
            ShowBookmarkNote($"\"{Shorten(lastAlready)}\" is bookmarked already", lastAlreadyPath, withLink: true);
    }

    // ── The note in the info line: what Ctrl+D did, and a link to the Bookmarks ──

    private string? _bookmarkNotePath;

    private static string Shorten(string name) => name.Length > 60 ? name[..60].TrimEnd() + "..." : name;

    /// <summary>
    /// Shows <paramref name="text"/> in the info line (right of "File 2/3"), with the link "Go to Bookmarks now?" when
    /// <paramref name="withLink"/>. It stays until another folder or file is clicked (<see cref="HideBookmarkNote"/>).
    /// </summary>
    private void ShowBookmarkNote(string text, string? bookmarkPath, bool withLink)
    {
        _bookmarkNotePath = bookmarkPath;
        BookmarkNoteText.Text = text;
        BookmarkNoteLink.Inlines.Clear();
        BookmarkNoteLink.Inlines.Add(new System.Windows.Documents.Run("Go to Bookmarks now?"));
        BookmarkNoteLink.TextDecorations = TextDecorations.Underline;
        // The link is hidden by emptying the run before it and the link's own text: a Hyperlink cannot be collapsed on its own.
        if (!withLink) BookmarkNoteLink.Inlines.Clear();
        BookmarkNote.Visibility = Visibility.Visible;
    }

    private void HideBookmarkNote()
    {
        if (BookmarkNote.Visibility != Visibility.Visible) return;
        BookmarkNote.Visibility = Visibility.Collapsed;
        _bookmarkNotePath = null;
    }

    /// <summary>The link: the Bookmarks node of the tree is selected (as Ctrl+Shift+O does) and the bookmark is selected in the list.</summary>
    private void BookmarkNoteLink_Click(object sender, RoutedEventArgs e)
    {
        var path = _bookmarkNotePath;
        GoToBookmarksNode();
        if (path == null) return;

        // The Bookmarks folder is opened by the selection of its node (in the background): select the bookmark as soon as it is listed.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        int tries = 0;
        timer.Tick += (_, _) =>
        {
            bool inBookmarks = string.Equals(VM.CurrentPath.TrimEnd('\\'), BookmarkStore.Folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            var item = inBookmarks ? VM.CurrentItems.FirstOrDefault(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase)) : null;
            if (item != null)
            {
                FileListView.SelectedItem = item;
                FileListView.ScrollIntoView(item);
                timer.Stop();
            }
            else if (++tries >= 40) timer.Stop();   // 4 seconds: give up quietly
        };
        timer.Start();
    }

    private void TreeCtxBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) AddBookmarks(new[] { _treeMenuNode.FullPath });
    }

    private void CtxBookmark_Click(object sender, RoutedEventArgs e) => BookmarkSelectionOrFolder(menu: true);

    /// <summary>Ctrl+D and the list's menu: the selected files and folders of the list, or the folder shown when nothing is selected (or the list does not have the focus).</summary>
    /// <summary>
    /// Ctrl+Shift+O: the "Bookmarks" node of the tree gets the focus and is selected (so the list shows the bookmarks, as for a click).
    /// The tree is shown first if it was hidden (F8). A tab whose tree has a root of its own has no Bookmarks node: the title bar says so.
    /// </summary>
    private void GoToBookmarksNode()
    {
        var node = VM.ActiveTab.BookmarksNode;
        if (node == null)
        {
            FlashTitle($"{BaseTitle} - This tree has no Bookmarks node (Reset Tree Root brings it back)");
            return;
        }

        if (!VM.ShowTree) VM.ShowTree = true;

        // The node is the first of the tree: its container exists once the tree has been laid out (at once, or after F8 showed it).
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (NavigationTree.ItemContainerGenerator.ContainerFromItem(node) is not TreeViewItem container) return;
            if (!container.IsSelected) container.IsSelected = true;
            container.Focus();
            container.BringIntoView();
        });
    }

    private void BookmarkSelectionOrFolder(bool menu = false)
    {
        var selected = menu || FileListView.IsKeyboardFocusWithin
            ? FileListView.SelectedItems.OfType<FileItemViewModel>().Select(i => i.FullPath).ToList()
            : new List<string>();
        if (selected.Count == 0 && !VM.HasFolder) return;   // This PC: no folder shown
        AddBookmarks(selected.Count > 0 ? selected : new[] { VM.CurrentPath });
    }

    /// <summary>
    /// The right-click menu of a node in the Bookmarks part of the tree, made here in code (as the tab's menu: handlers in XAML styles
    /// broke the window's loading once). Rename and Remove change the shortcut, never the folder it points to.
    /// </summary>
    /// <summary>
    /// Removes a bookmark (or a group) with no question: its shortcut goes to the Recycle Bin, so it can be brought back, and the folder it
    /// points to is never touched. Used by the menu and by the Delete key on a bookmark of the tree.
    /// </summary>
    private void RemoveBookmark(BookmarkEntry entry)
    {
        if (BookmarkStore.Remove(entry, out var error))
        {
            VM.ReloadBookmarks();
            FlashTitle($"{BaseTitle} - Bookmark removed: {entry.Name}");
        }
        else MessageBox.Show(this, error, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowBookmarkMenu(TreeItemViewModel node)
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        OutlineTreeNode(node);
        menu.Closed += (_, _) => ClearTreeOutline();
        var entry = node.Bookmark;

        if (entry != null)
        {
            if (!entry.IsGroup && entry.Target != null && entry.Available && !entry.IsFile)
            {
                var newTab = new MenuItem { Header = "Open in New Tab" };
                newTab.Click += (_, _) => VM.OpenNewTab(entry.Target, activate: false);
                menu.Items.Add(newTab);
            }

            var rename = new MenuItem { Header = entry.IsGroup ? "Rename Group..." : "Rename Bookmark...",
                                        ToolTip = "Change the name of the bookmark. The folder it points to keeps its name." };
            rename.Click += (_, _) =>
            {
                var dlg = new InputDialog("Rename Bookmark", "Name of the bookmark:", entry.Name,
                    hint: "This only changes the name of the bookmark (its shortcut), not the name of the folder or file it points to.")
                    { Owner = this, Width = 520 };
                if (dlg.ShowDialog() != true) return;

                if (BookmarkStore.Rename(entry, dlg.InputText, out var error)) VM.ReloadBookmarks();
                else MessageBox.Show(this, error, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            };
            menu.Items.Add(rename);

            var remove = new MenuItem { Header = entry.IsGroup ? "Remove Group" : "Remove Bookmark",
                                        ToolTip = "Remove the bookmark. Its shortcut goes to the Recycle Bin; the folder it points to is not touched." };
            remove.Click += (_, _) => RemoveBookmark(entry);
            menu.Items.Add(remove);
            menu.Items.Add(new Separator());
        }
        else
        {
            var addCurrent = new MenuItem { Header = "Add This Folder to Bookmarks", InputGestureText = "Ctrl+D" };
            addCurrent.Click += (_, _) => AddBookmarks(new[] { VM.CurrentPath });
            menu.Items.Add(addCurrent);
            menu.Items.Add(new Separator());
        }

        var show = new MenuItem { Header = "Show Bookmarks Folder", ToolTip = "Open the folder where the shortcuts are kept, in a new tab" };
        show.Click += (_, _) =>
        {
            try { System.IO.Directory.CreateDirectory(BookmarkStore.Folder); } catch { /* shown as not found */ }
            VM.OpenNewTab(BookmarkStore.Folder, activate: true);
        };
        menu.Items.Add(show);

        menu.IsOpen = true;
    }

    // ── Open in Windows Explorer ──────────────────────────────────────────────

    /// <summary>
    /// Shows <paramref name="path"/> in Windows' own File Explorer: a folder is opened, a file is shown selected in its folder
    /// (<c>explorer /select,</c>). Nothing happens for a path that is not on the disk (inside a compressed file).
    /// </summary>
    private void OpenInWindowsExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;

        bool isFile   = System.IO.File.Exists(path);
        bool isFolder = !isFile && System.IO.Directory.Exists(path);
        if (!isFile && !isFolder) return;

        // The arguments are one text: a path with spaces is quoted, a drive's root ("D:\") never is (its closing backslash would
        // escape the quote).
        string arg = path.Contains(' ') ? $"\"{path}\"" : path;
        try
        {
            // Maximized (the user's wish; a setting for it may come later).
            Process.Start(new ProcessStartInfo("explorer.exe", isFile ? $"/select,{arg}" : arg)
            {
                UseShellExecute = true,
                WindowStyle     = ProcessWindowStyle.Maximized,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Windows Explorer could not be opened:\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TreeCtxExplorer_Click(object sender, RoutedEventArgs e) => OpenInWindowsExplorer(_treeMenuNode?.FullPath);

    /// <summary>From the list's menu: the selected item (one), else the folder shown.</summary>
    private string? ListExplorerTarget()
    {
        if (VM.InArchive) return null;
        if (FileListView.SelectedItems.Count == 1 && FileListView.SelectedItem is FileItemViewModel item) return item.FullPath;
        return VM.HasFolder ? VM.CurrentPath : null;
    }

    private void CtxExplorer_Click(object sender, RoutedEventArgs e) => OpenInWindowsExplorer(ListExplorerTarget());

    // ── File Info Viewer (sibling app) ───────────────────────────────────────

    /// <summary>
    /// Shows <paramref name="item"/> (only when the sibling app is found and <paramref name="show"/> says the click was on one file,
    /// never a folder) with the program's own icon.
    /// </summary>
    private static void ShowFileInfoItem(MenuItem item, bool show)
    {
        var exe = show ? FileInfoViewerLauncher.FindExe() : null;
        item.Visibility = exe != null ? Visibility.Visible : Visibility.Collapsed;
        if (exe != null && item.Icon == null && FileInfoViewerLauncher.GetIcon(exe) is { } icon)
            item.Icon = new Image { Source = icon, Width = 16, Height = 16 };
    }

    private void CtxFileInfo_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItems.Count != 1 || FileListView.SelectedItem is not FileItemViewModel { IsDirectory: false } item) return;
        var path = FolderTabViewModel.RealPathOf(item);   // a file inside an archive is unpacked to the temp folder first
        if (path != null) FileInfoViewerLauncher.Open(path);
    }

    private void TreeCtxFileInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode is { } node && System.IO.File.Exists(node.FullPath)) FileInfoViewerLauncher.Open(node.FullPath);
    }

    // ── Open in Terminal / PowerShell / Command Prompt ───────────────────────

    /// <summary>
    /// Shows the terminal items of a menu that are switched on in Settings and installed, with the program's icon; all hidden (and
    /// the separator above them) when <paramref name="folder"/> is null (not a folder on the disk).
    /// </summary>
    private static void ShowTerminalItems(Separator separator, (MenuItem Item, TerminalKind Kind, bool On)[] items, string? folder)
    {
        bool any = false;
        foreach (var (item, kind, on) in items)
        {
            bool show = folder != null && on && TerminalLauncher.IsInstalled(kind);
            item.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) continue;

            any = true;
            if (item.Icon == null && TerminalLauncher.GetIcon(kind) is { } icon)
                item.Icon = new Image { Source = icon, Width = 16, Height = 16 };
        }
        separator.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private static (MenuItem, TerminalKind, bool)[] TerminalItems(MenuItem terminal, MenuItem powerShell, MenuItem command)
    {
        var s = AppSettingsStore.Current;
        return new[]
        {
            (terminal,   TerminalKind.WindowsTerminal, s.ShowOpenInTerminal),
            (powerShell, TerminalKind.PowerShell,      s.ShowOpenInPowerShell),
            (command,    TerminalKind.CommandPrompt,   s.ShowOpenInCommandPrompt),
        };
    }

    /// <summary>
    /// Opens the terminal the menu item stands for. As the administrator (Windows asks first) when the item was right-clicked, or
    /// clicked with Shift held: no extra question and no extra items in the menu.
    /// </summary>
    private void OpenTerminal(object sender, string? folder, bool admin = false)
    {
        if (folder == null || sender is not MenuItem { Tag: string tag } || !Enum.TryParse<TerminalKind>(tag, out var kind)) return;

        try { TerminalLauncher.Open(kind, folder, admin); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == TerminalLauncher.UserDeclined)
        {
            // The user said no to Windows' question: nothing to say.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cannot open it in \"{folder}\":\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TreeCtxTerminal_Click(object sender, RoutedEventArgs e) =>
        OpenTerminal(sender, _treeMenuNode?.FullPath, admin: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));

    /// <summary>A right-click on a menu item (WPF does nothing with it by itself): the administrator's version, and the menu closes.</summary>
    private void TreeCtxTerminal_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var folder = _treeMenuNode?.FullPath;
        if (sender is MenuItem { Parent: ContextMenu menu }) menu.IsOpen = false;
        OpenTerminal(sender, folder, admin: true);
    }

    /// <summary>The folder a terminal opens in from the list's menu: the selected folder, else the folder shown (on the disk only).</summary>
    private string? ListTerminalFolder()
    {
        if (VM.InArchive) return null;
        var path = FileListView.SelectedItems.Count == 1 && FileListView.SelectedItem is FileItemViewModel { IsDirectory: true } dir
            ? dir.FullPath
            : (VM.HasFolder ? VM.CurrentPath : null);
        return !string.IsNullOrEmpty(path) && System.IO.Directory.Exists(path) ? path : null;
    }

    private void CtxTerminal_Click(object sender, RoutedEventArgs e) =>
        OpenTerminal(sender, ListTerminalFolder(), admin: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));

    private void CtxTerminal_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var folder = ListTerminalFolder();   // before the menu closes: the folder depends on what is selected
        if (sender is MenuItem { Parent: ContextMenu menu }) menu.IsOpen = false;
        OpenTerminal(sender, folder, admin: true);
    }

    private void TreeContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode == null) return;

        string path       = _treeMenuNode.FullPath;
        bool realFolder   = System.IO.Directory.Exists(path);
        ShowTerminalItems(TreeTerminalSeparator, TerminalItems(TreeTerminalItem, TreePowerShellItem, TreeCommandItem), realFolder ? path : null);
        bool canChange    = CanChangeFolder(path);   // a drive cannot be cut, deleted or renamed
        // Only for a file node of the tree (a packed file or a playlist) that lies on the disk, never a folder.
        ShowFileInfoItem(TreeFileInfoItem, (_treeMenuNode.IsArchive || _treeMenuNode.IsPlaylist) && System.IO.File.Exists(path));

        // Read the SendTo folder each time, so new shortcuts show up at once (as in the list's menu). Not for a drive.
        FillSendToMenu(TreeSendToMenu, canChange ? SendToHelper.GetEntries() : new(), TreeSendToItem_Click);

        var menu = (ContextMenu)sender;
        foreach (var mi in menu.Items.OfType<MenuItem>())
        {
            mi.IsEnabled = mi.Header switch
            {
                "Set as Tree Root"   => !(VM.HasTreeRoot && SharedState.Key(path).Equals(SharedState.Key(VM.TreeRootPath!), StringComparison.OrdinalIgnoreCase)),
                "Reset Tree Root"    => VM.HasTreeRoot,
                "Send to"    => canChange,
                "Open in Windows Explorer" => realFolder || System.IO.File.Exists(path),   // a packed file is shown selected in its folder
                "Add to Bookmarks" => realFolder,   // not inside a compressed file
                "Set Tags..." => canChange,   // a folder on the disk, not a drive
                "Cut"        => canChange,
                "Create Shortcut..." => realFolder,   // a drive too
                "Create Playlist..." => realFolder,
                "Paste"      => realFolder && FileClipboard.HasFiles(),
                "Delete"     => CanRenameOrDelete(path),
                "Rename..."  => CanRenameOrDelete(path),
                "New Folder" => realFolder,
                "New Text Document..." => realFolder && !FolderTabViewModel.IsUnderBookmarks(path),
                "New File from Clipboard" => realFolder && !FolderTabViewModel.IsUnderBookmarks(path) && ClipboardFile.Detect() != ClipboardFile.Kind.None,
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

    // ── Jump to Last Opened File (as in TeraTool's folder menu) ──────────────

    private void TreeCtxJumpLast_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) JumpToLastOpened(_treeMenuNode.FullPath);
    }

    private void CtxJumpLast_Click(object sender, RoutedEventArgs e) => JumpToLastOpened(VM.CurrentPath);

    /// <summary>
    /// Goes to the folder (if it is not the one shown) and selects the file that was opened last in it: a file opened in this
    /// program (Enter, double-click, the picture viewer, the media player started from here). Says in the title bar when
    /// there is none, or when it is gone or hidden.
    /// </summary>
    private void JumpToLastOpened(string folder)
    {
        LibraryService.WaitForQueued(1000);   // a file opened a moment ago is written first
        var name = string.IsNullOrEmpty(folder) ? null : LibraryStore.GetLastOpened(folder);
        if (name == null)
        {
            FlashTitle($"{BaseTitle} - No file has been opened in this folder yet");
            return;
        }

        if (!string.Equals(SharedState.Key(folder), SharedState.Key(VM.CurrentPath), StringComparison.OrdinalIgnoreCase))
            VM.Navigate(folder);

        var item = VM.CurrentItems.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            FlashTitle($"{BaseTitle} - \"{name}\" is not in this folder any more, or it is hidden");
            return;
        }

        FileListView.SelectedItem = item;
        FileListView.ScrollIntoView(item);
        FileListView.Focus();
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

    // ── Create Shortcut... ─────────────────────────────────────────────────────

    private void CtxMoveCopyTo_Click(object sender, RoutedEventArgs e) =>
        MoveCopyTo(FileListView.SelectedItems.OfType<FileItemViewModel>().Select(i => i.FullPath).ToList(), inArchive: VM.InArchive || VM.InPlaylist);

    private void TreeCtxMoveCopyTo_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode == null) return;
        string path = _treeMenuNode.FullPath;
        bool isDrive = System.IO.Path.GetPathRoot(path)?.TrimEnd('\\', '/').Equals(path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) == true;
        if (isDrive)
        {
            MessageBox.Show("A whole drive cannot be moved or copied.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        MoveCopyTo(new List<string> { path }, inArchive: !System.IO.Directory.Exists(path) && !System.IO.File.Exists(path));
    }

    /// <summary>
    /// Asks where to (a typed or browsed folder, or one from the user's list; Move is pre-selected) and then moves or copies the items there
    /// with the same code as Paste and drag and drop: progress window, questions about names that are taken, related files, the library follows.
    /// </summary>
    private void MoveCopyTo(List<string> items, bool inArchive)
    {
        if (inArchive)
        {
            MessageBox.Show("Move/Copy to Folder does not work inside a compressed file or a playlist. Use Copy and Paste to unpack files.",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        items = items.Where(p => System.IO.File.Exists(p) || System.IO.Directory.Exists(p)).ToList();
        if (items.Count == 0)
        {
            MessageBox.Show("Select the files or folders to move or copy first.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new MoveCopyToDialog(items) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try { System.IO.Directory.CreateDirectory(dialog.FolderPath); }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot make the folder \"{dialog.FolderPath}\": {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        VM.DropFiles(dialog.FolderPath, items, dialog.IsMove);
    }

    private void CtxCreateShortcut_Click(object sender, RoutedEventArgs e) =>
        CreateShortcutsFor(FileListView.SelectedItems.OfType<FileItemViewModel>().Where(i => !i.IsInArchive).Select(i => i.FullPath).ToList());

    private void TreeCtxCreateShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) CreateShortcutsFor(new[] { _treeMenuNode.FullPath });
    }

    /// <summary>
    /// Asks in which folder (the folder shown is suggested; Windows' own Create Shortcut always uses the item's folder) and makes a shortcut
    /// to each item there. The ones that cannot be made are listed at the end. If the folder is the one shown, the list is read again and the
    /// new shortcuts are selected.
    /// </summary>
    private void CreateShortcutsFor(IReadOnlyList<string> targets)
    {
        targets = targets.Where(t => System.IO.File.Exists(t) || System.IO.Directory.Exists(t)).ToList();
        if (targets.Count == 0) return;

        string NameOf(string t) => System.IO.Path.GetFileName(t.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : t;
        var what = targets.Count == 1 ? $"\"{NameOf(targets[0])}\"" : $"these {targets.Count} items";
        var suggested = VM.HasFolder && !VM.InArchive && System.IO.Directory.Exists(VM.CurrentPath)
            ? VM.CurrentPath
            : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        MakeShortcuts(targets.Select(t => new ShortcutSpec(t)).ToList(), what, suggested);
    }

    /// <summary>Asks in which folder, then makes the shortcuts with the questions about an existing one and about no permission (see <see cref="ShortcutMaker.CreateAll(IReadOnlyList{ShortcutSpec}, string, string, Func{string, bool}, Func{string, bool})"/>).</summary>
    private void MakeShortcuts(IReadOnlyList<ShortcutSpec> specs, string what, string suggested)
    {
        var folder = CreateShortcutToDialog.Ask(this, what, suggested);
        if (folder == null) return;
        CreateShortcutsIn(specs, folder);
    }

    /// <summary>Makes the shortcuts in <paramref name="folder"/> (overwrite and no-permission questions as for "Create Shortcut..."), selects them if that folder is shown.</summary>
    private void CreateShortcutsIn(IReadOnlyList<ShortcutSpec> specs, string folder)
    {
        var result = ShortcutMaker.CreateAll(specs, folder, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            confirmOverwrite: lnk => MessageBox.Show(this,
                $"A shortcut with this name already exists in this folder:\n\n{System.IO.Path.GetFileName(lnk)}\n\nDo you want to overwrite it?",
                "Create Shortcut", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK,
            useDesktop: inFolder => MessageBox.Show(this,
                $"You do not have permission to make a shortcut in this folder:\n\n{inFolder}\n\nDo you want to make it on the Desktop instead?",
                "Create Shortcut", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        var made = result.Made;
        var errors = result.Errors;

        if (errors.Count > 0)
            MessageBox.Show(this, "These shortcuts could not be made:\n\n" + string.Join("\n", errors.Take(8)) + (errors.Count > 8 ? $"\n... and {errors.Count - 8} more." : ""),
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);

        if (made.Count == 0) return;

        // The folder shown: the new shortcuts appear and are selected (the folder watcher would show them a moment later anyway).
        if (VM.HasFolder && !VM.InArchive && made.Any(m => string.Equals(System.IO.Path.GetDirectoryName(m)?.TrimEnd('\\'), VM.CurrentPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            VM.RefreshCommand.Execute(null);
            ReselectInList(made);
        }
        FlashTitle($"{BaseTitle} - {(made.Count == 1 ? "Shortcut made" : $"{made.Count} shortcuts made")}");
    }

    // ── Create Shortcut to Workspace / Session ────────────────────────────────

    /// <summary>The item shows only when there is a workspace (opened or saved in this run) and says which.</summary>
    private void WorkspaceRoot_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != sender) return;   // the submenus below open too
        var file = _lastWorkspaceFile;
        bool has = file != null && System.IO.File.Exists(file);
        WorkspaceShortcutItem.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (has) WorkspaceShortcutItem.Header = $"Create Shortcut to Workspace \"{System.IO.Path.GetFileNameWithoutExtension(file).Replace("_", "__")}\"...";
    }

    /// <summary>The item shows only while a session is on.</summary>
    private void SessionRoot_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != sender) return;
        var file = VM.Session.FilePath;
        bool has = VM.Session.IsActive && file != null && System.IO.File.Exists(file);
        SessionShortcutItem.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (has) SessionShortcutItem.Header = $"Create Shortcut to Session \"{(VM.Session.Name ?? "").Replace("_", "__")}\"...";
    }

    private void WorkspaceShortcut_Click(object sender, RoutedEventArgs e) => ShortcutToTabSet(_lastWorkspaceFile, "Workspace");

    private void SessionShortcut_Click(object sender, RoutedEventArgs e) => ShortcutToTabSet(VM.Session.FilePath, "Session");

    /// <summary>
    /// A shortcut that starts this program with the workspace or session file as its argument (the program opens such a file given on the
    /// command line: a session goes on being saved into it). Named "Name (Workspace)" / "Name (Session)", suggested on the Desktop.
    /// </summary>
    private void ShortcutToTabSet(string? file, string kind)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(file) || !System.IO.File.Exists(file))
        {
            MessageBox.Show(this, $"The {kind.ToLowerInvariant()} file was not found.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var name = System.IO.Path.GetFileNameWithoutExtension(file);
        var spec = new ShortcutSpec(exe, Arguments: $"\"{file}\"", Name: $"{name} ({kind})",
                                    Description: $"Opens the {kind.ToLowerInvariant()} \"{name}\" in {AppInfo.Name}");
        MakeShortcuts(new[] { spec }, $"the {kind.ToLowerInvariant()} \"{name}\"", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
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
    private static bool CanRenameOrDelete(string path) =>
        CanChangeFolder(path) || PlaylistFile.IsPlaylist(path) || IsArchiveFileOnDisk(path);   // a playlist or a zip of the tree is a file: it can be renamed and deleted too

    /// <summary>A packed file itself (D:\Photos\Trip.zip), not something inside it.</summary>
    private static bool IsArchiveFileOnDisk(string path) =>
        ArchiveSupport.IsArchiveName(path) && System.IO.File.Exists(path);

    private static bool CanChangeFolder(string path) =>
        System.IO.Directory.Exists(path)
        && !string.Equals(System.IO.Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase);

    private void RenameTreeFolder(TreeItemViewModel node)
    {
        bool archive  = IsArchiveFileOnDisk(node.FullPath);
        bool playlist = PlaylistFile.IsPlaylist(node.FullPath) || archive;   // a file: the extension has its own box
        var dlg = new InputDialog(archive ? "Rename Compressed File" : playlist ? "Rename Playlist" : "Rename Folder", "Enter new name:", node.Name,
            name => FolderTabViewModel.CheckNewName(node.FullPath, name), keepExtension: playlist, extensionReadOnly: playlist, currentName: node.Name) { Owner = this };
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

        // Delete and Ctrl+Shift+C in the tree, as in the tree's menu (the menu shows these keys).
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            // A bookmark of the tree stands for the folder it points to: Delete removes the bookmark, never that folder.
            if (NavigationTree.SelectedItem is TreeItemViewModel { Bookmark: { } bookmark })
                RemoveBookmark(bookmark);
            else if (NavigationTree.SelectedItem is TreeItemViewModel { IsBookmarksRoot: true })
            { /* the Bookmarks node itself stays */ }
            else if (NavigationTree.SelectedItem is TreeItemViewModel { FullPath.Length: > 0 } doomed && CanRenameOrDelete(doomed.FullPath))
                VM.DeleteFolder(doomed.FullPath);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (NavigationTree.SelectedItem is TreeItemViewModel { FullPath.Length: > 0 } pathNode)
                VM.CopyPathsAsText(new[] { pathNode.FullPath });
            e.Handled = true;
            return;
        }

        // Ctrl+C / Ctrl+X / Ctrl+V in the tree work on the selected folder, as the tree's menu does (Cut: not a drive or anything
        // in an archive; Paste: into the folder, if it is a real folder and the clipboard holds files).
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.C or Key.X or Key.V)
        {
            if (NavigationTree.SelectedItem is TreeItemViewModel { FullPath.Length: > 0 } selected)
            {
                if (e.Key == Key.C) VM.PutFolderOnClipboard(selected.FullPath, cut: false);
                else if (e.Key == Key.X && CanChangeFolder(selected.FullPath)) VM.PutFolderOnClipboard(selected.FullPath, cut: true);
                else if (e.Key == Key.V) VM.PasteFromKeyboard(selected.FullPath);   // says why when there is nothing to paste or nowhere to paste it
            }
            else if (e.Key == Key.V) VM.PasteFromKeyboard("");   // This PC, Quick Access: no folder: the message says so
            e.Handled = true;
            return;
        }

        if (e.Key != Key.F2) return;

        if (NavigationTree.SelectedItem is TreeItemViewModel { FullPath.Length: > 0 } node && CanRenameOrDelete(node.FullPath))
            RenameTreeFolder(node);

        e.Handled = true;
    }

    private void TreeCtxNewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) VM.CreateFolderIn(_treeMenuNode.FullPath);
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

    /// <summary>The existing files of a file drop (from Explorer, another program or this one), else null.</summary>
    private static List<string>? DroppedFiles(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] paths) return null;
            var list = paths.Where(p => System.IO.File.Exists(p) || System.IO.Directory.Exists(p)).ToList();
            return list.Count > 0 ? list : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// What a file drop does, as in Explorer: Ctrl copies, Shift moves, otherwise a move on the same drive and a copy to another.
    /// Whatever the source does not allow is changed to the other one; None for a move to the folder the files are in already.
    /// </summary>
    private DragDropEffects FileDropEffect(DragEventArgs e, string folder, List<string> files)
    {
        if (AppSettingsStore.Current.DisableInternalDragDrop) return DragDropEffects.None;   // Settings > Drag and Drop
        if (VM.ActiveTab.IsBookmarksFolder(folder))
        {
            // The Bookmarks folder only holds bookmarks: files and folders dropped there become bookmarks (nothing is copied or moved).
            // Whatever comes from the Bookmarks folder itself is not handled. The link cursor tells that a link is made.
            if (files.Any(f => VM.ActiveTab.IsBookmarksFolder(f))) return DragDropEffects.None;
            return (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link
                 : (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        }

        // Right button: the menu (Copy Here, Move Here, Create Shortcut Here) decides at the drop; show a copy cursor meanwhile.
        _rightDrag = (e.KeyStates & DragDropKeyStates.RightMouseButton) != 0 || (_rightDrag && e.KeyStates == DragDropKeyStates.None);
        if (_rightDrag)
            return (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
                 : (e.AllowedEffects & DragDropEffects.Move) != 0 ? DragDropEffects.Move
                 : (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link : DragDropEffects.None;

        bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        bool sameDrive = string.Equals(System.IO.Path.GetPathRoot(files[0]), System.IO.Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase);
        var effect = ctrl ? DragDropEffects.Copy : shift ? DragDropEffects.Move : sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;

        if ((e.AllowedEffects & effect) == 0)
            effect = effect == DragDropEffects.Move ? DragDropEffects.Copy : DragDropEffects.Move;
        if ((e.AllowedEffects & effect) == 0) return DragDropEffects.None;

        if (effect == DragDropEffects.Move)
        {
            var target = folder.TrimEnd('\\', '/');
            bool allThere = files.All(f => string.Equals(System.IO.Path.GetDirectoryName(f.TrimEnd('\\', '/'))?.TrimEnd('\\', '/'), target, StringComparison.OrdinalIgnoreCase));
            if (allThere) return DragDropEffects.None;   // moving where they are already: nothing to do
        }
        return effect;
    }

    /// <summary>The folder a drop on the list goes to: a folder row under the mouse, else the folder shown.</summary>
    private string? ListDropFolder(DragEventArgs e)
    {
        if (FindItemUnder<FileItemViewModel>(e.OriginalSource) is { IsDirectory: true, IsInArchive: false } row && CanSaveIn(row.FullPath))
            return row.FullPath;
        var shown = VM.ActiveTab.FolderPath;
        return CanSaveIn(shown) ? shown : null;
    }

    private void FileListView_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var folder = ListDropFolder(e);
        if (folder == null) { e.Effects = DragDropEffects.None; return; }
        if (DroppedImage.CanAccept(e.Data)) { e.Effects = VM.ActiveTab.IsBookmarksFolder(folder) ? DragDropEffects.None : DragDropEffects.Copy; return; }
        e.Effects = DroppedFiles(e.Data) is { } files ? FileDropEffect(e, folder, files) : DragDropEffects.None;
    }

    private bool _rightDrag;   // the drag over us is made with the right mouse button (the key state may be gone when Drop comes)

    private void FileListView_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var folder = ListDropFolder(e);
        if (folder != null) HandleDrop(e, folder);
    }

    /// <summary>
    /// The menu of a drop with the right mouse button, as in Explorer: Copy Here, Move Here, Create Shortcut Here, Cancel. The bold
    /// one is what a drop with the left button would have done (a move on the same drive, else a copy).
    /// </summary>
    private void ShowDropMenu(string folder, List<string> files, DragDropEffects allowed)
    {
        bool sameDrive = string.Equals(System.IO.Path.GetPathRoot(files[0]), System.IO.Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase);
        var target = folder.TrimEnd('\\', '/');
        bool allThere = files.All(f => string.Equals(System.IO.Path.GetDirectoryName(f.TrimEnd('\\', '/'))?.TrimEnd('\\', '/'), target, StringComparison.OrdinalIgnoreCase));
        bool canMove = (allowed & DragDropEffects.Move) != 0 && !allThere;
        bool canCopy = (allowed & DragDropEffects.Copy) != 0;
        bool canLink = (allowed & DragDropEffects.Link) != 0;
        bool moveIsDefault = sameDrive && canMove;

        MenuItem Item(string header, bool enabled, bool bold, Action action)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };
            mi.Click += (_, _) => action();
            return mi;
        }

        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, PlacementTarget = this };
        menu.Items.Add(Item("_Copy Here", canCopy, !moveIsDefault && canCopy, () => VM.DropFiles(folder, files, move: false)));
        menu.Items.Add(Item("_Move Here", canMove, moveIsDefault, () => VM.DropFiles(folder, files, move: true)));
        menu.Items.Add(Item("Create _Shortcut Here", canLink, false,
            () => CreateShortcutsIn(files.Select(f => new ShortcutSpec(f)).ToList(), folder)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Cancel", true, false, () => { }));

        // The drag came from another program (Explorer is in front): a menu in a window that is not active closes at once, so take the
        // front first and open the menu when that is done.
        try
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Dispatcher.BeginInvoke(new Action(() => menu.IsOpen = true), System.Windows.Threading.DispatcherPriority.Input);
        }
        catch (Exception ex) { LogFiles.AppendError($"Drop menu: {ex}"); }
    }

    /// <summary>A browser's picture becomes a file; files are copied or moved like a paste (after the drop has returned to the sender).</summary>
    private void HandleDrop(DragEventArgs e, string folder)
    {
        if (DroppedImage.CanAccept(e.Data))
        {
            if (!VM.ActiveTab.IsBookmarksFolder(folder)) SavePicture(e, folder);   // no picture files in the Bookmarks folder
            return;
        }

        var files = DroppedFiles(e.Data);
        if (files == null) return;
        var effect = FileDropEffect(e, folder, files);

        if (VM.ActiveTab.IsBookmarksFolder(folder))
        {
            // Dropped in the Bookmarks folder (either mouse button): bookmarks are made, after the drop has returned to the sender.
            _rightDrag = false;
            e.Effects = effect;
            if (effect == DragDropEffects.None) return;
            Dispatcher.BeginInvoke(new Action(() => BookmarkPaths(files, folder, move: false)), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }

        if (_rightDrag)
        {
            // Dropped with the right button, as in Explorer: a menu asks what to do. Nothing is done by the sender (effect None).
            _rightDrag = false;
            e.Effects = DragDropEffects.None;
            if (effect == DragDropEffects.None) return;
            var allowed = e.AllowedEffects;
            Dispatcher.BeginInvoke(new Action(() => ShowDropMenu(folder, files, allowed)), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }
        e.Effects = effect;
        if (effect == DragDropEffects.None) return;

        // The sender (Explorer ...) waits until Drop returns: dialogs and the transfer start afterwards.
        bool move = effect == DragDropEffects.Move;
        Dispatcher.BeginInvoke(new Action(() => VM.DropFiles(folder, files, move)), System.Windows.Threading.DispatcherPriority.Background);
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
        e.Handled = true;
        var folder = TreeFolderUnder(e);
        if (folder == null) { e.Effects = DragDropEffects.None; return; }
        if (DroppedImage.CanAccept(e.Data)) { e.Effects = VM.ActiveTab.IsBookmarksFolder(folder) ? DragDropEffects.None : DragDropEffects.Copy; return; }
        e.Effects = DroppedFiles(e.Data) is { } files ? FileDropEffect(e, folder, files) : DragDropEffects.None;
    }

    private void NavigationTree_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var folder = TreeFolderUnder(e);
        if (folder != null) HandleDrop(e, folder);
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

    // ── Rename all selected music and video files with smart rename, and undo ─────────────────

    private async void CtxSmartRenameAll_Click(object sender, RoutedEventArgs e)
    {
        var selected = FileListView.SelectedItems.OfType<FileItemViewModel>().ToList();
        var renamed  = await VM.ActiveTab.SmartRenameSelectedAsync(selected);
        if (renamed.Count > 0) ReselectInList(renamed);   // the renamed files stay selected
    }

    private async void UndoRename_Click(object sender, RoutedEventArgs e)
    {
        var restored = await VM.ActiveTab.UndoSmartRenameAsync();
        if (restored.Count > 0) ReselectInList(restored);
    }

    // ── Ctrl+V in the Bookmarks folder ────────────────────────────────────────

    /// <summary>
    /// A paste in the Bookmarks folder or one of its groups: a path (or files and folders) on the clipboard becomes a bookmark at
    /// once; a web address becomes an Internet shortcut after the page's title and icon were fetched (15 seconds at the most) and the
    /// name was confirmed; anything else is refused with an explanation. Nothing is ever copied into the Bookmarks folder.
    /// </summary>
    private void PasteIntoBookmarks(string folder)
    {
        var input = BookmarkPaste.Read();
        switch (input.Kind)
        {
            case BookmarkPaste.Kind.Paths:
                BookmarkPaths(input.Paths, folder, input.Cut);
                break;
            case BookmarkPaste.Kind.WebAddress:
                BookmarkWebAddress(input.Url!, folder);
                break;
            default:
                MessageBox.Show(this, BookmarkPaste.RefusedText, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                break;
        }
    }

    /// <summary>
    /// Files and folders that were pasted or dropped into the Bookmarks folder (or a group) become bookmarks; a shortcut file (.lnk, .url) is
    /// copied as it is, because a bookmark of a shortcut would be no use.
    /// </summary>
    private void BookmarkPaths(IReadOnlyList<string> paths, string folder, bool move)
    {
        var shortcuts = paths.Where(BookmarkPaste.IsShortcutFile).ToList();
        var others    = paths.Where(p => !shortcuts.Contains(p)).ToList();
        if (shortcuts.Count > 0) CopyShortcutsIntoBookmarks(shortcuts, folder, move);
        if (others.Count > 0)    AddBookmarks(others, folder);
    }

    /// <summary>
    /// Shortcut files pasted into the Bookmarks folder (or a group) are the bookmarks themselves: they are copied (a cut moves them), a
    /// name that is taken gets (2), (3), nothing is ever replaced.
    /// </summary>
    private void CopyShortcutsIntoBookmarks(List<string> shortcuts, string folder, bool move)
    {
        var done = new List<string>();
        var problems = new List<string>();

        foreach (var source in shortcuts)
        {
            try
            {
                var target = BookmarkStore.UniquePath(folder, System.IO.Path.GetFileNameWithoutExtension(source), System.IO.Path.GetExtension(source));
                if (move) System.IO.File.Move(source, target);
                else      System.IO.File.Copy(source, target, overwrite: false);
                done.Add(target);
            }
            catch (Exception ex)
            {
                problems.Add($"{System.IO.Path.GetFileName(source)}: {ex.Message}");
            }
        }

        if (done.Count > 0)
        {
            if (move) FileClipboard.Clear();   // as after any cut and paste
            VM.RefreshFolder(folder);
            OnSelectPathRequested(done[^1]);
            FlashTitle(done.Count == 1
                ? $"{BaseTitle} - Shortcut copied: {System.IO.Path.GetFileNameWithoutExtension(done[0])}"
                : $"{BaseTitle} - {done.Count} shortcuts copied");
        }

        if (problems.Count > 0)
            MessageBox.Show(this, "These shortcuts could not be copied:\n\n" + string.Join("\n", problems), AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void BookmarkWebAddress(string url, string folder)
    {
        if (BookmarkStore.FindUrlBookmark(url, folder) is { } existing)
        {
            // A message box, not only the title bar: a paste that seems to do nothing looks like a fault.
            MessageBox.Show(this, $"This web address is bookmarked in this folder already, as \"{System.IO.Path.GetFileNameWithoutExtension(existing)}\".",
                            AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // The page's title and icon: the window says what is going on, and Cancel (or 15 seconds) goes on without them.
        var info = WaitWindow.Run(this, AppInfo.Name, "Getting the title and icon of the web page...", url,
                                  ct => WebPageInfoReader.ReadAsync(url, 15, ct));

        // The suggested name, made different from a bookmark of that name that is there already.
        var suggested = BookmarkStore.SuggestUrlName(url, info?.Title);
        var name = suggested;
        for (int i = 2; System.IO.File.Exists(System.IO.Path.Combine(folder, name + ".url")); i++) name = $"{suggested} ({i})";

        var dlg = new InputDialog("New Bookmark", "Enter a name for the bookmark:", name, typed =>
        {
            var clean = BookmarkStore.CleanName(typed);
            if (clean.Length == 0) return "Type a name.";
            return System.IO.File.Exists(System.IO.Path.Combine(folder, clean + ".url"))
                ? $"There is a bookmark named \"{clean}\" already. Choose a different name." : null;
        }, hint: url) { Owner = this, Width = 570 };   // 50 % wider than the usual 380: the title of a page is often long
        if (dlg.ShowDialog() != true) return;

        var path = BookmarkStore.AddUrl(url, dlg.InputText, info?.IconIco, folder, out var error);
        if (path == null)
        {
            MessageBox.Show(this, $"The bookmark could not be made:\n{error}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        VM.RefreshFolder(folder);
        OnSelectPathRequested(path);
        FlashTitle($"{BaseTitle} - Bookmark added: {System.IO.Path.GetFileNameWithoutExtension(path)}");
    }

    // ── New File from Clipboard ───────────────────────────────────────────────

    /// <summary>
    /// Makes a file of the clipboard's text (.txt) or picture (.png) in <paramref name="folder"/> at once, with a name that has the date
    /// and time, then asks for the name. OK renames it if the name was changed; Cancel sends the file to the Recycle Bin.
    /// </summary>
    private void NewFileFromClipboard(string folder)
    {
        if (!CanSaveIn(folder)) return;
        if (FolderTabViewModel.IsUnderBookmarks(folder))   // the Bookmarks folder only holds bookmarks
        {
            MessageBox.Show(this, BookmarkPaste.RefusedText, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var path = ClipboardFile.Create(folder, out var error);
        if (path == null)
        {
            MessageBox.Show(this, $"Cannot create the file: {error}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var name = System.IO.Path.GetFileName(path);
        string Real(string typed) => ShellExtensions.RestoreExtension(name, typed);   // a hidden extension is put back, as in Rename

        var shown = ShellExtensions.DisplayName(name);
        var dlg = new InputDialog("New File from Clipboard", "Enter a name for the new file:", shown,
            typed => FolderTabViewModel.CheckNewName(path, Real(typed)),
            keepExtension: shown == name) { Owner = this };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.InputText))
        {
            var finalPath = System.IO.Path.Combine(folder, Real(dlg.InputText).Trim());
            try
            {
                if (!string.Equals(finalPath, path, StringComparison.Ordinal))
                    System.IO.File.Move(path, finalPath);
                path = finalPath;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Cannot rename the file: {ex.Message}\n\nIt keeps the name {name}.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            VM.RefreshFolder(folder);
            OnSelectPathRequested(path);   // the new file is selected (when its folder is the one shown)
        }
        else
        {
            // Cancel: the file goes to the Recycle Bin again (nothing of the user's was in it).
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex) { LogFiles.AppendError($"New file from clipboard: could not recycle {path}: {ex.Message}"); }
            VM.RefreshFolder(folder);
        }
    }

    // ── New Text Document ─────────────────────────────────────────────────────

    /// <summary>
    /// Asks for a name (suggested: New Text Document.txt, numbered when taken) and makes an empty text file in <paramref name="folder"/>.
    /// Nothing is made until OK; Cancel makes no file.
    /// </summary>
    private void NewTextDocument(string folder)
    {
        if (!CanSaveIn(folder)) return;
        if (FolderTabViewModel.IsUnderBookmarks(folder))   // the Bookmarks folder only holds bookmarks
        {
            MessageBox.Show(this, BookmarkPaste.RefusedText, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        const string baseName = "New Text Document";
        var name = baseName + ".txt";
        for (int n = 2; System.IO.File.Exists(System.IO.Path.Combine(folder, name)) || System.IO.Directory.Exists(System.IO.Path.Combine(folder, name)); n++)
            name = $"{baseName} ({n}).txt";

        string Real(string typed) => ShellExtensions.RestoreExtension(name, typed);   // a hidden extension is put back, as in Rename

        var shown = ShellExtensions.DisplayName(name);
        var dummy = System.IO.Path.Combine(folder, "\0");   // CheckNewName compares with an existing item: there is none yet
        var dlg = new InputDialog("New Text Document", "Enter a name for the new file:", shown,
            typed => FolderTabViewModel.CheckNewName(dummy, Real(typed)),
            keepExtension: shown == name) { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.InputText)) return;

        var path = System.IO.Path.Combine(folder, Real(dlg.InputText).Trim());
        try
        {
            using (new System.IO.FileStream(path, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None)) { }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cannot create the file: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        VM.RefreshFolder(folder);
        OnSelectPathRequested(path);   // the new file is selected (when its folder is the one shown)
    }

    private void CtxNewTextDocument_Click(object sender, RoutedEventArgs e) => NewTextDocument(VM.CurrentPath);

    private void TreeCtxNewTextDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) NewTextDocument(_treeMenuNode.FullPath);
    }

    private void CtxNewFileFromClipboard_Click(object sender, RoutedEventArgs e) => NewFileFromClipboard(VM.CurrentPath);

    private void TreeCtxNewFileFromClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (_treeMenuNode != null) NewFileFromClipboard(_treeMenuNode.FullPath);
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
        // Smart name: only for a music or video file on the disk (not new, not in an archive), and only when it has a better name.
        var smartApplies = !isNew && !item.IsDirectory && !item.IsInArchive && SmartName.Applies(item.Name);
        var smart = smartApplies ? ShellExtensions.DisplayName(SmartName.Suggest(item.FullPath) ?? item.Name) : null;   // no better name: the present one; without a hidden extension, as the box
        var dlg = new InputDialog(title, prompt, item.DisplayName,
            name => FolderTabViewModel.CheckNewName(item.FullPath, Real(name)),
            caseButtons: !isNew, keepExtension: !item.IsDirectory && item.DisplayName == item.Name, smartName: smart, smartApplies: smartApplies, currentName: isNew ? null : item.Name) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.InputText))
        {
            VM.RenameItem(item, Real(dlg.InputText));

            // A video renamed with Use Smart Name: offer to move it to its place in the Videos folder (if the setting is on).
            if (dlg.SmartNameChecked && !item.IsDirectory)
            {
                var newPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.FullPath)!, Real(dlg.InputText).Trim());
                bool samePath = string.Equals(newPath, item.FullPath, StringComparison.OrdinalIgnoreCase);   // already named so: just the move
                if (System.IO.File.Exists(newPath) && (samePath || !System.IO.File.Exists(item.FullPath)))   // the rename did happen
                    VM.OfferMoveAfterSmartRename(newPath);
            }
        }
        else if (isNew)
            VM.ActiveTab.FolderCreationCanceled = true;   // Cancel: the new folder is removed again if it is still empty
    }
}
