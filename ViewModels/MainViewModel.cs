using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using SwewolfFileExplorer.Commands;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.ViewModels;

/// <summary>
/// The window's view model: the list of tabs and the active one. What the window binds to
/// (CurrentPath, CurrentItems, TreeItems, the commands, ...) is the active tab's.
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private readonly SharedState _shared = new();

    /// <summary>What all tabs share (tree sort orders, hidden folders shown in the tree).</summary>
    public SharedState Shared => _shared;
    private FolderTabViewModel _activeTab;

    public ObservableCollection<FolderTabViewModel> Tabs { get; } = new();

    /// <summary>Remembers the open tabs all the time, when a session is started or opened.</summary>
    public SessionRecorder Session { get; }

    public MainViewModel()
    {
        Session = new SessionRecorder(this);
        Tabs.CollectionChanged += (_, _) => Session.TouchStructure();

        _activeTab = AddTab(null);   // starts on This PC
        _activeTab.SetActive(true);

        // A new tab starts on the default node, This PC (a setting for that may come later), not in the folder of the active tab.
        NewTabCommand      = new RelayCommand(_ => OpenNewTab("", activate: true));
        CloseTabCommand    = new RelayCommand(p => CloseTab(p as FolderTabViewModel ?? ActiveTab));
        NextTabCommand     = new RelayCommand(_ => SwitchTab(+1), _ => Tabs.Count > 1);
        PreviousTabCommand = new RelayCommand(_ => SwitchTab(-1), _ => Tabs.Count > 1);
        ReopenTabCommand   = new RelayCommand(_ => ReopenTab(),   _ => _closedTabs.Count > 0);
        ToggleTreeCommand  = new RelayCommand(_ => ShowTree = !ShowTree);
        HideFolderPictureCommand = new RelayCommand(_ => ShowFolderPicture = false);
        ShowFolderPictureCommand = new RelayCommand(_ => ShowFolderPicture = true);

        FolderTabViewModel.DrivesRefreshRequested += SyncDrivesNow;
        StartDriveWatch();
    }

    // ── Drives that come and go (USB sticks, VeraCrypt volumes ...) ──────────

    private System.Windows.Threading.DispatcherTimer? _driveTimer;
    private string _driveLetters = string.Join("", Directory.GetLogicalDrives());
    private int _driveRetries;
    private bool _driveReading;

    /// <summary>
    /// Looks every 2 seconds at the list of drive letters (a cheap call that touches no disk); when it changed, and for a few rounds after
    /// that while a drive is not ready yet (a volume still being mounted), the drives are read in the background and every tab's This PC
    /// is brought in line. F5 and the window coming to the front do it at once.
    /// </summary>
    private void StartDriveWatch()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        _driveTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(2), System.Windows.Threading.DispatcherPriority.Background,
                                                                   (_, _) => DriveTimerTick(), dispatcher);
        _driveTimer.Start();
    }

    private void DriveTimerTick()
    {
        string letters;
        try { letters = string.Join("", Directory.GetLogicalDrives()); } catch { return; }
        if (letters != _driveLetters) { _driveLetters = letters; _driveRetries = 6; SyncDrivesNow(); }
        else if (_driveRetries > 0) { _driveRetries--; SyncDrivesNow(); }
    }

    /// <summary>Reads the drives in the background and brings the This PC node of every tab in line with them.</summary>
    public void SyncDrivesNow()
    {
        if (_driveReading) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        _driveReading = true;

        _ = Task.Run(() =>
        {
            (List<FolderTabViewModel.DriveEntry> Drives, bool AnyNotReady) read;
            try { read = FolderTabViewModel.ReadDrives(); }
            catch { dispatcher.InvokeAsync(() => _driveReading = false); return; }

            dispatcher.InvokeAsync(() =>
            {
                _driveReading = false;
                foreach (var tab in Tabs) tab.SyncDrives(read.Drives);
                if (!read.AnyNotReady) _driveRetries = 0;   // everything is ready: nothing to wait for
            });
        });
    }

    public RelayCommand HideFolderPictureCommand { get; }
    public RelayCommand ShowFolderPictureCommand { get; }

    /// <summary>The picture of the folder under the tree is shown (saved in the settings; the "x" on it hides it).</summary>
    public bool ShowFolderPicture
    {
        get => _shared.Settings.ShowFolderPicture;
        set
        {
            if (_shared.Settings.ShowFolderPicture == value) return;
            _shared.Settings.ShowFolderPicture = value;
            AppSettingsStore.Save(_shared.Settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowFolderInfoRow));
        }
    }

    /// <summary>The line under the tree with the status: there while it has a text, and while the picture is hidden (the button that brings it back is in it).</summary>
    public bool ShowFolderInfoRow => !string.IsNullOrEmpty(FolderStatusLine) || !ShowFolderPicture;

    /// <summary>"Status: Unread / Partly Read / Read", or "Empty Folder".</summary>
    public string FolderStatusLine => _activeTab.FolderStatusLine;

    public RelayCommand ToggleTreeCommand { get; }

    // ── Tabs ─────────────────────────────────────────────────────────────────

    public RelayCommand NewTabCommand      { get; }
    public RelayCommand CloseTabCommand    { get; }
    public RelayCommand NextTabCommand     { get; }
    public RelayCommand PreviousTabCommand { get; }
    public RelayCommand ReopenTabCommand   { get; }

    // Closed tabs are kept whole (history, sort, tree), most recent last, so Ctrl+Shift+T can bring one back.
    private const int MaxClosedTabs = 20;
    private readonly List<(FolderTabViewModel Tab, int Index)> _closedTabs = new();

    /// <summary>Raised just before and after another tab becomes the active one.</summary>
    public event Action? ActiveTabChanging;
    public event Action? ActiveTabChanged;

    /// <summary>Raised when the last tab is closed: the window closes with it, as in Explorer.</summary>
    public event Action? LastTabClosed;

    public FolderTabViewModel ActiveTab
    {
        get => _activeTab;
        set
        {
            if (value == null || value == _activeTab) return;

            ActiveTabChanging?.Invoke();
            _activeTab.SetActive(false);   // the folder of the tab we leave is no longer watched
            _activeTab = value;

            // Every binding (address bar, list, tree, commands) now reads the new tab.
            OnPropertyChanged(string.Empty);
            _activeTab.RaiseAllCanExecuteChanged();
            ActiveTabChanged?.Invoke();

            _activeTab.SetActive(true);           // watches its folder and brings its list up to date
            _activeTab.RaisePendingSelection();   // a tab restored from a workspace or session selects its file now
            Session.TouchStructure();             // which tab is active is saved too
        }
    }

    private FolderTabViewModel AddTab(string? path)
    {
        var tab = new FolderTabViewModel(_shared, path);
        tab.PropertyChanged += (s, e) =>
        {
            // Only what the active tab shows is of interest to the window.
            if (s == _activeTab) OnPropertyChanged(e.PropertyName);
            if (s == _activeTab && e.PropertyName == nameof(FolderTabViewModel.FolderStatusLine)) OnPropertyChanged(nameof(ShowFolderInfoRow));

            // A session follows every tab: the folder it shows (the title changes when a navigation succeeded) and its root.
            if (e.PropertyName is nameof(FolderTabViewModel.Title) or nameof(FolderTabViewModel.CustomTitle) or nameof(FolderTabViewModel.HasTreeRoot))
                Session.TouchStructure();
        };
        tab.SelectionRemembered += Session.TouchSelection;
        tab.SortChanged    += () => { if (tab == _activeTab) SortChanged?.Invoke(); };
        tab.FolderCreated  += item => { if (tab == _activeTab) FolderCreated?.Invoke(item); };
        tab.TagsLoaded += () => { if (tab == _activeTab) TagsLoaded?.Invoke(); };
        tab.ItemsChangedInPlace += () => { if (tab == _activeTab) ItemsChangedInPlace?.Invoke(); };
        tab.SelectPathRequested += path => { if (tab == _activeTab) SelectPathRequested?.Invoke(path); };
        tab.ClipboardFileRequested += folder => { if (tab == _activeTab) ClipboardFileRequested?.Invoke(folder); };
        tab.BookmarksPasteRequested += folder => { if (tab == _activeTab) BookmarksPasteRequested?.Invoke(folder); };
        // The tree sort order is shared: the other tabs' trees show the same folder in the same order.
        tab.TreeSortChanged += path => { foreach (var t in Tabs) t.ResortTree(path); };
        // So is whether the tree shows the hidden folders of a folder.
        tab.TreeHiddenChanged += path => { foreach (var t in Tabs) t.SyncTreeHidden(path); };
        Tabs.Add(tab);
        return tab;
    }

    /// <summary>Opens <paramref name="path"/> in a new tab, furthest to the right (as browsers do; a setting may come later).</summary>
    public void OpenNewTab(string path, bool activate)
    {
        var tab = AddTab(string.IsNullOrWhiteSpace(path) ? null : path);   // no folder: a tab on This PC; AddTab puts it last
        if (activate) ActiveTab = tab;

        NextTabCommand.RaiseCanExecuteChanged();
        PreviousTabCommand.RaiseCanExecuteChanged();
    }

    // ── Workspaces and sessions ──────────────────────────────────────────────

    /// <summary>
    /// True if the user has been clicking around: more than one tab, or a tab that was navigated or has a tree root.
    /// Then opening a workspace asks whether to close the tabs first; just after the start it does not ask.
    /// </summary>
    public bool HasUserActivity => Tabs.Count > 1 || Tabs.Any(t => t.HasHistory || t.HasTreeRoot);

    /// <summary>The open tabs as a workspace or session file saves them.</summary>
    public TabSetFile CaptureState() => new()
    {
        Tabs      = Tabs.Select(t => t.CaptureState()).ToList(),
        ActiveTab = Math.Max(0, Tabs.IndexOf(_activeTab)),
    };

    /// <summary>
    /// Opens the tabs of a workspace or session. With <paramref name="replace"/> the tabs that were open are closed
    /// (after the new ones are there, so the window never closes with its last tab); otherwise the new ones are added.
    /// A folder that is not there now gets a tab that says "currently unavailable".
    /// </summary>
    public void RestoreTabs(TabSetFile data, bool replace)
    {
        if (data.Tabs.Count == 0) return;

        var old   = Tabs.ToList();
        var added = new List<FolderTabViewModel>();
        foreach (var state in data.Tabs)
        {
            var tab = AddTab(null);
            tab.Restore(state);
            added.Add(tab);
        }

        ActiveTab = added[Math.Clamp(data.ActiveTab, 0, added.Count - 1)];
        if (replace)
        {
            foreach (var tab in old) Tabs.Remove(tab);
            _closedTabs.Clear();   // Ctrl+Shift+T brings back tabs of the old set: not what the user wants now
            ReopenTabCommand.RaiseCanExecuteChanged();
        }

        NextTabCommand.RaiseCanExecuteChanged();
        PreviousTabCommand.RaiseCanExecuteChanged();
    }

    private void CloseTab(FolderTabViewModel tab)
    {
        if (!tab.ConfirmLeavePlaylist()) return;   // a playlist with changes that are not saved
        if (Tabs.Count == 1)
        {
            LastTabClosed?.Invoke();
            return;
        }

        int index = Tabs.IndexOf(tab);
        if (index < 0) return;

        if (tab == _activeTab)
            ActiveTab = Tabs[index < Tabs.Count - 1 ? index + 1 : index - 1];

        Tabs.Remove(tab);

        _closedTabs.Add((tab, index));
        if (_closedTabs.Count > MaxClosedTabs) _closedTabs.RemoveAt(0);

        NextTabCommand.RaiseCanExecuteChanged();
        PreviousTabCommand.RaiseCanExecuteChanged();
        ReopenTabCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Brings back the most recently closed tab, where it was, and makes it active.</summary>
    private void ReopenTab()
    {
        if (_closedTabs.Count == 0) return;

        var (tab, index) = _closedTabs[^1];
        _closedTabs.RemoveAt(_closedTabs.Count - 1);

        Tabs.Insert(Math.Min(index, Tabs.Count), tab);
        tab.RefreshCommand.Execute(null);   // the folder may have changed while the tab was closed
        ActiveTab = tab;

        NextTabCommand.RaiseCanExecuteChanged();
        PreviousTabCommand.RaiseCanExecuteChanged();
        ReopenTabCommand.RaiseCanExecuteChanged();
    }

    private void SwitchTab(int delta)
    {
        int i = (Tabs.IndexOf(_activeTab) + delta + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[i];
    }

    // ── What the window binds to: the active tab ─────────────────────────────

    public string CurrentPath
    {
        get => _activeTab.CurrentPath;
        set => _activeTab.CurrentPath = value;
    }

    /// <summary>The text of the address bar (what is typed there); <see cref="CurrentPath"/> is the folder shown.</summary>
    public string AddressText
    {
        get => _activeTab.AddressText;
        set => _activeTab.AddressText = value;
    }

    public string StatusText => _activeTab.StatusText;

    public ObservableCollection<FileItemViewModel> CurrentItems => _activeTab.CurrentItems;
    public ObservableCollection<TreeItemViewModel>  TreeItems    => _activeTab.TreeItems;

    public RelayCommand BackCommand            => _activeTab.BackCommand;
    public RelayCommand ForwardCommand         => _activeTab.ForwardCommand;
    public RelayCommand UpCommand              => _activeTab.UpCommand;
    public RelayCommand RefreshCommand         => _activeTab.RefreshCommand;
    public RelayCommand NavigateCommand        => _activeTab.NavigateCommand;
    public RelayCommand DeleteCommand          => _activeTab.DeleteCommand;
    public RelayCommand PermanentDeleteCommand => _activeTab.PermanentDeleteCommand;
    public RelayCommand NewFolderCommand       => _activeTab.NewFolderCommand;
    public RelayCommand CopyCommand            => _activeTab.CopyCommand;
    public RelayCommand CutCommand             => _activeTab.CutCommand;
    public RelayCommand PasteCommand           => _activeTab.PasteCommand;
    public RelayCommand PasteKeyCommand        => _activeTab.PasteKeyCommand;
    public bool HasFolder                      => _activeTab.HasFolder;
    public void PasteFromKeyboard(string? destination) => _activeTab.PasteFromKeyboard(destination);
    public RelayCommand ToggleHiddenCommand    => _activeTab.ToggleHiddenCommand;
    public RelayCommand CopyPathCommand        => _activeTab.CopyPathCommand;

    /// <summary>The folder shown is a packed file or in one: read-only.</summary>
    public bool   InArchive      => _activeTab.InArchive;

    /// <summary>The folder shown is an M3U playlist.</summary>
    public bool   InPlaylist     => _activeTab.InPlaylist;

    /// <summary>The playlist shown has changes that are not saved.</summary>
    public bool   PlaylistDirty  => _activeTab.PlaylistDirty;

    public bool   HasHiddenItems => _activeTab.HasHiddenItems;
    public string HiddenText     => _activeTab.HiddenText;
    public bool   HasRenameUndo  => _activeTab.HasRenameUndo;
    public string RenameUndoTip  => _activeTab.RenameUndoTip;

    public System.Windows.Media.ImageSource? FolderPicture => _activeTab.FolderPicture;
    public bool   HasFolderPicture => _activeTab.HasFolderPicture;
    public string? GetFolderPictureFile() => _activeTab.GetFolderPictureFile();

    // The root of the tree of this tab (not remembered)
    public bool    HasTreeRoot  => _activeTab.HasTreeRoot;
    public string? TreeRootPath => _activeTab.RootPath;
    public bool SetTreeRoot(string? path) => _activeTab.SetTreeRoot(path);
    public void ResetTreeRoot()           => _activeTab.ResetTreeRoot();

    public RelayCommand ToggleTreeHiddenCommand => _activeTab.ToggleTreeHiddenCommand;
    public bool   HasTreeInfo          => _activeTab.HasTreeInfo;
    public string TreeCountsText       => _activeTab.TreeCountsText;
    public bool   HasTreeHiddenFolders => _activeTab.HasTreeHiddenFolders;
    public string TreeHiddenText       => _activeTab.TreeHiddenText;

    public void ReloadTags(IEnumerable<FileItemViewModel> items) => _activeTab.ReloadTags(items);

    // Rating and expectation (the library)
    public bool HasFolderMarks          => _activeTab.HasFolderMarks;
    public string FolderRatingText      => _activeTab.FolderRatingText;
    public string FolderExpectationText => _activeTab.FolderExpectationText;
    public SwewolfFileExplorer.Helpers.LibraryStore.Marks GetMarks(string path) => _activeTab.GetMarks(path);
    public void SetRating(IReadOnlyCollection<string> paths, int? value)         => _activeTab.SetRating(paths, value);
    public void SetExpectation(IReadOnlyCollection<string> paths, double? value) => _activeTab.SetExpectation(paths, value);

    public void RefreshFolderPicture() => _activeTab.RefreshFolderPicture();

    // Tags of the folder (its desktop.ini, read only)
    public string FolderTagsText       => _activeTab.FolderTagsText;
    public bool HasFolderTags          => _activeTab.HasFolderTags;

    // Read / unread (the library)
    public string FolderStatusText     => _activeTab.FolderStatusText;
    public void RefreshReadState()     => _activeTab.RefreshReadState();
    public void MarkRead(IEnumerable<FileItemViewModel> items, bool read) => _activeTab.MarkRead(items, read);
    public void MarkPathRead(string path)                                 => _activeTab.MarkPathRead(path);
    public void MarkFolderTree(string folder, bool read)                  => _activeTab.MarkFolderTree(folder, read);

    public SortColumn SortColumn    => _activeTab.SortColumn;
    public bool       SortAscending => _activeTab.SortAscending;

    /// <summary>The "Sort By" choice of the tree header: 0 Name, 1 Date Created, 2 Date Modified.</summary>
    public int TreeSortIndex
    {
        get => _activeTab.TreeSortIndex;
        set => _activeTab.TreeSortIndex = value;
    }

    /// <summary>The direction of the tree sort: 0 ascending, 1 descending.</summary>
    public int TreeSortDirectionIndex
    {
        get => _activeTab.TreeSortDirectionIndex;
        set => _activeTab.TreeSortDirectionIndex = value;
    }

    private bool _showTree = true;

    /// <summary>Whether the navigation tree pane is shown (the "x" in its header hides it, F8 toggles it).</summary>
    public bool ShowTree
    {
        get => _showTree;
        set
        {
            if (_showTree == value) return;
            _showTree = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Show folders in the file list. Changing it reloads the lists of all tabs and is remembered.</summary>
    public bool ShowFolders
    {
        get => _shared.ShowFolders;
        set
        {
            if (_shared.ShowFolders == value) return;
            _shared.ShowFolders = value;
            AppSettingsStore.Save(_shared.Settings);

            foreach (var tab in Tabs) tab.Refresh();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Show the extensions of known file types (.txt, .jpg ...) in the Name column: the toolbar's ".ext" button. The opposite of
    /// <see cref="AppSettings.HideKnownExtensions"/> (Windows' "Hide extensions for known file types"). Remembered; every tab follows at once.
    /// </summary>
    public bool ShowExtensions
    {
        get => !_shared.Settings.HideKnownExtensions;
        set
        {
            if (_shared.Settings.HideKnownExtensions == !value) return;
            _shared.Settings.HideKnownExtensions = !value;
            AppSettingsStore.Save(_shared.Settings);
            ExtensionSettingChanged();
        }
    }

    /// <summary>The names the lists show are worked out again in every tab (also after Settings changed the setting), and the button follows.</summary>
    public void ExtensionSettingChanged()
    {
        foreach (var tab in Tabs)
            foreach (var item in tab.CurrentItems)
            {
                item.RefreshDisplayName();
                if (!item.IsDirectory) item.TypeName = IconHelper.GetTypeNameForName(item.Name);   // the "registered type names" setting may have changed
            }
        OnPropertyChanged(nameof(ShowExtensions));
        DisplayNamesChanged?.Invoke();
    }

    /// <summary>The names shown in the Name column changed (extensions shown or hidden): the window filters and fits the columns again.</summary>
    public event Action? DisplayNamesChanged;

    public double ListFontSize
    {
        get => _shared.Settings.ListFontSize;
        set
        {
            value = ClampFontSize(value);
            if (_shared.Settings.ListFontSize == value) return;
            _shared.Settings.ListFontSize = value;
            AppSettingsStore.Save(_shared.Settings);
            OnPropertyChanged();
        }
    }

    public double TreeFontSize
    {
        get => _shared.Settings.TreeFontSize;
        set
        {
            value = ClampFontSize(value);
            if (_shared.Settings.TreeFontSize == value) return;
            _shared.Settings.TreeFontSize = value;
            AppSettingsStore.Save(_shared.Settings);
            OnPropertyChanged();
        }
    }

    private static double ClampFontSize(double size) => AppSettingsStore.ClampFontSize(size);

    public bool ConsumeProgrammaticTreeSelection(TreeItemViewModel node) =>
        _activeTab.ConsumeProgrammaticTreeSelection(node);

    public void SetSort(SortColumn column, bool ascending) => _activeTab.SetSort(column, ascending);

    /// <summary>Raised when the active tab's sort order changed.</summary>
    public event Action? SortChanged;

    /// <summary>Raised after the active tab created a new folder.</summary>
    public event Action<FileItemViewModel>? FolderCreated;
    public event Action<string>? SelectPathRequested;
    public event Action<string>? ClipboardFileRequested;
    public event Action<string>? BookmarksPasteRequested;

    /// <summary>The tags of the music files in the list have been read.</summary>
    public event Action? TagsLoaded;

    /// <summary>The list was brought in line with the disk in place (a file changed size or date): the window sorts again.</summary>
    public event Action? ItemsChangedInPlace;

    /// <summary>Tells whether a column that needs the tags of music files is in use; if so the active tab reads them.</summary>
    public void SetTagColumnsVisible(bool visible)
    {
        _shared.TagColumnsVisible = visible;
        _activeTab.EnsureTags();
    }

    public void Navigate(string? path, bool addToHistory = true) => _activeTab.Navigate(path, addToHistory);
    public void OfferMoveAfterSmartRename(string path)            => _activeTab.OfferMoveAfterSmartRename(path);
    /// <summary>The Bookmarks folder changed (one was added, renamed or removed): every tab's tree reads it again.</summary>
    public void ReloadBookmarks() { foreach (var tab in Tabs) tab.ReloadBookmarks(); }

    /// <summary>Reads the Bookmarks folder again in the tabs where something in it changed since (also from Windows Explorer).</summary>
    public void ReloadBookmarksIfChanged() { foreach (var tab in Tabs) tab.ReloadBookmarksIfChanged(); }

    public void NavigateAsync(string? path)                       => _activeTab.NavigateAsync(path);   // a click in the tree: slow folders are read in the background
    public void ShowNoFolder(string? place = null)                => _activeTab.ShowNoFolder(place);

    /// <summary>
    /// Alt+Home: the active tab goes to the default folder. That is This PC for now (also where a new tab starts); a setting may change it
    /// later, and this is the one place to do it.
    /// </summary>
    public void GoToDefaultFolder() => _activeTab.ShowThisPc();
    public void OpenItem(FileItemViewModel item)                  => _activeTab.OpenItem(item);
    public void RenameItem(FileItemViewModel item, string name)   => _activeTab.RenameItem(item, name);

    // What the right-click menu of the tree does with a folder (the folder need not be the current one).
    public void CopyPathsAsText(IEnumerable<string> paths)        => _activeTab.CopyPathsAsText(paths);
    public void PutFolderOnClipboard(string path, bool cut)       => _activeTab.PutFolderOnClipboard(path, cut);
    public void PasteInto(string path)                            => _activeTab.PasteInto(path);
    public void DeleteFolder(string path)                         => _activeTab.DeleteFolder(path);
    public void RenameFolder(string path, string name)            => _activeTab.RenameFolder(path, name);
    public void CreateFolderIn(string path)                       => _activeTab.CreateNewFolderIn(path);
    public void RefreshFolder(string path)                        => _activeTab.RefreshFolder(path);
    public void UpdateSelectedItems(List<FileItemViewModel> items) => _activeTab.UpdateSelectedItems(items);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
