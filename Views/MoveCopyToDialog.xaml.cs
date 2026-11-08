using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// "Move/Copy to": the selected files and folders go to a folder that is typed, browsed for or taken from a list of the user's own folders
/// (kept in settings.json, AppSettings.MoveCopyFolders). Move is always the choice when the window opens: it is what is used most.
/// The moving or copying itself is done by the caller (the same code as Paste and drag and drop, with its progress window and questions).
/// </summary>
public partial class MoveCopyToDialog : Window
{
    private readonly IReadOnlyList<string> _sources;
    private readonly ObservableCollection<string> _folders;

    /// <summary>The folder chosen (the whole path) when the window closed with OK.</summary>
    public string FolderPath { get; private set; } = "";

    /// <summary>True for Move, false for Copy.</summary>
    public bool IsMove { get; private set; } = true;

    /// <summary>True if the checkbox "Open This Folder in a New Tab When Finished" was checked when the window closed with OK.</summary>
    public bool OpenInNewTab { get; private set; }

    /// <param name="showOpenInTab">False hides the "open in a new tab" checkbox (the picture viewer covers the screen: a tab opened behind it would not be seen).</param>
    public MoveCopyToDialog(IReadOnlyList<string> sources, bool showOpenInTab = true)
    {
        _sources = sources;
        InitializeComponent();

        OpenFolderCheck.Visibility = showOpenInTab ? Visibility.Visible : Visibility.Collapsed;
        OpenFolderCheck.IsChecked  = showOpenInTab && AppSettingsStore.Current.MoveCopyOpenInNewTab;

        _folders = new ObservableCollection<string>(AppSettingsStore.Current.MoveCopyFolders);
        FolderList.ItemsSource = _folders;

        ShowSelected();
        UpdateButtons();
        Loaded += (_, _) => { if (_folders.Count > 0) FolderList.Focus(); else FolderBox.Focus(); };
    }

    /// <summary>"Selected: photo.jpg" or "Selected: 5 items (photo.jpg, ...)", with the full paths in the tooltip.</summary>
    private void ShowSelected()
    {
        string NameOf(string p) => Path.GetFileName(p.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : p;

        SelectedText.Text = _sources.Count == 1
            ? NameOf(_sources[0])
            : $"{_sources.Count} items ({string.Join(", ", _sources.Take(3).Select(NameOf))}{(_sources.Count > 3 ? ", ..." : "")})";

        var shown = _sources.Take(25).ToList();
        SelectedText.ToolTip = string.Join(Environment.NewLine, shown) + (_sources.Count > shown.Count ? $"{Environment.NewLine}... and {_sources.Count - shown.Count} more" : "");
    }

    /// <summary>A name with no drive or root (Test5) is a folder inside the folder the items are in, as in Explorer's address bar.</summary>
    private string Resolve(string folder)
    {
        if (Path.IsPathRooted(folder)) return folder;
        var baseFolder = _sources.Count > 0 ? Path.GetDirectoryName(_sources[0].TrimEnd('\\', '/')) : null;
        return string.IsNullOrEmpty(baseFolder) ? folder : Path.Combine(baseFolder, folder);
    }

    private void SaveFolders()
    {
        AppSettingsStore.Current.MoveCopyFolders = _folders.ToList();
        AppSettingsStore.Save(AppSettingsStore.Current);
    }

    private void UpdateButtons()
    {
        int index = FolderList.SelectedIndex;
        RemoveButton.IsEnabled = index >= 0;
        UpButton.IsEnabled     = index > 0;
        DownButton.IsEnabled   = index >= 0 && index < _folders.Count - 1;
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FolderList.SelectedItem is string folder) FolderBox.Text = folder;
        UpdateButtons();
    }

    private void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on an item (not on the empty part of the list).
        if (ItemsControl.ContainerFromElement(FolderList, (DependencyObject)e.OriginalSource) is ListBoxItem) OkButton_Click(sender, e);
    }

    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = FolderBox.Text.Trim();
        string full = "";
        try { full = text.Length > 0 ? Path.GetFullPath(Resolve(text)) : ""; } catch { /* not a usable path: Check says so on OK */ }
        HintText.Text = full.Length > 0 && !Directory.Exists(full)
            ? (Path.IsPathRooted(text) ? "This folder does not exist yet. It will be created."
                                       : $"This folder does not exist yet. It will be created: {full}")
            : (full.Length > 0 && !Path.IsPathRooted(text) ? $"A folder next to the items: {full}" : "");
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder to add to the list", InitialDirectory = NearestExistingFolder(Resolve(FolderBox.Text.Trim())) };
        if (dlg.ShowDialog(this) != true) return;

        string folder = dlg.FolderName;
        int index = _folders.ToList().FindIndex(f => SamePath(f, folder));
        if (index < 0) { _folders.Add(folder); index = _folders.Count - 1; SaveFolders(); }

        FolderList.SelectedIndex = index;   // also puts it in the Folder box
        FolderBox.Text = _folders[index];
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        int index = FolderList.SelectedIndex;
        if (index < 0) return;

        _folders.RemoveAt(index);
        SaveFolders();
        if (_folders.Count > 0) FolderList.SelectedIndex = Math.Min(index, _folders.Count - 1);
        UpdateButtons();
    }

    private void UpButton_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void DownButton_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int direction)
    {
        int index = FolderList.SelectedIndex, to = index + direction;
        if (index < 0 || to < 0 || to >= _folders.Count) return;

        _folders.Move(index, to);
        FolderList.SelectedIndex = to;
        SaveFolders();
        UpdateButtons();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose the folder", InitialDirectory = NearestExistingFolder(Resolve(FolderBox.Text.Trim())) };
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        IsMove = MoveRadio.IsChecked == true;

        var problem = Check(FolderBox.Text.Trim());
        if (problem != null)
        {
            MessageBox.Show(this, problem, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            FolderBox.Focus();
            return;
        }

        FolderPath   = Path.GetFullPath(Resolve(FolderBox.Text.Trim()));

        if (OpenFolderCheck.Visibility == Visibility.Visible)
        {
            OpenInNewTab = OpenFolderCheck.IsChecked == true;
            if (AppSettingsStore.Current.MoveCopyOpenInNewTab != OpenInNewTab)
            {
                AppSettingsStore.Current.MoveCopyOpenInNewTab = OpenInNewTab;   // remembered for next time
                AppSettingsStore.Save(AppSettingsStore.Current);
            }
        }

        DialogResult = true;
    }

    /// <summary>What is wrong with the folder, or null if the items can go there.</summary>
    private string? Check(string folder)
    {
        if (folder.Length == 0) return "Enter the folder to move or copy to.";

        try
        {
            var full = Path.GetFullPath(Resolve(folder));
            if (File.Exists(full)) return "That is the name of a file, not a folder.";

            // Moving to the folder the items are in changes nothing (copying there makes copies with new names).
            if (IsMove && _sources.All(s => SamePath(Path.GetDirectoryName(s.TrimEnd('\\', '/')), full)))
                return _sources.Count == 1 ? "The item is already in that folder." : "The items are already in that folder.";
        }
        catch (Exception ex)
        {
            return $"That is not a usable folder: {ex.Message}";
        }

        return null;
    }

    private static bool SamePath(string? a, string? b) =>
        a != null && b != null && string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder a Browse window should start in: the typed one, or the nearest one above it that exists.</summary>
    private static string NearestExistingFolder(string typed)
    {
        string start = typed.Trim();
        try
        {
            while (!string.IsNullOrEmpty(start) && !Directory.Exists(start)) start = Path.GetDirectoryName(start) ?? "";
        }
        catch { start = ""; }
        return start;
    }
}
