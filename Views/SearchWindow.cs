using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// "Search" (Ctrl+Shift+F): where to look and what to look for. OK opens a new tab that fills with the results (see
/// FolderTabViewModel.StartSearch), so this window is only the questions. Built in code. What was asked last time is remembered for
/// this run (not saved), the folder is the one shown.
/// </summary>
public sealed class SearchWindow : Window
{
    private static SearchCriteria? _last;   // this run only

    private readonly TextBox _lookIn = new() { Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox _subfolders = new() { Content = "Include Subfolders", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox _name = new() { Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _kinds = new();
    private readonly ComboBox _hidden = new();
    private readonly DatePicker _modFrom = new(), _modTo = new(), _creFrom = new(), _creTo = new();

    /// <summary>The criteria when the window closed with Search.</summary>
    public SearchCriteria? Result { get; private set; }

    public SearchWindow(string suggestedFolder)
    {
        Title = "Search";
        Icon = GlyphIcon.Make("");   // the magnifier of Windows' symbol font
        FontSize = 14;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;

        foreach (var s in new[] { "Files and Folders", "Files Only", "Folders Only" }) _kinds.Items.Add(s);
        foreach (var s in new[] { "Visible Items Only", "Visible and Hidden Items", "Hidden Items Only" }) _hidden.Items.Add(s);
        _kinds.SelectedIndex = 0;
        _hidden.SelectedIndex = 0;
        foreach (var d in new[] { _modFrom, _modTo, _creFrom, _creTo }) d.Width = 150;

        var grid = new Grid { Margin = new Thickness(24), MinWidth = 640 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;

        RowDefinition NewRow() { var r = new RowDefinition { Height = GridLength.Auto }; grid.RowDefinitions.Add(r); return r; }
        TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        TextBlock Hint(string text) => new() { Text = text, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(0, 4, 0, 0) };
        void Put(UIElement e, int r, int col) { Grid.SetRow(e, r); Grid.SetColumn(e, col); grid.Children.Add(e); }

        // Look in
        NewRow();
        Put(Label("Look in:"), row, 0);
        var lookRow = new DockPanel();
        var browse = new Button { Content = "Browse...", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        lookRow.Children.Add(browse);
        lookRow.Children.Add(_lookIn);
        Put(lookRow, row, 1);
        row++;
        NewRow();
        var lookHint = new StackPanel();
        lookHint.Children.Add(_subfolders);
        lookHint.Children.Add(Hint("Leave the box empty to search all local drives. Separate several folders with a semicolon (;)."));
        Put(lookHint, row, 1);
        row++;

        // Name
        NewRow();
        grid.RowDefinitions[^1].Height = GridLength.Auto;
        var nameLabel = Label("Name:");
        nameLabel.Margin = new Thickness(0, 26, 16, 0);
        nameLabel.VerticalAlignment = VerticalAlignment.Top;
        Put(nameLabel, row, 0);
        var namePanel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        namePanel.Children.Add(_name);
        namePanel.Children.Add(Hint("Every word must be in the name, in any order and in any case. Use * and ? as wildcards, and quotes for a phrase. Only names are searched, not what is inside the files."));
        Put(namePanel, row, 1);
        row++;

        // Type and hidden
        NewRow();
        var typeLabel = Label("Search for:");
        typeLabel.Margin = new Thickness(0, 14, 16, 0);
        Put(typeLabel, row, 0);
        var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        _kinds.Width = 190;
        _hidden.Width = 220;
        _hidden.Margin = new Thickness(12, 0, 0, 0);
        typeRow.Children.Add(_kinds);
        typeRow.Children.Add(_hidden);
        Put(typeRow, row, 1);
        row++;

        // Dates
        DateRow(grid, ref row, "Date Modified:", _modFrom, _modTo, 14);
        DateRow(grid, ref row, "Date Created:", _creFrom, _creTo, 8);
        NewRow();
        Put(Hint("Leave a date empty for no limit on that side. The last day is included."), row, 1);
        row++;

        // The limit of what is searched
        NewRow();
        var note = new TextBlock
        {
            Text = "Compressed files (zip, RAR, 7z ...) are not searched inside. A compressed file itself is found by its name.",
            Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 560, Margin = new Thickness(0, 16, 0, 0)
        };
        Grid.SetColumnSpan(note, 2);
        Put(note, row, 0);
        row++;

        // Buttons
        NewRow();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var search = new Button { Content = "Search", MinWidth = 115, Padding = new Thickness(16, 6, 16, 6), IsDefault = true };
        search.SetResourceReference(Control.BorderBrushProperty, SystemColors.AccentColorBrushKey);   // the default button shows it, as in the other dialogs
        search.BorderThickness = new Thickness(2);
        var cancel = new Button { Content = "Cancel", MinWidth = 115, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(search);
        buttons.Children.Add(cancel);
        Grid.SetColumnSpan(buttons, 2);
        Put(buttons, row, 0);

        Content = grid;

        // What was asked last time comes back (not the folder: that is the one shown now).
        _lookIn.Text = suggestedFolder;
        if (_last != null)
        {
            _name.Text = _last.Text;
            _subfolders.IsChecked = _last.Subfolders;
            _kinds.SelectedIndex = (int)_last.Kinds;
            _hidden.SelectedIndex = (int)_last.Hidden;
            _modFrom.SelectedDate = _last.ModifiedFrom;
            _modTo.SelectedDate = _last.ModifiedTo;
            _creFrom.SelectedDate = _last.CreatedFrom;
            _creTo.SelectedDate = _last.CreatedTo;
        }

        browse.Click += (_, _) => Browse();
        search.Click += (_, _) => Accept();
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    private void DateRow(Grid grid, ref int row, string label, DatePicker from, DatePicker to, double top)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, top, 16, 0) };
        Grid.SetRow(l, row); Grid.SetColumn(l, 0); grid.Children.Add(l);

        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, top, 0, 0) };
        p.Children.Add(new TextBlock { Text = "From", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        p.Children.Add(from);
        p.Children.Add(new TextBlock { Text = "To", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) });
        p.Children.Add(to);
        Grid.SetRow(p, row); Grid.SetColumn(p, 1); grid.Children.Add(p);
        row++;
    }

    /// <summary>Asks; returns what to search for, or null if the user canceled.</summary>
    public static SearchCriteria? Ask(Window? owner, string suggestedFolder)
    {
        var dlg = new SearchWindow(suggestedFolder) { Owner = owner is { IsVisible: true } ? owner : null };
        if (dlg.Owner == null) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    private void Browse()
    {
        var first = _lookIn.Text.Split(';')[0].Trim().Trim('"');
        try { while (!string.IsNullOrEmpty(first) && !Directory.Exists(first)) first = Path.GetDirectoryName(first) ?? ""; }
        catch { first = ""; }

        var dlg = new OpenFolderDialog { Title = "Choose the folder to search", InitialDirectory = first };
        if (dlg.ShowDialog(this) != true) return;

        // Shift: add it to the folders already there.
        _lookIn.Text = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _lookIn.Text.Trim().Length > 0
            ? _lookIn.Text.TrimEnd().TrimEnd(';') + "; " + dlg.FolderName
            : dlg.FolderName;
    }

    private void Accept()
    {
        var roots = new List<string>();
        foreach (var piece in _lookIn.Text.Split(';'))
        {
            var p = ViewModels.FolderTabViewModel.CleanTypedPath(piece);   // quotes of a pasted path are forgiven
            if (p.Length == 0) continue;
            if (!Path.IsPathRooted(p) || !Directory.Exists(p))
            {
                MessageBox.Show(this, $"The folder \"{p}\" does not exist. Enter the whole path of a folder, for example D:\\Videos.",
                                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
                _lookIn.Focus();
                return;
            }
            roots.Add(Path.GetFullPath(p));
        }

        if (roots.Count == 0)   // empty: every local drive
            foreach (var d in DriveInfo.GetDrives())
                try { if (d.DriveType == DriveType.Fixed && d.IsReady) roots.Add(d.RootDirectory.FullName); } catch { }
        if (roots.Count == 0)
        {
            MessageBox.Show(this, "No local drive was found to search.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var c = new SearchCriteria
        {
            Roots = roots,
            Text = _name.Text.Trim(),
            Subfolders = _subfolders.IsChecked == true,
            Kinds = (SearchKinds)Math.Max(0, _kinds.SelectedIndex),
            Hidden = (SearchHidden)Math.Max(0, _hidden.SelectedIndex),
            ModifiedFrom = _modFrom.SelectedDate, ModifiedTo = _modTo.SelectedDate,
            CreatedFrom = _creFrom.SelectedDate, CreatedTo = _creTo.SelectedDate,
        };

        if ((c.ModifiedFrom > c.ModifiedTo) || (c.CreatedFrom > c.CreatedTo))
        {
            MessageBox.Show(this, "The first date is later than the last date. Change one of them.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _last = c.Clone();
        Result = c;
        DialogResult = true;
    }
}
