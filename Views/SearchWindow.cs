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

    // The size filter: Any Size / At Least / At Most / Between, one or two numbers and a unit. A size means files only (a folder has no size).
    private readonly ComboBox _sizeMode = new() { Width = 130 };
    private readonly TextBox _size1 = new() { Width = 80, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(10, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _size2 = new() { Width = 80, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sizeAnd = new() { Text = "and", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
    private readonly ComboBox _sizeUnit = new() { Width = 72, Margin = new Thickness(10, 0, 0, 0) };
    private TextBlock? _sizeNote;

    // Folders to leave out: the check box (remembered) and, while it is checked, the list (filled from Settings > Search; edits count for this search
    // only, unless "Save as My Standard List" is pressed).
    private readonly CheckBox _skipExcluded = new() { Content = "Skip My Excluded Folders", Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox _excluded = new() { AcceptsReturn = true, MinHeight = 66, MaxHeight = 120, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6, 4, 6, 4),
                                                 VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
    private readonly Button _addExcluded = new() { Content = "Add Folder...", Padding = new Thickness(14, 4, 14, 4) };
    private readonly Button _saveExcluded = new() { Content = "Save as My Standard List", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
    private StackPanel? _excludedPanel;

    // Stop After Finding N Items: a check box and a whole number (this run only; unchecked = only the built-in limit of 50,000).
    private readonly CheckBox _stopAfter = new() { Content = "Stop After Finding", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _stopAfterCount = new() { Text = "1000", Width = 80, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(10, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private static (bool On, string Text) _lastStop = (false, "1000");
    private int _kindsBeforeSize;   // what "Search for" was before a size forced Files Only
    private static (int Mode, string Text1, string Text2, int Unit) _lastSize;   // this run only
    private static int _lastKindsChoice;

    /// <summary>The criteria when the window closed with Search.</summary>
    public SearchCriteria? Result { get; private set; }

    public SearchWindow(string suggestedFolder)
    {
        Title = "Search";
        Icon = IconHelper.GetStockIcon(IconHelper.SIID_FIND) ?? GlyphIcon.Make("îœ¡");   // Windows' own search icon; the symbol-font magnifier if Windows has none
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
        namePanel.Children.Add(Hint("Every word must be in the name, in any order and in any case. A word with * or ? must fit the whole name (*.ico ends in .ico). Use quotes for a phrase. Only names are searched, not what is inside the files."));
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

        // Size
        foreach (var s in new[] { "Any Size", "At Least", "At Most", "Between" }) _sizeMode.Items.Add(s);
        foreach (var s in new[] { "KB", "MB", "GB" }) _sizeUnit.Items.Add(s);
        _sizeMode.SelectedIndex = 0;
        _sizeUnit.SelectedIndex = 1;
        NewRow();
        var sizeLabel = Label("Size:");
        sizeLabel.Margin = new Thickness(0, 14, 16, 0);
        Put(sizeLabel, row, 0);
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        sizeRow.Children.Add(_sizeMode);
        sizeRow.Children.Add(_size1);
        sizeRow.Children.Add(_sizeAnd);
        sizeRow.Children.Add(_size2);
        sizeRow.Children.Add(_sizeUnit);
        Put(sizeRow, row, 1);
        row++;
        NewRow();
        _sizeNote = Hint("A folder has no size, so with a size only files are searched.");
        Put(_sizeNote, row, 1);
        row++;
        _sizeMode.SelectionChanged += (_, _) => UpdateSizeUi();

        // Excluded folders: a check box; the list (with its two buttons) shows only while it is checked.
        NewRow();
        var exPanel = new StackPanel();
        exPanel.Children.Add(_skipExcluded);
        _excludedPanel = new StackPanel { Visibility = Visibility.Collapsed };
        _excludedPanel.Children.Add(_excluded);
        var exButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        exButtons.Children.Add(_addExcluded);
        exButtons.Children.Add(_saveExcluded);
        _excludedPanel.Children.Add(exButtons);
        _excludedPanel.Children.Add(Hint("One folder per line: a full path (C:\\MyFolder) or just a name (node_modules). The folder searched in is never skipped."));
        exPanel.Children.Add(_excludedPanel);
        Put(exPanel, row, 1);
        row++;
        // Stop after finding N items: below the excluded folders.
        NewRow();
        var stopRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        stopRow.Children.Add(_stopAfter);
        stopRow.Children.Add(_stopAfterCount);
        stopRow.Children.Add(new TextBlock { Text = "Items", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        var stopPanel = new StackPanel();
        stopPanel.Children.Add(stopRow);
        stopPanel.Children.Add(Hint("The search ends when this many are found: the first ones found, not the best ones. Without it a search ends at 50,000."));
        Put(stopPanel, row, 1);
        row++;
        _stopAfter.Checked += (_, _) => _stopAfterCount.IsEnabled = true;
        _stopAfter.Unchecked += (_, _) => _stopAfterCount.IsEnabled = false;

        _skipExcluded.Checked += (_, _) => UpdateExcludedUi();
        _skipExcluded.Unchecked += (_, _) => UpdateExcludedUi();
        _excluded.TextChanged += (_, _) => UpdateExcludedUi();
        _addExcluded.Click += (_, _) => AddExcludedFolder();
        _saveExcluded.Click += (_, _) => SaveExcludedAsStandard();

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
            _kinds.SelectedIndex = _lastKindsChoice;
            _hidden.SelectedIndex = (int)_last.Hidden;
            _modFrom.SelectedDate = _last.ModifiedFrom;
            _modTo.SelectedDate = _last.ModifiedTo;
            _creFrom.SelectedDate = _last.CreatedFrom;
            _creTo.SelectedDate = _last.CreatedTo;
            _size1.Text = _lastSize.Text1;
            _size2.Text = _lastSize.Text2;
            _sizeUnit.SelectedIndex = _lastSize.Unit;
            _sizeMode.SelectedIndex = _lastSize.Mode;   // (this also shows or hides the boxes)
        }
        UpdateSizeUi();

        _stopAfterCount.Text = _lastStop.Text;
        _stopAfter.IsChecked = _lastStop.On;
        _stopAfterCount.IsEnabled = _lastStop.On;

        // The list always starts as Settings > Search has it; the check box is as it was left.
        _excluded.Text = AppSettingsStore.Current.SearchExcludedFolders.Replace("\n", Environment.NewLine);
        _skipExcluded.IsChecked = AppSettingsStore.Current.SearchSkipExcluded;
        UpdateExcludedUi();

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

    /// <summary>The list shows while the box is checked; "Save as My Standard List" is there only when the list differs from the saved one.</summary>
    private void UpdateExcludedUi()
    {
        bool on = _skipExcluded.IsChecked == true;
        if (_excludedPanel != null) _excludedPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        var saved = string.Join("\n", FileSearch.ParseExcluded(AppSettingsStore.Current.SearchExcludedFolders));
        var now = string.Join("\n", FileSearch.ParseExcluded(_excluded.Text));
        _saveExcluded.IsEnabled = !string.Equals(saved, now, StringComparison.OrdinalIgnoreCase);
        _saveExcluded.ToolTip = _saveExcluded.IsEnabled
            ? "Keep this list in Settings > Search, so every search starts with it"
            : "This is the list in Settings > Search already";
        SizeToContent = SizeToContent.WidthAndHeight;   // the window grows and shrinks with the list
    }

    private void AddExcludedFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder to exclude" };
        if (dlg.ShowDialog(this) != true) return;
        var text = _excluded.Text.TrimEnd();
        _excluded.Text = (text.Length > 0 ? text + Environment.NewLine : "") + dlg.FolderName;
        _excluded.CaretIndex = _excluded.Text.Length;
    }

    private void SaveExcludedAsStandard()
    {
        var s = AppSettingsStore.Current;
        s.SearchExcludedFolders = string.Join("\n", FileSearch.ParseExcluded(_excluded.Text));
        AppSettingsStore.Save(AppSettingsStore.Current);
        UpdateExcludedUi();
    }

    /// <summary>Shows what the chosen size mode needs. With a size "Search for" is Files Only and locked (a folder has no size); Any Size gives the old choice back.</summary>
    private void UpdateSizeUi()
    {
        int mode = _sizeMode.SelectedIndex;
        bool on = mode > 0;
        _size1.Visibility = _sizeUnit.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _sizeAnd.Visibility = _size2.Visibility = mode == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (_sizeNote != null) _sizeNote.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (on && _kinds.IsEnabled)
        {
            _kindsBeforeSize = _kinds.SelectedIndex;
            _kinds.SelectedIndex = (int)SearchKinds.FilesOnly;
            _kinds.IsEnabled = false;
        }
        else if (!on && !_kinds.IsEnabled)
        {
            _kinds.SelectedIndex = _kindsBeforeSize;
            _kinds.IsEnabled = true;
        }
    }

    /// <summary>Reads a size box: "200", "1.5" or "1,5", with KB, MB or GB typed after it overriding the unit box. Says what is wrong and returns false.</summary>
    private bool TryReadSize(TextBox box, out long? bytes)
    {
        bytes = null;
        var m = System.Text.RegularExpressions.Regex.Match(box.Text.Trim(), @"^(\d+(?:[.,]\d+)?)\s*(kb|mb|gb)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            MessageBox.Show(this, "Enter the size as a number, for example 200 or 1.5. The unit (KB, MB or GB) is chosen in the box next to it.",
                            AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            box.Focus();
            return false;
        }
        double number = double.Parse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
        int unit = m.Groups[2].Success ? "kb mb gb".Split(' ').ToList().IndexOf(m.Groups[2].Value.ToLowerInvariant()) : _sizeUnit.SelectedIndex;
        bytes = (long)Math.Round(number * Math.Pow(1024, unit + 1));
        return true;
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

        // The size: a number (with a unit typed after it, 1.5GB, if wanted) for At Least and At Most, two for Between.
        int mode = _sizeMode.SelectedIndex;
        if (mode > 0)
        {
            long? lo = null, hi = null;
            if (mode != 2 && !TryReadSize(_size1, out lo)) return;
            if (mode == 2 && !TryReadSize(_size1, out hi)) return;
            if (mode == 3 && !TryReadSize(_size2, out hi)) return;
            if (mode == 1) hi = null;
            if (lo > hi)
            {
                MessageBox.Show(this, "The first size is larger than the second. Change one of them.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
                _size1.Focus();
                return;
            }
            c.MinBytes = lo;
            c.MaxBytes = hi;
            c.Kinds = SearchKinds.FilesOnly;
        }
        // Stop After Finding: a whole number from 1 to the built-in limit.
        if (_stopAfter.IsChecked == true)
        {
            if (!int.TryParse(_stopAfterCount.Text.Trim().Replace(" ", "").Replace(",", ""), out var n) || n < 1 || n > FileSearch.MaxResults)
            {
                MessageBox.Show(this, $"Enter a whole number from 1 to {FileSearch.MaxResults:N0}, or uncheck Stop After Finding.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
                _stopAfterCount.Focus();
                _stopAfterCount.SelectAll();
                return;
            }
            c.StopAfter = n;
        }
        _lastStop = (_stopAfter.IsChecked == true, _stopAfterCount.Text);

        // Folders to leave out (when the box is checked); the check box itself is remembered.
        bool skip = _skipExcluded.IsChecked == true;
        if (skip) c.ExcludedFolders = FileSearch.ParseExcluded(_excluded.Text);
        if (AppSettingsStore.Current.SearchSkipExcluded != skip)
        {
            AppSettingsStore.Current.SearchSkipExcluded = skip;
            AppSettingsStore.Save(AppSettingsStore.Current);
        }

        _lastSize = (mode, _size1.Text, _size2.Text, _sizeUnit.SelectedIndex);
        _lastKindsChoice = _kinds.IsEnabled ? _kinds.SelectedIndex : _kindsBeforeSize;   // not the Files Only that a size forced

        _last = c.Clone();
        Result = c;
        DialogResult = true;
    }
}
