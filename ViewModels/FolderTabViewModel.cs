using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.VisualBasic.FileIO;
using MyFileExplorer.Commands;
using MyFileExplorer.Helpers;

namespace MyFileExplorer.ViewModels;

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

    // ── Constructor ──────────────────────────────────────────────────────────

    public FolderTabViewModel(SharedState shared, string startPath)
    {
        _shared = shared;

        BackCommand      = new RelayCommand(_ => GoBack(),        _ => _backHistory.Count > 0);
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
        PasteCommand     = new RelayCommand(_ => Paste(),         _ => _shared.ClipboardPaths != null && !_inArchive);
        ToggleHiddenCommand = new RelayCommand(_ => ToggleHidden());

        InitializeTree();
        Navigate(startPath);
    }

    /// <summary>Re-evaluates every command's CanExecute, e.g. when this tab becomes the active one.</summary>
    public void RaiseAllCanExecuteChanged()
    {
        foreach (var c in new[] { BackCommand, ForwardCommand, UpCommand, RefreshCommand, NavigateCommand,
                                  DeleteCommand, PermanentDeleteCommand, NewFolderCommand,
                                  CopyCommand, CutCommand, PasteCommand })
            c.RaiseCanExecuteChanged();
    }

    // ── Tree initialization ──────────────────────────────────────────────────

    private void InitializeTree()
    {
        // Quick Access
        var quickAccess = new TreeItemViewModel("Quick Access", "", null, isVirtual: true, sortOf: _shared.GetTreeSort);
        quickAccess.IsExpanded = true;
        AddQuickAccessItem(quickAccess, "Desktop",   Environment.SpecialFolder.Desktop);
        AddQuickAccessItem(quickAccess, "Documents", Environment.SpecialFolder.MyDocuments);
        AddQuickAccessItem(quickAccess, "Pictures",  Environment.SpecialFolder.MyPictures);
        AddQuickAccessItem(quickAccess, "Music",     Environment.SpecialFolder.MyMusic);
        AddQuickAccessItem(quickAccess, "Videos",    Environment.SpecialFolder.MyVideos);

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
        {
            var item = new TreeItemViewModel("Downloads", downloads, IconHelper.GetIcon(downloads), sortOf: _shared.GetTreeSort);
            item.InitializeLazyLoad();
            quickAccess.Children.Add(item);
        }

        TreeItems.Add(quickAccess);

        // This PC
        var thisPC = _thisPcNode = new TreeItemViewModel("This PC", "", null, isVirtual: true, sortOf: _shared.GetTreeSort);
        thisPC.IsExpanded = true;

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var label    = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
            var driveItem = new TreeItemViewModel(label, drive.RootDirectory.FullName, IconHelper.GetIcon(drive.RootDirectory.FullName),
                                                  sortOf: _shared.GetTreeSort);
            driveItem.InitializeLazyLoad();
            thisPC.Children.Add(driveItem);
        }

        TreeItems.Add(thisPC);
    }

    private void AddQuickAccessItem(TreeItemViewModel parent, string name, Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (!Directory.Exists(path)) return;
        var item = new TreeItemViewModel(name, path, IconHelper.GetIcon(path), sortOf: _shared.GetTreeSort);
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

        if (addToHistory && !string.IsNullOrEmpty(_currentPath))
        {
            _backHistory.Push(_currentPath);
            _forwardHistory.Clear();
        }

        _showHidden = false;   // hidden items are shown for one folder at a time
        CurrentPath = path;
        Title       = GetTabTitle(path);
        ApplyFolderSort();
        OnPropertyChanged(nameof(TreeSortIndex));   // the tree header shows this folder's tree sort order
        OnPropertyChanged(nameof(TreeSortDirectionIndex));
        LoadItems(path);
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
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        var node = _thisPcNode?.Children.FirstOrDefault(c => NodeIs(c, root!));
        if (node == null) return null;   // e.g. a network path

        var current = root!;
        var segments = Path.GetRelativePath(root!, path).Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (segment == ".") continue;
            current = Path.Combine(current, segment);

            node.IsExpanded = true;   // loads the children the first time
            var next = node.Children.FirstOrDefault(c => NodeIs(c, current));
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
        var parent = Directory.GetParent(_currentPath);
        if (parent != null) Navigate(parent.FullName);
    }

    private bool CanGoUp() =>
        !string.IsNullOrEmpty(_currentPath) && Directory.GetParent(_currentPath) != null;

    public void Refresh()
    {
        if (string.IsNullOrEmpty(_currentPath)) return;

        ArchiveSupport.Invalidate(_currentPath);   // an archive is read again, in case the file changed
        LoadItems(_currentPath);

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
                if (entry.IsDirectory && !_shared.ShowFolders) continue;   // the folders are in the tree

                var vm = new FileItemViewModel
                {
                    Name         = entry.Name,
                    FullPath     = entry.FullPath,
                    IsDirectory  = entry.IsDirectory,
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

    private void LoadItems(string path)
    {
        CurrentItems.Clear();
        _hiddenCount = 0;

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
                    bool hidden = (file.Attributes & HiddenAttributes) != 0;
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
        if (path != null) OpenFile(path);
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot open: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void DeleteSelected(bool permanent = false)
    {
        var items = _selectedItems.ToList();
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
                    FileSystem.DeleteDirectory(item.FullPath, UIOption.OnlyErrorDialogs, recycle);
                else
                    FileSystem.DeleteFile(item.FullPath, UIOption.OnlyErrorDialogs, recycle);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot delete '{item.Name}': {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        Refresh();
    }

    public void RenameItem(FileItemViewModel item, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrEmpty(newName) || newName == item.Name) return;


        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show("A file name cannot contain any control characters or any of the following characters:\n\\ / : * ? \" < > |",
                "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var dir     = Path.GetDirectoryName(item.FullPath)!;
            var newPath = Path.Combine(dir, newName);

            // A change of case only (a.txt -> A.txt) points at the same item: allow it.
            bool sameItem = string.Equals(newPath, item.FullPath, StringComparison.OrdinalIgnoreCase);
            if (!sameItem && (File.Exists(newPath) || Directory.Exists(newPath)))
            {
                MessageBox.Show($"'{newName}' already exists in this folder. Choose a different name.",
                    "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (item.IsDirectory) Directory.Move(item.FullPath, newPath);
            else                  File.Move(item.FullPath, newPath);

            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot rename: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void CreateNewFolder()
    {
        var baseName = "New Folder";
        var path     = Path.Combine(_currentPath, baseName);
        int i        = 1;

        while (Directory.Exists(path))
            path = Path.Combine(_currentPath, $"New Folder ({i++})");

        try
        {
            Directory.CreateDirectory(path);
            Refresh();

            // The window selects the new folder and asks for its name. With the folders hidden
            // it is not in the list, but it can still be renamed.
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

    private void CopySelected()
    {
        _shared.ClipboardPaths = _selectedItems.Select(i => i.FullPath).ToList();
        _shared.ClipboardIsCut = false;
        PasteCommand.RaiseCanExecuteChanged();
    }

    private void CutSelected()
    {
        _shared.ClipboardPaths = _selectedItems.Select(i => i.FullPath).ToList();
        _shared.ClipboardIsCut = true;
        PasteCommand.RaiseCanExecuteChanged();
    }

    private void Paste()
    {
        if (_shared.ClipboardPaths == null) return;

        foreach (var src in _shared.ClipboardPaths)
        {
            try
            {
                var name = Path.GetFileName(src.TrimEnd('\\', '/'));

                // Copied from inside an archive: unpack it here.
                if (!File.Exists(src) && !Directory.Exists(src) && ArchiveSupport.IsInside(src))
                {
                    ArchiveSupport.ExtractTo(src, GetUniqueDestPath(_currentPath, name));
                    continue;
                }

                if (Directory.Exists(src) && IsSameOrSubPath(src, _currentPath))
                {
                    MessageBox.Show(
                        $"Cannot {(_shared.ClipboardIsCut ? "move" : "copy")} '{name}' into itself or one of its subfolders.",
                        "Paste", MessageBoxButton.OK, MessageBoxImage.Warning);
                    continue;
                }

                // Cutting an item and pasting it where it already is changes nothing.
                if (_shared.ClipboardIsCut && IsSamePath(Path.GetDirectoryName(src), _currentPath))
                    continue;

                var dest = GetUniqueDestPath(_currentPath, name!);

                if (Directory.Exists(src))
                {
                    if (_shared.ClipboardIsCut) Directory.Move(src, dest);
                    else                 CopyDirectory(src, dest);
                }
                else if (File.Exists(src))
                {
                    if (_shared.ClipboardIsCut) File.Move(src, dest);
                    else                 File.Copy(src, dest);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Paste error: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        if (_shared.ClipboardIsCut)
        {
            _shared.ClipboardPaths = null;
            PasteCommand.RaiseCanExecuteChanged();
        }

        Refresh();
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

        StatusText = items.Count switch
        {
            0 => $"{CurrentItems.Count} item(s)",
            1 => "1 item selected",
            _ => $"{items.Count} items selected"
        };

        DeleteCommand.RaiseCanExecuteChanged();
        PermanentDeleteCommand.RaiseCanExecuteChanged();
        CopyCommand.RaiseCanExecuteChanged();
        CutCommand.RaiseCanExecuteChanged();
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
