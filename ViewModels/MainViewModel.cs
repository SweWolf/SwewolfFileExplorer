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
    private FolderTabViewModel _activeTab;

    public ObservableCollection<FolderTabViewModel> Tabs { get; } = new();

    public MainViewModel()
    {
        _activeTab = AddTab(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        NewTabCommand      = new RelayCommand(_ => OpenNewTab(ActiveTab.CurrentPath, activate: true));
        CloseTabCommand    = new RelayCommand(p => CloseTab(p as FolderTabViewModel ?? ActiveTab));
        NextTabCommand     = new RelayCommand(_ => SwitchTab(+1), _ => Tabs.Count > 1);
        PreviousTabCommand = new RelayCommand(_ => SwitchTab(-1), _ => Tabs.Count > 1);
        ReopenTabCommand   = new RelayCommand(_ => ReopenTab(),   _ => _closedTabs.Count > 0);
        ToggleTreeCommand  = new RelayCommand(_ => ShowTree = !ShowTree);
    }

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
            _activeTab = value;

            // Every binding (address bar, list, tree, commands) now reads the new tab.
            OnPropertyChanged(string.Empty);
            _activeTab.RaiseAllCanExecuteChanged();
            ActiveTabChanged?.Invoke();
        }
    }

    private FolderTabViewModel AddTab(string path)
    {
        var tab = new FolderTabViewModel(_shared, path);
        tab.PropertyChanged += (s, e) =>
        {
            // Only what the active tab shows is of interest to the window.
            if (s == _activeTab) OnPropertyChanged(e.PropertyName);
        };
        tab.SortChanged    += () => { if (tab == _activeTab) SortChanged?.Invoke(); };
        tab.FolderCreated  += item => { if (tab == _activeTab) FolderCreated?.Invoke(item); };
        // The tree sort order is shared: the other tabs' trees show the same folder in the same order.
        tab.TreeSortChanged += path => { foreach (var t in Tabs) t.ResortTree(path); };
        // So is whether the tree shows the hidden folders of a folder.
        tab.TreeHiddenChanged += path => { foreach (var t in Tabs) t.SyncTreeHidden(path); };
        Tabs.Add(tab);
        return tab;
    }

    /// <summary>Opens <paramref name="path"/> in a new tab, next to the active one.</summary>
    public void OpenNewTab(string path, bool activate)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var tab = AddTab(path);
        Tabs.Move(Tabs.IndexOf(tab), Tabs.IndexOf(_activeTab) + 1);
        if (activate) ActiveTab = tab;

        NextTabCommand.RaiseCanExecuteChanged();
        PreviousTabCommand.RaiseCanExecuteChanged();
    }

    private void CloseTab(FolderTabViewModel tab)
    {
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
    public RelayCommand ToggleHiddenCommand    => _activeTab.ToggleHiddenCommand;

    /// <summary>The folder shown is a packed file or in one: read-only.</summary>
    public bool   InArchive      => _activeTab.InArchive;

    public bool   HasHiddenItems => _activeTab.HasHiddenItems;
    public string HiddenText     => _activeTab.HiddenText;

    public RelayCommand ToggleTreeHiddenCommand => _activeTab.ToggleTreeHiddenCommand;
    public bool   HasTreeHiddenFolders => _activeTab.HasTreeHiddenFolders;
    public string TreeHiddenText       => _activeTab.TreeHiddenText;

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

    public void Navigate(string? path, bool addToHistory = true) => _activeTab.Navigate(path, addToHistory);
    public void OpenItem(FileItemViewModel item)                  => _activeTab.OpenItem(item);
    public void RenameItem(FileItemViewModel item, string name)   => _activeTab.RenameItem(item, name);
    public void UpdateSelectedItems(List<FileItemViewModel> items) => _activeTab.UpdateSelectedItems(items);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
