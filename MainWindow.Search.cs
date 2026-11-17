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

    private void SearchCancel_Click(object sender, RoutedEventArgs e) => VM.CancelSearch();

    /// <summary>The search bar is there only in a tab that shows search results; Cancel only while the search still runs.</summary>
    private void UpdateSearchBar()
    {
        SearchBar.Visibility = VM.InSearch ? Visibility.Visible : Visibility.Collapsed;
        SearchStatusText.Text = VM.SearchStatus;
        SearchCancelButton.Visibility = VM.SearchRunning ? Visibility.Visible : Visibility.Collapsed;
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
