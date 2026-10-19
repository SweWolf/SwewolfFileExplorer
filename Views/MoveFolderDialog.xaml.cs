using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// "Do you also want to move this file to this folder?" with the folder in a box the user can change (or Browse for): what TeraTool
/// asked after a smart rename of a video. A folder that is not there yet is made when the file is moved.
/// </summary>
public partial class MoveFolderDialog : Window
{
    private readonly string _fileName;
    private readonly string _currentFolder;

    /// <summary>The folder chosen when the dialog closed with Move.</summary>
    public string FolderPath { get; private set; } = "";

    public MoveFolderDialog(string fileName, string currentFolder, string suggestedFolder)
    {
        _fileName      = fileName;
        _currentFolder = currentFolder;
        InitializeComponent();
        MessageText.Text = $"Do you also want to move \"{fileName}\" to this folder?";
        FolderBox.Text   = suggestedFolder;
        Loaded += (_, _) => { FolderBox.Focus(); FolderBox.CaretIndex = FolderBox.Text.Length; };
    }

    /// <summary>Asks; returns the folder to move to, or null if the user does not want to move the file.</summary>
    public static string? Ask(Window? owner, string fileName, string currentFolder, string suggestedFolder)
    {
        var dlg = new MoveFolderDialog(fileName, currentFolder, suggestedFolder) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.FolderPath : null;
    }

    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = FolderBox.Text.Trim();
        HintText.Text = text.Length > 0 && Path.IsPathRooted(text) && !Directory.Exists(text)
            ? "This folder does not exist yet. It will be created."
            : "";
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        // Start at the nearest folder that exists.
        string start = FolderBox.Text.Trim();
        try
        {
            while (!string.IsNullOrEmpty(start) && !Directory.Exists(start)) start = Path.GetDirectoryName(start) ?? "";
        }
        catch { start = ""; }

        var dlg = new OpenFolderDialog { Title = "Choose the folder to move the file to", InitialDirectory = start };
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    private void MoveButton_Click(object sender, RoutedEventArgs e)
    {
        var problem = Check(FolderBox.Text.Trim());
        if (problem != null)
        {
            MessageBox.Show(this, problem, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            FolderBox.Focus();
            return;
        }

        FolderPath   = Path.GetFullPath(FolderBox.Text.Trim());
        DialogResult = true;
    }

    /// <summary>What is wrong with the folder, or null if the file can go there.</summary>
    private string? Check(string folder)
    {
        if (folder.Length == 0) return "Enter the folder to move the file to.";

        try
        {
            if (!Path.IsPathRooted(folder)) return "Enter the whole path of the folder, for example D:\\Videos\\Movies.";
            var full = Path.GetFullPath(folder);

            if (string.Equals(full.TrimEnd('\\'), _currentFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return "The file is already in that folder.";
            if (File.Exists(full)) return "That is the name of a file, not a folder.";
            if (File.Exists(Path.Combine(full, _fileName)) || Directory.Exists(Path.Combine(full, _fileName)))
                return $"A file named \"{_fileName}\" already exists in that folder.";
        }
        catch (Exception ex)
        {
            return $"That is not a usable folder: {ex.Message}";
        }

        return null;
    }
}
