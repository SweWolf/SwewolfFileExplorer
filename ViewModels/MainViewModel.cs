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

public class MainViewModel : INotifyPropertyChanged
{
    private string _currentPath = "";
    private string _statusText = "";
    private FileItemViewModel? _selectedItem;

    private readonly Stack<string> _backHistory    = new();
    private readonly Stack<string> _forwardHistory = new();
    private List<FileItemViewModel> _selectedItems = new();

    // Clipboard state
    private List<string>? _clipboardPaths;
    private bool _clipboardIsCut;

    // ── Observable collections ───────────────────────────────────────────────

    public ObservableCollection<FileItemViewModel> CurrentItems { get; } = new();
    public ObservableCollection<TreeItemViewModel> TreeItems    { get; } = new();

    // ── Bindable properties ──────────────────────────────────────────────────

    public string CurrentPath
    {
        get => _currentPath;
        set { _currentPath = value; OnPropertyChanged(); }
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

    /// <summary>Raised after a new folder was created and the list refreshed.</summary>
    public event Action<FileItemViewModel>? FolderCreated;

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

    public MainViewModel()
    {
        BackCommand      = new RelayCommand(_ => GoBack(),        _ => _backHistory.Count > 0);
        ForwardCommand   = new RelayCommand(_ => GoForward(),     _ => _forwardHistory.Count > 0);
        UpCommand        = new RelayCommand(_ => GoUp(),          _ => CanGoUp());
        RefreshCommand   = new RelayCommand(_ => Refresh());
        NavigateCommand  = new RelayCommand(_ => Navigate(CurrentPath));
        DeleteCommand    = new RelayCommand(_ => DeleteSelected(), _ => _selectedItems.Count > 0);
        PermanentDeleteCommand = new RelayCommand(_ => DeleteSelected(permanent: true), _ => _selectedItems.Count > 0);
        NewFolderCommand = new RelayCommand(_ => CreateNewFolder());
        CopyCommand      = new RelayCommand(_ => CopySelected(),  _ => _selectedItems.Count > 0);
        CutCommand       = new RelayCommand(_ => CutSelected(),   _ => _selectedItems.Count > 0);
        PasteCommand     = new RelayCommand(_ => Paste(),         _ => _clipboardPaths != null);

        InitializeTree();
        Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    // ── Tree initialization ──────────────────────────────────────────────────

    private void InitializeTree()
    {
        // Quick Access
        var quickAccess = new TreeItemViewModel("Quick Access", "", null, isVirtual: true);
        quickAccess.IsExpanded = true;
        AddQuickAccessItem(quickAccess, "Desktop",   Environment.SpecialFolder.Desktop);
        AddQuickAccessItem(quickAccess, "Documents", Environment.SpecialFolder.MyDocuments);
        AddQuickAccessItem(quickAccess, "Pictures",  Environment.SpecialFolder.MyPictures);
        AddQuickAccessItem(quickAccess, "Music",     Environment.SpecialFolder.MyMusic);
        AddQuickAccessItem(quickAccess, "Videos",    Environment.SpecialFolder.MyVideos);

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
        {
            var item = new TreeItemViewModel("Downloads", downloads, IconHelper.GetIcon(downloads));
            item.InitializeLazyLoad();
            quickAccess.Children.Add(item);
        }

        TreeItems.Add(quickAccess);

        // This PC
        var thisPC = new TreeItemViewModel("This PC", "", null, isVirtual: true);
        thisPC.IsExpanded = true;

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var label    = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
            var driveItem = new TreeItemViewModel(label, drive.RootDirectory.FullName, IconHelper.GetIcon(drive.RootDirectory.FullName));
            driveItem.InitializeLazyLoad();
            thisPC.Children.Add(driveItem);
        }

        TreeItems.Add(thisPC);
    }

    private static void AddQuickAccessItem(TreeItemViewModel parent, string name, Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (!Directory.Exists(path)) return;
        var item = new TreeItemViewModel(name, path, IconHelper.GetIcon(path));
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
            if (File.Exists(path)) { OpenFile(path); return; }
            StatusText = $"Path not found: {path}";
            return;
        }

        if (addToHistory && !string.IsNullOrEmpty(_currentPath))
        {
            _backHistory.Push(_currentPath);
            _forwardHistory.Clear();
        }

        CurrentPath = path;
        LoadItems(path);
        UpdateNavState();
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

    private void Refresh()
    {
        if (!string.IsNullOrEmpty(_currentPath))
            LoadItems(_currentPath);
    }

    // ── File listing ─────────────────────────────────────────────────────────

    private void LoadItems(string path)
    {
        CurrentItems.Clear();

        try
        {
            var dirInfo = new DirectoryInfo(path);

            // Folders first (sorted)
            foreach (var dir in dirInfo.GetDirectories().OrderBy(d => d.Name))
            {
                try
                {
                    var vm = new FileItemViewModel
                    {
                        Name         = dir.Name,
                        FullPath     = dir.FullName,
                        IsDirectory  = true,
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
                    var vm = new FileItemViewModel
                    {
                        Name         = file.Name,
                        FullPath     = file.FullName,
                        IsDirectory  = false,
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
    }

    // ── Item actions ─────────────────────────────────────────────────────────

    public void OpenItem(FileItemViewModel item)
    {
        if (item.IsDirectory)
            Navigate(item.FullPath);
        else
            OpenFile(item.FullPath);
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

            // The window selects the new folder and asks for its name.
            var newItem = CurrentItems.FirstOrDefault(x => x.FullPath == path);
            if (newItem != null)
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
        _clipboardPaths = _selectedItems.Select(i => i.FullPath).ToList();
        _clipboardIsCut = false;
        PasteCommand.RaiseCanExecuteChanged();
    }

    private void CutSelected()
    {
        _clipboardPaths = _selectedItems.Select(i => i.FullPath).ToList();
        _clipboardIsCut = true;
        PasteCommand.RaiseCanExecuteChanged();
    }

    private void Paste()
    {
        if (_clipboardPaths == null) return;

        foreach (var src in _clipboardPaths)
        {
            try
            {
                var name = Path.GetFileName(src);

                if (Directory.Exists(src) && IsSameOrSubPath(src, _currentPath))
                {
                    MessageBox.Show(
                        $"Cannot {(_clipboardIsCut ? "move" : "copy")} '{name}' into itself or one of its subfolders.",
                        "Paste", MessageBoxButton.OK, MessageBoxImage.Warning);
                    continue;
                }

                // Cutting an item and pasting it where it already is changes nothing.
                if (_clipboardIsCut && IsSamePath(Path.GetDirectoryName(src), _currentPath))
                    continue;

                var dest = GetUniqueDestPath(_currentPath, name!);

                if (Directory.Exists(src))
                {
                    if (_clipboardIsCut) Directory.Move(src, dest);
                    else                 CopyDirectory(src, dest);
                }
                else if (File.Exists(src))
                {
                    if (_clipboardIsCut) File.Move(src, dest);
                    else                 File.Copy(src, dest);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Paste error: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        if (_clipboardIsCut)
        {
            _clipboardPaths = null;
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
