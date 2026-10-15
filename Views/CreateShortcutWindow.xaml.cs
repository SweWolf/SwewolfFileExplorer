using System.IO;
using System.Windows;
using SweWolfFileExplorer.Helpers;

namespace SweWolfFileExplorer.Views;

/// <summary>Creates a shortcut to the running EXE on the desktop and/or in the Start menu.</summary>
public partial class CreateShortcutWindow : Window
{
    private readonly string _exePath = Environment.ProcessPath ?? "";

    public CreateShortcutWindow()
    {
        InitializeComponent();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (DesktopBox.IsChecked != true && StartMenuBox.IsChecked != true)
        {
            MessageBox.Show(this, "Please select at least one location.",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool allUsers = AllUsersButton.IsChecked == true;
        var created = new List<string>();
        var failed  = new List<string>();

        if (DesktopBox.IsChecked == true)
        {
            string folder = Environment.GetFolderPath(allUsers
                ? Environment.SpecialFolder.CommonDesktopDirectory
                : Environment.SpecialFolder.DesktopDirectory);
            TryCreate(Path.Combine(folder, $"{AppInfo.Name}.lnk"), created, failed);
        }

        if (StartMenuBox.IsChecked == true)
        {
            string startMenu = Environment.GetFolderPath(allUsers
                ? Environment.SpecialFolder.CommonStartMenu
                : Environment.SpecialFolder.StartMenu);
            string programs = Path.Combine(startMenu, "Programs");
            try
            {
                Directory.CreateDirectory(programs);
                TryCreate(Path.Combine(programs, $"{AppInfo.Name}.lnk"), created, failed);
            }
            catch (Exception ex)
            {
                failed.Add($"{programs}\n  ({ex.Message})");
            }
        }

        if (failed.Count > 0)
        {
            string message = "Could not create the following shortcut(s):\n\n" + string.Join("\n", failed);
            if (allUsers)
                message += "\n\nCreating shortcuts for all users requires administrator privileges.";
            MessageBox.Show(this, message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (created.Count > 0)
        {
            MessageBox.Show(this, "Shortcut(s) created successfully:\n\n" + string.Join("\n", created),
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
    }

    private void TryCreate(string shortcutPath, List<string> created, List<string> failed)
    {
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell  = Activator.CreateInstance(shellType)!;
            dynamic link   = shell.CreateShortcut(shortcutPath);
            link.TargetPath       = _exePath;
            link.WorkingDirectory = Path.GetDirectoryName(_exePath);
            link.Description      = AppInfo.Name;
            link.IconLocation     = _exePath;
            link.Save();
            created.Add(shortcutPath);
        }
        catch (Exception ex)
        {
            failed.Add($"{shortcutPath}\n  ({ex.Message})");
        }
    }
}
