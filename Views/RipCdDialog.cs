using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// The questions before an audio CD is ripped (the Rip button): file type, quality, whether to verify, and the output folder (made if it is not
/// there; Browse... for a folder). What was chosen is kept for next time (<see cref="AppSettings.RipFormat"/> and the others). OK / Cancel.
/// Built in code. The ripping itself is <see cref="CdRipper"/>.
/// </summary>
public sealed class RipCdDialog : Window
{
    private readonly ComboBox _format  = new() { MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private TextBlock _qualityLabel = null!;
    private readonly ComboBox _quality = new() { MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _verify  = new() { Content = "Verify the Rip by Reading Each Track Twice", Margin = new Thickness(0, 16, 0, 0) };
    private readonly TextBox _folder   = new() { MinWidth = 440, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint   = new() { Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MinHeight = 20 };

    public CdRipper.RipFormat Format { get; private set; } = CdRipper.Formats[0];
    public int QualityIndex { get; private set; }
    public bool Verify { get; private set; }

    /// <summary>The output folder chosen (the full path; it may not exist yet).</summary>
    public string FolderPath { get; private set; } = "";

    private readonly RadioButton _radioSelected = new() { GroupName = "RipWhich", Margin = new Thickness(0, 0, 0, 6) };
    private readonly RadioButton _radioAll      = new() { GroupName = "RipWhich" };

    /// <summary>True: rip all the tracks of the disc. False: only the ones that were selected.</summary>
    public bool AllTracks { get; private set; } = true;

    private static string TracksText(string what, string? disc) =>
        disc != null ? $"Rip {what} of \"{disc}\" to music files." : $"Rip {what} of the audio CD to music files.";

    /// <param name="disc">The name of the disc (null if not known).</param>
    /// <param name="total">How many tracks the disc has.</param>
    /// <param name="selected">How many of them are selected in the list.</param>
    /// <param name="icon">The icon of the CD drive, as the tree shows it.</param>
    private readonly bool _folderFixed;

    /// <param name="fixedFolder">Tracks were dropped in this folder: it is the output folder and cannot be changed (null: the user chooses).</param>
    public RipCdDialog(string? disc, int total, int selected, System.Windows.Media.ImageSource? icon = null, string? fixedFolder = null)
    {
        _folderFixed = !string.IsNullOrEmpty(fixedFolder);
        Title = "Rip Audio CD";
        if (icon != null) Icon = icon;
        string allText = TracksText(total == 1 ? "the track" : $"all {total} tracks", disc);
        bool choice = selected > 0 && selected < total;   // with none or all selected there is nothing to choose
        FontSize = 14;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 900;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;

        var settings = AppSettingsStore.Current;
        var panel = new StackPanel { Margin = new Thickness(24) };

        if (choice)
        {
            // "All" is listed first, but the selected tracks are the default: the other way is one click away.
            _radioSelected.Content = new TextBlock { Text = TracksText(selected == 1 ? "the selected track" : $"the {selected} selected tracks", disc), TextWrapping = TextWrapping.Wrap, MaxWidth = 600 };
            _radioAll.Content      = new TextBlock { Text = allText, TextWrapping = TextWrapping.Wrap, MaxWidth = 600 };
            _radioSelected.IsChecked = true;
            var which = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            _radioAll.Margin = new Thickness(0, 0, 0, 6);   // "all" on top; "selected" below it is still the one checked
            _radioSelected.Margin = new Thickness(0);
            which.Children.Add(_radioAll);
            which.Children.Add(_radioSelected);
            panel.Children.Add(which);
        }
        else
            panel.Children.Add(new TextBlock { Text = allText, TextWrapping = TextWrapping.Wrap, MaxWidth = 640, Margin = new Thickness(0, 0, 0, 14) });

        // File type and quality.
        panel.Children.Add(Label("File Type:", _format));
        panel.Children.Add(_format);
        panel.Children.Add(_qualityLabel = Label("Quality:", _quality, top: 12));
        panel.Children.Add(_quality);

        foreach (var f in CdRipper.Formats) _format.Items.Add(f.Name);
        int formatIndex = Math.Max(0, Array.FindIndex(CdRipper.Formats, f => f.Key == settings.RipFormat));
        _format.SelectedIndex = formatIndex;
        FillQualities(formatIndex, settings.RipQuality);
        _format.SelectionChanged += (_, _) => FillQualities(_format.SelectedIndex, 0);

        // Verify.
        _verify.IsChecked = settings.RipVerify;
        _verify.ToolTip = "Reads every track a second time and compares the two reads. Finds damage that the drive does not report, and takes about twice as long.";
        panel.Children.Add(_verify);
        panel.Children.Add(new TextBlock
        {
            Text = "Slower (about twice as long), but finds read problems on scratched discs.",
            Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MaxWidth = 640, Margin = new Thickness(22, 2, 0, 0),
        });

        // Output folder.
        panel.Children.Add(Label("Output Folder:", _folder, top: 16));
        var row = new DockPanel();
        var browse = new Button { Content = "Browse...", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(_folder);
        panel.Children.Add(row);
        panel.Children.Add(_hint);

        // OK / Cancel.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = "OK", MinWidth = 115, Padding = new Thickness(16, 6, 16, 6), IsDefault = true };
        ok.SetResourceReference(Control.BorderBrushProperty, SystemColors.AccentColorBrushKey);   // the default button shows it, as in the other dialogs
        ok.BorderThickness = new Thickness(2);
        var cancel = new Button { Content = "Cancel", MinWidth = 115, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        // The folder: the last one used, else the user's Music folder.
        var start = settings.RipFolder;
        if (string.IsNullOrWhiteSpace(start)) start = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (_folderFixed)
        {
            // Dropped in a folder: that is where the files go. Shown, but not to be changed.
            start = fixedFolder!;
            _folder.IsEnabled = false;
            browse.IsEnabled = false;
        }
        _folder.Text = start;
        _folder.TextChanged += (_, _) => UpdateHint();
        browse.Click += (_, _) => Browse();
        ok.Click += (_, _) => Accept();
        Loaded += (_, _) => { ok.Focus(); UpdateHint(); };
    }

    /// <summary>Asks; returns the dialog when the user chose OK (null if canceled).</summary>
    public static RipCdDialog? Ask(Window? owner, string? disc, int total, int selected, System.Windows.Media.ImageSource? icon = null, string? fixedFolder = null)
    {
        var dlg = new RipCdDialog(disc, total, selected, icon, fixedFolder) { Owner = owner is { IsVisible: true } ? owner : null };
        if (dlg.Owner == null) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? dlg : null;
    }

    private static TextBlock Label(string text, UIElement target, double top = 0) =>
        new() { Text = text, Margin = new Thickness(0, top, 0, 4) };

    private void FillQualities(int formatIndex, int wanted)
    {
        var format = CdRipper.Formats[Math.Clamp(formatIndex, 0, CdRipper.Formats.Length - 1)];
        _qualityLabel.Text = format.QualityHeader;
        _quality.Items.Clear();
        foreach (var q in format.Qualities) _quality.Items.Add(q.Label);
        _quality.SelectedIndex = Math.Clamp(wanted, 0, format.Qualities.Length - 1);
    }

    private void UpdateHint()
    {
        if (_folderFixed) { _hint.Text = "The tracks go to the folder you copied or dropped them in."; return; }
        var text = CleanFolder(_folder.Text);
        _hint.Text = text.Length > 0 && Path.IsPathRooted(text) && !Directory.Exists(text) && !File.Exists(text)
            ? "This folder does not exist yet. It will be created."
            : "";
    }

    private void Browse()
    {
        // Start at the nearest folder that exists.
        string start = CleanFolder(_folder.Text);
        try { while (!string.IsNullOrEmpty(start) && !Directory.Exists(start)) start = Path.GetDirectoryName(start) ?? ""; }
        catch { start = ""; }

        var dlg = new OpenFolderDialog { Title = "Choose the folder for the music files", InitialDirectory = start };
        if (dlg.ShowDialog(this) == true) _folder.Text = dlg.FolderName;
    }

    private void Accept()
    {
        var problem = Check(CleanFolder(_folder.Text));
        if (problem != null)
        {
            MessageBox.Show(this, problem, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            _folder.Focus();
            return;
        }

        AllTracks    = _radioAll.IsChecked == true || _radioSelected.IsChecked != true;
        Format       = CdRipper.Formats[Math.Max(0, _format.SelectedIndex)];
        QualityIndex = Math.Max(0, _quality.SelectedIndex);
        Verify       = _verify.IsChecked == true;
        FolderPath   = Path.GetFullPath(CleanFolder(_folder.Text));

        var settings = AppSettingsStore.Current;   // remembered for next time
        settings.RipFormat  = Format.Key;
        settings.RipQuality = QualityIndex;
        settings.RipVerify  = Verify;
        if (!_folderFixed) settings.RipFolder = FolderPath;   // a dropped-in folder is not "the last folder chosen"
        AppSettingsStore.Save(settings);

        DialogResult = true;
    }

    // A folder pasted from Explorer's "Copy as path" comes in quotes: the same forgiveness as the address bar.
    private static string CleanFolder(string text) => ViewModels.FolderTabViewModel.CleanTypedPath(text);

    /// <summary>What is wrong with the folder, or null if the files can go there.</summary>
    internal static string? Check(string folder)
    {
        if (folder.Length == 0) return "Enter the folder for the music files.";
        try
        {
            if (!Path.IsPathRooted(folder)) return "Enter the whole path of the folder, for example D:\\Music\\Ripped.";
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
