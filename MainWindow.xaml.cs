using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MyFileExplorer.ViewModels;
using MyFileExplorer.Views;

namespace MyFileExplorer;

public partial class MainWindow : Window
{
    private MainViewModel VM => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    // ── Navigation tree ───────────────────────────────────────────────────────

    private void NavigationTree_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeItemViewModel item && !string.IsNullOrEmpty(item.FullPath))
            VM.Navigate(item.FullPath);
    }

    // ── File list ─────────────────────────────────────────────────────────────

    private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = FileListView.SelectedItems
            .Cast<FileItemViewModel>()
            .ToList();

        VM.UpdateSelectedItems(selected);
    }

    private void FileListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileListView.SelectedItem is FileItemViewModel item)
            VM.OpenItem(item);
    }

    private void FileListView_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && FileListView.SelectedItem is FileItemViewModel openItem)
        {
            VM.OpenItem(openItem);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && FileListView.SelectedItem is FileItemViewModel renameItem)
        {
            ShowRenameDialog(renameItem);
            e.Handled = true;
        }
    }

    // ── Context menu ──────────────────────────────────────────────────────────

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool hasSelection = FileListView.SelectedItem != null;
        bool canPaste     = VM.PasteCommand.CanExecute(null);

        var menu = (ContextMenu)sender;
        foreach (var mi in menu.Items.OfType<MenuItem>())
        {
            mi.IsEnabled = mi.Header switch
            {
                "Open"       => hasSelection,
                "Cut"        => hasSelection,
                "Copy"       => hasSelection,
                "Paste"      => canPaste,
                "Delete"     => hasSelection,
                "Rename"     => hasSelection,
                "New Folder" => true,
                _            => true
            };
        }
    }

    private void CtxOpen_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is FileItemViewModel item)
            VM.OpenItem(item);
    }

    private void CtxCut_Click(object sender, RoutedEventArgs e)     => VM.CutCommand.Execute(null);
    private void CtxCopy_Click(object sender, RoutedEventArgs e)    => VM.CopyCommand.Execute(null);
    private void CtxPaste_Click(object sender, RoutedEventArgs e)   => VM.PasteCommand.Execute(null);
    private void CtxDelete_Click(object sender, RoutedEventArgs e)  => VM.DeleteCommand.Execute(null);
    private void CtxNewFolder_Click(object sender, RoutedEventArgs e) => VM.NewFolderCommand.Execute(null);

    private void CtxRename_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is FileItemViewModel item)
            ShowRenameDialog(item);
    }

    // ── Rename dialog ─────────────────────────────────────────────────────────

    private void ShowRenameDialog(FileItemViewModel item)
    {
        var dlg = new InputDialog("Rename", "Enter new name:", item.Name) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.InputText))
            VM.RenameItem(item, dlg.InputText);
    }
}
