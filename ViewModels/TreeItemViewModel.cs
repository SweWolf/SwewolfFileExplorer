using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.ViewModels;

public class TreeItemViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _childrenLoaded;

    private string _name = "";
    public string Name
    {
        get => _name;
        private set { if (_name == value) return; _name = value; OnPropertyChanged(); }
    }

    /// <summary>Changes the text of the node (a drive that is an audio CD gets the album's name once it is known).</summary>
    public void SetLabel(string text) => Name = text;

    public string FullPath { get; }
    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The icon of this node at the given size (when the tree's font size changes), worked out the same way as when the node was made.
    /// Null = keep the icon it has (the dummy child, and the nodes with a drawn icon). Reads the disk: call it from a background thread.
    /// </summary>
    public ImageSource? ComputeIcon(int size)
    {
        if (IconShape != null) return null;
        if (Bookmark != null)
        {
            if (Bookmark.IsGroup || Bookmark.Target == null) return IconHelper.GetIconForName("folder", true, size);
            return Bookmark.IsFile ? IconHelper.GetIconForName(Path.GetFileName(Bookmark.Target), false, size)
                                   : IconHelper.GetIconForName("folder", true, size);
        }
        if (string.IsNullOrEmpty(FullPath)) return null;
        if (_inArchive) return IconHelper.GetIconForName(Name, !IsArchive, size);
        return IconHelper.GetIcon(FullPath, true, size);
    }

    public ObservableCollection<TreeItemViewModel> Children { get; } = new();

    /// <summary>A drawn icon for the nodes that are no folder: "bookmark", "star" (Quick Access) or "pc" (This PC). Null = <see cref="Icon"/> is used.</summary>
    public string? IconShape { get; init; }

    /// <summary>A packed file (a zip): shown as a folder, after the real folders. What is in it is read from the file.</summary>
    public bool IsArchive { get; }

    /// <summary>An M3U playlist: shown like a packed file (after the folders), but it has no subfolders and no expand arrow.</summary>
    public bool IsPlaylist { get; init; }

    /// <summary>A file the tree lists next to the folders: a packed file or a playlist.</summary>
    public static bool IsTreeFile(string name) => ArchiveSupport.IsArchiveName(name) || PlaylistFile.IsPlaylistName(name);

    /// <summary>A hidden or system folder, in the tree only because the user asked for the hidden folders: shown faded.</summary>
    public bool IsHidden { get; }

    /// <summary>The bookmark (its shortcut, or a group's folder) this node stands for: only in the "Bookmarks" part of the tree.</summary>
    public BookmarkEntry? Bookmark { get; init; }

    /// <summary>The "Bookmarks" node itself, at the top of the tree.</summary>
    public bool IsBookmarksRoot { get; init; }

    public override string ToString() => Name;   // what accessibility tools read for the node

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            OnPropertyChanged();
            if (value && !_childrenLoaded && !string.IsNullOrEmpty(FullPath))
                LoadChildren();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    private bool _isContextTarget;

    /// <summary>
    /// The folder the right-click menu is open on, drawn with an outline (only when a right-click does not select it: Settings > Folder Tree).
    /// </summary>
    public bool IsContextTarget
    {
        get => _isContextTarget;
        set { if (_isContextTarget == value) return; _isContextTarget = value; OnPropertyChanged(); }
    }

    /// <summary>Tells how the subfolders of a folder are sorted. Handed down from the nodes to their children.</summary>
    public delegate (SortColumn Column, bool Ascending) TreeSortLookup(string folderPath);

    private readonly TreeSortLookup? _sortOf;

    /// <summary>Tells if the hidden subfolders of a folder are shown. Handed down from the nodes to their children.</summary>
    private readonly Func<string, bool>? _showHiddenOf;

    private const FileAttributes HiddenAttributes = FileAttributes.Hidden | FileAttributes.System;

    /// <summary>The hidden and system subfolders of this node are in the tree (the packed files too).</summary>
    private bool ShowHidden => !ChildrenInArchive && (_showHiddenOf?.Invoke(FullPath) ?? false);

    // True if this node is a folder inside an archive. Its children are inside the archive too.
    private readonly bool _inArchive;

    /// <summary>The children of this node are in an archive: it is a packed file, or a folder in one.</summary>
    private bool ChildrenInArchive => IsArchive || _inArchive;

    // isVirtual = true for synthetic nodes (Quick Access, This PC) that manage their own children
    public TreeItemViewModel(string name, string fullPath, ImageSource? icon, bool isVirtual = false,
                             TreeSortLookup? sortOf = null, bool isArchive = false, bool inArchive = false,
                             Func<string, bool>? showHiddenOf = null, bool isHidden = false)
    {
        Name = name;
        FullPath = fullPath;
        _icon = icon;
        _childrenLoaded = isVirtual;
        _sortOf = sortOf;
        IsArchive = isArchive;
        _inArchive = inArchive;
        _showHiddenOf = showHiddenOf;
        IsHidden = isHidden;
    }

    /// <summary>Adds a dummy child so the expand arrow is visible before loading.</summary>
    public void InitializeLazyLoad()
    {
        if (string.IsNullOrEmpty(FullPath)) return;

        // A packed file always gets the arrow: finding out if it has folders in it means opening it,
        // and a folder can have hundreds of them. Opening it shows what is in it.
        if (IsArchive || HasSubDirectories())
            Children.Add(new TreeItemViewModel("", "", null));
    }

    /// <summary>
    /// For the nodes made when a folder is expanded: every one gets the expand arrow at once, and a background thread then takes the
    /// arrow away from the folders that have nothing to show. (Asking each folder on the UI thread, one listing per subfolder, froze the
    /// window for about 10 seconds when a click in the tree had to expand a drive with many folders on a busy disk, 2026-11-05.)
    /// </summary>
    private static void CheckArrowsInBackground(List<TreeItemViewModel> nodes)
    {
        if (nodes.Count == 0) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) { foreach (var n in nodes) n.DropArrowIfEmpty(n.HasSubDirectories()); return; }

        Task.Run(() =>
        {
            // A few at a time, and the answers are given to the window in small batches, so it is never busy for long.
            var empty = new List<TreeItemViewModel>();
            foreach (var node in nodes)
            {
                if (!node.HasSubDirectories()) empty.Add(node);
                if (empty.Count >= 20) { Post(empty); empty = new List<TreeItemViewModel>(); }
            }
            Post(empty);

            void Post(List<TreeItemViewModel> list)
            {
                if (list.Count == 0) return;
                dispatcher.BeginInvoke(() => { foreach (var n in list) n.DropArrowIfEmpty(false); },
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        });
    }

    // Takes the dummy child away (no arrow) if the node is still unopened and has only the dummy.
    private void DropArrowIfEmpty(bool hasSubDirectories)
    {
        if (hasSubDirectories || _childrenLoaded) return;
        if (Children.Count == 1 && string.IsNullOrEmpty(Children[0].FullPath)) Children.Clear();
    }

    private bool HasSubDirectories()
    {
        try
        {
            if (_inArchive) return ArchiveSupport.HasSubfolders(FullPath);

            // One listing of the folder, stopping at the first folder or packed file that is shown. (Asking the disk for the
            // folders and then once for each kind of packed file was six listings per folder: on a cloud drive with 70
            // folders that was 4 seconds when a tab was opened.)
            bool showHidden = ShowHidden;
            foreach (var entry in new DirectoryInfo(FullPath).EnumerateFileSystemInfos())
            {
                bool hidden = (entry.Attributes & HiddenAttributes) != 0;
                if (hidden && !showHidden) continue;

                bool isFolder = (entry.Attributes & FileAttributes.Directory) != 0;
                if (isFolder || IsTreeFile(entry.Name)) return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static bool IsHiddenPath(string path)
    {
        try { return (File.GetAttributes(path) & HiddenAttributes) != 0; }
        catch { return false; }
    }

    /// <summary>
    /// The subfolders and the packed files of this folder to list (not the hidden and system ones, unless they are asked for),
    /// and which of those are hidden: all from ONE listing of the folder, whose entries carry their attributes. (Asking the
    /// disk about each folder separately, and listing the folder once more for each kind of packed file, took 4 seconds for
    /// the 72 folders of a cloud drive.)
    /// </summary>
    private (List<string> Folders, List<string> Archives, HashSet<string> Hidden) ListChildren()
    {
        bool showHidden = ShowHidden;
        var folders  = new List<string>();
        var archives = new List<string>();
        var hidden   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in new DirectoryInfo(FullPath).EnumerateFileSystemInfos())
        {
            bool isFolder = (entry.Attributes & FileAttributes.Directory) != 0;
            if (!isFolder && !IsTreeFile(entry.Name)) continue;   // "*.zip" also finds ".zipx": the name decides

            bool isHidden = (entry.Attributes & HiddenAttributes) != 0;
            if (isHidden && !showHidden) continue;

            (isFolder ? folders : archives).Add(entry.FullName);
            if (isHidden) hidden.Add(entry.FullName);
        }

        return (folders, archives, hidden);
    }

    /// <summary>How many subfolders and packed files a folder has, the hidden and system ones counted apart (the line under the tree).</summary>
    public sealed record ChildCounts(int Folders, int Archives, int HiddenFolders, int HiddenArchives)
    {
        public int Hidden => HiddenFolders + HiddenArchives;
    }

    /// <summary>The counts of the real folder <paramref name="folder"/> (one listing, attributes included); null if it is not a folder or cannot be read.</summary>
    public static ChildCounts? CountChildren(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;

            int folders = 0, archives = 0, hiddenFolders = 0, hiddenArchives = 0;
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                bool isFolder = (entry.Attributes & FileAttributes.Directory) != 0;
                if (!isFolder && !IsTreeFile(entry.Name)) continue;

                bool hidden = (entry.Attributes & HiddenAttributes) != 0;
                if (isFolder) { if (hidden) hiddenFolders++;  else folders++; }
                else          { if (hidden) hiddenArchives++; else archives++; }
            }
            return new ChildCounts(folders, archives, hiddenFolders, hiddenArchives);
        }
        catch { return null; }
    }

    /// <summary>
    /// What the tree would show under the real folder <paramref name="folder"/>: the names of its subfolders and packed files, with a mark
    /// for the hidden ones, from one listing. Two equal answers mean the tree is up to date. Safe on any thread; "" if it cannot be read.
    /// </summary>
    public static string ChildrenSignature(string folder)
    {
        try
        {
            var names = new List<string>();
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                bool isFolder = (entry.Attributes & FileAttributes.Directory) != 0;
                if (!isFolder && !IsTreeFile(entry.Name)) continue;
                names.Add(entry.Name + ((entry.Attributes & HiddenAttributes) != 0 ? "*" : "") + (isFolder ? "/" : ""));
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join("\n", names);
        }
        catch { return ""; }
    }

    /// <summary>True if <paramref name="path"/> is a real folder with the Hidden or System attribute.</summary>
    public static bool IsHiddenFolder(string path) => Directory.Exists(path) && IsHiddenPath(path);

    /// <summary>Reads the subfolders again (a folder was created, renamed or deleted).</summary>
    public void Refresh()
    {
        if (string.IsNullOrEmpty(FullPath)) return;

        if (ChildrenInArchive) ArchiveSupport.Invalidate(FullPath);

        if (_childrenLoaded)
        {
            LoadChildren();
        }
        else
        {
            Children.Clear();
            InitializeLazyLoad();   // the expand arrow depends on whether there are subfolders
        }
    }

    public void LoadChildren()
    {
        _childrenLoaded = true;
        Children.Clear();
        if (string.IsNullOrEmpty(FullPath)) return;

        try
        {
            if (ChildrenInArchive) LoadArchiveChildren();
            else                   LoadFolderChildren();
        }
        catch { }
    }

    private void LoadFolderChildren()
    {
        foreach (var child in BuildFolderChildren())
            Children.Add(child);
    }

    /// <summary>
    /// The nodes of the subfolders, in order: all the folders first, then the packed files, which open like folders.
    /// A node that is in <paramref name="reuse"/> is used again, so it keeps what is expanded below it.
    /// </summary>
    private List<TreeItemViewModel> BuildFolderChildren(Dictionary<string, TreeItemViewModel>? reuse = null)
    {
        var nodes = new List<TreeItemViewModel>();
        var needCheck = new List<TreeItemViewModel>();
        var (folders, archives, hidden) = ListChildren();

        foreach (var dir in SortFolders(folders))
        {
            try
            {
                if (reuse != null && reuse.TryGetValue(dir, out var old)) { nodes.Add(old); continue; }

                var item = new TreeItemViewModel(
                    Path.GetFileName(dir) ?? dir,
                    dir,
                    IconHelper.TreeIcon(dir),
                    sortOf: _sortOf,
                    showHiddenOf: _showHiddenOf,
                    isHidden: hidden.Contains(dir));
                item.Children.Add(new TreeItemViewModel("", "", null));   // the arrow at once; a background check removes it where there is nothing below
                nodes.Add(item);
                needCheck.Add(item);
            }
            catch { /* skip inaccessible */ }
        }

        foreach (var file in SortFolders(archives))
        {
            try
            {
                if (reuse != null && reuse.TryGetValue(file, out var old)) { nodes.Add(old); continue; }

                bool playlist = PlaylistFile.IsPlaylistName(file);
                var item = new TreeItemViewModel(
                    Path.GetFileName(file),
                    file,
                    IconHelper.TreeIcon(file),
                    sortOf: _sortOf,
                    isArchive: !playlist,
                    showHiddenOf: _showHiddenOf,
                    isHidden: hidden.Contains(file)) { IsPlaylist = playlist };
                if (!playlist) item.InitializeLazyLoad();   // a playlist has nothing below it
                nodes.Add(item);
            }
            catch { /* skip inaccessible */ }
        }

        CheckArrowsInBackground(needCheck);
        return nodes;
    }

    /// <summary>
    /// Reads the subfolders again (the hidden ones were shown or hidden, a folder was created, renamed or deleted):
    /// adds and removes nodes in place, so the nodes that stay keep their place, what is expanded below
    /// them, and the selection. A packed file is read again, which starts over.
    /// </summary>
    public void SyncChildren()
    {
        if (string.IsNullOrEmpty(FullPath)) return;

        if (ChildrenInArchive)
        {
            Refresh();
            return;
        }

        if (!_childrenLoaded)
        {
            Children.Clear();
            InitializeLazyLoad();   // the expand arrow depends on whether there is a folder to show
            return;
        }

        try
        {
            var existing = Children.Where(c => !string.IsNullOrEmpty(c.FullPath)).ToDictionary(c => c.FullPath);
            var wanted   = BuildFolderChildren(existing);

            foreach (var gone in Children.Where(c => !wanted.Contains(c)).ToList())
            {
                // The tree selects the parent when the selected node is removed, and the window would take
                // that for a click and navigate there: so nothing selected is removed.
                Deselect(gone);
                Children.Remove(gone);
            }

            for (int i = 0; i < wanted.Count; i++)
            {
                if (i < Children.Count && ReferenceEquals(Children[i], wanted[i])) continue;

                int at = Children.IndexOf(wanted[i]);
                if (at >= 0) Children.Move(at, i);
                else         Children.Insert(i, wanted[i]);
            }
        }
        catch { }
    }

    private void LoadArchiveChildren()
    {
        var entries = ArchiveSupport.List(FullPath);

        foreach (var folder in SortEntries(entries.Where(e => e.IsDirectory)))
        {
            var item = new TreeItemViewModel(folder.Name, folder.FullPath, IconHelper.TreeIconForName(folder.Name, true),
                sortOf: _sortOf, inArchive: true);
            item.InitializeLazyLoad();
            Children.Add(item);
        }

        // A zip inside the zip.
        foreach (var archive in SortEntries(entries.Where(e => e.IsArchive)))
        {
            var item = new TreeItemViewModel(archive.Name, archive.FullPath, IconHelper.TreeIconForName(archive.Name, false),
                sortOf: _sortOf, isArchive: true, inArchive: true);
            item.InitializeLazyLoad();
            Children.Add(item);
        }
    }

    /// <summary>Puts the folders in the order chosen for this folder (Name, ascending if none was chosen).</summary>
    private IEnumerable<string> SortFolders(IEnumerable<string> folders) =>
        SortBy(folders, Path.GetFileName,
            f => ChildrenInArchive ? ArchiveSupport.GetDate(f, created: true)  : Directory.GetCreationTime(f),
            f => ChildrenInArchive ? ArchiveSupport.GetDate(f, created: false) : Directory.GetLastWriteTime(f));

    private IEnumerable<VirtualEntry> SortEntries(IEnumerable<VirtualEntry> entries) =>
        SortBy(entries, e => e.Name, e => e.Modified, e => e.Modified);   // an archive keeps one date per entry

    private IEnumerable<T> SortBy<T>(IEnumerable<T> items, Func<T, string?> nameOf,
        Func<T, DateTime> createdOf, Func<T, DateTime> modifiedOf) where T : notnull
    {
        var (column, ascending) = _sortOf?.Invoke(FullPath) ?? (SortColumn.Name, true);
        if (column == SortColumn.None) return items;   // no sort: the order they were read in (inside an archive: as stored)

        var byName = Comparer<string?>.Create(FileItemComparer.NaturalCompare);

        if (column == SortColumn.Name)
        {
            return ascending
                ? items.OrderBy(nameOf, byName)
                : items.OrderByDescending(nameOf, byName);
        }

        // Read each date once; the comparer would ask for it again and again.
        var dateOf = column == SortColumn.DateCreated ? createdOf : modifiedOf;
        var dates = items.ToDictionary(i => i, dateOf);

        // Ties are always broken by name, ascending, as in the file list.
        return (ascending ? dates.Keys.OrderBy(i => dates[i]) : dates.Keys.OrderByDescending(i => dates[i]))
            .ThenBy(nameOf, byName);
    }

    /// <summary>
    /// Puts the loaded subfolders in the order now chosen for this folder, by moving the nodes,
    /// so the expanded nodes below them stay expanded. The packed files stay after the folders.
    /// </summary>
    public void Resort()
    {
        if (!_childrenLoaded || string.IsNullOrEmpty(FullPath)) return;

        var nodes    = Children.Where(c => !string.IsNullOrEmpty(c.FullPath)).ToList();
        var folders  = nodes.Where(c => !c.IsArchive && !c.IsPlaylist).ToDictionary(c => c.FullPath);
        var archives = nodes.Where(c => c.IsArchive || c.IsPlaylist).ToDictionary(c => c.FullPath);

        var sorted = SortFolders(folders.Keys).Select(p => folders[p])
            .Concat(SortFolders(archives.Keys).Select(p => archives[p]))
            .ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            if (!ReferenceEquals(Children[i], sorted[i]))
                Children.Move(Children.IndexOf(sorted[i]), i);
        }
    }

    private static void Deselect(TreeItemViewModel node)
    {
        node.IsSelected = false;
        foreach (var child in node.Children) Deselect(child);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
