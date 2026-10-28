using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.VisualBasic.FileIO;
using SwewolfFileExplorer.Commands;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.ViewModels;

/// <summary>
/// One tab: its own folder, file list, navigation tree, history, selection and sort.
/// The clipboard and the remembered per-folder sort orders are shared between the tabs.
/// </summary>
public class FolderTabViewModel : INotifyPropertyChanged
{
    private readonly SharedState _shared;

    private string _currentPath = "";
    private string _title = "";
    private string _statusText = "";
    private FileItemViewModel? _selectedItem;

    private readonly Stack<string> _backHistory    = new();
    private readonly Stack<string> _forwardHistory = new();
    private List<FileItemViewModel> _selectedItems = new();

    // ── Observable collections ───────────────────────────────────────────────

    public ObservableCollection<FileItemViewModel> CurrentItems { get; } = new();
    public ObservableCollection<TreeItemViewModel> TreeItems    { get; } = new();

    // ── Bindable properties ──────────────────────────────────────────────────

    /// <summary>
    /// The folder shown. It changes only when a navigation succeeds. (It used to be the text of the address bar, which changed with every
    /// key typed there: F5, Paste, New Folder and the folder watcher then acted on half-typed text.) What the address bar shows and
    /// the user types is <see cref="AddressText"/>.
    /// </summary>
    public string CurrentPath
    {
        get => _currentPath;
        set { _currentPath = value; _addressText = value; OnPropertyChanged(); OnPropertyChanged(nameof(AddressText)); }
    }

    private string _addressText = "";

    /// <summary>The text of the address bar: the folder shown until the user types something else. Esc puts <see cref="CurrentPath"/> back.</summary>
    public string AddressText
    {
        get => _addressText;
        set { if (_addressText == value) return; _addressText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The text on the tab: the name the user gave the tab (<see cref="CustomTitle"/>), else the folder name. Changes when a
    /// navigation succeeds, not while typing. A tab whose folder is not there says "(unavailable)" after either.
    /// </summary>
    public string Title => _title;

    private string _baseTitle = "";      // the folder name, or "This PC"
    private string? _customTitle;        // the user's own text for the tab, if any

    /// <summary>The name the user gave this tab (it stays when another folder is opened in the tab); null for the folder name. Saved in workspaces and sessions.</summary>
    public string? CustomTitle => _customTitle;

    /// <summary>What a rename box starts with: the user's own name, else the name of the folder.</summary>
    public string TitleForEditing => _customTitle ?? _baseTitle;

    /// <summary>Gives the tab a name of its own (empty or null: the folder name again). The folder is not renamed.</summary>
    public void SetCustomTitle(string? title)
    {
        title = title?.Trim();
        _customTitle = string.IsNullOrEmpty(title) ? null : title;
        UpdateTitle();
        OnPropertyChanged(nameof(CustomTitle));
    }

    private void UpdateTitle()
    {
        var title = (_customTitle ?? _baseTitle) + (_unavailable ? " (unavailable)" : "");
        if (title == _title) return;
        _title = title;
        OnPropertyChanged(nameof(Title));
    }

    public override string ToString() => Title;   // what accessibility tools read for the tab

    public SortColumn SortColumn { get; private set; } = SortColumn.Name;
    public bool SortAscending { get; private set; } = true;

    /// <summary>Raised when the sort order of this tab changed (the user chose one, or a folder with its own was opened).</summary>
    public event Action? SortChanged;

    /// <summary>Raised after a new folder was created and the list refreshed.</summary>
    public event Action<FileItemViewModel>? FolderCreated;

    /// <summary>Raised with the path of an item that the window should select in the list again (after a rename or a refresh).</summary>
    public event Action<string>? SelectPathRequested;
    /// <summary>Several items of the list are to be selected (the files just pasted).</summary>
    public event Action<List<string>>? SelectPathsRequested;

    /// <summary>The user picked a sort order: use it now and remember it for this folder.</summary>
    public void SetSort(SortColumn column, bool ascending)
    {
        SortColumn    = column;
        SortAscending = ascending;
        if (!string.IsNullOrEmpty(_currentPath))
            _shared.SetFolderSort(_currentPath, column, ascending);   // also saved for the next run
        SortChanged?.Invoke();
    }

    /// <summary>
    /// Takes over the sort order remembered for the current folder. If there is none: Name, ascending, for a real folder;
    /// for a zip / cbz no sort at all, so the files show in the order they are stored in it, until a header is clicked (a RAR, cbr or
    /// 7z is sorted by name: see <see cref="ArchiveSupport.ShowsStoredOrder"/>).
    /// </summary>
    private void ApplyFolderSort()
    {
        var (column, ascending) = _shared.TryGetFolderSort(_currentPath, out var s)
            ? s
            : (ArchiveSupport.ShowsStoredOrder(_currentPath) || PlaylistFile.IsPlaylistName(_currentPath) ? SortColumn.None : SortColumn.Name, true);   // a playlist: in the order of the list

        SortColumn    = column;
        SortAscending = ascending;
        SortChanged?.Invoke();
    }

    // ── Sort order of the navigation tree ────────────────────────────────────

    // The order of the "Sort By" list in the tree header.
    private static readonly SortColumn[] TreeSortColumns =
        { SortColumn.Name, SortColumn.DateCreated, SortColumn.DateModified };

    /// <summary>How the subfolders of the current folder are sorted in the tree.</summary>
    private (SortColumn Column, bool Ascending) CurrentTreeSort =>
        string.IsNullOrEmpty(_currentPath) ? (SortColumn.Name, true) : _shared.GetTreeSort(_currentPath);

    /// <summary>The "Sort By" choice of the tree: 0 Name, 1 Date Created, 2 Date Modified; -1 (empty) if none is chosen (inside an archive).</summary>
    public int TreeSortIndex
    {
        get => Array.IndexOf(TreeSortColumns, CurrentTreeSort.Column);
        set
        {
            if (value >= 0 && value < TreeSortColumns.Length)
                SetTreeSort(TreeSortColumns[value], CurrentTreeSort.Ascending);
        }
    }

    /// <summary>The direction of the tree sort: 0 ascending, 1 descending.</summary>
    public int TreeSortDirectionIndex
    {
        get => CurrentTreeSort.Ascending ? 0 : 1;
        set
        {
            if (value >= 0)
                // With no sort chosen yet (inside an archive), choosing a direction means sorting by Name.
                SetTreeSort(CurrentTreeSort.Column == SortColumn.None ? SortColumn.Name : CurrentTreeSort.Column, value == 0);
        }
    }

    /// <summary>Raised with the folder whose subfolders were sorted in another order.</summary>
    public event Action<string>? TreeSortChanged;

    private void SetTreeSort(SortColumn column, bool ascending)
    {
        // The address bar can hold half a path while the user types: only a real folder is remembered.
        if (!(Directory.Exists(_currentPath) || ArchiveSupport.FolderExists(_currentPath, out _))
            || CurrentTreeSort == (column, ascending)) return;

        _shared.SetTreeSort(_currentPath, column, ascending);   // also saved for the next run
        OnPropertyChanged(nameof(TreeSortIndex));
        OnPropertyChanged(nameof(TreeSortDirectionIndex));
        TreeSortChanged?.Invoke(_currentPath);
    }

    /// <summary>
    /// Reads the subfolders of <paramref name="path"/> again in this tab's tree (where it is loaded),
    /// keeping what is expanded and selected.
    /// </summary>
    public void RefreshTreeFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { SyncHiddenNodes(TreeItems, path); }
        catch { /* not a path */ }
    }

    /// <summary>
    /// Selects the node of <paramref name="path"/> in the tree without navigating there (the list stays where it is).
    /// The tree's own selection is the folder that F2 renames.
    /// </summary>
    public void SelectFolderInTree(string path)
    {
        try
        {
            var node = ResolveNode(path);
            if (node == null || !NodeIs(node, path) || node.IsSelected) return;

            _programmaticSelection = node;   // the window must not take this for a click
            node.IsSelected = true;
        }
        catch { /* not a folder of the tree */ }
    }

    /// <summary>The Refresh of the tree's right-click menu: the folder in the tree, and the list if it shows that folder.</summary>
    public void RefreshFolder(string path)
    {
        ArchiveSupport.Invalidate(path);
        RefreshTreeFolder(path);
        if (IsSamePath(path, _currentPath)) Refresh();
    }

    /// <summary>Puts the loaded subfolders of <paramref name="path"/> in their (new) order.</summary>
    public void ResortTree(string path) => ResortNodes(TreeItems, path);

    private static void ResortNodes(IEnumerable<TreeItemViewModel> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (NodeIs(node, path)) node.Resort();
            ResortNodes(node.Children, path);
        }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public FileItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set { _selectedItem = value; OnPropertyChanged(); }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    public RelayCommand BackCommand      { get; }
    public RelayCommand ForwardCommand   { get; }
    public RelayCommand UpCommand        { get; }
    public RelayCommand RefreshCommand   { get; }
    public RelayCommand NavigateCommand  { get; }
    public RelayCommand DeleteCommand    { get; }
    public RelayCommand PermanentDeleteCommand { get; }
    public RelayCommand NewFolderCommand { get; }
    public RelayCommand CopyCommand      { get; }
    public RelayCommand CutCommand       { get; }
    public RelayCommand PasteCommand     { get; }

    /// <summary>Ctrl+V: always runs, and says why when there is nothing to paste or nowhere to paste it (PasteCommand, which the menus use to enable "Paste", just stays disabled).</summary>
    public RelayCommand PasteKeyCommand  { get; }
    public RelayCommand CopyPathCommand  { get; }

    // ── Constructor ──────────────────────────────────────────────────────────

    /// <param name="startPath">The folder to open; null for a tab that is filled in by <see cref="ShowUnavailable"/>.</param>
    public FolderTabViewModel(SharedState shared, string? startPath)
    {
        _shared = shared;
        LibraryService.FolderChanged += OnLibraryFolderChanged;

        BackCommand     = new RelayCommand(_ => GoBack(),        _ => _backHistory.Count > 0);
        ForwardCommand   = new RelayCommand(_ => GoForward(),     _ => _forwardHistory.Count > 0);
        UpCommand        = new RelayCommand(_ => GoUp(),          _ => CanGoUp());
        RefreshCommand   = new RelayCommand(_ => Refresh());
        NavigateCommand  = new RelayCommand(_ => Navigate(AddressText));   // Enter in the address bar: what was typed
        // An archive is read-only: only copying out of it is possible.
        DeleteCommand    = new RelayCommand(_ => DeleteSelected(), _ => _selectedItems.Count > 0 && (!_inArchive || _inPlaylist));   // in a playlist: remove from the list
        PermanentDeleteCommand = new RelayCommand(_ => DeleteSelected(permanent: true), _ => _selectedItems.Count > 0 && !_inArchive);
        NewFolderCommand = new RelayCommand(_ => CreateNewFolder(), _ => HasFolder && !_inArchive);
        CopyCommand      = new RelayCommand(_ => CopySelected(),  _ => _selectedItems.Count > 0);
        CutCommand       = new RelayCommand(_ => CutSelected(),   _ => _selectedItems.Count > 0 && !_inArchive);
        PasteCommand     = new RelayCommand(_ => Paste(),         _ => HasFolder && (!_inArchive || _inPlaylist) && (FileClipboard.HasFiles()   // in a playlist: add entries
                                                                  || IsUnderBookmarks(_currentPath)));   // in Bookmarks: always on, it explains what can be pasted
        PasteKeyCommand  = new RelayCommand(_ => PasteFromKeyboard(null));
        ToggleHiddenCommand = new RelayCommand(_ => ToggleHidden());
        CopyPathCommand  = new RelayCommand(_ => CopyPathsAsText(_selectedItems.Select(i => i.FullPath)),
                                            _ => _selectedItems.Count > 0);
        ToggleTreeHiddenCommand = new RelayCommand(_ => ToggleTreeHidden());

        CurrentItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FolderStatusLine));   // "Empty Folder"
        InitializeTree();
        if (startPath != null) Navigate(startPath);
        else ShowThisPc();
    }

    /// <summary>
    /// The start of the program and of a new tab with no folder: This PC is selected, its drives are not expanded and the
    /// list is empty. (Quick Access above it shows its places, as it always does.)
    /// </summary>
    public void ShowThisPc()
    {
        if (_playlistDirty && !ConfirmLeavePlaylist()) return;   // Alt+Home while a playlist has unsaved changes
        ShowNoFolder("pc");
        _unavailable = false;
        FolderPath  = "";
        CurrentPath = "";
        _baseTitle  = "This PC";
        UpdateTitle();

        if (_thisPcNode != null && !_thisPcNode.IsSelected)
        {
            _programmaticSelection = _thisPcNode;
            _thisPcNode.IsSelected = true;
        }
        UpdateNavState();
    }

    /// <summary>Re-evaluates every command's CanExecute, e.g. when this tab becomes the active one.</summary>
    public void RaiseAllCanExecuteChanged()
    {
        foreach (var c in new[] { BackCommand, ForwardCommand, UpCommand, RefreshCommand, NavigateCommand,
                                  DeleteCommand, PermanentDeleteCommand, NewFolderCommand,
                                  CopyCommand, CutCommand, PasteCommand, CopyPathCommand })
            c.RaiseCanExecuteChanged();
    }

    // ── Tree initialization ──────────────────────────────────────────────────

    private void InitializeTree()
    {
        // Bookmarks: the shortcuts in the Bookmarks folder, read in the background (a disk that is not connected must not hold up the start).
        var bookmarks = _bookmarksNode = new TreeItemViewModel("Bookmarks", BookmarkStore.Folder, null, isVirtual: true, sortOf: _shared.GetTreeSort,
                                                               showHiddenOf: _shared.ShowsHiddenInTree) { IsBookmarksRoot = true, IconShape = "bookmark" };
        try { Directory.CreateDirectory(BookmarkStore.Folder); } catch { /* a click then says it is not there */ }
        bookmarks.IsExpanded = true;
        TreeItems.Add(bookmarks);
        ReloadBookmarks();

        // Quick Access
        var quickAccess = new TreeItemViewModel("Quick Access", "", null, isVirtual: true, sortOf: _shared.GetTreeSort, showHiddenOf: _shared.ShowsHiddenInTree) { IconShape = "star" };
        quickAccess.IsExpanded = true;
        // The order of Explorer's Quick Access. Downloads is asked of Windows (it is often moved to another drive, and .NET
        // has no entry for it): the folder in the user profile does not have to exist.
        AddQuickAccessItem(quickAccess, "Desktop",   Environment.SpecialFolder.Desktop);
        AddQuickAccessPath(quickAccess, "Downloads", KnownFolders.Downloads());
        AddQuickAccessItem(quickAccess, "Documents", Environment.SpecialFolder.MyDocuments);
        AddQuickAccessItem(quickAccess, "Pictures",  Environment.SpecialFolder.MyPictures);
        AddQuickAccessItem(quickAccess, "Music",     Environment.SpecialFolder.MyMusic);
        AddQuickAccessItem(quickAccess, "Videos",    Environment.SpecialFolder.MyVideos);

        TreeItems.Add(quickAccess);

        // This PC
        var thisPC = _thisPcNode = new TreeItemViewModel("This PC", "", null, isVirtual: true, sortOf: _shared.GetTreeSort, showHiddenOf: _shared.ShowsHiddenInTree) { IconShape = "pc" };
        thisPC.IsExpanded = true;

        foreach (var drive in ReadDrives().Drives)
            thisPC.Children.Add(MakeDriveNode(drive));

        TreeItems.Add(thisPC);
    }

    // ── Drives under This PC ─────────────────────────────────────────────────

    /// <summary>A drive as the tree shows it: its root and the text of its node ("Data (D:)").</summary>
    public sealed record DriveEntry(string Root, string Label);

    /// <summary>
    /// The drives that are ready now. Can be slow (a network drive that is gone, a drive still mounting): call it off the UI thread.
    /// AnyNotReady says a drive exists that is not ready yet (a volume that is being mounted), so it is worth looking again soon.
    /// </summary>
    public static (List<DriveEntry> Drives, bool AnyNotReady) ReadDrives()
    {
        var result = new List<DriveEntry>();
        bool notReady = false;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) { if (drive.DriveType != DriveType.CDRom) notReady = true; continue; }
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                result.Add(new DriveEntry(drive.RootDirectory.FullName, label));
            }
            catch { notReady = true; }   // gone between the two calls
        }
        return (result, notReady);
    }

    private TreeItemViewModel MakeDriveNode(DriveEntry drive)
    {
        var item = new TreeItemViewModel(drive.Label, drive.Root, IconHelper.GetIcon(drive.Root),
                                         sortOf: _shared.GetTreeSort, showHiddenOf: _shared.ShowsHiddenInTree);
        item.InitializeLazyLoad();
        return item;
    }

    /// <summary>
    /// Brings the drives under This PC in line with the list: a drive that came (a USB stick, a VeraCrypt volume) is added in its place,
    /// one that went is removed, one whose name changed is made again. The drive the tab is in, or that is selected, is never removed or
    /// replaced under the user's feet (the folder watcher deals with a folder that is gone). Nothing else in the tree moves.
    /// </summary>
    public void SyncDrives(IReadOnlyList<DriveEntry> drives)
    {
        var node = _thisPcNode;
        if (node == null) return;

        bool Holds(TreeItemViewModel c) =>
            c.IsSelected || (!string.IsNullOrEmpty(_currentPath) && _currentPath.StartsWith(c.FullPath, StringComparison.OrdinalIgnoreCase));

        for (int i = node.Children.Count - 1; i >= 0; i--)
        {
            var child = node.Children[i];
            var drive = drives.FirstOrDefault(d => string.Equals(d.Root, child.FullPath, StringComparison.OrdinalIgnoreCase));
            if (drive == null) { if (!Holds(child)) node.Children.RemoveAt(i); }
            else if (drive.Label != child.Name && !Holds(child)) node.Children[i] = MakeDriveNode(drive);
        }

        foreach (var drive in drives)
        {
            if (node.Children.Any(c => string.Equals(c.FullPath, drive.Root, StringComparison.OrdinalIgnoreCase))) continue;
            int at = 0;
            while (at < node.Children.Count && string.Compare(node.Children[at].FullPath, drive.Root, StringComparison.OrdinalIgnoreCase) < 0) at++;
            node.Children.Insert(at, MakeDriveNode(drive));
        }
    }

    /// <summary>Raised by F5 (also on This PC): the window looks at the drives again.</summary>
    public static event Action? DrivesRefreshRequested;

    // ── Bookmarks (shortcuts in the Bookmarks folder) ────────────────────────

    private TreeItemViewModel? _bookmarksNode;

    /// <summary>The "Bookmarks" node at the top of the tree; null when the tree has a root of its own (it then holds no Bookmarks node).</summary>
    public TreeItemViewModel? BookmarksNode => _bookmarksNode != null && TreeItems.Contains(_bookmarksNode) ? _bookmarksNode : null;
    private int _bookmarkVersion;
    private string _bookmarkSnapshot = "";

    /// <summary>Reads the Bookmarks folder in the background and puts what is in it under the "Bookmarks" node of this tab's tree.</summary>
    public void ReloadBookmarks()
    {
        var node = _bookmarksNode;
        if (node == null) return;

        int version    = ++_bookmarkVersion;
        var dispatcher = Application.Current.Dispatcher;
        _ = Task.Run(() =>
        {
            var snapshot = BookmarkStore.Snapshot();
            var entries  = BookmarkStore.Read(checkAvailable: true);   // may be slow for a disk that is not there

            dispatcher.InvokeAsync(() =>
            {
                if (version != _bookmarkVersion || node != _bookmarksNode) return;   // a newer read, or the tree was rebuilt

                node.Children.Clear();
                foreach (var entry in entries) if (IsTreeBookmark(entry)) node.Children.Add(MakeBookmarkNode(entry));
                _bookmarkSnapshot = snapshot;

                // The selected node may have been one of the old bookmark nodes: the tree follows the folder again.
                if (FindSelectedNode(TreeItems) == null && !string.IsNullOrEmpty(_currentPath)) SelectInTree(_currentPath);
            });
        });
    }

    /// <summary>Reads the Bookmarks folder again if something in it changed (also from Windows Explorer) since it was read last.</summary>
    public void ReloadBookmarksIfChanged()
    {
        if (BookmarkStore.Snapshot() != _bookmarkSnapshot) ReloadBookmarks();
    }

    /// <summary>Only folders and groups are in the tree; a bookmark of a file is seen in the list when "Bookmarks" (or its group) is clicked.</summary>
    private static bool IsTreeBookmark(BookmarkEntry entry) => entry.IsGroup || (entry.Target != null && !entry.IsFile);

    private TreeItemViewModel MakeBookmarkNode(BookmarkEntry entry)
    {
        // A group: a folder of bookmarks. No path of its own, so a click on it shows nothing.
        if (entry.IsGroup)
        {
            var group = new TreeItemViewModel(entry.Name, entry.Path, IconHelper.GetIconForName("folder", true), isVirtual: true,
                                              sortOf: _shared.GetTreeSort, showHiddenOf: _shared.ShowsHiddenInTree) { Bookmark = entry };
            foreach (var child in entry.Children) if (IsTreeBookmark(child)) group.Children.Add(MakeBookmarkNode(child));
            return group;
        }

        // A shortcut that points to nothing usable.
        if (entry.Target == null)
            return new TreeItemViewModel(entry.Name, "", IconHelper.GetIconForName("folder", true), isVirtual: true, isHidden: true) { Bookmark = entry };

        // A file: no children; a click goes to its folder and selects it (see the window).
        if (entry.IsFile)
            return new TreeItemViewModel(entry.Name, entry.Target, IconHelper.GetIconForName(Path.GetFileName(entry.Target), false), isVirtual: true,
                                         isHidden: !entry.Available) { Bookmark = entry };

        // A folder, which opens up like any folder of the tree (faded when it is not there: a disk that is not connected).
        var folder = new TreeItemViewModel(entry.Name, entry.Target, IconHelper.GetIconForName("folder", true), sortOf: _shared.GetTreeSort,
                                           showHiddenOf: _shared.ShowsHiddenInTree, isHidden: !entry.Available) { Bookmark = entry };
        if (entry.Available) folder.InitializeLazyLoad();
        return folder;
    }

    private void AddQuickAccessItem(TreeItemViewModel parent, string name, Environment.SpecialFolder folder) =>
        AddQuickAccessPath(parent, name, Environment.GetFolderPath(folder));

    private void AddQuickAccessPath(TreeItemViewModel parent, string name, string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        var item = new TreeItemViewModel(name, path, IconHelper.GetIcon(path), sortOf: _shared.GetTreeSort, showHiddenOf: _shared.ShowsHiddenInTree);
        item.InitializeLazyLoad();
        parent.Children.Add(item);
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    public void Navigate(string? path, bool addToHistory = true)
    {
        _navVersion++;   // a slow listing that is still being read for an earlier click is no longer wanted
        NavigateCore(path, addToHistory, null);
    }

    private int _navVersion;

    /// <summary>What a folder holds, read with no UI work: the listing, so that a slow folder can be waited for in the background.</summary>
    private sealed record FolderListing(DirectoryInfo[] Dirs, FileInfo[] Files, TreeItemViewModel.ChildCounts? Counts, bool ShowFolders, Exception? Error);

    /// <summary>
    /// The listing of a real folder: its subfolders (only when the list shows folders), its files, and (when asked) how many hidden
    /// subfolders the tree has to leave out. Touches the disk only; safe on any thread. Throws what the disk throws.
    /// </summary>
    private static FolderListing ReadListing(string path, bool showFolders, bool countTreeHidden)
    {
        var dirInfo = new DirectoryInfo(path);
        var counts = countTreeHidden ? TreeItemViewModel.CountChildren(path) : null;
        var dirs  = showFolders ? dirInfo.GetDirectories().OrderBy(d => d.Name).ToArray() : Array.Empty<DirectoryInfo>();
        var files = dirInfo.GetFiles().OrderBy(f => f.Name).ToArray();
        return new FolderListing(dirs, files, counts, showFolders, null);
    }

    /// <summary>
    /// Navigate for a click on a folder of the tree: the folder is read on a background thread, so a folder that answers slowly
    /// (a cloud drive, a disk that has gone to sleep, a stuck network share) cannot freeze the window. The status bar says
    /// "Reading folder..." meanwhile and the old list stays; another click drops this one. (found 2026-10-19: the whole program
    /// hung for minutes inside Windows' folder listing, on the UI thread.) Anything that is not a plain folder goes the normal way.
    /// </summary>
    public async void NavigateAsync(string? path)
    {
        path = CleanTypedPath(path);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_playlistDirty && !IsSamePath(path, _currentPath) && !ConfirmLeavePlaylist()) return;

        if (path.Contains('%') || !Directory.Exists(path))   // variables, packed files, missing folders: Navigate knows what to say
        {
            Navigate(path);
            return;
        }

        int version = ++_navVersion;
        bool showFolders = _shared.ShowFolders;
        StatusText = "Reading folder...";

        var listing = await Task.Run(() =>
        {
            try { return ReadListing(path, showFolders, countTreeHidden: true); }
            catch (Exception ex) { return new FolderListing(Array.Empty<DirectoryInfo>(), Array.Empty<FileInfo>(), null, showFolders, ex); }
        });

        if (version != _navVersion) return;   // another navigation started meanwhile
        NavigateCore(path, true, listing);
    }

    /// <summary>
    /// A path as the user typed or pasted it, made usable: spaces at the ends and **every double quote** are removed (a path copied from
    /// Explorer's "Copy as path" comes in quotes, and a quote at only one end is a common slip; a Windows path can never contain
    /// a quote, so nothing real is lost). Curly quotes (from a document or a web page) count as quotes.
    /// </summary>
    public static string CleanTypedPath(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Replace("\"", "").Replace("\u201C", "").Replace("\u201D", "").Replace("\u201E", "").Trim();
    }

    private void NavigateCore(string? path, bool addToHistory, FolderListing? listing)
    {
        path = CleanTypedPath(path);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_playlistDirty && !IsSamePath(path, _currentPath) && !ConfirmLeavePlaylist()) return;   // unsaved changes of a playlist

        // shell:sendto, shell:startup, shell:appdata ... : Windows' names for its special folders, as in Explorer's address bar.
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            if (!KnownFolders.TryResolveShellPath(path, out var resolved, out var shellError)) { StatusText = shellError; return; }
            path = resolved;
        }

        // %APPDATA%, %TEMP%, %USERPROFILE% ...: Windows' variables, also with a path after them (%APPDATA%\Microsoft).
        // A name that does not exist stays as typed and ends as "Path not found". A folder really named like that wins.
        if (path.Contains('%') && !Directory.Exists(path))
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            try
            {
                // Tidy the result (slashes, a closing backslash) when it is a folder; otherwise keep it for the message.
                var full = Path.GetFullPath(expanded);
                if (full.Length > 3) full = full.TrimEnd('\\');
                if (Directory.Exists(full)) expanded = full;
            }
            catch { /* not a usable path: the checks below say so */ }
            path = expanded;
        }

        if (!Directory.Exists(path) && !PlaylistFile.IsPlaylist(path))   // a playlist is shown like a folder
        {
            // A packed file, or a folder inside one, is a folder here: it is entered, not opened.
            if (!ArchiveSupport.FolderExists(path, out var archiveError))
            {
                if (archiveError != null) { StatusText = $"Cannot open the archive: {archiveError}"; return; }
                if (File.Exists(path)) { OpenFile(path); return; }
                StatusText = $"Path not found: {path}";
                return;
            }
        }

        // A folder outside the root of the tree: the tree goes back to normal (the user asked for it by going there).
        if (RootPath != null && !IsSameOrSubPath(RootPath, path))
            ClearTreeRoot();
        if (_pendingRoot != null && !IsSameOrSubPath(_pendingRoot, path))
            _pendingRoot = null;   // the user went somewhere else: the saved root is no longer wanted

        if (addToHistory && !string.IsNullOrEmpty(_currentPath))
        {
            _backHistory.Push(_currentPath);
            _forwardHistory.Clear();
        }

        ForgetRenameUndoUnlessHere(path);   // the Undo button of a smart rename lasts until another folder is shown
        _showHidden = false;   // hidden items are shown for one folder at a time
        _unavailable = false;
        _noFolderShown = false;
        FolderPath   = path;
        LastSelectedPath = null;   // a new folder: nothing is selected in it yet
        CurrentPath = path;
        _baseTitle  = GetTabTitle(path);
        UpdateTitle();
        ApplyFolderSort();
        OnPropertyChanged(nameof(TreeSortIndex));   // the tree header shows this folder's tree sort order
        OnPropertyChanged(nameof(TreeSortDirectionIndex));
        LoadItems(path, listing);
        UpdateFolderPicture(path);
        UpdateFolderTags(path);
        UpdateNavState();
        SelectInTree(path);
    }

    /// <summary>
    /// "This PC" or "Quick Access" was clicked: they are no folder, so the list, the picture and the labels are emptied.
    /// The current folder stays as it is (history, session, Back), but F5 does not bring its files back.
    /// </summary>
    public void ShowNoFolder(string? place = null)
    {
        if (_playlistDirty && !ConfirmLeavePlaylist()) return;
        ForgetRenameUndoUnlessHere(null);
        _noFolderShown = true;
        RaiseTreeInfoChanged();   // no folder: no count line under the tree
        RaiseAllCanExecuteChanged();   // nothing can be pasted or made here (the tab still remembers the folder it came from)
        StopWatching();
        _tagsCts?.Cancel();
        _tagsCts = null;
        _coverCts?.Cancel();
        var placeCover = FolderCoverFinder.ForVirtual(place);   // This PC, Quick Access: a picture of their own; no folder, so nothing to look up
        if (_folderCover != null || placeCover != null)
        {
            _folderCover = placeCover;
            OnPropertyChanged(nameof(FolderPicture));
            OnPropertyChanged(nameof(HasFolderPicture));
        }
        _folderTagsCts?.Cancel();
        SetFolderTags("");

        CurrentItems.Clear();
        _hiddenCount = 0;
        ClearReadState();
        OnPropertyChanged(nameof(HasHiddenItems));
        OnPropertyChanged(nameof(HiddenText));
        StatusText = "";
    }

    private bool _noFolderShown;

    /// <summary>True when a folder is shown. False on This PC and Quick Access (the tab still remembers the folder it came from, but the list is empty and nothing can be done "in" it).</summary>
    public bool HasFolder => !_noFolderShown && !string.IsNullOrEmpty(_currentPath);

    // ── Keeping the tree on the current folder ───────────────────────────────

    private TreeItemViewModel? _thisPcNode;

    /// <summary>
    /// The node this tab selected by itself, so the window can tell that selection from a click.
    /// Cleared by <see cref="ConsumeProgrammaticTreeSelection"/>.
    /// </summary>
    private TreeItemViewModel? _programmaticSelection;

    /// <summary>True if the tree selected <paramref name="node"/> because of a navigation, not a click.</summary>
    public bool ConsumeProgrammaticTreeSelection(TreeItemViewModel node)
    {
        var mine = _programmaticSelection;
        _programmaticSelection = null;
        return mine == node;
    }

    /// <summary>Expands the tree down to <paramref name="path"/> and selects its node, as Explorer does.</summary>
    private void SelectInTree(string path)
    {
        // The node of this folder is already selected (e.g. a Quick Access shortcut was clicked).
        var selected = FindSelectedNode(TreeItems);
        if (selected != null && NodeIs(selected, path))
            return;

        var node = ResolveNode(path);
        if (node == null || node.IsSelected) return;

        _programmaticSelection = node;
        node.IsSelected = true;
    }

    /// <summary>
    /// The tree node of <paramref name="path"/> under This PC, expanding the nodes above it.
    /// If the folder is not listed (no access), the deepest node found; null if not on a drive in the tree.
    /// </summary>
    private TreeItemViewModel? ResolveNode(string path)
    {
        // With a root, the tree is that one folder: start from its node.
        if (RootPath != null)
        {
            if (!IsSameOrSubPath(RootPath, path)) return null;
            var rootNode = TreeItems.FirstOrDefault();
            return rootNode == null ? null : WalkDown(rootNode, RootPath, path);
        }

        var root = Path.GetPathRoot(Path.GetFullPath(path));
        var node = _thisPcNode?.Children.FirstOrDefault(c => NodeIs(c, root!));
        if (node == null) return null;   // e.g. a network path

        return WalkDown(node, root!, path);
    }

    /// <summary>Goes down the tree from <paramref name="node"/> (the folder <paramref name="current"/>) to <paramref name="path"/>.</summary>
    private TreeItemViewModel WalkDown(TreeItemViewModel node, string current, string path)
    {
        var segments = Path.GetRelativePath(current, path).Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (segment == ".") continue;
            current = Path.Combine(current, segment);

            node.IsExpanded = true;   // loads the children the first time
            var next = node.Children.FirstOrDefault(c => NodeIs(c, current));

            // A hidden folder (opened from the list, or typed in the address bar): the tree shows
            // the hidden folders of its parent from now on, so the folder has a node.
            if (next == null && TreeItemViewModel.IsHiddenFolder(current))
            {
                _shared.TreeShownHidden.Add(SharedState.Key(node.FullPath));
                node.SyncChildren();   // at once: a new tab has no listener yet
                TreeHiddenChanged?.Invoke(node.FullPath);
                next = node.Children.FirstOrDefault(c => NodeIs(c, current));
            }

            if (next == null) break;  // not listed (no access): stay on the deepest folder found
            node = next;
        }

        return node;
    }

    /// <summary>True if the node is the folder <paramref name="path"/> (the dummy and virtual nodes have no path).</summary>
    private static bool NodeIs(TreeItemViewModel node, string path) =>
        !string.IsNullOrEmpty(node.FullPath) && IsSamePath(node.FullPath, path);

    private static TreeItemViewModel? FindSelectedNode(IEnumerable<TreeItemViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsSelected) return node;
            var inChild = FindSelectedNode(node.Children);
            if (inChild != null) return inChild;
        }
        return null;
    }

    /// <summary>The folder's name, or the drive for a root such as C:\.</summary>
    private static string GetTabTitle(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private void GoBack()
    {
        if (_backHistory.Count == 0) return;
        _forwardHistory.Push(_currentPath);
        Navigate(_backHistory.Pop(), addToHistory: false);
    }

    private void GoForward()
    {
        if (_forwardHistory.Count == 0) return;
        _backHistory.Push(_currentPath);
        Navigate(_forwardHistory.Pop(), addToHistory: false);
    }

    private void GoUp()
    {
        if (!CanGoUp()) return;
        var parent = Directory.GetParent(_currentPath);
        if (parent != null) Navigate(parent.FullName);
    }

    // With a root for the tree, Up stops there: nothing above it exists in this tab.
    private bool CanGoUp() =>
        !string.IsNullOrEmpty(_currentPath) && Directory.GetParent(_currentPath) != null
        && !(RootPath != null && IsSamePath(_currentPath, RootPath));

    // ── Workspaces and sessions: what a tab remembers ────────────────────────

    /// <summary>
    /// The folder of the last successful navigation (a session must not save a half-typed path: that is <see cref="AddressText"/>).
    /// Same as <see cref="CurrentPath"/> since the address bar has a text of its own; kept for the places that mean "committed" explicitly.
    /// </summary>
    public string FolderPath { get; private set; } = "";

    /// <summary>The file selected last in the current folder (null when nothing was selected since the folder was opened).</summary>
    public string? LastSelectedPath { get; private set; }

    /// <summary>Raised when the user selected another file (a session saves that, but not at once: see SessionRecorder).</summary>
    public event Action? SelectionRemembered;

    /// <summary>True if the tab has been navigated: the user has been clicking around in it.</summary>
    public bool HasHistory => _backHistory.Count > 0;

    private bool _unavailable;
    private string? _pendingRoot;      // a root that was saved but could not be used yet (its drive is not there)
    private string? _pendingSelect;    // a file to select when the tab first becomes the active one

    /// <summary>True if a folder, packed file or folder in one can be opened now.</summary>
    public static bool IsAvailable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Directory.Exists(path) || PlaylistFile.IsPlaylist(path) || ArchiveSupport.FolderExists(path, out _); }
        catch { return false; }
    }

    private static string UnavailableText(string path) =>
        $"Currently unavailable: {path}. The drive may not be connected. Press F5 to try again.";

    /// <summary>
    /// What a workspace or session saves for this tab. The path stays even while it is unavailable, and so does a root
    /// that could not be used yet: nothing is removed because it is not there at the moment.
    /// </summary>
    public TabState CaptureState() => new()
    {
        Path         = FolderPath,
        Root         = RootPath ?? _pendingRoot,
        SelectedFile = LastSelectedPath,
        Name         = _customTitle,
    };

    /// <summary>
    /// Fills in a tab from a saved state. The folder is opened if it is there; if not, the tab says "currently unavailable"
    /// and keeps the path (and root and file) for the next time. Call on a tab made with a null start path, or on any tab.
    /// </summary>
    public void Restore(TabState state)
    {
        if (string.IsNullOrWhiteSpace(state.Path))
        {
            ShowThisPc();   // a tab saved while it showed This PC
        }
        else if (IsAvailable(state.Path))
        {
            Navigate(state.Path, addToHistory: false);
        }
        else
        {
            ShowUnavailable(state.Path);
        }

        if (!string.IsNullOrWhiteSpace(state.Root))
        {
            _pendingRoot = state.Root;
            ApplyPendingRoot();
        }

        LastSelectedPath = state.SelectedFile;
        _pendingSelect   = state.SelectedFile;

        if (!string.IsNullOrWhiteSpace(state.Name)) SetCustomTitle(state.Name);   // the name the user gave the tab
    }

    /// <summary>A tab for a folder that is not there now: empty, with a note, and the path kept (Go or F5 tries again).</summary>
    private void ShowUnavailable(string path)
    {
        _unavailable = true;
        FolderPath   = path;
        CurrentPath  = path;
        _baseTitle   = GetTabTitle(path);
        UpdateTitle();   // " (unavailable)" is added to it
        CurrentItems.Clear();
        SetFolderMarks(default);
        StatusText   = UnavailableText(path);
        UpdateNavState();
    }

    /// <summary>Uses the saved root as soon as it is there and the tab's folder lies inside it.</summary>
    private void ApplyPendingRoot()
    {
        if (_pendingRoot == null || RootPath != null || _unavailable) return;
        if (!IsAvailable(_pendingRoot) || !IsSameOrSubPath(_pendingRoot, FolderPath)) return;

        RootPath = _pendingRoot;
        _pendingRoot = null;
        RebuildTree();
        OnPropertyChanged(nameof(HasTreeRoot));
        SelectInTree(FolderPath);
        UpdateNavState();
    }

    /// <summary>The file saved as selected is selected now (asked for once, when the tab is the one on screen).</summary>
    public void RaisePendingSelection()
    {
        var path = _pendingSelect;
        _pendingSelect = null;
        if (path != null) SelectPathRequested?.Invoke(path);
    }

    // ── Root of the tree ─────────────────────────────────────────────────────

    /// <summary>
    /// The folder (or packed file) that is the top of this tab's tree; null = the normal tree (Quick Access and This PC).
    /// Not remembered: a new tab starts with the normal tree.
    /// </summary>
    public string? RootPath { get; private set; }

    public bool HasTreeRoot => RootPath != null;

    /// <summary>
    /// Makes <paramref name="path"/> the root of the tree and opens it. A folder of the disk, a packed file, or a folder
    /// in one. Returns false (and changes nothing) if there is no such folder.
    /// </summary>
    public bool SetTreeRoot(string? path)
    {
        path = CleanTypedPath(path);
        if (string.IsNullOrEmpty(path)) return false;

        string root;
        if (Directory.Exists(path))
        {
            root = Path.GetFullPath(path);
            if (root.Length > 3) root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);   // C:\ keeps its slash
        }
        else if (File.Exists(path) ? ArchiveSupport.IsArchiveName(path) || PlaylistFile.IsPlaylistName(path) : ArchiveSupport.FolderExists(path, out _))
        {
            root = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        else
        {
            return false;
        }

        RootPath = root;
        RebuildTree();
        OnPropertyChanged(nameof(HasTreeRoot));

        Navigate(root);   // inside the root, so the tree stays; selects the root node
        return true;
    }

    /// <summary>Back to the normal tree; the current folder is selected in it again.</summary>
    public void ResetTreeRoot()
    {
        if (RootPath == null) return;
        ClearTreeRoot();
        if (!string.IsNullOrEmpty(_currentPath)) SelectInTree(_currentPath);
        UpdateNavState();
    }

    private void ClearTreeRoot()
    {
        RootPath = null;
        RebuildTree();
        OnPropertyChanged(nameof(HasTreeRoot));
    }

    /// <summary>Builds the tree again: the root's node alone, or the normal tree.</summary>
    private void RebuildTree()
    {
        // Nothing selected is removed: the tree would select something else, and the window would take that for a click.
        var selected = FindSelectedNode(TreeItems);
        if (selected != null) selected.IsSelected = false;

        TreeItems.Clear();
        _thisPcNode = null;

        if (RootPath == null) InitializeTree();
        else                  TreeItems.Add(BuildRootNode(RootPath));
    }

    private TreeItemViewModel BuildRootNode(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name)) name = path;   // a drive: C:\

        bool isArchive, inArchive;
        System.Windows.Media.ImageSource? icon;

        if (Directory.Exists(path))
        {
            isArchive = inArchive = false;
            icon = IconHelper.GetIcon(path);
        }
        else if (File.Exists(path))
        {
            isArchive = true; inArchive = false;   // a packed file on the disk
            icon = IconHelper.GetIcon(path);
        }
        else
        {
            var entry = ArchiveSupport.GetEntry(path);   // inside an archive: a folder, or a zip in the zip
            isArchive = entry != null && !entry.IsDirectory;
            inArchive = true;
            icon = IconHelper.GetIconForName(name, !isArchive);
        }

        var node = new TreeItemViewModel(name, path, icon, sortOf: _shared.GetTreeSort,
                                         isArchive: isArchive, inArchive: inArchive, showHiddenOf: _shared.ShowsHiddenInTree);
        node.InitializeLazyLoad();
        node.IsExpanded = true;   // its folders show at once
        return node;
    }

    public void Refresh()
    {
        DrivesRefreshRequested?.Invoke();   // F5 also looks for drives that came or went, on This PC too
        if (string.IsNullOrEmpty(_currentPath) || _noFolderShown) return;
        if (_playlistDirty) { StatusText = "The playlist has changes that are not saved yet. Save it first."; return; }

        // A tab that was "currently unavailable" tries again (the USB drive may be connected now).
        if (_unavailable)
        {
            if (!IsAvailable(FolderPath)) { StatusText = UnavailableText(FolderPath); return; }
            Navigate(FolderPath, addToHistory: false);
            ApplyPendingRoot();
            return;
        }
        ApplyPendingRoot();

        ArchiveSupport.Invalidate(_currentPath);   // an archive is read again, in case the file changed
        LoadItems(_currentPath);
        UpdateFolderPicture(_currentPath);
        UpdateFolderTags(_currentPath);

        // Folders may have come or gone: the tree is the only place they show when the list hides them.
        var node = ResolveNode(_currentPath);
        if (node != null && NodeIs(node, _currentPath))
            node.Refresh();
    }

    // ── File listing ─────────────────────────────────────────────────────────

    // ── Hidden items ─────────────────────────────────────────────────────────

    private const FileAttributes HiddenAttributes = FileAttributes.Hidden | FileAttributes.System;

    private bool _showHidden;
    private int  _hiddenCount;

    /// <summary>True when the folder has hidden or system items (shown or not).</summary>
    public bool HasHiddenItems => _hiddenCount > 0;

    /// <summary>The line under the list: how many items are hidden, and what a click does.</summary>
    public string HiddenText
    {
        get
        {
            string what = _hiddenCount == 1 ? "hidden item" : "hidden items";
            return _showHidden ? $"Showing {_hiddenCount} {what}" : $"{_hiddenCount} {what}";
        }
    }

    /// <summary>Shows the hidden items of this folder, or hides them again. The next folder starts hidden again.</summary>
    public RelayCommand ToggleHiddenCommand { get; private set; } = null!;

    /// <summary>Shows the hidden items of this folder (as a click on the hidden-items line does), if they are not shown already.</summary>
    public void ShowHiddenItems()
    {
        if (!_showHidden) ToggleHidden();
    }

    private void ToggleHidden()
    {
        _showHidden = !_showHidden;
        LoadItems(_currentPath);
    }

    // ── Hidden folders in the tree ───────────────────────────────────────────

    private TreeItemViewModel.ChildCounts? _treeCounts;

    private int _treeHiddenCount => _treeCounts?.Hidden ?? 0;

    /// <summary>True when a real folder is shown: the line under the tree counts its subfolders and archive files (always, even 0).</summary>
    public bool HasTreeInfo => _treeCounts != null && HasFolder;

    /// <summary>True when the current folder has hidden or system subfolders (shown in the tree or not).</summary>
    public bool HasTreeHiddenFolders => HasTreeInfo && _treeHiddenCount > 0;

    /// <summary>"6 subfolders, 2 archive files" (what the tree shows; the hidden ones count when they are shown), with a comma last when the hidden link follows.</summary>
    public string TreeCountsText
    {
        get
        {
            if (_treeCounts is not { } c) return "";
            bool shown    = _shared.ShowsHiddenInTree(_currentPath);
            int  folders  = c.Folders  + (shown ? c.HiddenFolders  : 0);
            int  archives = c.Archives + (shown ? c.HiddenArchives : 0);
            var  text     = $"{folders} {(folders == 1 ? "subfolder" : "subfolders")}";
            if (archives > 0) text += $", {archives} {(archives == 1 ? "archive file" : "archive files")}";
            return c.Hidden > 0 ? text + "," : text;
        }
    }

    /// <summary>The link under the tree: how many subfolders of the current folder are hidden (the click toggles them).</summary>
    public string TreeHiddenText
    {
        get
        {
            string what = _treeHiddenCount == 1 ? "hidden folder" : "hidden folders";
            return _shared.ShowsHiddenInTree(_currentPath) ? $"Showing {_treeHiddenCount} {what}" : $"{_treeHiddenCount} {what}";
        }
    }

    /// <summary>Shows the hidden subfolders of the current folder in the tree, or hides them again.</summary>
    public RelayCommand ToggleTreeHiddenCommand { get; private set; } = null!;

    /// <summary>Raised with the folder whose hidden subfolders are now shown or hidden in the tree.</summary>
    public event Action<string>? TreeHiddenChanged;

    private void ToggleTreeHidden()
    {
        if (!Directory.Exists(_currentPath)) return;

        var key = SharedState.Key(_currentPath);
        if (!_shared.TreeShownHidden.Remove(key)) _shared.TreeShownHidden.Add(key);

        TreeHiddenChanged?.Invoke(_currentPath);   // every tab's tree, as the setting is shared
    }

    /// <summary>Adds or removes the nodes of the hidden subfolders of <paramref name="path"/> in this tab's tree.</summary>
    public void SyncTreeHidden(string path)
    {
        SyncHiddenNodes(TreeItems, path);
        RaiseTreeInfoChanged();
    }

    private static void SyncHiddenNodes(IEnumerable<TreeItemViewModel> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (NodeIs(node, path)) node.SyncChildren();
            SyncHiddenNodes(node.Children, path);
        }
    }

    private void UpdateTreeHidden(string path, TreeItemViewModel.ChildCounts? known = null)
    {
        _treeCounts = known ?? TreeItemViewModel.CountChildren(path);
        RaiseTreeInfoChanged();
    }

    private void RaiseTreeInfoChanged()
    {
        OnPropertyChanged(nameof(FolderStatusLine));
        OnPropertyChanged(nameof(HasTreeInfo));
        OnPropertyChanged(nameof(TreeCountsText));
        OnPropertyChanged(nameof(HasTreeHiddenFolders));
        OnPropertyChanged(nameof(TreeHiddenText));
    }

    // ── Archives ─────────────────────────────────────────────────────────────

    // True while the folder shown is a packed file or a folder in one. The content is read-only:
    // it can be opened and copied out, but not changed.
    private bool _inArchive;

    public bool InArchive => _inArchive;

    private bool _inPlaylist;

    /// <summary>The "folder" shown is an M3U playlist: its files, in the order of the list (read-only; the window opens the Play bar).</summary>
    public bool InPlaylist => _inPlaylist;

    private void LoadPlaylistItems(string path)
    {
        try
        {
            foreach (var entry in PlaylistFile.Read(path))
            {
                FileItemViewModel vm;
                if (entry.Exists)
                {
                    vm = NewFileItem(new FileInfo(entry.Path), hidden: false);
                    vm.PlaylistInfo = entry.ExtInf;
                }
                else
                {
                    vm = new FileItemViewModel
                    {
                        Name      = System.IO.Path.GetFileName(entry.Path),
                        FullPath  = entry.Path,
                        IsHidden  = true,   // dimmed
                        IsMissing = true,
                        PlaylistInfo = entry.ExtInf,
                        TypeName  = "Missing file",
                        Size      = -1,
                    };
                }
                CurrentItems.Add(vm);
                if (entry.Exists) vm.LoadIconAsync();
            }

            StatusText = "";
            EnsureTags();
        }
        catch (Exception ex)
        {
            StatusText = $"Cannot read the playlist: {ex.Message}";
        }
    }

    // ── Editing a playlist ───────────────────────────────────────────────────
    // The list of the playlist is CurrentItems in its own order (the playlist has no sort of its own until the user moves something).
    // Changes are kept in memory until Save Playlist; leaving the playlist with changes asks first.

    private bool _playlistDirty;

    /// <summary>The playlist shown has changes that are not saved.</summary>
    public bool PlaylistDirty => _playlistDirty;

    private void SetPlaylistDirty(bool value)
    {
        if (_playlistDirty == value) return;
        _playlistDirty = value;
        OnPropertyChanged(nameof(PlaylistDirty));
    }

    /// <summary>Puts links to files in the playlist, after <paramref name="after"/> (at the end if null). Folders are not added. Returns how many.</summary>
    public int AddToPlaylist(IEnumerable<string> paths, FileItemViewModel? after)
    {
        if (!_inPlaylist) return 0;

        int at = after != null ? CurrentItems.IndexOf(after) + 1 : 0;
        if (at <= 0) at = CurrentItems.Count;

        int added = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            var vm = NewFileItem(new FileInfo(path), hidden: false);
            CurrentItems.Insert(at++, vm);
            vm.LoadIconAsync();
            added++;
        }

        if (added > 0) SetPlaylistDirty(true);
        return added;
    }

    private void PasteIntoPlaylist()
    {
        if (FileClipboard.Get() is not var (paths, _))
        {
            MessageBox.Show("There are no files on the clipboard to add.\nCopy some files first.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var last = _selectedItems.OrderBy(i => CurrentItems.IndexOf(i)).LastOrDefault();
        StatusText = AddToPlaylist(paths, last) == 0 ? "Only files can be added to a playlist." : "";
    }

    /// <summary>Takes the entries out of the list (the files are not touched).</summary>
    public void RemoveFromPlaylist(IEnumerable<FileItemViewModel> items)
    {
        bool any = false;
        foreach (var item in items.ToList())
            any |= CurrentItems.Remove(item);
        if (any) SetPlaylistDirty(true);
    }

    /// <summary>
    /// The order the grid shows (a sort by a column) becomes the order of the playlist: the user starts to move entries, so the list has an
    /// order of its own from now on. <paramref name="shownOrder"/> compares two items as the sort does.
    /// </summary>
    public void SetPlaylistOrderFrom(System.Collections.IComparer shownOrder)
    {
        var order = CurrentItems.Select((item, index) => (item, index))
                                .OrderBy(x => x.item, Comparer<FileItemViewModel>.Create((a, b) => shownOrder.Compare(a, b)))
                                .ThenBy(x => x.index)
                                .Select(x => x.item)
                                .ToList();
        for (int i = 0; i < order.Count; i++)
        {
            int from = CurrentItems.IndexOf(order[i]);
            if (from != i) CurrentItems.Move(from, i);
        }
    }

    /// <summary>Moves the selected entries one place up (direction -1) or down (+1), the ones that can move. True if something moved.</summary>
    public bool MovePlaylistItems(IEnumerable<FileItemViewModel> selected, int direction)
    {
        var chosen = new HashSet<FileItemViewModel>(selected);
        bool moved = false;

        if (direction < 0)
        {
            for (int i = 1; i < CurrentItems.Count; i++)
                if (chosen.Contains(CurrentItems[i]) && !chosen.Contains(CurrentItems[i - 1])) { CurrentItems.Move(i, i - 1); moved = true; }
        }
        else
        {
            for (int i = CurrentItems.Count - 2; i >= 0; i--)
                if (chosen.Contains(CurrentItems[i]) && !chosen.Contains(CurrentItems[i + 1])) { CurrentItems.Move(i, i + 1); moved = true; }
        }

        if (moved) SetPlaylistDirty(true);
        return moved;
    }

    /// <summary>
    /// Saves the playlist: in its own file, or (<paramref name="asPath"/>) in another one, which the caller then opens. False after a message
    /// if it could not be written.
    /// </summary>
    public bool SavePlaylist(string? asPath = null)
    {
        if (!_inPlaylist) return false;
        try
        {
            PlaylistFile.Write(asPath ?? _currentPath, CurrentItems.Select(i => new PlaylistEntry(i.FullPath, !i.IsMissing, i.PlaylistInfo)));
            SetPlaylistDirty(false);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The playlist could not be saved.\n\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>
    /// Before the playlist is left (another folder, the tab or the program closes) with changes that are not saved: asks. False = stay
    /// (Cancel, or the save failed); true = go on (saved, or the changes were thrown away).
    /// </summary>
    public bool ConfirmLeavePlaylist()
    {
        if (!_playlistDirty) return true;

        var answer = MessageBox.Show($"Do you want to save the changes to the playlist \"{Path.GetFileName(_currentPath)}\"?",
            AppInfo.Name, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return SavePlaylist();

        SetPlaylistDirty(false);
        return true;
    }

    private void LoadArchiveItems(string path)
    {
        try
        {
            // In the order they are stored in the archive: the list is not sorted until the user picks a column.
            foreach (var entry in ArchiveSupport.List(path))
            {
                if ((entry.IsDirectory || entry.IsArchive) && !_shared.ShowFolders) continue;   // the folders (and packed files) are in the tree

                bool hidden = !entry.IsDirectory && HiddenItems.IsHiddenInArchive(entry.Name);
                if (hidden) _hiddenCount++;
                if (hidden && !_showHidden) continue;

                var vm = new FileItemViewModel
                {
                    Name         = entry.Name,
                    FullPath     = entry.FullPath,
                    IsDirectory  = entry.IsDirectory,
                    IsHidden     = hidden,
                    IsInArchive  = true,
                    DateModified = entry.Modified,
                    DateCreated  = entry.Modified,   // an archive keeps one date for an entry
                    TypeName     = entry.IsDirectory ? "File folder" : IconHelper.GetTypeNameForName(entry.Name),
                    Size         = entry.Size
                };
                CurrentItems.Add(vm);
                vm.LoadIconAsync();
            }

            StatusText = "";
            UpdateReadState(path);
        }
        catch (Exception ex)
        {
            StatusText = $"Cannot read the archive: {ex.Message}";
        }
    }

    /// <summary>
    /// The path of a file on the disk to hand to another program: the file itself, or for a file inside an archive
    /// a copy unpacked to the temporary folder. Null (after a message) if it cannot be unpacked.
    /// </summary>
    public static string? RealPathOf(FileItemViewModel item)
    {
        if (!item.IsInArchive) return item.FullPath;

        try
        {
            return ArchiveSupport.ExtractToTemp(item.FullPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot open '{item.Name}': {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    // ── The picture of the folder (under the tree) ───────────────────────────

    private CancellationTokenSource? _coverCts;
    private FolderCover? _folderCover;

    /// <summary>The picture that stands for the current folder (a cover); null for most folders.</summary>
    public System.Windows.Media.ImageSource? FolderPicture => _folderCover?.Image;

    public bool HasFolderPicture => _folderCover != null;

    /// <summary>The picture as a file the picture viewer can open: the file itself, or a copy in the temporary folder for a cover embedded in a music file.</summary>
    public string? GetFolderPictureFile()
    {
        var cover = _folderCover;
        if (cover == null) return null;
        if (cover.Path != null) return cover.Path;

        try { return ArchiveSupport.SaveToTemp("Cover.jpg", cover.Data!); }
        catch { return null; }
    }

    /// <summary>Looks for the picture of the current folder again (a picture was chosen for it, or the choice was removed).</summary>
    public void RefreshFolderPicture()
    {
        if (!string.IsNullOrEmpty(_currentPath) && !_noFolderShown) UpdateFolderPicture(_currentPath);
    }

    /// <summary>The path is the Bookmarks folder or inside it (a group).</summary>
    public static bool IsUnderBookmarks(string path)
    {
        try
        {
            var root = Path.GetFullPath(BookmarkStore.Folder).TrimEnd('\\', '/') + "\\";
            return (Path.GetFullPath(path).TrimEnd('\\', '/') + "\\").StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Looks for the picture of <paramref name="path"/> in the background; the old one goes at once, as it is not this folder's.</summary>
    private void UpdateFolderPicture(string path)
    {
        _coverCts?.Cancel();
        var cts = _coverCts = new CancellationTokenSource();
        var token = cts.Token;

        if (_folderCover != null)
        {
            _folderCover = null;
            OnPropertyChanged(nameof(FolderPicture));
            OnPropertyChanged(nameof(HasFolderPicture));
        }

        var dispatcher = Application.Current.Dispatcher;
        _ = Task.Run(() =>
        {
            var cover = FolderCoverFinder.Find(path, 500, token);   // 500 px: sharp enough for the widest tree pane
            if (token.IsCancellationRequested) return;

            // The Bookmarks node and its groups: no picture of their own, so the bookmark ribbon (drawn on the UI thread).
            if (cover == null && IsUnderBookmarks(path))
            {
                dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    _folderCover = FolderCoverFinder.ForVirtual("bookmark");
                    OnPropertyChanged(nameof(FolderPicture));
                    OnPropertyChanged(nameof(HasFolderPicture));
                });
                return;
            }
            if (cover == null) return;

            dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;   // another folder was opened meanwhile
                _folderCover = cover;
                OnPropertyChanged(nameof(FolderPicture));
                OnPropertyChanged(nameof(HasFolderPicture));
            });
        });
    }

    // ── Tags of the folder (read from its desktop.ini; read only) ────────────

    private string _folderTags = "";
    private CancellationTokenSource? _folderTagsCts;

    /// <summary>"Tags: ..." for the current folder, from its desktop.ini (where Windows and TeraTool keep them); empty if it has none.</summary>
    public string FolderTagsText => _folderTags.Length == 0 ? "" : "Tags: " + _folderTags;
    public bool HasFolderTags => _folderTags.Length > 0;

    private void SetFolderTags(string tags)
    {
        if (_folderTags == tags) return;
        _folderTags = tags;
        OnPropertyChanged(nameof(FolderTagsText));
        OnPropertyChanged(nameof(HasFolderTags));
    }

    /// <summary>The tags of the current folder were changed (Set Tags): read them again.</summary>
    public void ReloadFolderTags() => UpdateFolderTags(_currentPath);

    /// <summary>Reads the tags of the folder in the background (a desktop.ini on a slow drive must not hold up the list); the old ones go at once.</summary>
    private void UpdateFolderTags(string path)
    {
        _folderTagsCts?.Cancel();
        var cts = _folderTagsCts = new CancellationTokenSource();
        var token = cts.Token;

        SetFolderTags("");

        var dispatcher = Application.Current.Dispatcher;
        _ = Task.Run(() =>
        {
            var tags = FolderTagReader.Read(path);
            if (tags == null || token.IsCancellationRequested) return;

            dispatcher.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested) SetFolderTags(tags);   // another folder was opened meanwhile: ignored
            });
        });
    }

    // ── Tags of music files (the Artist, Title and Track Number columns) ─────

    private CancellationTokenSource? _tagsCts;

    /// <summary>Raised on the UI thread when the tags of the folder have been read (a sort by a tag column can then be redone).</summary>
    public event Action? TagsLoaded;

    /// <summary>
    /// Reads the tags of the music files of the list that have not been read yet, a few at a time in the background,
    /// if a column that needs them is shown. Each cell fills in when its file is done.
    /// </summary>
    public void EnsureTags()
    {
        if (!_shared.TagColumnsVisible || (_inArchive && !_inPlaylist)) return;   // not on the disk: the tags of a file in an archive are not read (yet)

        var pending = CurrentItems
            .Where(i => !i.IsDirectory && !i.TagsRequested && AudioTagReader.HasMediaInfo(i.FullPath))
            .ToList();
        if (pending.Count == 0) return;

        foreach (var item in pending) item.TagsRequested = true;

        _tagsCts ??= new CancellationTokenSource();
        var token      = _tagsCts.Token;
        var dispatcher = Application.Current.Dispatcher;

        _ = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(pending,
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                    (item, _) =>
                    {
                        var tags = AudioTagReader.Read(item.FullPath);
                        if (tags != null) dispatcher.InvokeAsync(() => item.SetTags(tags));
                        return ValueTask.CompletedTask;
                    });

                if (!token.IsCancellationRequested)
                    await dispatcher.InvokeAsync(() => TagsLoaded?.Invoke());
            }
            catch (OperationCanceledException) { /* another folder was opened */ }
            catch { /* the cells stay empty */ }
        });
    }

    /// <summary>Reads the tags of these music files again (they were edited, e.g. in the Properties window).</summary>
    public void ReloadTags(IEnumerable<FileItemViewModel> items)
    {
        foreach (var item in items) item.TagsRequested = false;
        EnsureTags();   // when the tags are in, TagsLoaded lets the window sort again if it is sorted by a tag column
    }

    private void LoadItems(string path, FolderListing? preread = null)
    {
        _tagsCts?.Cancel();   // the tags of the folder we leave are no longer wanted
        _tagsCts = null;

        CurrentItems.Clear();
        _hiddenCount = 0;
        ClearReadState();
        UpdateTreeHidden(path, preread?.Counts);

        bool playlist = PlaylistFile.IsPlaylist(path);
        SetPlaylistDirty(false);   // a fresh read
        _inArchive = playlist || ArchiveSupport.IsInside(path);   // a playlist is read-only too
        if (playlist != _inPlaylist)
        {
            _inPlaylist = playlist;
            OnPropertyChanged(nameof(InPlaylist));
        }
        OnPropertyChanged(nameof(InArchive));
        RaiseAllCanExecuteChanged();   // nothing can be deleted or pasted in an archive

        if (_isActive)
        {
            // The folder is watched once the list is filled (see below); an archive is not watched.
            if (_inArchive) StopWatching();
            else
            {
                StartWatching(path);
                // Its tree node may be out of date: a folder made inside it by another program while it was not the one shown
                // (found by the user: only F5 showed it). The in-place sync looks at the node once, in the background.
                _treeSeen = null;
                SyncListInPlace();
            }
        }

        if (playlist)
        {
            LoadPlaylistItems(path);
            OnPropertyChanged(nameof(HasHiddenItems));
            OnPropertyChanged(nameof(HiddenText));
            return;
        }

        if (_inArchive)
        {
            LoadArchiveItems(path);
            OnPropertyChanged(nameof(HasHiddenItems));
            OnPropertyChanged(nameof(HiddenText));
            return;
        }

        try
        {
            // Read here, or already read in the background (a click in the tree). If the "Folders" button was toggled while it was
            // being read, the listing is read again here.
            var listing = preread != null && preread.ShowFolders == _shared.ShowFolders
                ? preread
                : ReadListing(path, _shared.ShowFolders, countTreeHidden: false);
            if (listing.Error != null) throw listing.Error;

            // Folders first (sorted)
            var folders = listing.Dirs;

            foreach (var dir in folders)
            {
                try
                {
                    bool hidden = (dir.Attributes & HiddenAttributes) != 0;
                    if (hidden) _hiddenCount++;
                    if (hidden && !_showHidden) continue;

                    var vm = NewDirItem(dir, hidden);
                    CurrentItems.Add(vm);
                    vm.LoadIconAsync();
                }
                catch { }
            }

            // Files (sorted)
            foreach (var file in listing.Files)
            {
                try
                {
                    // A packed file is a folder here (it is in the tree): listed only when the folders are.
                    if (!_shared.ShowFolders && TreeItemViewModel.IsTreeFile(file.Name)) continue;

                    bool hidden = HiddenItems.IsHiddenFile(file.Attributes, file.Name);
                    if (hidden) _hiddenCount++;
                    if (hidden && !_showHidden) continue;

                    var vm = NewFileItem(file, hidden);
                    CurrentItems.Add(vm);
                    vm.LoadIconAsync();
                }
                catch { }
            }

            StatusText = "";
            EnsureTags();
            UpdateReadState(path);
        }
        catch (UnauthorizedAccessException)
        {
            StatusText = "Access denied";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }

        OnPropertyChanged(nameof(HasHiddenItems));
        OnPropertyChanged(nameof(HiddenText));
    }

    private static FileItemViewModel NewDirItem(DirectoryInfo dir, bool hidden) => new()
    {
        Name         = dir.Name,
        FullPath     = dir.FullName,
        IsDirectory  = true,
        IsHidden     = hidden,
        DateModified = dir.LastWriteTime,
        DateCreated  = dir.CreationTime,
        TypeName     = "File folder",
        Size         = -1
    };

    private static FileItemViewModel NewFileItem(FileInfo file, bool hidden) => new()
    {
        Name         = file.Name,
        FullPath     = file.FullName,
        IsDirectory  = false,
        IsHidden     = hidden,
        DateModified = file.LastWriteTime,
        DateCreated  = file.CreationTime,
        TypeName     = IconHelper.GetTypeNameForName(file.Name),   // from the extension, kept per extension: no disk access per file (Google Drive took 6 ms each)
        Size         = file.Length
    };

    // ── The folder shown is watched: the list follows changes made by other programs ──────────────────────────

    // Only the active tab watches its folder (one watcher at a time); a tab that becomes active brings its list up to date at once.
    // A change is not applied to the list one event at a time: the events of a burst (a download, a copy of 400 files) are
    // collected, the folder is read once on a background thread and the list is **brought in line in place** (items added, removed
    // or updated; no reload, so the selection, the scroll position and the sort stay). See AppSettings.WatchFolders.
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _parentWatcher;   // the folder that holds the one shown: sees it renamed or deleted by another program
    private volatile string? _renamedTo;          // where another program renamed the folder shown to
    private FileSystemWatcher? _treeWatcher;   // folders made, renamed and deleted at any depth below the folder shown (names of folders only)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _dirtyParents = new(StringComparer.OrdinalIgnoreCase);
    private string? _watchedPath;
    private bool _isActive;
    private bool _syncing, _syncAgain;
    private System.Windows.Threading.DispatcherTimer? _watchTimer;
    private DateTime _watchBurstStart;

    private static readonly TimeSpan WatchQuiet   = TimeSpan.FromMilliseconds(600);   // no event for this long: apply
    private static readonly TimeSpan WatchMaxWait = TimeSpan.FromSeconds(3);          // events never stop (a growing log): apply anyway

    /// <summary>Raised after the list was brought in line in place with values that may change the order (size, dates): the window sorts again.</summary>
    public event Action? ItemsChangedInPlace;

    /// <summary>Called by MainViewModel when this tab becomes the active tab (true) or stops being it (false).</summary>
    public void SetActive(bool active)
    {
        if (_isActive == active) return;
        _isActive = active;
        if (!active) { StopWatching(); return; }

        StartWatchingCurrent();
        SyncListInPlace();   // the folder may have changed while this tab was in the background
    }

    private void StartWatchingCurrent()
    {
        if (!_isActive || !AppSettingsStore.Current.WatchFolders || _noFolderShown || _unavailable || _inArchive
            || string.IsNullOrEmpty(_currentPath) || !Directory.Exists(_currentPath))
        {
            StopWatching();
            return;
        }
        StartWatching(_currentPath);
    }

    private void StartWatching(string path)
    {
        if (_watcher != null && string.Equals(_watchedPath, path, StringComparison.OrdinalIgnoreCase)) return;
        StopWatching();
        try
        {
            var w = new FileSystemWatcher(path)
            {
                NotifyFilter          = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                                        | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                IncludeSubdirectories = false,
                InternalBufferSize    = 64 * 1024,
            };
            w.Created += OnWatched;
            w.Deleted += OnWatched;
            w.Changed += OnWatched;
            w.Renamed += OnWatched;
            w.Error   += (_, _) => OnWatched(null, null);   // too many changes for the buffer: read the folder again
            w.EnableRaisingEvents = true;
            _watcher     = w;
            _watchedPath = path;

            // The second watcher is for the tree: a folder made inside a folder that the tree shows (not the one shown) gives that node an
            // expand arrow, or takes it away. Folder names only, so the files of the subtree cost nothing; one watch for the whole subtree.
            var t = new FileSystemWatcher(path)
            {
                NotifyFilter          = NotifyFilters.DirectoryName,
                IncludeSubdirectories = true,
                InternalBufferSize    = 64 * 1024,
            };
            t.Created += OnTreeWatched;
            t.Deleted += OnTreeWatched;
            t.Renamed += OnTreeWatched;
            t.EnableRaisingEvents = true;
            _treeWatcher = t;

            // The third one watches the folder above (folder names only, not below it): the watcher of the folder shown itself says
            // nothing when that folder is renamed or deleted, and the tree node of the folder above must follow the change.
            var parent = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                var pw = new FileSystemWatcher(parent)
                {
                    NotifyFilter          = NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                };
                pw.Created += OnParentWatched;
                pw.Deleted += OnParentWatched;
                pw.Renamed += OnParentWatched;
                pw.EnableRaisingEvents = true;
                _parentWatcher = pw;
            }
        }
        catch
        {
            // A place that cannot be watched (some network shares, no access): the list is then read on F5 and on activation, as before.
            _watcher = null;
            _watchedPath = null;
        }
    }

    private void StopWatching()
    {
        _watchTimer?.Stop();
        _dirtyParents.Clear();
        _renamedTo = null;
        var pw = _parentWatcher;
        _parentWatcher = null;
        if (pw != null) { pw.EnableRaisingEvents = false; pw.Dispose(); }
        var t = _treeWatcher;
        _treeWatcher = null;
        if (t != null) { t.EnableRaisingEvents = false; t.Dispose(); }
        var w = _watcher;
        _watcher = null;
        _watchedPath = null;
        if (w == null) return;
        w.EnableRaisingEvents = false;
        w.Dispose();
    }

    private void OnParentWatched(object? sender, FileSystemEventArgs e)
    {
        try
        {
            var parent = Path.GetDirectoryName(e.FullPath);
            if (!string.IsNullOrEmpty(parent)) _dirtyParents[parent] = 0;
            if (e is RenamedEventArgs r && IsSamePath(r.OldFullPath, _watchedPath)) _renamedTo = r.FullPath;   // the folder shown got a new name
        }
        catch { /* not a usable path */ }
        Application.Current?.Dispatcher.BeginInvoke(new Action(KickWatch), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnTreeWatched(object? sender, FileSystemEventArgs e)
    {
        // The folder that holds the one that came, went or was renamed: its node in the tree is the one to look at.
        try
        {
            var parent = Path.GetDirectoryName(e.FullPath);
            if (!string.IsNullOrEmpty(parent)) _dirtyParents[parent] = 0;
            if (e is RenamedEventArgs r && Path.GetDirectoryName(r.OldFullPath) is { Length: > 0 } oldParent) _dirtyParents[oldParent] = 0;
        }
        catch { /* not a usable path */ }
        Application.Current?.Dispatcher.BeginInvoke(new Action(KickWatch), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// The node of a folder that is already in the tree below the folder shown, found without expanding anything (null if the tree does not
    /// have it: nobody sees such a folder, so there is nothing to update).
    /// </summary>
    private TreeItemViewModel? FindLoadedNode(string path)
    {
        if (string.IsNullOrEmpty(_currentPath)) return null;
        var node = ResolveNode(_currentPath);   // the folder shown: the way to it is expanded already
        if (node == null || !NodeIs(node, _currentPath)) return null;

        var relative = Path.GetRelativePath(_currentPath, path);
        if (relative == ".") return node;
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return null;

        foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            var wanted = Path.Combine(node.FullPath, segment);
            node = node.Children.FirstOrDefault(c => NodeIs(c, wanted));
            if (node == null) return null;
        }
        return node;
    }

    /// <summary>The node of the folder that holds the folder shown (it is in the tree: the way to the folder shown is expanded).</summary>
    private TreeItemViewModel? FindNodeOfParent(string path)
    {
        if (string.IsNullOrEmpty(_currentPath) || !IsSamePath(Path.GetDirectoryName(_currentPath.TrimEnd('\\', '/')), path)) return null;
        var node = ResolveNode(path);
        return node != null && NodeIs(node, path) ? node : null;
    }

    /// <summary>The folders that changed below the folder shown: each of their nodes in the tree reads its subfolders again, in place (a node that is not expanded only checks whether it has an expand arrow).</summary>
    private void SyncDirtyTreeNodes()
    {
        if (_dirtyParents.IsEmpty) return;
        var parents = _dirtyParents.Keys.ToList();
        _dirtyParents.Clear();

        foreach (var parent in parents)
        {
            try { (FindLoadedNode(parent) ?? FindNodeOfParent(parent))?.SyncChildren(); }
            catch { /* a folder that went away meanwhile */ }
        }
    }

    private void OnWatched(object? sender, FileSystemEventArgs? e)
    {
        // The temporary files of our own paste: they are renamed to the real name when complete, and that is what shows.
        if (e?.Name != null && e.Name.EndsWith(".swewolf-part", StringComparison.OrdinalIgnoreCase)) return;
        Application.Current?.Dispatcher.BeginInvoke(new Action(KickWatch), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void KickWatch()
    {
        if (!_isActive || _watcher == null) return;

        var now = DateTime.UtcNow;
        if (_watchTimer == null || !_watchTimer.IsEnabled) _watchBurstStart = now;

        _watchTimer ??= new System.Windows.Threading.DispatcherTimer();
        _watchTimer.Stop();
        // Wait until it is quiet, but never longer than WatchMaxWait since the burst began.
        var left = WatchMaxWait - (now - _watchBurstStart);
        _watchTimer.Interval = left < WatchQuiet ? (left > TimeSpan.FromMilliseconds(50) ? left : TimeSpan.FromMilliseconds(50)) : WatchQuiet;
        _watchTimer.Tick -= WatchTimerTick;
        _watchTimer.Tick += WatchTimerTick;
        _watchTimer.Start();
    }

    private void WatchTimerTick(object? sender, EventArgs e)
    {
        _watchTimer?.Stop();
        SyncDirtyTreeNodes();

        // The folder shown was renamed by another program: follow it (the tree already shows the new name).
        var renamed = _renamedTo;
        _renamedTo = null;
        if (renamed != null && Directory.Exists(renamed) && !Directory.Exists(_currentPath))
        {
            Navigate(renamed, addToHistory: false);
            return;
        }
        SyncListInPlace();
    }

    /// <summary>Reads the folder in the background and brings the list in line with it in place. Never touches an archive, This PC or an unavailable tab.</summary>
    private async void SyncListInPlace()
    {
        var path = _currentPath;
        if (!_isActive || string.IsNullOrEmpty(path) || _noFolderShown || _unavailable || _inArchive) return;
        if (_syncing) { _syncAgain = true; return; }

        _syncing = true;
        try
        {
            int version = _navVersion;
            bool showFolders = _shared.ShowFolders;
            FolderListing listing;
            string treeSignature;
            try
            {
                // The list and the tree are two views of the folder: what the tree shows (subfolders, packed files) is not what the list shows.
                (listing, treeSignature) = await Task.Run(() => (ReadListing(path, showFolders, countTreeHidden: true), TreeItemViewModel.ChildrenSignature(path)));
            }
            catch (DirectoryNotFoundException) { FolderIsGone(path); return; }
            catch { return; }   // no access, a drive that went away: the list stays as it is

            // Another folder was opened, the Folders button toggled or the tab left meanwhile: this answer is old.
            if (version != _navVersion || !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase)
                || showFolders != _shared.ShowFolders || !_isActive || _inArchive) return;

            ApplyListing(path, listing, treeSignature);
        }
        finally
        {
            _syncing = false;
            if (_syncAgain) { _syncAgain = false; KickWatch(); }
        }
    }

    /// <summary>The folder shown was deleted or renamed by another program: go to the nearest folder above that is left.</summary>
    private void FolderIsGone(string path)
    {
        if (Directory.Exists(path)) return;
        var left = path;
        while (!string.IsNullOrEmpty(left) && !Directory.Exists(left)) left = Path.GetDirectoryName(left) ?? "";
        if (left != "") Navigate(left, addToHistory: false);
        else StatusText = $"The folder no longer exists: {path}";
    }

    private (string Path, string Signature)? _treeSeen;   // what the tree was last brought in line with

    private void ApplyListing(string path, FolderListing listing, string treeSignature)
    {
        // What the list should hold: the same choices as LoadItems (hidden items, packed files, the Folders button).
        var wanted = new Dictionary<string, (FileSystemInfo Info, bool Hidden)>(StringComparer.OrdinalIgnoreCase);
        int hiddenCount = 0;

        foreach (var dir in listing.Dirs)
        {
            bool hidden = (dir.Attributes & HiddenAttributes) != 0;
            if (hidden) hiddenCount++;
            if (hidden && !_showHidden) continue;
            wanted[dir.Name] = (dir, hidden);
        }
        foreach (var file in listing.Files)
        {
            if (!_shared.ShowFolders && TreeItemViewModel.IsTreeFile(file.Name)) continue;
            bool hidden = HiddenItems.IsHiddenFile(file.Attributes, file.Name);
            if (hidden) hiddenCount++;
            if (hidden && !_showHidden) continue;
            wanted[file.Name] = (file, hidden);
        }

        bool structural = false, valuesChanged = false;
        var changedFiles = new List<FileItemViewModel>();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in CurrentItems.ToList())
        {
            if (!wanted.TryGetValue(item.Name, out var w) || w.Info is DirectoryInfo != item.IsDirectory || w.Hidden != item.IsHidden)
            {
                CurrentItems.Remove(item);   // gone, or turned into something else (a hidden one when hidden ones are not shown)
                structural = true;
                continue;
            }

            present.Add(item.Name);
            if (w.Info is FileInfo f)
            {
                if (item.Size != f.Length || item.DateModified != f.LastWriteTime || item.DateCreated != f.CreationTime)
                {
                    item.Size = f.Length;
                    item.DateModified = f.LastWriteTime;
                    item.DateCreated = f.CreationTime;
                    changedFiles.Add(item);
                    valuesChanged = true;
                }
            }
            else if (item.DateModified != w.Info.LastWriteTime)
            {
                item.DateModified = w.Info.LastWriteTime;
                valuesChanged = true;
            }
        }

        // New items: folders first, then files, as LoadItems does (the sort then places them).
        foreach (var (name, w) in wanted.OrderByDescending(x => x.Value.Info is DirectoryInfo).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (present.Contains(name)) continue;
            var vm = w.Info is DirectoryInfo d ? NewDirItem(d, w.Hidden) : NewFileItem((FileInfo)w.Info, w.Hidden);
            CurrentItems.Add(vm);
            vm.LoadIconAsync();
            structural = true;
        }

        if (hiddenCount != _hiddenCount)
        {
            _hiddenCount = hiddenCount;
            OnPropertyChanged(nameof(HasHiddenItems));
            OnPropertyChanged(nameof(HiddenText));
        }

        if (structural)
        {
            EnsureTags();
            UpdateReadState(path);
            UpdateFolderPicture(path);
        }

        // The tree: a folder or a packed file (a zip) that came, went, was renamed or got hidden under the folder shown. Decided from the
        // folder's own subfolders and packed files, not from the list (which hides folders and packed files unless the Folders button is
        // on). SyncChildren works in place: the nodes that stay keep their place, what is expanded below them, and the selection.
        if (_treeSeen is not { } seen || !string.Equals(seen.Path, path, StringComparison.OrdinalIgnoreCase) || seen.Signature != treeSignature)
        {
            _treeSeen = (path, treeSignature);
            UpdateTreeHidden(path, listing.Counts);
            var node = ResolveNode(path);
            if (node != null && NodeIs(node, path)) node.SyncChildren();
        }
        if (changedFiles.Count > 0) ReloadTags(changedFiles);   // a changed music file may have other tags now
        if (valuesChanged) ItemsChangedInPlace?.Invoke();
    }

    // ── Item actions ─────────────────────────────────────────────────────────

    public void OpenItem(FileItemViewModel item)
    {
        // A packed file opens like a folder (a real one, or a zip inside a zip).
        if (item.IsDirectory || ArchiveSupport.IsArchiveName(item.Name) || PlaylistFile.IsPlaylistName(item.Name))
        {
            Navigate(item.FullPath);
            return;
        }

        var path = RealPathOf(item);
        if (path == null) return;

        // Video and music play in SweWolf Media Player, which marks them read after 60 seconds of playing (the explorer
        // does not know how long it was watched). A file from an archive is remembered under its path in the archive.
        if (MediaPlayerLauncher.IsMedia(item.Name))
        {
            var result = MediaPlayerLauncher.Open(path, item.IsInArchive ? item.FullPath : null);
            if (result != LaunchResult.UseDefaultProgram)
            {
                if (result == LaunchResult.Started) LibraryService.SetLastOpened(item.FullPath);   // opened (not canceled)
                return;
            }
        }

        if (OpenFile(path))
        {
            MarkRead(new[] { item }, read: true);
            LibraryService.SetLastOpened(item.FullPath);   // "Jump to Last Opened File" finds it
        }
        else if (!item.IsInArchive && !File.Exists(path))
        {
            Refresh();   // the file is gone: the list no longer shows it
        }
    }

    /// <summary>The window was activated: something may have been marked in another program (the media player).</summary>
    public void RefreshReadState()
    {
        if (!string.IsNullOrEmpty(_currentPath) && !_noFolderShown && CurrentItems.Count > 0)
            UpdateReadState(_currentPath);
    }

    public static bool OpenFile(string path)
    {
        // A .nfo file: Windows gives them to System Information, but almost all are text or XML; those go to the text editor.
        if (NfoOpener.TryOpenInTextEditor(path)) return true;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            // The user said no to the "run as administrator" question of Windows: not an error.
            if (ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }) return false;

            // The file is gone (deleted or renamed by another program): say which file, nothing about this program.
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                MessageBox.Show($"The file \"{name}\" was not found. It may have been moved, renamed or deleted.",
                    AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // Windows' own text ("The system cannot find the file specified.") without the program, folder and path the framework adds.
            var reason = ex is System.ComponentModel.Win32Exception w ? new System.ComponentModel.Win32Exception(w.NativeErrorCode).Message : ex.Message;
            MessageBox.Show($"Cannot open \"{name}\": {reason}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ── Read and unread (the library) ────────────────────────────────────────

    private int _readVersion;   // an answer from the library for a folder we have left is ignored

    private string _folderStatusText = "";

    /// <summary>"Status: Unread / Partly Read / Read" for the current folder, under the tree (as in TeraTool). Empty for an empty folder.</summary>
    public string FolderStatusText
    {
        get => _folderStatusText;
        private set
        {
            if (_folderStatusText == value) return;
            _folderStatusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FolderStatusLine));
        }
    }

    /// <summary>What the line under the tree says: "Empty Folder" when nothing is in the folder (no files, folders, archive files or hidden items), else the status.</summary>
    public string FolderStatusLine => IsEmptyFolder ? "Empty Folder" : _folderStatusText;

    private bool IsEmptyFolder =>
        HasFolder && _treeCounts is { Folders: 0, Archives: 0, HiddenFolders: 0, HiddenArchives: 0 } && CurrentItems.Count == 0 && _hiddenCount == 0;

    private void ClearReadState()
    {
        _readVersion++;
        FolderStatusText = "";
        SetFolderMarks(default);
    }

    /// <summary>
    /// Asks the library (in the background) which files of the folder were opened, then bolds the others; the ratings and
    /// expectations of the items come in the same answer.
    /// </summary>
    private void UpdateReadState(string path)
    {
        int version = ++_readVersion;
        int startEpoch = _markEpoch;   // a rating set after this point is newer than what this read finds
        var dispatcher = Application.Current.Dispatcher;

        Task.Run(() =>
        {
            FolderInfo info;
            Dictionary<string, LibraryStore.Marks> marks;
            LibraryStore.Marks own;
            try
            {
                WaitForMarkWrites();   // a rating just set is written before the folder is read again
                info  = LibraryService.Sync(path, create: false);
                marks = LibraryStore.GetRatings(path);
                own   = LibraryStore.GetMark(path);
            }
            catch { return; }   // the list stays as it is
            dispatcher.InvokeAsync(() =>
            {
                if (version != _readVersion) return;
                ApplyReadState(info);
                foreach (var item in CurrentItems)
                {
                    // An item rated on the screen after this read began keeps what the user just set: the read may have
                    // started (when a dialog closed and the window woke up) before the new value was written.
                    if (_markChanged.TryGetValue(item.FullPath, out var changed) && changed > startEpoch) continue;

                    marks.TryGetValue(item.Name, out var m);
                    item.SetMarks(m.Rating, m.Expectation);
                }
                if (!(_markChanged.TryGetValue(FolderPath, out var folderChanged) && folderChanged > startEpoch))
                    SetFolderMarks(own);
            });
        });
    }

    // ── Rating and expectation (1-10, kept in the library) ───────────────────

    private LibraryStore.Marks _folderMarks;

    /// <summary>"Rating: 8" for the current folder, under the tree (clicking it edits it).</summary>
    public string FolderRatingText => "Rating: " + (_folderMarks.Rating is { } r ? RatingRules.Format(r) : "-");

    /// <summary>"Expectation: 7.5" for the current folder, under the tree.</summary>
    public string FolderExpectationText => "Expectation: " + (_folderMarks.Expectation is { } e ? RatingRules.Format(e) : "-");

    /// <summary>The current folder has a rating or an expectation: only then the row under the tree is shown.</summary>
    public bool HasFolderMarks => _folderMarks.Rating != null || _folderMarks.Expectation != null;

    private void SetFolderMarks(LibraryStore.Marks marks)
    {
        _folderMarks = marks;
        OnPropertyChanged(nameof(HasFolderMarks));
        OnPropertyChanged(nameof(FolderRatingText));
        OnPropertyChanged(nameof(FolderExpectationText));
    }

    /// <summary>The rating and expectation of a file or folder (for the input box to start with).</summary>
    public LibraryStore.Marks GetMarks(string path) =>
        IsSamePath(path, _currentPath) ? _folderMarks : LibraryStore.GetMark(path);

    /// <summary>Sets (null removes) the rating of files and folders. Shown at once; saved in the background.</summary>
    public void SetRating(IReadOnlyCollection<string> paths, int? value) => ApplyMark(paths, isRating: true, value, null);

    /// <summary>Sets (null removes) the expectation of files and folders. Shown at once; saved in the background.</summary>
    public void SetExpectation(IReadOnlyCollection<string> paths, double? value) => ApplyMark(paths, isRating: false, null, value);

    private void ApplyMark(IReadOnlyCollection<string> paths, bool isRating, int? rating, double? expectation)
    {
        if (paths.Count == 0) return;

        int epoch = ++_markEpoch;
        foreach (var path in paths) _markChanged[path] = epoch;

        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var item in CurrentItems)
        {
            if (!wanted.Contains(item.FullPath)) continue;
            item.SetMarks(isRating ? rating : item.Rating, isRating ? item.Expectation : expectation);
        }

        if (paths.Any(p => IsSamePath(p, _currentPath)))   // the folder shown: its label under the tree
            SetFolderMarks(new LibraryStore.Marks(
                isRating ? rating : _folderMarks.Rating, isRating ? _folderMarks.Expectation : expectation));

        var dispatcher = Application.Current.Dispatcher;
        var list = paths.ToList();

        // Saved in the background, one after the other; reading the folder waits for them (see UpdateReadState), or the
        // window's re-reading when a dialog closes would bring back the old value before it was written.
        LibraryService.Queue(() =>
        {
            bool saved = isRating ? LibraryStore.SetRating(list, rating) : LibraryStore.SetExpectation(list, expectation);
            if (!saved)
                dispatcher.InvokeAsync(() =>
                    StatusText = $"The {(isRating ? "rating" : "expectation")} could not be saved. The drive may be write-protected.");
        });
    }

    // Which items were rated on the screen, and when (a counter): see UpdateReadState. Only the UI thread touches them.
    private int _markEpoch;
    private readonly Dictionary<string, int> _markChanged = new(StringComparer.OrdinalIgnoreCase);

    private static void WaitForMarkWrites() => LibraryService.WaitForQueued();

    private void ApplyReadState(FolderInfo info)
    {
        foreach (var item in CurrentItems)
            if (!item.IsDirectory && !ArchiveSupport.IsArchiveName(item.Name))
                item.SetUnread(!item.IsHidden && !info.ReadNames.Contains(item.Name));

        FolderStatusText = info.State switch
        {
            FolderState.Unread     => "Status: Unread",
            FolderState.PartlyRead => "Status: Partly Read",
            FolderState.Read       => "Status: Read",
            _                      => "",
        };
    }

    /// <summary>The library says the state of a folder changed (a file was marked, a tree was marked): update if it is the one shown.</summary>
    private void OnLibraryFolderChanged(string folder, FolderInfo info)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (string.Equals(SharedState.Key(folder), SharedState.Key(_currentPath), StringComparison.OrdinalIgnoreCase))
            {
                _readVersion++;
                ApplyReadState(info);
            }
        });
    }

    /// <summary>Files were opened (or marked): they stop being bold at once, the library remembers it in the background.</summary>
    public void MarkRead(IEnumerable<FileItemViewModel> items, bool read)
    {
        var files = items.Where(i => !i.IsDirectory && !ArchiveSupport.IsArchiveName(i.Name)).ToList();
        if (files.Count == 0) return;

        foreach (var item in files) item.SetUnread(!read && !item.IsHidden);
        LibraryService.SetRead(files.Select(i => i.FullPath), read);
    }

    /// <summary>A file of the current folder was opened somewhere else in the window (the picture viewer).</summary>
    public void MarkPathRead(string path)
    {
        var item = CurrentItems.FirstOrDefault(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (item != null) MarkRead(new[] { item }, read: true);
        else LibraryService.SetRead(new[] { path }, true);

        LibraryService.SetLastOpened(path);   // the picture shown last in the viewer is the last opened file of its folder
    }

    /// <summary>Mark as Read / Unread on a folder: everything below it. Slow for big trees, so done in the background.</summary>
    public void MarkFolderTree(string folder, bool read)
    {
        StatusText = read ? "Marking as read..." : "Marking as unread...";
        Task.Run(() =>
        {
            try { LibraryService.MarkTree(folder, read); }
            catch { /* nothing remembered */ }
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                StatusText = "";
                UpdateReadState(_currentPath);
            });
        });
    }

    public void DeleteSelected(bool permanent = false)
    {
        if (_inPlaylist)   // the files stay where they are: only the entries leave the list
        {
            RemoveFromPlaylist(_selectedItems.ToList());
            return;
        }
        DeleteEntries(_selectedItems.Select(i => (i.FullPath, i.Name, i.IsDirectory)).ToList(), permanent);
    }

    /// <summary>Moves a folder of the tree to the Recycle Bin (its right-click menu).</summary>
    public void DeleteFolder(string path)
    {
        if (_playlistDirty && IsSamePath(path, _currentPath) && !ConfirmLeavePlaylist()) return;   // the playlist shown, with changes not saved
        DeleteEntries(new() { (path, Path.GetFileName(path.TrimEnd('\\', '/')), Directory.Exists(path)) }, permanent: false);   // (a playlist of the tree is a file)
    }

    private bool _deleting;

    private static string? ParentFolder(string path) =>
        Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>
    /// Deletes the items (to the Recycle Bin, or for good) on a background thread with a progress window (after half a second) and a
    /// Cancel button: see <see cref="FileDelete"/>. Items that cannot reach the Recycle Bin (a drive with none, a file bigger than the
    /// bin's limit) are said so in the question, as they would be deleted for good.
    /// </summary>
    private async void DeleteEntries(List<(string Path, string Name, bool IsDirectory)> items, bool permanent)
    {
        if (items.Count == 0 || _deleting) return;

        var names = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count} items";
        var nl2   = Environment.NewLine;

        // The questions (and the flash message when none is needed) are in DeleteConfirm: same rules for the list, the tree and the picture viewer.
        bool go;
        _deleting = true;   // a second Delete while folders are being looked at is ignored
        try { go = await DeleteConfirm.AskAsync(null, items, permanent); }
        finally { _deleting = false; }
        if (!go) return;

        var entries = items.Select(i => new FileDelete.Item(i.Path, i.Name, i.IsDirectory)).ToList();

        TransferResult outcome;
        _deleting = true;
        try
        {
            // Windows' own Recycle Bin call deletes for good what cannot go to the bin (the user said yes to that above).
            outcome = await RunWithProgressAsync((progress, token) => FileDelete.RunAsync(entries, permanent, progress, token),
                                                 "Deleting", names, ParentFolder(items[0].Path) ?? "", heading: $"Deleting {names}",
                                                 icon: IconHelper.GetRecycleBinIcon(full: true));
        }
        finally { _deleting = false; }

        var deleted = outcome.Done.Select(d => d.From).ToList();

        if (outcome.Failed.Count > 0)
        {
            var lines = outcome.Failed.Take(8).Select(f => $"{f.Name}: {f.Reason}");
            var more  = outcome.Failed.Count > 8 ? $"{nl2}... and {outcome.Failed.Count - 8} more." : "";
            MessageBox.Show($"These could not be deleted:{nl2}{nl2}" + string.Join(nl2, lines) + more,
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // What is remembered about them (read marks, ratings ...) goes with them. A restore from the Recycle Bin starts over.
        LibraryService.Forget(deleted);

        // Once for each folder the items were in: reading the same folder for every one of 400 files took minutes on a slow drive.
        foreach (var parent in items.Select(i => ParentFolder(i.Path)).Distinct(StringComparer.OrdinalIgnoreCase))
            RefreshTreeFolder(parent);

        // The folder shown may be one of those deleted (or inside one): go to the nearest folder that is left.
        if (!Directory.Exists(_currentPath) && !ArchiveSupport.IsInside(_currentPath))
        {
            var left = _currentPath;
            while (!string.IsNullOrEmpty(left) && !Directory.Exists(left)) left = Path.GetDirectoryName(left) ?? "";
            if (left != "") Navigate(left, addToHistory: false);
        }
        else
        {
            Refresh();
        }

        if (outcome.Canceled)
            StatusText = $"Delete canceled: {outcome.Done.Count} of {outcome.Total} items were deleted.";
    }


    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    /// <summary>Renames a folder of the tree (its right-click menu).</summary>
    public void RenameFolder(string path, string newName) =>
        RenameItem(new FileItemViewModel { Name = Path.GetFileName(path.TrimEnd('\\', '/')), FullPath = path, IsDirectory = Directory.Exists(path) }, newName);   // (a playlist of the tree is a file)

    /// <summary>
    /// What is wrong with <paramref name="newName"/> as the new name of the item at <paramref name="path"/>:
    /// the message to show, or null if the name can be used. The rename dialog asks this, so it stays open
    /// until the name is good (or Cancel is clicked).
    /// </summary>
    public static string? CheckNewName(string path, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0)
            return "Type a name.";

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "A file name cannot contain any control characters or any of the following characters:\n\\ / : * ? \" < > |";

        var dir = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
        if (dir == null) return null;

        // A change of case only (a.txt -> A.txt) points at the same item: allow it.
        var newPath = Path.Combine(dir, newName);
        bool sameItem = string.Equals(newPath, path, StringComparison.OrdinalIgnoreCase);
        if (!sameItem && (File.Exists(newPath) || Directory.Exists(newPath)))
            return $"'{newName}' already exists in this folder. Choose a different name.";

        return null;
    }

    /// <summary>
    /// A video or music file is renamed or moved: finds the files that belong to it by name (<see cref="RelatedFiles"/>) and asks
    /// whether they follow (Yes: all of them, No: only the file itself, Cancel: nothing happens). Returns false on Cancel. Asked
    /// before anything is changed. <paramref name="related"/> holds the (from, to) pairs to do when the answer is Yes, else it is empty.
    /// </summary>
    /// <param name="media">Each video or music file with the path it is going to get.</param>
    /// <param name="alsoMoving">Paths that are moved anyway (cut together): they are not asked about.</param>
    private static bool AskAboutRelated(IReadOnlyList<(string Media, string NewMedia)> media, ISet<string>? alsoMoving, bool rename,
                                        out List<RelatedMove> related, out List<NotDone> notDone)
    {
        related = new List<RelatedMove>();
        notDone = new List<NotDone>();
        var willMove = new List<RelatedMove>();
        var skipped  = new List<NotDone>();   // (no longer filled: a taken name gets a number, see below)
        var planned  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // the new names handed out in this question
        int numbered = 0;

        foreach (var (path, newPath) in media)
        {
            if (!RelatedFiles.HasRelated(path)) continue;

            foreach (var file in RelatedFiles.Find(path))
            {
                if (alsoMoving != null && alsoMoving.Contains(file)) continue;

                var target = Path.Combine(Path.GetDirectoryName(newPath)!,
                    RelatedFiles.NewName(Path.GetFileName(file), Path.GetFileName(path), Path.GetFileName(newPath)));

                if (string.Equals(target, file, StringComparison.Ordinal)) continue;   // nothing changes for this one
                bool sameFile = string.Equals(target, file, StringComparison.OrdinalIgnoreCase);   // a change of case only

                // The new name is taken (by something else already there, or by another file of this question): the file still follows,
                // with a number, as Windows does ("Movie (2005).en (1).srt"). Only the video or music file itself is never given a taken name.
                if ((!sameFile && (File.Exists(target) || Directory.Exists(target))) || planned.Contains(target))
                {
                    var dir  = Path.GetDirectoryName(target)!;
                    var stem = Path.GetFileNameWithoutExtension(target);
                    var ext  = Path.GetExtension(target);
                    string free;
                    int n = 1;
                    do { free = Path.Combine(dir, $"{stem} ({n++}){ext}"); }
                    while (File.Exists(free) || Directory.Exists(free) || planned.Contains(free));
                    target = free;
                    numbered++;
                }

                planned.Add(target);
                willMove.Add(new RelatedMove(file, target, path));
            }
        }

        if (willMove.Count == 0 && skipped.Count == 0) return true;   // nothing to ask about

        // Only the names of the files that would follow; the new names are made by the same pattern as the video's.
        var list = RelatedFiles.Describe(willMove.Select(m => Path.GetFileName(m.From)).ToList(),
                                         skipped.Select(s => Path.GetFileName(s.From)).ToList());

        string verb  = rename ? "rename" : "move";
        string title = rename ? (media.Count == 1 ? "Rename File" : "Multiple Rename") : media.Count == 1 ? "Move File" : "Move Files";
        string head  = media.Count == 1
            ? $"You are {verb.TrimEnd('e')}ing \"{Path.GetFileName(media[0].Media)}\"" +
              (rename ? $" to \"{Path.GetFileName(media[0].NewMedia)}\"." : $" to \"{Path.GetDirectoryName(media[0].NewMedia)}\".")
            : $"You are {verb.TrimEnd('e')}ing {media.Count} video or music files.";

        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

        // Only files that cannot follow (their new names are taken): nothing to decide, only to know.
        if (willMove.Count == 0)
        {
            Ask(owner, title, $"{head}\n\n{list}\n\nThey are not {(rename ? "renamed" : "moved")}.", new[] { "OK" }, 0, -1);
            LogNotDone(skipped, rename);
            return true;
        }

        string ask = rename
            ? "Do you also want to rename these files according to the same pattern?"
            : "Do you also want to move these files?";

        if (numbered > 0)
            list += $"\n\n{numbered} of them get a number, as their new name is taken already.";

        string question = $"{head}\n\n{ask}\n\n{list}\n\n" +
                          $"Yes: {char.ToUpper(verb[0])}{verb[1..]} them too.\n" +
                          $"No: {char.ToUpper(verb[0])}{verb[1..]} only the video or music file.\n" +
                          "Cancel: Change nothing.";

        int answer = Ask(owner, title, question, new[] { "Yes", "No", "Cancel" }, 0, 2);
        if (answer == 2) return false;
        if (answer == 0) { related = willMove; notDone = skipped; }   // the skipped ones are told about at the end
        return true;
    }

    /// <summary>A related file to rename or move, and the video or music file it belongs to (it only follows if that one was done).</summary>
    private sealed record RelatedMove(string From, string To, string Owner);

    /// <summary>A related file that could not be renamed or moved: where from, where to, and why (in words the user can read).</summary>
    private sealed record NotDone(string From, string To, string Reason);

    private static void LogNotDone(IEnumerable<NotDone> items, bool rename)
    {
        foreach (var n in items)
            LogFiles.AppendError($"Could not {(rename ? "rename" : "move")} \"{n.From}\" to \"{n.To}\": {n.Reason}");
    }

    // The app's own dialog (it follows the style the user chose, which a Windows message box does not).
    private static int Ask(Window? owner, string title, string message, IList<string> buttons, int defaultIndex, int cancelIndex)
    {
        var dialog = new SwewolfFileExplorer.Views.ChoiceDialog(message, buttons, defaultIndex, cancelIndex)
        {
            Title    = title,
            FontSize = 16,
            MinWidth = 760,
        };
        if (owner != null) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Result;
    }

    /// <summary>Renames or moves the related files; what is remembered about them follows. A failure is reported once, at the end.</summary>
    private static List<(string From, string To)> MoveRelated(List<RelatedMove> moves, List<NotDone> notDone, bool rename, string? title = null)
    {
        var done = new List<(string From, string To)>();

        // Every file that can be done is done; the ones that cannot are collected (and the ones skipped before, whose
        // new name was taken) and told about once at the end.
        foreach (var (from, to, _) in moves)
        {
            try
            {
                if (File.Exists(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("a file with the new name already exists");   // it appeared since the question

                File.Move(from, to);
                done.Add((from, to));
            }
            catch (Exception ex)
            {
                notDone.Add(new NotDone(from, to, ReasonOf(ex)));
            }
        }

        LibraryService.Move(done);

        if (notDone.Count == 0) return done;

        LogNotDone(notDone, rename);   // the log has the full paths and the system's words

        var verb  = rename ? "renamed" : "moved";
        var lines = notDone.Take(15).Select(n => $"    {Path.GetFileName(n.From)}  ({n.Reason})").ToList();
        if (notDone.Count > 15) lines.Add($"    ... and {notDone.Count - 15} more");

        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        Ask(owner, title ?? (rename ? "Rename File" : "Move File"),
            $"These files were not {verb}:\n\n{string.Join("\n", lines)}\n\nThe other files were {verb}. Details are in the log file (Help > Open Log File).",
            new[] { "OK" }, 0, -1);
        return done;
    }

    // Short words for the usual reasons; anything else is the system's own text.
    private static string ReasonOf(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access denied",
        FileNotFoundException or DirectoryNotFoundException => "the file is gone",
        IOException io when io.Message.Contains("being used", StringComparison.OrdinalIgnoreCase) => "the file is in use",
        _ => ex.Message.TrimEnd('.'),
    };

    // ── Smart rename of all the selected music and video files, and undoing it ───────────────────

    /// <summary>The (old path, new path) pairs of the last "rename all with smart rename" in this folder, kept until another folder is shown.</summary>
    private List<(string From, string To)>? _renameUndo;
    private string? _renameUndoFolder;
    private bool _renamingBatch;

    /// <summary>True while the last smart rename of several files can be undone (the button next to "File 3/4").</summary>
    public bool HasRenameUndo => _renameUndo is { Count: > 0 };

    public string RenameUndoTip => _renameUndo is { } u
        ? $"Give the {u.Count} renamed {(u.Count == 1 ? "file" : "files")} their old names back. Possible until another folder is shown."
        : "";

    private void SetRenameUndo(List<(string From, string To)>? pairs)
    {
        _renameUndo       = pairs is { Count: > 0 } ? pairs : null;
        _renameUndoFolder = _renameUndo == null ? null : _currentPath;
        OnPropertyChanged(nameof(HasRenameUndo));
        OnPropertyChanged(nameof(RenameUndoTip));
    }

    /// <summary>Another folder is shown (or no folder): the last smart rename can no longer be undone.</summary>
    private void ForgetRenameUndoUnlessHere(string? path)
    {
        if (_renameUndo == null) return;
        if (path != null && IsSamePath(path, _renameUndoFolder)) return;
        SetRenameUndo(null);
    }

    /// <summary>What a smart rename of the selected files would do: the files that get a new name, and the ones that cannot.</summary>
    private sealed record SmartPlan(List<(FileItemViewModel Item, string NewName)> Renames, List<NotDone> Left, int Candidates);

    private static SmartPlan PlanSmartRename(IReadOnlyList<FileItemViewModel> items)
    {
        var renames    = new List<(FileItemViewModel, string)>();
        var left       = new List<NotDone>();
        int candidates = 0;
        var given      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // the paths handed out in this batch

        foreach (var item in items)
        {
            if (item.IsDirectory || item.IsInArchive || !SmartName.Applies(item.Name)) continue;
            candidates++;

            // The same suggestion as the Rename box makes: no better name means this file is left alone (not an error).
            var suggested = SmartName.Suggest(item.FullPath);
            if (suggested == null || string.Equals(suggested, item.Name, StringComparison.Ordinal)) continue;

            var target  = Path.Combine(Path.GetDirectoryName(item.FullPath)!, suggested);
            var problem = CheckNewName(item.FullPath, suggested);
            if (problem != null)
                left.Add(new NotDone(item.FullPath, target, problem.Contains("already exists", StringComparison.Ordinal)
                    ? "a file with the new name already exists" : "the new name is not allowed"));
            else if (!given.Add(target))
                left.Add(new NotDone(item.FullPath, target, "another selected file gets the same new name"));
            else
                renames.Add((item, suggested));
        }

        return new SmartPlan(renames, left, candidates);
    }

    /// <summary>
    /// "Rename All Selected Music Files and Video Files Using Smart Rename": every selected music or video file gets the name the Rename
    /// box suggests with "Use Smart Name". Asks first (Multiple Rename: OK / Cancel), then once about the files that belong to them
    /// (subtitles, posters ...). What is remembered about the files (read marks, ratings ...) follows, and the rename can be undone
    /// until another folder is shown. Returns the new paths, so the window can select them again.
    /// </summary>
    public async Task<List<string>> SmartRenameSelectedAsync(IReadOnlyList<FileItemViewModel> selected)
    {
        var renamed = new List<string>();
        if (_renamingBatch || InArchive || selected.Count == 0) return renamed;
        _renamingBatch = true;

        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        try
        {
            // The tags of the files are read to find the names: a moment for many files.
            Mouse.OverrideCursor = Cursors.Wait;
            SmartPlan plan;
            try { plan = await Task.Run(() => PlanSmartRename(selected)); }
            finally { Mouse.OverrideCursor = null; }

            if (plan.Candidates == 0)
            {
                Ask(owner, "Multiple Rename", "None of the selected files is a music file or a video file.", new[] { "OK" }, 0, -1);
                return renamed;
            }
            if (plan.Renames.Count == 0)
            {
                string why = plan.Left.Count > 0
                    ? $"\n\n{plan.Left.Count} {(plan.Left.Count == 1 ? "file has a better name, which is" : "files have better names, which are")} taken by other files."
                    : "";
                Ask(owner, "Multiple Rename", "None of the selected music files and video files has a better name than it has now." + why,
                    new[] { "OK" }, 0, -1);
                if (plan.Left.Count > 0) LogNotDone(plan.Left, rename: true);
                return renamed;
            }

            int n = plan.Renames.Count;
            string question = "Do you want to rename all selected music files and video files using smart rename?\n\n" +
                              $"{n} {(n == 1 ? "file gets" : "files get")} a new name." +
                              (plan.Left.Count > 0 ? $"\n{plan.Left.Count} {(plan.Left.Count == 1 ? "file is" : "files are")} left as {(plan.Left.Count == 1 ? "it is" : "they are")} (the new name is taken)." : "") +
                              (plan.Candidates > n + plan.Left.Count ? "\nThe others have no better name." : "");
            if (Ask(owner, "Multiple Rename", question, new[] { "OK", "Cancel" }, 1, 1) != 0) return renamed;

            // The files that belong to them (subtitles, poster ...): asked once for all of them, before anything changes.
            var media = plan.Renames.Select(r => (r.Item.FullPath, Path.Combine(Path.GetDirectoryName(r.Item.FullPath)!, r.NewName))).ToList();
            if (!AskAboutRelated(media, null, rename: true, out var related, out var notDone)) return renamed;
            notDone.AddRange(plan.Left);

            // The files themselves first, on a background thread (a slow drive must not freeze the window).
            Mouse.OverrideCursor = Cursors.Wait;
            var done = new List<(string From, string To)>();
            try
            {
                await Task.Run(() =>
                {
                    foreach (var (from, to) in media)
                    {
                        try
                        {
                            if (File.Exists(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                                throw new IOException("a file with the new name already exists");   // it appeared since the question
                            File.Move(from, to);
                            done.Add((from, to));
                        }
                        catch (Exception ex)
                        {
                            notDone.Add(new NotDone(from, to, ReasonOf(ex)));
                        }
                    }
                });
            }
            finally { Mouse.OverrideCursor = null; }

            LibraryService.Move(done);   // read marks, ratings, resume points ... follow the new names

            // A related file only follows a file whose own rename worked.
            var doneFrom    = new HashSet<string>(done.Select(d => d.From), StringComparer.OrdinalIgnoreCase);
            var relatedDone = MoveRelated(related.Where(r => doneFrom.Contains(r.Owner)).ToList(), notDone, rename: true, title: "Multiple Rename");   // tells about everything that was not done

            SetRenameUndo(done.Concat(relatedDone).ToList());
            Refresh();
            StatusText = $"{done.Count} {(done.Count == 1 ? "file" : "files")} renamed";
            return done.Select(d => d.To).ToList();
        }
        finally
        {
            _renamingBatch = false;
        }
    }

    /// <summary>
    /// Gives the files of the last smart rename their old names back (the Undo button). Every file is checked first: one that is gone
    /// or whose old name is taken again is not touched and is told about; the rest are restored, and what is remembered about them
    /// follows. What could not be restored stays in the undo list. Returns the restored paths.
    /// </summary>
    public async Task<List<string>> UndoSmartRenameAsync()
    {
        var restored = new List<string>();
        if (_renamingBatch || _renameUndo is not { Count: > 0 } pairs) return restored;
        _renamingBatch = true;

        try
        {
            var problems = new List<NotDone>();
            var todo     = new List<(string Now, string Original)>();

            // Last renamed, first restored.
            foreach (var (original, now) in Enumerable.Reverse(pairs))
            {
                bool caseOnly = string.Equals(original, now, StringComparison.OrdinalIgnoreCase);
                if (!File.Exists(now))
                    problems.Add(new NotDone(now, original, "the file is gone"));
                else if (!caseOnly && (File.Exists(original) || Directory.Exists(original)))
                    problems.Add(new NotDone(now, original, "a file with the old name exists now"));
                else
                    todo.Add((now, original));
            }

            var done = new List<(string Now, string Original)>();
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                await Task.Run(() =>
                {
                    foreach (var (now, original) in todo)
                    {
                        try
                        {
                            File.Move(now, original);
                            done.Add((now, original));
                        }
                        catch (Exception ex)
                        {
                            problems.Add(new NotDone(now, original, ReasonOf(ex)));
                        }
                    }
                });
            }
            finally { Mouse.OverrideCursor = null; }

            LibraryService.Move(done);   // what is remembered goes back to the old names too

            // What was not restored stays in the list, so it can be tried again.
            var restoredNow = new HashSet<string>(done.Select(d => d.Now), StringComparer.OrdinalIgnoreCase);
            SetRenameUndo(pairs.Where(p => !restoredNow.Contains(p.To)).ToList());

            Refresh();
            StatusText = $"{done.Count} {(done.Count == 1 ? "file" : "files")} got the old name back";

            if (problems.Count > 0)
            {
                LogNotDone(problems.Select(p => new NotDone(p.From, p.To, p.Reason)), rename: true);
                var lines = problems.Take(15).Select(p => $"    {Path.GetFileName(p.From)}  ({p.Reason})").ToList();
                if (problems.Count > 15) lines.Add($"    ... and {problems.Count - 15} more");
                var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
                Ask(owner, "Undo Rename", $"These files could not get their old names back:\n\n{string.Join("\n", lines)}\n\nThe other files were restored. Details are in the log file (Help > Open Log File).",
                    new[] { "OK" }, 0, -1);
            }

            return done.Select(d => d.Original).ToList();
        }
        finally
        {
            _renamingBatch = false;
        }
    }

    public void RenameItem(FileItemViewModel item, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrEmpty(newName) || newName == item.Name) return;
        if (_playlistDirty && !item.IsDirectory && IsSamePath(item.FullPath, _currentPath) && !ConfirmLeavePlaylist()) return;   // the playlist shown, with changes not saved

        var problem = CheckNewName(item.FullPath, newName);
        if (problem != null)
        {
            MessageBox.Show(problem, "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // A video or music file: do the files that belong to it (subtitles, poster ...) follow? Asked before anything changes.
        var relatedMoves = new List<RelatedMove>();
        var notDone = new List<NotDone>();
        if (!item.IsDirectory && !item.IsInArchive &&
            !AskAboutRelated(new[] { (item.FullPath, Path.Combine(Path.GetDirectoryName(item.FullPath)!, newName)) },
                             null, rename: true, out relatedMoves, out notDone))
            return;

        try
        {
            var dir     = Path.GetDirectoryName(item.FullPath)!;
            var newPath = Path.Combine(dir, newName);

            bool sameItem = string.Equals(newPath, item.FullPath, StringComparison.OrdinalIgnoreCase);

            // Where the tab must go afterwards, worked out now, before anything changes: the folder shown
            // is the one renamed (or inside it), so it is at a new place when the move is done.
            string? newCurrent = null;
            if (item.IsDirectory && !sameItem && IsSameOrSubPath(item.FullPath, _currentPath))
            {
                var rest = Path.GetRelativePath(item.FullPath, _currentPath);
                newCurrent = rest == "." ? newPath : Path.Combine(newPath, rest);
            }
            else if (!item.IsDirectory && !sameItem && IsSamePath(item.FullPath, _currentPath))
            {
                newCurrent = newPath;   // the playlist shown is renamed: it is open under its new name
            }

            if (item.IsDirectory) Directory.Move(item.FullPath, newPath);
            else                  File.Move(item.FullPath, newPath);

            // What is remembered about it (read marks, ratings, a folder's state and what is below it) follows the new name.
            LibraryService.Move(new[] { (item.FullPath, newPath) });

            // The Undo button of a smart rename is for right after it: another rename (by hand) ends it.
            SetRenameUndo(null);

            // The files that belong to it, if the user said yes.
            MoveRelated(relatedMoves, notDone, rename: true);

            // The tree first: it must know the new name before it is asked for the node of the folder.
            RefreshTreeFolder(dir);

            if (newCurrent != null)
            {
                Navigate(newCurrent, addToHistory: false);
            }
            else
            {
                Refresh();

                // The renamed item stays the one in focus: in the tree, and in the list if it is listed.
                if (item.IsDirectory) SelectFolderInTree(newPath);
                SelectPathRequested?.Invoke(newPath);
            }
        }
        catch (Exception ex)
        {
            // Say who holds the file when it is in use (Windows' Restart Manager), and keep it in the log.
            var holders = ex is IOException && !item.IsDirectory ? FileLockInfo.WhoUses(item.FullPath) : new List<string>();
            string who = holders.Count > 0 ? "\n\nIn use by: " + string.Join(", ", holders) : "";
            LogFiles.AppendError($"Rename failed: {item.FullPath} -> {newName}: {ex.GetType().Name}: {ex.Message}" +
                                 (holders.Count > 0 ? " In use by: " + string.Join(", ", holders) : ""));
            MessageBox.Show($"Cannot rename: {ex.Message}{who}", AppInfo.Name,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void CreateNewFolder() => CreateNewFolderIn(_currentPath);

    /// <summary>Makes a new folder in <paramref name="parent"/> (the current folder, or a folder of the tree) and asks for its name.</summary>
    public void CreateNewFolderIn(string parent)
    {
        var baseName = "New Folder";
        var path     = Path.Combine(parent, baseName);
        int i        = 1;

        while (Directory.Exists(path))
            path = Path.Combine(parent, $"New Folder ({i++})");

        try
        {
            Directory.CreateDirectory(path);
            Refresh();
            RefreshTreeFolder(parent);

            // Go into the new folder, so the tree and the list agree on where we are (the list is empty).
            // It then is the current folder while its name is asked for, and the rename follows it.
            Navigate(path);

            // The window asks for the name. With the folders hidden the new folder is not in the list,
            // but it can still be renamed.
            var newItem = CurrentItems.FirstOrDefault(x => x.FullPath == path)
                ?? new FileItemViewModel { Name = Path.GetFileName(path), FullPath = path, IsDirectory = true };
            FolderCreated?.Invoke(newItem);

            // The name box was canceled: the folder made a moment ago is removed again, but only while it is still empty (another
            // program may have put something in it). No message: a failure here is not worth the user's attention.
            if (FolderCreationCanceled)
            {
                FolderCreationCanceled = false;
                RemoveNewFolderIfEmpty(path, parent);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot create folder: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Set by the window when the name box of a new folder was canceled (it asks for the name inside <see cref="FolderCreated"/>).</summary>
    public bool FolderCreationCanceled { get; set; }

    private void RemoveNewFolderIfEmpty(string path, string parent)
    {
        try
        {
            if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any()) return;
            Directory.Delete(path, recursive: false);   // only an empty folder is deleted, never with contents
        }
        catch { return; }

        // We stood in the new folder while its name was asked for: back to the folder it was made in, and the tree is read again.
        if (IsSamePath(_currentPath, path)) Navigate(parent);
        Refresh();
        RefreshTreeFolder(parent);
    }

    // ── Clipboard ────────────────────────────────────────────────────────────

    private void CopySelected() => PutOnClipboard(cut: false);

    private void CutSelected() => PutOnClipboard(cut: true);

    /// <summary>
    /// Puts the selected items on the Windows clipboard. What is in an archive is unpacked to the
    /// temporary folder first, as the clipboard can only hold files on the disk.
    /// </summary>
    private void PutOnClipboard(bool cut)
    {
        var paths = new List<string>();
        foreach (var item in _selectedItems)
        {
            var path = RealPathOf(item);
            if (path == null) return;   // it said why
            paths.Add(path);
        }

        PutPathsOnClipboard(paths, cut);
    }

    /// <summary>The same for a folder of the tree (its right-click menu).</summary>
    public void PutFolderOnClipboard(string folder, bool cut)
    {
        var path = folder;
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            try { path = ArchiveSupport.ExtractToTemp(folder); }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot copy '{Path.GetFileName(folder)}': {ex.Message}", AppInfo.Name,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        PutPathsOnClipboard(new List<string> { path }, cut);
    }

    private void PutPathsOnClipboard(List<string> paths, bool cut)
    {
        try
        {
            FileClipboard.Set(paths, cut);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot use the clipboard: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        PasteCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Copy as Path: the paths as text, each in quotes and one to a line, as Explorer does.</summary>
    public void CopyPathsAsText(IEnumerable<string> paths)
    {
        try
        {
            FileClipboard.SetText(string.Join(Environment.NewLine, paths.Select(p => "\"" + p + "\"")));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot use the clipboard: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Paste()
    {
        if (_inPlaylist) PasteIntoPlaylist();
        else if (TryPasteAsBookmarks(_currentPath)) return;
        else PasteInto(_currentPath);
    }

    /// <summary>
    /// The Bookmarks folder only holds bookmarks: a paste there makes bookmarks (paths, web addresses, files and folders on the clipboard)
    /// or says why it cannot be done; the window does it (<see cref="BookmarksPasteRequested"/>). Not for files that come from the Bookmarks
    /// folder itself: moving a bookmark to another group is an ordinary cut and paste.
    /// </summary>
    /// <summary>True for the Bookmarks folder and its groups: files cannot be dropped there (only bookmarks live in it).</summary>
    public bool IsBookmarksFolder(string path) => IsUnderBookmarks(path);

    private bool TryPasteAsBookmarks(string destination)
    {
        if (!IsUnderBookmarks(destination)) return false;
        if (FileClipboard.Get() is { } files && files.Paths.Any(IsUnderBookmarks)) return false;

        BookmarksPasteRequested?.Invoke(destination);
        return true;
    }

    /// <summary>A paste in the Bookmarks folder (or a group): the window makes the bookmarks of what is on the clipboard.</summary>
    public event Action<string>? BookmarksPasteRequested;

    /// <summary>
    /// Ctrl+V. The paste goes into <paramref name="destination"/> (a folder of the tree; null = the folder shown). When there is nothing to
    /// paste, or nowhere to paste it, a message says so (the user: a key that does nothing looks like a fault).
    /// </summary>
    public void PasteFromKeyboard(string? destination)
    {
        if (_pasting) return;
        if (_inPlaylist && destination == null) { PasteIntoPlaylist(); return; }   // a playlist takes links to the files

        destination ??= HasFolder ? _currentPath : null;
        if (string.IsNullOrEmpty(destination) || (destination == _currentPath && !HasFolder))
        {
            MessageBox.Show("Files cannot be pasted here. Open a folder first.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ArchiveSupport.IsInside(destination) || !Directory.Exists(destination))
        {
            MessageBox.Show("Files cannot be pasted here. A compressed file is read-only, and this folder is not on the disk.",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (TryPasteAsBookmarks(destination)) return;
        if (!FileClipboard.HasFiles())
        {
            // A picture or text is there: offer to make a file of it (the same as "New File from Clipboard").
            var kind = ClipboardFile.Detect();
            if (kind != ClipboardFile.Kind.None)
            {
                var what = kind == ClipboardFile.Kind.Image ? "a picture" : "text";
                var make = kind == ClipboardFile.Kind.Image ? "a new PNG file" : "a new text file";
                if (MessageBox.Show($"There are no files on the clipboard, but there is {what}.\nDo you want to save it as {make} in this folder?",
                        AppInfo.Name, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
                    ClipboardFileRequested?.Invoke(destination);
                return;
            }

            MessageBox.Show("There are no files on the clipboard to paste.\nCopy or cut some files first.", AppInfo.Name,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        PasteInto(destination);
    }

    /// <summary>Ctrl+V found a picture or text and not files, and the user said yes: the window asks for the name and makes the file in this folder.</summary>
    public event Action<string>? ClipboardFileRequested;

    private bool _pasting;   // a paste is going on (its window may not be up yet): another one waits for the user to ask again

    /// <summary>
    /// Pastes what is on the clipboard into <paramref name="destination"/>, a folder of the disk. The files are copied or moved on
    /// a background thread; a long one shows a progress window (file n of N, a bar, Cancel). See <see cref="FileTransfer"/>.
    /// </summary>
    public async void PasteInto(string destination, List<string>? dropped = null, bool dropMove = false)
    {
        if (_pasting) return;

        // Files dropped from Explorer (or another program) are handled like a paste of those files; the clipboard is not touched.
        List<string> sources;
        bool isCut;
        if (dropped != null) (sources, isCut) = (dropped, dropMove);
        else if (FileClipboard.Get() is { } clip) (sources, isCut) = (clip.Paths, clip.IsCut);
        else
        {
            if (RemoteFiles.HasVirtualFiles()) await PasteRemoteAsync(destination);
            return;
        }

        // Cut video or music files: do the files that belong to them follow? Asked before anything is moved; Cancel stops the paste.
        var relatedMoves = new List<RelatedMove>();
        var notDone = new List<NotDone>();
        if (isCut)
        {
            var media = sources
                .Where(s => File.Exists(s) && RelatedFiles.HasRelated(s) && !IsSamePath(Path.GetDirectoryName(s), destination))
                .Select(s => (Media: s, NewMedia: GetUniqueDestPath(destination, Path.GetFileName(s))))
                .ToList();

            if (media.Count > 0 && !AskAboutRelated(media, new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase), rename: false, out relatedMoves, out notDone))
                return;
        }

        // What there really is to do.
        var toDo = new List<string>();
        foreach (var src in sources)
        {
            var name = Path.GetFileName(src.TrimEnd('\\', '/'));

            if (Directory.Exists(src) && IsSameOrSubPath(src, destination))
            {
                MessageBox.Show(
                    $"Cannot {(isCut ? "move" : "copy")} '{name}' into itself or one of its subfolders.",
                    AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            // Cutting an item and pasting it where it already is changes nothing.
            if (isCut && IsSamePath(Path.GetDirectoryName(src), destination))
                continue;

            if (Directory.Exists(src) || File.Exists(src)) toDo.Add(src);
        }

        var result = new TransferResult(new(), new(), false, 0);
        if (toDo.Count > 0)
        {
            _pasting = true;
            try { result = await RunTransferAsync(toDo, destination, isCut); }
            finally { _pasting = false; }
        }

        if (result.Failed.Count > 0)
        {
            var lines = result.Failed.Take(8).Select(f => $"{f.Name}: {f.Reason}");
            var more  = result.Failed.Count > 8 ? $"{Environment.NewLine}... and {result.Failed.Count - 8} more." : "";
            MessageBox.Show($"These could not be {(isCut ? "moved" : "copied")}:{Environment.NewLine}{Environment.NewLine}"
                            + string.Join(Environment.NewLine, lines) + more,
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var moved = isCut ? result.Done : new List<(string From, string To)>();
        LibraryService.Move(moved);   // what is remembered about the moved items (to another drive too) goes along

        // The files that belong to a moved video or music file (nothing if nothing was moved).
        // Only for a video or music file whose own move worked; the others' related files stay where they are.
        var movedFrom = new HashSet<string>(moved.Select(m => m.From), StringComparer.OrdinalIgnoreCase);
        var followers = relatedMoves.Where(r => movedFrom.Contains(r.Owner)).ToList();
        if (followers.Count > 0 || notDone.Count > 0) MoveRelated(followers, notDone, rename: false);

        if (isCut && !result.Canceled && dropped == null)
        {
            FileClipboard.Clear();   // what was cut is moved: there is nothing left to paste (after a Cancel the rest can be pasted again)
            PasteCommand.RaiseCanExecuteChanged();
        }

        Refresh();
        RefreshTreeFolder(destination);   // the folder pasted into need not be the current one

        // A folder that was moved away must also go from the tree where it was: read the folders it was taken out of again.
        foreach (var parent in moved.Select(m => Path.GetDirectoryName(m.From.TrimEnd('\\', '/'))).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            RefreshTreeFolder(parent);

        // Make the result visible: the pasted items are selected and scrolled into view in the list (when that folder is the one
        // shown), and a pasted folder is selected in the tree.
        var pasted = result.Done
            .Select(d => TopLevelUnder(d.To, destination))   // a merged folder reports its files: the folder itself is what stands out
            .Where(p => p != null).Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (pasted.Count > 0)
        {
            if (IsSamePath(destination, _currentPath))
            {
                // One folder pasted: go into it, so the tree and the list agree (a tree selection opens the folder in the list).
                // Otherwise the pasted items are selected in the list and the tree is left alone.
                if (pasted.Count == 1 && Directory.Exists(pasted[0])) Navigate(pasted[0]);
                else SelectPathsRequested?.Invoke(pasted);
            }
            else if (pasted.Count == 1 && Directory.Exists(pasted[0])) SelectFolderInTree(pasted[0]);
        }

        if (result.Canceled)
            StatusText = $"{(isCut ? "Move" : "Copy")} canceled: {result.Done.Count} of {result.Total} items were done.";
        else if (result.Skipped > 0)
            StatusText = $"{result.Skipped} {(result.Skipped == 1 ? "file was" : "files were")} skipped.";
    }

    /// <summary>The item directly inside <paramref name="folder"/> that <paramref name="path"/> is (or lies below), else null.</summary>
    private static string? TopLevelUnder(string path, string folder)
    {
        var p = path.TrimEnd('\\', '/');
        for (int i = 0; i < 64 && !string.IsNullOrEmpty(p); i++)
        {
            var parent = Path.GetDirectoryName(p);
            if (string.IsNullOrEmpty(parent)) return null;
            if (IsSamePath(parent, folder)) return p;
            p = parent;
        }
        return null;
    }

    /// <summary>
    /// After a video file was renamed with "Use Smart Name" (and Settings > File Names > Auto-Suggest File Move When Renaming Video
    /// Files is on): offers to move it to <c>Videos\Movies</c> or <c>Videos\TV Shows\Show Name</c>, as TeraTool did. The user
    /// can change the folder; a folder that is not there is made. Nothing is offered when the file already is in the Videos folder, or
    /// when the new name has neither a year nor an episode. The move is the same as a Cut and Paste (progress window, related files,
    /// what is remembered about the file follows).
    /// </summary>
    public async void OfferMoveAfterSmartRename(string path)
    {
        if (!AppSettingsStore.Current.SuggestMoveAfterSmartRename || _pasting) return;
        if (!File.Exists(path) || !MediaPlayerLauncher.IsVideo(path)) return;

        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos)) return;
        if (IsSameOrSubPath(videos, path)) return;   // it is in the Videos folder already: nothing to suggest

        var target = SmartName.MoveTarget(Path.GetFileNameWithoutExtension(path));
        if (target == null) return;

        var dir        = Path.GetDirectoryName(path)!;
        var name       = Path.GetFileName(path);
        var suggested  = target.Value.FolderName.Length > 0
            ? Path.Combine(videos, target.Value.Subfolder, target.Value.FolderName)   // a show: TV Shows\Show Name
            : Path.Combine(videos, target.Value.Subfolder);                           // a movie: Movies
        var chosen     = Views.MoveFolderDialog.Ask(Application.Current?.MainWindow, name, dir, suggested);
        if (chosen == null) return;

        try { Directory.CreateDirectory(chosen); }
        catch (Exception ex)
        {
            MessageBox.Show($"The folder could not be made: {ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Subtitles, poster ... : do they follow? (Cancel: the file stays where it is.)
        if (!AskAboutRelated(new[] { (path, Path.Combine(chosen, name)) }, null, rename: false, out var relatedMoves, out var notDone))
            return;

        TransferResult result;
        _pasting = true;
        try { result = await RunTransferAsync(new List<string> { path }, chosen, move: true); }
        finally { _pasting = false; }

        if (result.Failed.Count > 0)
            MessageBox.Show($"The file could not be moved: {result.Failed[0].Reason}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);

        LibraryService.Move(result.Done);
        var movedFrom = new HashSet<string>(result.Done.Select(m => m.From), StringComparer.OrdinalIgnoreCase);
        var followers = relatedMoves.Where(r => movedFrom.Contains(r.Owner)).ToList();
        if (followers.Count > 0 || notDone.Count > 0) MoveRelated(followers, notDone, rename: false);

        Refresh();
        RefreshTreeFolder(chosen);
    }

    /// <summary>
    /// Runs the transfer and, when it takes longer than half a second, shows the progress window (a quick one finishes before a
    /// window would be worth the flicker, as in Explorer). The window is modal: nothing else can be changed meanwhile.
    /// </summary>
    private Task<TransferResult> RunTransferAsync(List<string> sources, string destination, bool move)
    {
        var what   = move ? "Moving" : "Copying";
        var thing  = sources.Count == 1 ? $"'{Path.GetFileName(sources[0].TrimEnd('\\', '/'))}'" : $"{sources.Count} items";
        var policy = NewConflictPolicy();
        return RunWithProgressAsync((progress, token) => FileTransfer.RunAsync(sources, destination, move, progress, token, policy), what, thing, destination, policy);
    }

    /// <summary>The "file exists" question: shown on the UI thread (the copying runs on another), on top of the window that is in front.</summary>
    private static ConflictPolicy NewConflictPolicy() => new(info =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
            var dlg = new Views.ConflictDialog(info) { Owner = owner };
            dlg.ShowDialog();
            return (dlg.Choice, dlg.ForAll);
        }));

    private static async Task<TransferResult> RunWithProgressAsync(Func<TransferProgress, CancellationToken, Task<TransferResult>> work,
                                                                   string what, string thing, string destination, ConflictPolicy? policy = null,
                                                                   string? heading = null, System.Windows.Media.ImageSource? icon = null)
    {
        var progress = new TransferProgress();
        using var cts = new CancellationTokenSource();
        var task = work(progress, cts.Token);

        if (await Task.WhenAny(task, Task.Delay(500)) != task)
        {
            // A "file exists" question may be on screen: the progress window waits until it is answered (two windows on top of each other would be confusing).
            while (policy?.IsAsking == true && !task.IsCompleted) await Task.Delay(100);
            if (task.IsCompleted) return await task;

            var target = Path.GetFileName(destination.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(target)) target = destination;   // a drive: D:\\

            var dlg = new Views.ProgressDialog(what, heading ?? $"{what} {thing} to '{target}'", progress, () => cts.Cancel(), icon)
                      { Owner = Application.Current?.MainWindow };
            _ = task.ContinueWith(_ => dlg.Dispatcher.BeginInvoke(new Action(dlg.Finish)), TaskScheduler.Default);
            dlg.ShowDialog();
        }

        return await task;
    }

    /// <summary>
    /// Paste of files that are not on a disk (Remote Desktop clipboard): see <see cref="RemoteFiles"/>. Always a copy: a Cut on the
    /// other side is never answered with a delete over there, and the clipboard is left as it is.
    /// </summary>
    private async Task PasteRemoteAsync(string destination)
    {
        _pasting = true;
        TransferResult result;
        var policy = NewConflictPolicy();
        try { result = await RunWithProgressAsync((progress, token) => RemoteFiles.RunAsync(destination, progress, token, policy),
                                                  "Copying", "files from the clipboard", destination, policy); }
        finally { _pasting = false; }

        if (result.Failed.Count > 0)
        {
            var lines = result.Failed.Take(8).Select(f => $"{f.Name}: {f.Reason}");
            var more  = result.Failed.Count > 8 ? $"{Environment.NewLine}... and {result.Failed.Count - 8} more." : "";
            MessageBox.Show($"These could not be copied:{Environment.NewLine}{Environment.NewLine}" + string.Join(Environment.NewLine, lines) + more,
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Refresh();
        RefreshTreeFolder(destination);

        if (result.Canceled)
            StatusText = $"Copy canceled: {result.Done.Count} of {result.Total} items were done.";
        else if (result.Skipped > 0)
            StatusText = $"{result.Skipped} {(result.Skipped == 1 ? "file was" : "files were")} skipped.";
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsSamePath(string? a, string? b) =>
        a != null && b != null &&
        string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="path"/> is the folder <paramref name="folder"/> or lies inside it.</summary>
    private static bool IsSameOrSubPath(string folder, string path)
    {
        var f = NormalizePath(folder);
        var p = NormalizePath(path);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetUniqueDestPath(string destDir, string name) => FileTransfer.GetUniqueDestPath(destDir, name);

    // ── Selection ────────────────────────────────────────────────────────────

    public void UpdateSelectedItems(List<FileItemViewModel> items)
    {
        _selectedItems = items;
        SelectedItem   = items.Count > 0 ? items[0] : null;

        // Remembered for a session: only a real selection (switching tabs clears the list, which must not erase it).
        if (items.Count > 0 && items[0].FullPath != LastSelectedPath)
        {
            LastSelectedPath = items[0].FullPath;
            SelectionRemembered?.Invoke();
        }

        StatusText = items.Count switch
        {
            0 => _unavailable ? UnavailableText(FolderPath) : "",   // what is selected / how many items: the info panel says
            _ => ""
        };

        DeleteCommand.RaiseCanExecuteChanged();
        PermanentDeleteCommand.RaiseCanExecuteChanged();
        CopyCommand.RaiseCanExecuteChanged();
        CutCommand.RaiseCanExecuteChanged();
        CopyPathCommand.RaiseCanExecuteChanged();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void UpdateNavState()
    {
        BackCommand.RaiseCanExecuteChanged();
        ForwardCommand.RaiseCanExecuteChanged();
        UpCommand.RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(HasHiddenItems))   // "Empty Folder" depends on the hidden items too
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FolderStatusLine)));
    }
}
