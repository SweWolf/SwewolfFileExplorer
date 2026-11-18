using System.Windows;
using SwewolfFileExplorer.Helpers;
using SwewolfFileExplorer.ViewModels;
using SwewolfFileExplorer.Views;

namespace SwewolfFileExplorer;

/// <summary>
/// Search (2026-11-17): the Search button and Ctrl+Shift+F open the <see cref="SearchWindow"/>; its Search button opens a new tab whose list
/// fills with the results while <see cref="FileSearch"/> walks the folders in the background (FolderTabViewModel.StartSearch). The search
/// bar above the list of such a tab says how it goes and has Cancel. Plan: D:\Dev\dev-notes\FileExplorer-search-plan.md.
/// </summary>
public partial class MainWindow
{
    private void SearchButton_Click(object sender, RoutedEventArgs e) => ShowSearchWindow();

    private void ShowSearchWindow()
    {
        // The folder to start with: the folder shown, or where the search in this tab looked.
        string suggested = VM.InSearch && VM.ActiveTab.Criteria != null ? string.Join("; ", VM.ActiveTab.Criteria.Roots)
                         : VM.HasFolder && !VM.InArchive && System.IO.Directory.Exists(VM.CurrentPath) ? VM.CurrentPath
                         : "";

        var criteria = SearchWindow.Ask(this, suggested);
        if (criteria == null) return;

        VM.OpenSearchTab(criteria);
        FileListView.Focus();
    }

    private void SearchCancel_Click(object sender, RoutedEventArgs e) => SearchCancelOrClose();

    // When a search ends, the Stop button turns into Close. For one second Close does nothing (the button is disabled and Esc does not close
    // the tab): the user who aimed at Stop just as the search finished (or who pressed Stop twice) must not close the results by accident.
    private FolderTabViewModel? _searchBarTab;
    private bool _searchBarWasRunning;
    private DateTime _closeBlockedUntil;
    private System.Windows.Threading.DispatcherTimer? _closeBlockTimer;

    /// <summary>Stop while the search still runs; once it is over the same button (and Esc) closes the tab of the results (after a second).</summary>
    private void SearchCancelOrClose()
    {
        if (VM.SearchRunning) VM.CancelSearch();
        else if (DateTime.UtcNow < _closeBlockedUntil) return;
        else if (VM.Tabs.Count > 1) VM.CloseTabCommand.Execute(VM.ActiveTab);   // never the last tab: that would close the program
    }

    /// <summary>The search bar is there only in a tab that shows search results.</summary>
    private void UpdateSearchBar()
    {
        SearchBar.Visibility = VM.InSearch ? Visibility.Visible : Visibility.Collapsed;
        SearchStatusText.Text = VM.SearchStatus;
        SearchCancelButton.Content = VM.SearchRunning ? "Stop" : "Close";
        SearchCancelButton.ToolTip = VM.SearchRunning
            ? "Stop looking (Esc). What was found so far stays in the list."
            : "Close this tab of search results (Esc)";
        SearchCancelButton.Visibility = VM.SearchRunning || VM.Tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        // This very tab was running a moment ago and is not any more: Close waits a second. (Another tab coming to the front is no such moment.)
        bool ended = ReferenceEquals(_searchBarTab, VM.ActiveTab) && _searchBarWasRunning && !VM.SearchRunning && VM.InSearch;
        _searchBarTab = VM.ActiveTab;
        _searchBarWasRunning = VM.SearchRunning;
        if (ended)
        {
            _closeBlockedUntil = DateTime.UtcNow.AddSeconds(1);
            SearchCancelButton.IsEnabled = false;
            _closeBlockTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _closeBlockTimer.Stop();
            _closeBlockTimer.Tick -= CloseBlockEnded;
            _closeBlockTimer.Tick += CloseBlockEnded;
            _closeBlockTimer.Start();
        }
        else if (VM.SearchRunning || DateTime.UtcNow >= _closeBlockedUntil)
        {
            SearchCancelButton.IsEnabled = true;
        }
    }

    private void CloseBlockEnded(object? sender, EventArgs e)
    {
        _closeBlockTimer?.Stop();
        SearchCancelButton.IsEnabled = true;
    }

    /// <summary>Open File Location (the list's menu in search results): the item in its own folder, in a new tab.</summary>
    private void CtxOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is not FileItemViewModel item) return;
        var folder = System.IO.Path.GetDirectoryName(item.FullPath.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
        {
            MessageBox.Show(this, $"The folder of \"{item.Name}\" is not there any more.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ShowFolderInTab(folder, new List<string> { item.FullPath });
    }
}
