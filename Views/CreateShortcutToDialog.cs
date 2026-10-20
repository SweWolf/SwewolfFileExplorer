using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// "Create Shortcut...": in which folder should the shortcut go? The folder shown is suggested, the box can be edited or Browse... used (Windows'
/// own Create Shortcut always puts it beside the item; here the user chooses). A folder that is not there yet is made. Built in code.
/// What happens when the folder cannot be written to, or the shortcut is there already, is asked by the caller (MainWindow.CreateShortcutsFor).
/// </summary>
public sealed class CreateShortcutToDialog : Window
{
    private readonly TextBox _folderBox = new() { MinWidth = 440, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MinHeight = 20 };

    /// <summary>The folder chosen when the dialog closed with Create.</summary>
    public string FolderPath { get; private set; } = "";

    public CreateShortcutToDialog(string what, string suggestedFolder)
    {
        Title = "Create Shortcut";
        FontSize = 14;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 900;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = $"In which folder should the shortcut to {what} be made?",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var row = new DockPanel();
        var browse = new Button { Content = "Browse...", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(_folderBox);
        panel.Children.Add(row);
        panel.Children.Add(_hint);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var create = new Button { Content = "Create", MinWidth = 96, Padding = new Thickness(16, 6, 16, 6), IsDefault = true };
        create.SetResourceReference(Control.BorderBrushProperty, SystemColors.AccentColorBrushKey);   // the default button shows it, as in the other dialogs
        create.BorderThickness = new Thickness(2);
        var cancel = new Button { Content = "Cancel", MinWidth = 96, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        _folderBox.Text = suggestedFolder;
        _folderBox.TextChanged += (_, _) => UpdateHint();
        browse.Click += (_, _) => Browse();
        create.Click += (_, _) => Accept();
        Loaded += (_, _) => { _folderBox.Focus(); _folderBox.SelectAll(); };
        UpdateHint();
    }

    /// <summary>Asks; returns the folder for the shortcut, or null if the user canceled.</summary>
    public static string? Ask(Window? owner, string what, string suggestedFolder)
    {
        var dlg = new CreateShortcutToDialog(what, suggestedFolder) { Owner = owner is { IsVisible: true } ? owner : null };
        if (dlg.Owner == null) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? dlg.FolderPath : null;
    }

    private void UpdateHint()
    {
        var text = _folderBox.Text.Trim();
        _hint.Text = text.Length > 0 && Path.IsPathRooted(text) && !Directory.Exists(text) && !File.Exists(text)
            ? "This folder does not exist yet. It will be created."
            : "";
    }

    private void Browse()
    {
        // Start at the nearest folder that exists.
        string start = _folderBox.Text.Trim();
        try { while (!string.IsNullOrEmpty(start) && !Directory.Exists(start)) start = Path.GetDirectoryName(start) ?? ""; }
        catch { start = ""; }

        var dlg = new OpenFolderDialog { Title = "Choose the folder for the shortcut", InitialDirectory = start };
        if (dlg.ShowDialog(this) == true) _folderBox.Text = dlg.FolderName;
    }

    private void Accept()
    {
        var problem = Check(CleanFolder(_folderBox.Text));
        if (problem != null)
        {
            MessageBox.Show(this, problem, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            _folderBox.Focus();
            return;
        }
        FolderPath = Path.GetFullPath(CleanFolder(_folderBox.Text));
        DialogResult = true;
    }

    // A folder pasted from Explorer's "Copy as path" comes in quotes: the same forgiveness as the address bar.
    private static string CleanFolder(string text) => ViewModels.FolderTabViewModel.CleanTypedPath(text);

    /// <summary>What is wrong with the folder, or null if a shortcut can go there.</summary>
    internal static string? Check(string folder)
    {
        if (folder.Length == 0) return "Enter the folder for the shortcut.";
        try
        {
            if (!Path.IsPathRooted(folder)) return "Enter the whole path of the folder, for example D:\\Shortcuts.";
            var full = Path.GetFullPath(folder);
            if (File.Exists(full)) return "That is the name of a file, not a folder.";

            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root)) return $"The drive {root} is not available.";
        }
        catch (Exception ex)
        {
            return $"That is not a usable folder: {ex.Message}";
        }
        return null;
    }
}
