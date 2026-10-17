using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
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

    public string CurrentPath
    {
        get => _currentPath;
        set { _currentPath = value; OnPropertyChanged(); }
    }

    /// <summary>The text on the tab: the folder name. Set when a navigation succeeds, not while typing.</summary>
    public string Title
    {
        get => _title;
        private set { _title = value; OnPropertyChanged(); }
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

    /// <summary>The user picked a sort order: use it now and remember it for this folder.</summary>
    public void SetSort(SortColumn column, bool ascending)
    {
        SortColumn    = column;
        SortAscending = ascending;
        if (!string.IsNullOrEmpty(_currentPath))
            _shared.FolderSorts[SharedState.Key(_currentPath)] = (column, ascending);
        SortChanged?.Invoke();
    }

    /// <summary>
    /// Takes over the sort order remembered for the current folder. If there is none: Name, ascending, for a real folder;
    /// for an archive no sort at all, so the files show in the order they are stored in it, until a header is clicked.
    /// </summary>
    private void ApplyFolderSort()
    {
        var (column, ascending) = _shared.FolderSorts.TryGetValue(SharedState.Key(_currentPath), out var s)
            ? s
            : (ArchiveSupport.IsInside(_currentPath) ? SortColumn.None : SortColumn.Name, true);

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

        _shared.TreeSorts[SharedState.Key(_currentPath)] = (column, ascending);
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
        NavigateCommand  = new RelayCommand(_ => Navigate(CurrentPath));
        // An archive is read-only: only copying out of it is possible.
        DeleteCommand    = new RelayCommand(_ => DeleteSelected(), _ => _selectedItems.Count > 0 && !_inArchive);
        PermanentDeleteCommand = new RelayCommand(_ => DeleteSelected(permanent: true), _ => _selectedItems.Count > 0 && !_inArchive);
        NewFolderCommand = new RelayCommand(_ => CreateNewFolder(), _ => !_inArchive);
        CopyCommand      = new RelayCommand(_ => CopySelected(),  _ => _selectedItems.Count > 0);
        CutCommand       = new RelayCommand(_ => CutSelected(),   _ => _selectedItems.Count > 0 && !_inArchive);
        PasteCommand     = new RelayCommand(_ => Paste(),         _ => !_inArchive && FileClipboard.HasFiles());
        ToggleHiddenCommand = new RelayCommand(_ => ToggleHidden());
        CopyPathCommand  = new RelayCommand(_ => CopyPathsAsText(_selectedItems.Select(i => i.FullPath)),
                                            _ => _selectedItems.Count > 0);
        ToggleTreeHiddenCommand = new RelayCommand(_ => ToggleTreeHidden());

        InitializeTree();
        if (startPath != null) Navigate(startPath);
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
        // Quick Access
        var quickAccess = new TreeItemViewModel("Quick Access", "", null, isVirtual: true, sortOf: _shared.GetTreeSort, showHiddenOf: _shared.ShowsHiddenInTree);
        quickAccess.IsExpanded = true;
        AddQuickAccessItem(quickAccess, "Desktop",   Environment.SpecialFolder.Desktop);
        AddQuickAccessItem(quickAccess, "Documents", Environment.SpecialFolder.MyDocuments);
        AddQuickAccessItem(quickAccess, "Pictures",  Environment.SpecialFolder.MyPictures);
        AddQuickAccessItem(quickAccess, "Music",     Environment.SpecialFolder.MyMusic);
        AddQuickAccessItem(quickAccess, "Videos",    Environment.SpecialFolder.MyVideos);

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
        {
            var item = new TreeItemViewModel("Downloads", downloads, IconHelper.GetIcon(downloads), sortOf: _shared.GetTreeSort, showHiddenOf: _shared.ShowsHiddenInTree);
            item.InitializeLazyLoad();
            quickAccess.Children.Add(item);
        }

        TreeItems.Add(quickAccess);

        // This PC
        var thisPC = _thisPcNode = new TreeItemViewModel("This PC", "", null, isVirtual: true, sortOf: _shared.GetTreeSort, showHiddenOf: _shared.ShowsHiddenInTree);
        thisPC.IsExpanded = true;

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var label    = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
            var driveItem = new TreeItemViewModel(label, drive.RootDirectory.FullName, IconHelper.GetIcon(drive.RootDirectory.FullName),
                                                  sortOf: _shared.GetTreeSort, showHiddenOf: _shared.ShowsHiddenInTree);
            driveItem.InitializeLazyLoad();
            thisPC.Children.Add(driveItem);
        }

        TreeItems.Add(thisPC);
    }

    private void AddQuickAccessItem(TreeItemViewModel parent, string name, Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (!Directory.Exists(path)) return;
        var item = new TreeItemViewModel(name, path, IconHelper.GetIcon(path), sortOf: _shared.GetTreeSort, showHiddenOf: _shared.ShowsHiddenInTree);
        item.InitializeLazyLoad();
        parent.Children.Add(item);
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    public void Navigate(string? path, bool addToHistory = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        path = path.Trim();

        if (!Directory.Exists(path))
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

        _showHidden = false;   // hidden items are shown for one folder at a time
        _unavailable = false;
        FolderPath   = path;
        LastSelectedPath = null;   // a new folder: nothing is selected in it yet
        CurrentPath = path;
        Title       = GetTabTitle(path);
        ApplyFolderSort();
        OnPropertyChanged(nameof(TreeSortIndex));   // the tree header shows this folder's tree sort order
        OnPropertyChanged(nameof(TreeSortDirectionIndex));
        LoadItems(path);
        UpdateFolderPicture(path);
        UpdateNavState();
        SelectInTree(path);
    }

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
    /// The folder of the last successful navigation. <see cref="CurrentPath"/> is the text of the address bar, which changes
    /// while the user types: a session must not save a half-typed path.
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
        try { return Directory.Exists(path) || ArchiveSupport.FolderExists(path, out _); }
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
    };

    /// <summary>
    /// Fills in a tab from a saved state. The folder is opened if it is there; if not, the tab says "currently unavailable"
    /// and keeps the path (and root and file) for the next time. Call on a tab made with a null start path, or on any tab.
    /// </summary>
    public void Restore(TabState state)
    {
        if (IsAvailable(state.Path))
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
    }

    /// <summary>A tab for a folder that is not there now: empty, with a note, and the path kept (Go or F5 tries again).</summary>
    private void ShowUnavailable(string path)
    {
        _unavailable = true;
        FolderPath   = path;
        CurrentPath  = path;
        Title        = GetTabTitle(path) + " (unavailable)";
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
        path = path?.Trim().Trim('"');
        if (string.IsNullOrEmpty(path)) return false;

        string root;
        if (Directory.Exists(path))
        {
            root = Path.GetFullPath(path);
            if (root.Length > 3) root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);   // C:\ keeps its slash
        }
        else if (File.Exists(path) ? ArchiveSupport.IsArchiveName(path) : ArchiveSupport.FolderExists(path, out _))
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
        if (string.IsNullOrEmpty(_currentPath)) return;

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
            return _showHidden
                ? $"Showing {_hiddenCount} {what} (click to hide)"
                : $"{_hiddenCount} {what} (click to show)";
        }
    }

    /// <summary>Shows the hidden items of this folder, or hides them again. The next folder starts hidden again.</summary>
    public RelayCommand ToggleHiddenCommand { get; private set; } = null!;

    private void ToggleHidden()
    {
        _showHidden = !_showHidden;
        LoadItems(_currentPath);
    }

    // ── Hidden folders in the tree ───────────────────────────────────────────

    private int _treeHiddenCount;

    /// <summary>True when the current folder has hidden or system subfolders (shown in the tree or not).</summary>
    public bool HasTreeHiddenFolders => _treeHiddenCount > 0;

    /// <summary>The line under the tree: how many subfolders of the current folder are hidden, and what a click does.</summary>
    public string TreeHiddenText
    {
        get
        {
            string what = _treeHiddenCount == 1 ? "hidden folder" : "hidden folders";
            return _shared.ShowsHiddenInTree(_currentPath)
                ? $"Showing {_treeHiddenCount} {what} (click to hide)"
                : $"{_treeHiddenCount} {what} (click to show)";
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
        OnPropertyChanged(nameof(TreeHiddenText));
    }

    private static void SyncHiddenNodes(IEnumerable<TreeItemViewModel> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (NodeIs(node, path)) node.SyncChildren();
            SyncHiddenNodes(node.Children, path);
        }
    }

    private void UpdateTreeHidden(string path)
    {
        _treeHiddenCount = TreeItemViewModel.CountHiddenIn(path);
        OnPropertyChanged(nameof(HasTreeHiddenFolders));
        OnPropertyChanged(nameof(TreeHiddenText));
    }

    // ── Archives ─────────────────────────────────────────────────────────────

    // True while the folder shown is a packed file or a folder in one. The content is read-only:
    // it can be opened and copied out, but not changed.
    private bool _inArchive;

    public bool InArchive => _inArchive;

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

            StatusText = $"{CurrentItems.Count} item(s)";
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
            if (cover == null || token.IsCancellationRequested) return;

            dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;   // another folder was opened meanwhile
                _folderCover = cover;
                OnPropertyChanged(nameof(FolderPicture));
                OnPropertyChanged(nameof(HasFolderPicture));
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
        if (!_shared.TagColumnsVisible || _inArchive) return;   // not on the disk: the tags of a file in an archive are not read (yet)

        var pending = CurrentItems
            .Where(i => !i.IsDirectory && !i.TagsRequested && AudioTagReader.HasTags(i.FullPath))
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

    private void LoadItems(string path)
    {
        _tagsCts?.Cancel();   // the tags of the folder we leave are no longer wanted
        _tagsCts = null;

        CurrentItems.Clear();
        _hiddenCount = 0;
        ClearReadState();
        UpdateTreeHidden(path);

        _inArchive = ArchiveSupport.IsInside(path);
        OnPropertyChanged(nameof(InArchive));
        RaiseAllCanExecuteChanged();   // nothing can be deleted or pasted in an archive

        if (_inArchive)
        {
            LoadArchiveItems(path);
            OnPropertyChanged(nameof(HasHiddenItems));
            OnPropertyChanged(nameof(HiddenText));
            return;
        }

        try
        {
            var dirInfo = new DirectoryInfo(path);

            // Folders first (sorted)
            var folders = _shared.ShowFolders
                ? dirInfo.GetDirectories().OrderBy(d => d.Name)
                : Enumerable.Empty<DirectoryInfo>();

            foreach (var dir in folders)
            {
                try
                {
                    bool hidden = (dir.Attributes & HiddenAttributes) != 0;
                    if (hidden) _hiddenCount++;
                    if (hidden && !_showHidden) continue;

                    var vm = new FileItemViewModel
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
                    CurrentItems.Add(vm);
                    vm.LoadIconAsync();
                }
                catch { }
            }

            // Files (sorted)
            foreach (var file in dirInfo.GetFiles().OrderBy(f => f.Name))
            {
                try
                {
                    // A packed file is a folder here (it is in the tree): listed only when the folders are.
                    if (!_shared.ShowFolders && ArchiveSupport.IsArchiveName(file.Name)) continue;

                    bool hidden = HiddenItems.IsHiddenFile(file.Attributes, file.Name);
                    if (hidden) _hiddenCount++;
                    if (hidden && !_showHidden) continue;

                    var vm = new FileItemViewModel
                    {
                        Name         = file.Name,
                        FullPath     = file.FullName,
                        IsDirectory  = false,
                        IsHidden     = hidden,
                        DateModified = file.LastWriteTime,
                        DateCreated  = file.CreationTime,
                        TypeName     = IconHelper.GetFileType(file.FullName),
                        Size         = file.Length
                    };
                    CurrentItems.Add(vm);
                    vm.LoadIconAsync();
                }
                catch { }
            }

            StatusText = $"{CurrentItems.Count} item(s)";
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

    // ── Item actions ─────────────────────────────────────────────────────────

    public void OpenItem(FileItemViewModel item)
    {
        // A packed file opens like a folder (a real one, or a zip inside a zip).
        if (item.IsDirectory || ArchiveSupport.IsArchiveName(item.Name))
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
            if (result != LaunchResult.UseDefaultProgram) return;
        }

        if (OpenFile(path))
            MarkRead(new[] { item }, read: true);
    }

    /// <summary>The window was activated: something may have been marked in another program (the media player).</summary>
    public void RefreshReadState()
    {
        if (!string.IsNullOrEmpty(_currentPath) && CurrentItems.Count > 0)
            UpdateReadState(_currentPath);
    }

    private static bool OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot open: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
        }
    }

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

    private void SetFolderMarks(LibraryStore.Marks marks)
    {
        _folderMarks = marks;
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
        lock (_markWritesLock)
        {
            _markWrites = _markWrites.ContinueWith(_ =>
            {
                bool saved = isRating ? LibraryStore.SetRating(list, rating) : LibraryStore.SetExpectation(list, expectation);
                if (!saved)
                    dispatcher.InvokeAsync(() =>
                        StatusText = $"The {(isRating ? "rating" : "expectation")} could not be saved. The drive may be write-protected.");
            }, TaskScheduler.Default);
        }
    }

    // Which items were rated on the screen, and when (a counter): see UpdateReadState. Only the UI thread touches them.
    private int _markEpoch;
    private readonly Dictionary<string, int> _markChanged = new(StringComparer.OrdinalIgnoreCase);

    private static Task _markWrites = Task.CompletedTask;
    private static readonly object _markWritesLock = new();

    private static void WaitForMarkWrites()
    {
        Task pending;
        lock (_markWritesLock) pending = _markWrites;
        try { pending.Wait(3000); } catch { }
    }

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
                StatusText = $"{CurrentItems.Count} item(s)";
                UpdateReadState(_currentPath);
            });
        });
    }

    public void DeleteSelected(bool permanent = false) =>
        DeleteEntries(_selectedItems.Select(i => (i.FullPath, i.Name, i.IsDirectory)).ToList(), permanent);

    /// <summary>Moves a folder of the tree to the Recycle Bin (its right-click menu).</summary>
    public void DeleteFolder(string path) =>
        DeleteEntries(new() { (path, Path.GetFileName(path.TrimEnd('\\', '/')), true) }, permanent: false);

    private void DeleteEntries(List<(string Path, string Name, bool IsDirectory)> items, bool permanent)
    {
        if (items.Count == 0) return;

        var names  = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count} items";
        var result = permanent
            ? MessageBox.Show(
                $"Are you sure you want to permanently delete {names}?\nThis cannot be undone.",
                "Confirm Permanent Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No)
            : MessageBox.Show(
                $"Are you sure you want to move {names} to the Recycle Bin?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        var recycle = permanent ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;

        foreach (var item in items)
        {
            try
            {
                // UIOption.OnlyErrorDialogs: no progress dialog, but a failed recycle
                // (e.g. a network drive with no Recycle Bin) is reported, not silently permanent.
                if (item.IsDirectory)
                    FileSystem.DeleteDirectory(item.Path, UIOption.OnlyErrorDialogs, recycle);
                else
                    FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, recycle);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot delete '{item.Name}': {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        foreach (var item in items)
            RefreshTreeFolder(Path.GetDirectoryName(item.Path.TrimEnd('\\', '/')));

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
    }

    /// <summary>Renames a folder of the tree (its right-click menu).</summary>
    public void RenameFolder(string path, string newName) =>
        RenameItem(new FileItemViewModel { Name = Path.GetFileName(path.TrimEnd('\\', '/')), FullPath = path, IsDirectory = true }, newName);

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

    public void RenameItem(FileItemViewModel item, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrEmpty(newName) || newName == item.Name) return;

        var problem = CheckNewName(item.FullPath, newName);
        if (problem != null)
        {
            MessageBox.Show(problem, "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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

            if (item.IsDirectory) Directory.Move(item.FullPath, newPath);
            else                  File.Move(item.FullPath, newPath);

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
            MessageBox.Show($"Cannot rename: {ex.Message}", "Error",
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
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot create folder: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

    private void Paste() => PasteInto(_currentPath);

    /// <summary>Pastes what is on the clipboard into <paramref name="destination"/>, a folder of the disk.</summary>
    public void PasteInto(string destination)
    {
        if (FileClipboard.Get() is not var (sources, isCut)) return;

        foreach (var src in sources)
        {
            try
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

                var dest = GetUniqueDestPath(destination, name!);

                if (Directory.Exists(src))
                {
                    if (isCut) MoveDirectory(src, dest);
                    else       CopyDirectory(src, dest);
                }
                else if (File.Exists(src))
                {
                    if (isCut) File.Move(src, dest);
                    else       File.Copy(src, dest);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Paste error: {ex.Message}", AppInfo.Name,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        if (isCut)
        {
            FileClipboard.Clear();   // what was cut is moved: there is nothing left to paste
            PasteCommand.RaiseCanExecuteChanged();
        }

        Refresh();
        RefreshTreeFolder(destination);   // the folder pasted into need not be the current one
    }

    /// <summary>Moves a folder. To another drive that means copying it and then deleting the original.</summary>
    private static void MoveDirectory(string src, string dest)
    {
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(src)), Path.GetPathRoot(Path.GetFullPath(dest)),
                StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(src, dest);
            return;
        }

        CopyDirectory(src, dest);
        Directory.Delete(src, recursive: true);
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

    private static string GetUniqueDestPath(string destDir, string name)
    {
        var path = Path.Combine(destDir, name);
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        var ext             = Path.GetExtension(name);
        var nameWithoutExt  = Path.GetFileNameWithoutExtension(name);
        int i               = 1;

        do
        {
            path = Path.Combine(destDir, $"{nameWithoutExt} ({i++}){ext}");
        } while (File.Exists(path) || Directory.Exists(path));

        return path;
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

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
            0 => _unavailable ? UnavailableText(FolderPath) : $"{CurrentItems.Count} item(s)",
            1 => "1 item selected",
            _ => $"{items.Count} items selected"
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
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
