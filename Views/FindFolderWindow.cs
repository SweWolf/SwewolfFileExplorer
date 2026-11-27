using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// "Find Folder" (the magnifier above the folder tree): a small window of its own, open beside the main window, that finds a folder by its name.
/// It reads the folders below the chosen folder in the background (<see cref="FolderIndex"/>, in memory only) while the user types, and the grid
/// under the boxes shrinks as the typing goes on. Double-click or Enter takes the active tab to the folder (the tree follows); Ctrl+Enter opens
/// it in a new tab. Real folders only: no packed files, no playlists. Built in code. The Search window is a different thing and is not touched.
/// </summary>
/// <summary>Where Find Folder takes the user: the tab shown or a new tab, and with the folder as the root of the tree or not.</summary>
public enum FindFolderTarget { ThisTab, NewTab, TreeRoot, NewTabTreeRoot }

public sealed class FindFolderWindow : Window
{
    /// <summary>At most this many rows are shown (the rest: type more).</summary>
    public const int RowCap = 5000;

    private readonly Action<string, FindFolderTarget> _go;

    private readonly TextBox _lookIn = new() { Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _name = new() { Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox _approx = new()
    {
        Content = "Approximate Matching", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Capitals, accents (å = a), spaces, dots, underscores and other punctuation are ignored, and a letter written twice counts once (Shuter Island finds Shutter Island). Digits count."
    };
    private readonly ComboBox _hidden = new() { Width = 220 };
    private readonly DatePicker _modFrom = new(), _modTo = new(), _creFrom = new(), _creTo = new();
    private readonly CheckBox _showDates = new()
    {
        Content = "Show Date Columns", Margin = new Thickness(0, 10, 0, 0),
        ToolTip = "Show the date a folder was created and the date it was last modified.\nA folder's modified date changes only when something directly inside it is added, removed or renamed, so the created date is usually the more useful one.\nThe columns show by themselves when you use a date above."
    };
    private readonly TextBlock _status = new() { Opacity = 0.75, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _keepOpen = new()
    {
        Content = "Keep This Window Open After Opening a Folder", FontSize = 12, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Unchecked: the window closes when you open a folder.\nChecked: it stays open, so you can try another folder."
    };
    private readonly ListView _list = new() { Margin = new Thickness(0, 10, 0, 0), SelectionMode = SelectionMode.Single };

    private readonly GridView _grid = new();
    private readonly GridViewColumn _colName, _colLocation, _colCreated, _colModified;
    private readonly Dictionary<GridViewColumn, int> _columnId = new();
    private static readonly string[] ColumnTitles = { "Name", "Location", "Date Created", "Date Modified" };   // the ids: 0 name, 1 location, 2 created, 3 modified
    private int _sortColumn;   // the id of the column the list is sorted by (the same ids as FolderQuery.SortColumn)
    private bool _sortAscending = true;

    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private FolderIndex? _index;
    private CancellationTokenSource? _queryCts;
    private int _version;
    private bool _datesTouched;   // the user clicked the check box: the program stops switching it

    public FindFolderWindow(Window? owner, string scope, Action<string, FindFolderTarget> go)
    {
        _go = go;
        Title = "Find Folder";
        Icon = IconHelper.GetStockIcon(IconHelper.SIID_FIND) ?? GlyphIcon.Make("");
        FontSize = 14;
        Width = 760;
        Height = 560;
        MinWidth = 520;
        MinHeight = 360;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        Owner = owner is { IsVisible: true } ? owner : null;
        WindowStartupLocation = Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

        foreach (var s in new[] { "Visible Folders Only", "Visible and Hidden Folders", "Hidden Folders Only" }) _hidden.Items.Add(s);
        _hidden.SelectedIndex = 0;
        foreach (var d in new[] { _modFrom, _modTo, _creFrom, _creTo })
        {
            d.Width = 150;
            d.ToolTip = "Leave a date empty for no limit on that side. The last day is included.";
        }

        // The columns: the dates are added and taken away by the check box.
        _colName = new GridViewColumn { Header = ColumnTitles[0], Width = 210, CellTemplate = NameCell() };
        _colLocation = new GridViewColumn { Header = ColumnTitles[1], Width = 240, DisplayMemberBinding = new Binding(nameof(FolderEntry.Location)) };
        _colCreated = new GridViewColumn { Header = ColumnTitles[2], Width = 130, DisplayMemberBinding = new Binding(nameof(FolderEntry.Created)) { StringFormat = "g" } };
        _colModified = new GridViewColumn { Header = ColumnTitles[3], Width = 130, DisplayMemberBinding = new Binding(nameof(FolderEntry.Modified)) { StringFormat = "g" } };
        _columnId[_colName] = 0; _columnId[_colLocation] = 1; _columnId[_colCreated] = 2; _columnId[_colModified] = 3;
        _grid.Columns.Add(_colName);
        _grid.Columns.Add(_colLocation);
        _list.View = _grid;
        UpdateHeaders();

        var root = new Grid { Margin = new Thickness(16, 14, 16, 12) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        void Put(UIElement e, int row, int col, int span = 1) { Grid.SetRow(e, row); Grid.SetColumn(e, col); if (span > 1) Grid.SetColumnSpan(e, span); root.Children.Add(e); }
        TextBlock Label(string text, double top = 0) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, top, 14, 0) };

        // Look in
        Put(Label("Look in:"), 0, 0);
        var lookRow = new DockPanel();
        var refresh = new Button { Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14 },
                                   Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Read the folders again (F5)" };
        var browse = new Button { Content = "...", Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Browse for folder" };
        foreach (var b in new[] { refresh, browse })
        {
            b.VerticalAlignment = VerticalAlignment.Center;
            b.SetBinding(WidthProperty, new Binding(nameof(ActualHeight)) { Source = _lookIn });   // square, as high as the box
            b.SetBinding(HeightProperty, new Binding(nameof(ActualHeight)) { Source = _lookIn });
            DockPanel.SetDock(b, Dock.Right);
        }
        lookRow.Children.Add(refresh);
        lookRow.Children.Add(browse);
        lookRow.Children.Add(_lookIn);
        _lookIn.ToolTip = "The folder to look in. Everything below it is searched. Press Enter after typing a folder, or use the three dots. Separate several folders with a semicolon (;).";
        Put(lookRow, 0, 1);

        // Name
        Put(Label("Name:", 10), 1, 0);
        var nameRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(_approx, Dock.Right);
        nameRow.Children.Add(_approx);
        nameRow.Children.Add(_name);
        _name.ToolTip = "Every word must be in the folder's name, in any order and in any case. A word with * or ? must fit the whole name. Use quotes for a phrase. OR (capitals) between words finds either.\n" +
                        "Enter goes to the first folder in the list (or the selected one); Ctrl+Enter opens it in a new tab.";
        Put(nameRow, 1, 1);

        // More options: hidden folders and dates (closed at first)
        var more = new Expander { Header = "More Options", Margin = new Thickness(0, 10, 0, 0) };
        var moreBody = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        moreBody.Children.Add(_hidden);
        moreBody.Children.Add(DateLine("Date Modified:", _modFrom, _modTo, 10));
        moreBody.Children.Add(DateLine("Date Created:", _creFrom, _creTo, 6));
        moreBody.Children.Add(_showDates);
        more.Content = moreBody;
        Put(more, 2, 0, 2);

        // The grid and the status line
        Put(_list, 3, 0, 2);
        // Under the grid: the check box on the left, the status line takes the rest.
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(_keepOpen, Dock.Left);
        bottom.Children.Add(_keepOpen);
        bottom.Children.Add(_status);
        Put(bottom, 4, 0, 2);
        _keepOpen.IsChecked = AppSettingsStore.Current.FindFolderKeepOpen;
        _keepOpen.Click += (_, _) =>
        {
            AppSettingsStore.Current.FindFolderKeepOpen = _keepOpen.IsChecked == true;
            AppSettingsStore.Save(AppSettingsStore.Current);
        };
        Content = root;

        BuildRowMenu();

        // Events
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunQuery(); };
        _name.TextChanged += (_, _) => Requery();
        _approx.Click += (_, _) => Requery();
        _hidden.SelectionChanged += (_, _) => Requery();
        foreach (var d in new[] { _modFrom, _modTo, _creFrom, _creTo }) d.SelectedDateChanged += (_, _) => DatesChanged();
        _showDates.Click += (_, _) => { _datesTouched = true; ApplyDateColumns(); };
        refresh.Click += (_, _) => StartIndex();
        browse.Click += (_, _) => Browse();
        _lookIn.KeyDown += (_, e) => { if (e.Key == Key.Enter) { StartIndex(); e.Handled = true; } };
        _name.PreviewKeyDown += NameKeyDown;
        _list.PreviewKeyDown += ListKeyDown;
        _list.MouseDoubleClick += (_, e) => { if (ItemOf(e.OriginalSource as DependencyObject) != null) Open(FindFolderTarget.ThisTab); };
        _list.PreviewMouseRightButtonDown += (_, e) => { if (ItemOf(e.OriginalSource as DependencyObject) is { } item) item.IsSelected = true; };
        _list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(HeaderClick));
        PreviewKeyDown += WindowKeyDown;
        TextBoxMenu.Attach(_name);
        TextBoxMenu.Attach(_lookIn);
        Closed += (_, _) => StopIndex();
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };

        _lookIn.Text = scope;
        StartIndex();
    }

    /// <summary>Another folder to look in (the window is already open): it reads it at once.</summary>
    public void SetScope(string scope)
    {
        if (string.IsNullOrEmpty(scope)) return;
        _lookIn.Text = scope;
        StartIndex();
    }

    // ── Layout pieces ──────────────────────────────────────────────────────────────────────────────────

    private static DataTemplate NameCell()
    {
        // The Windows folder icon, the same one for every row (no disk is asked), and the name.
        var icon = IconHelper.GetIconForName("folder", true, IconHelper.ListIconSize);
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var image = new FrameworkElementFactory(typeof(Image));
        image.SetValue(Image.SourceProperty, icon);
        image.SetValue(FrameworkElement.WidthProperty, (double)IconHelper.ListIconSize);
        image.SetValue(FrameworkElement.HeightProperty, (double)IconHelper.ListIconSize);
        image.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
        image.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.AppendChild(image);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(FolderEntry.Name)));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }

    private static StackPanel DateLine(string label, DatePicker from, DatePicker to, double top)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, top, 0, 0) };
        p.Children.Add(new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(new TextBlock { Text = "From", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        p.Children.Add(from);
        p.Children.Add(new TextBlock { Text = "To", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) });
        p.Children.Add(to);
        return p;
    }

    private void BuildRowMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open", FontWeight = FontWeights.Bold, ToolTip = "Go to this folder in the tab you are in. The tree follows." };
        open.Click += (_, _) => Open(FindFolderTarget.ThisTab);
        var newTab = new MenuItem { Header = "Open in New Tab" };
        newTab.Click += (_, _) => Open(FindFolderTarget.NewTab);
        var asRoot = new MenuItem { Header = "Set as Tree Root", ToolTip = "Make this folder the top of the folder tree in the tab you are in, and go there. Not remembered." };
        asRoot.Click += (_, _) => Open(FindFolderTarget.TreeRoot);
        var newTabRoot = new MenuItem { Header = "Open in New Tab as Tree Root", ToolTip = "Open this folder in a new tab, with the folder as the top of that tab's folder tree. Not remembered." };
        newTabRoot.Click += (_, _) => Open(FindFolderTarget.NewTabTreeRoot);
        var copy = new MenuItem { Header = "Copy as Path", ToolTip = "Copy the full path of this folder as text, in quotes" };
        copy.Click += (_, _) =>
        {
            if (_list.SelectedItem is FolderEntry f) try { Clipboard.SetText("\"" + f.FullPath + "\""); } catch { /* the clipboard is busy */ }
        };
        menu.Items.Add(open);
        menu.Items.Add(newTab);
        menu.Items.Add(asRoot);
        menu.Items.Add(newTabRoot);
        menu.Items.Add(new Separator());
        menu.Items.Add(copy);
        menu.Opened += (_, _) => { foreach (var i in menu.Items.OfType<MenuItem>()) i.IsEnabled = _list.SelectedItem != null; };
        _list.ContextMenu = menu;
    }

    // ── The index and the list ─────────────────────────────────────────────────────────────────────────

    private void StopIndex()
    {
        _queryCts?.Cancel();
        _debounce.Stop();
        if (_index != null) { _index.Stop(); _index = null; }
    }

    private List<string> ParseRoots(out string problem)
    {
        problem = "";
        var roots = new List<string>();
        foreach (var raw in _lookIn.Text.Split(';'))
        {
            var s = raw.Trim().Trim('"').Trim();
            if (s.Length == 0) continue;
            if (s.Length > 3) s = s.TrimEnd('\\', '/');
            if (Directory.Exists(s)) { if (!roots.Contains(s, StringComparer.OrdinalIgnoreCase)) roots.Add(s); }
            else problem = $"The folder \"{s}\" does not exist.";
        }
        if (roots.Count == 0 && problem.Length == 0) problem = "Choose a folder to look in.";
        return roots;
    }

    /// <summary>(Re)starts reading the folders below the "Look in" folder(s).</summary>
    private void StartIndex()
    {
        StopIndex();
        _version++;
        _list.ItemsSource = null;

        var roots = ParseRoots(out var problem);
        if (roots.Count == 0)
        {
            _status.Text = problem;
            return;
        }

        var settings = AppSettingsStore.Current;
        var excluded = settings.SearchSkipExcluded ? FileSearch.ParseExcluded(settings.SearchExcludedFolders) : new List<string>();

        var index = new FolderIndex();
        _index = index;
        index.Changed += () => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_index, index)) Requery(); });
        index.Start(roots, excluded);
        _status.Text = "Reading folders...";
    }

    private void Requery()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private FolderQuery BuildQuery() => new(_name.Text, _approx.IsChecked == true, (SearchHidden)Math.Max(0, _hidden.SelectedIndex),
                                           _modFrom.SelectedDate, _modTo.SelectedDate, _creFrom.SelectedDate, _creTo.SelectedDate,
                                           _sortColumn, _sortAscending);

    private async void RunQuery()
    {
        var index = _index;
        if (index == null) return;

        _queryCts?.Cancel();
        var cts = _queryCts = new CancellationTokenSource();
        int version = ++_version;
        var all = index.Snapshot();
        var query = BuildQuery();

        FolderQueryResult result;
        try { result = await Task.Run(() => FolderIndex.Query(all, query, RowCap, cts.Token), cts.Token); }
        catch { return; }   // canceled: a newer query is on its way
        if (version != _version || !ReferenceEquals(_index, index)) return;

        // The selected folder stays selected while the list changes under it.
        var keep = (_list.SelectedItem as FolderEntry)?.FullPath;
        _list.ItemsSource = result.Rows;
        if (keep != null)
        {
            var again = result.Rows.FirstOrDefault(r => string.Equals(r.FullPath, keep, StringComparison.OrdinalIgnoreCase));
            if (again != null) { _list.SelectedItem = again; _list.ScrollIntoView(again); }
        }

        string count = result.Total == all.Length ? $"{all.Length:N0} folders" : $"{result.Total:N0} of {all.Length:N0} folders";
        if (result.Total > RowCap) count += $" (the first {RowCap:N0} are shown: type more)";
        if (index.Running) count = "Reading folders... " + count;
        else if (index.Capped) count += $". The reading stopped at {FolderIndex.MaxEntries:N0} folders: look in a smaller folder.";
        _status.Text = count;
    }

    // ── Dates and columns ──────────────────────────────────────────────────────────────────────────────

    private bool AnyDate => _modFrom.SelectedDate != null || _modTo.SelectedDate != null || _creFrom.SelectedDate != null || _creTo.SelectedDate != null;

    /// <summary>The date columns show when a date is used; the check box can override that (once it has been clicked the program leaves it alone).</summary>
    private void DatesChanged()
    {
        if (AnyDate && _showDates.IsChecked != true) { _showDates.IsChecked = true; ApplyDateColumns(); }
        else if (!AnyDate && !_datesTouched && _showDates.IsChecked == true) { _showDates.IsChecked = false; ApplyDateColumns(); }
        Requery();
    }

    private void ApplyDateColumns()
    {
        bool show = _showDates.IsChecked == true;
        if (show && !_grid.Columns.Contains(_colCreated)) { _grid.Columns.Add(_colCreated); _grid.Columns.Add(_colModified); }
        if (!show && _grid.Columns.Contains(_colCreated))
        {
            _grid.Columns.Remove(_colCreated);
            _grid.Columns.Remove(_colModified);
            if (_sortColumn >= 2) { _sortColumn = 0; _sortAscending = true; UpdateHeaders(); Requery(); }
        }
    }

    private void HeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column } || !_columnId.TryGetValue(column, out var id)) return;
        if (_sortColumn == id) _sortAscending = !_sortAscending;
        else { _sortColumn = id; _sortAscending = id < 2; }   // names and places A to Z, dates newest first
        UpdateHeaders();
        Requery();
    }

    /// <summary>The sorted column has the triangle (the same characters as the main list's headers).</summary>
    private void UpdateHeaders()
    {
        foreach (var (column, id) in _columnId)
            column.Header = ColumnTitles[id] + (id == _sortColumn ? (_sortAscending ? " ▲" : " ▼") : "");
    }

    // ── Going to a folder ──────────────────────────────────────────────────────────────────────────────

    private void Open(FindFolderTarget target)
    {
        var item = _list.SelectedItem as FolderEntry ?? _list.Items.OfType<FolderEntry>().FirstOrDefault();
        if (item == null) return;
        if (!Directory.Exists(item.FullPath))
        {
            MessageBox.Show(this, $"The folder \"{item.Name}\" is not there any more. Use the refresh button to read the folders again.",
                            AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _go(item.FullPath, target);
        if (!_keepOpen.IsChecked.GetValueOrDefault()) Close();
    }

    private void Browse()
    {
        var first = _lookIn.Text.Split(';')[0].Trim().Trim('"');
        try { while (!string.IsNullOrEmpty(first) && !Directory.Exists(first)) first = Path.GetDirectoryName(first) ?? ""; }
        catch { first = ""; }
        var dlg = new OpenFolderDialog { Title = "Choose the folder to look in", InitialDirectory = first };
        if (dlg.ShowDialog(this) != true) return;
        _lookIn.Text = dlg.FolderName;
        StartIndex();
    }

    // ── Keys and mouse ─────────────────────────────────────────────────────────────────────────────────

    private void NameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Open((Keyboard.Modifiers & ModifierKeys.Control) != 0 ? FindFolderTarget.NewTab : FindFolderTarget.ThisTab); e.Handled = true; }
        else if (e.Key == Key.Down && _list.Items.Count > 0)
        {
            if (_list.SelectedIndex < 0) _list.SelectedIndex = 0;
            FocusSelected();
            e.Handled = true;
        }
    }

    private void ListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Open((Keyboard.Modifiers & ModifierKeys.Control) != 0 ? FindFolderTarget.NewTab : FindFolderTarget.ThisTab); e.Handled = true; }
        else if (e.Key == Key.Up && _list.SelectedIndex == 0) { _name.Focus(); e.Handled = true; }   // back to the box from the top row
    }

    private void FocusSelected()
    {
        _list.Focus();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_list.SelectedItem != null && _list.ItemContainerGenerator.ContainerFromItem(_list.SelectedItem) is ListViewItem li) { li.Focus(); _list.ScrollIntoView(_list.SelectedItem); }
        });
    }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { StartIndex(); e.Handled = true; return; }
        // Esc closes the window, but not while a calendar is open (Esc closes that).
        if (e.Key == Key.Escape && !(new[] { _modFrom, _modTo, _creFrom, _creTo }.Any(d => d.IsDropDownOpen)))
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>The row (ListViewItem) the mouse is over, null for the header or empty space.</summary>
    private static ListViewItem? ItemOf(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ListViewItem li) return li;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
