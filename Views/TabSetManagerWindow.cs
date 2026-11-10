using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>What the user chose in the manager that the main window has to do after the manager has closed.</summary>
public enum ManagerAction { None, Open, Shortcut, ShowFolder }

/// <summary>
/// Workspace Manager / Session Manager (built in code): every saved workspace or session in one list (the name, with "(in use)" after
/// the one in use; with "Advanced" checked also the number of tabs and when it was last changed), with Open, Rename, Delete, Create
/// Shortcut and Move to Volume for the selected row. "Advanced" (remembered separately for workspaces and sessions) also shows Open
/// Folder, lower left. Rename, Delete and Move are done inside the window (through the callbacks, which hold the rules); Open, Create
/// Shortcut and Open Folder close it and the main window does them (<see cref="Action"/>, <see cref="SelectedName"/>), so their own
/// dialogs are not hidden behind this one.
/// </summary>
public sealed class TabSetManagerWindow : Window
{
    // Name is the real name (the file name); Display is what the list shows: the session in use has " (in use)" after its name.
    private sealed record Row(string Name, string Display, string Tabs, string Modified, bool InUse);

    private const double ButtonWidth = 180;   // every button of the window, Close too

    private readonly StoreKind _kind;
    private readonly Func<string?> _inUse;
    private readonly Func<Window, string, string?> _rename;
    private readonly Func<Window, string, bool> _delete;
    private readonly Func<Window, string, bool> _move;

    private readonly ListView _list = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock _empty = new() { Opacity = 0.7, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Button _open = Make("Open");
    private readonly Button _renameButton = Make("Rename...");
    private readonly Button _deleteButton = Make("Delete");
    private readonly Button _shortcut = Make("Create Shortcut...");
    private readonly Button _moveButton = Make("Move to Volume...");
    private readonly Button _folder = Make("Open Folder");
    private readonly CheckBox _advanced = new() { Content = "Advanced", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    private readonly GridViewColumn _nameColumn = new() { Header = "Name", Width = 400 };
    private readonly GridViewColumn _tabsColumn = new() { Header = "Tabs", Width = 70 };
    private readonly GridViewColumn _changedColumn = new() { Header = "Last Changed", Width = 160 };

    /// <summary>What to do after the window closed.</summary>
    public ManagerAction Action { get; private set; } = ManagerAction.None;

    /// <summary>The name the action is about (null for none).</summary>
    public string? SelectedName { get; private set; }

    public TabSetManagerWindow(StoreKind kind, Func<string?> inUse, Func<Window, string, string?> rename, Func<Window, string, bool> delete,
                                Func<Window, string, bool> move)
    {
        _kind = kind;
        _inUse = inUse;
        _rename = rename;
        _delete = delete;
        _move = move;

        string word = kind == StoreKind.Workspace ? "Workspace" : "Session";
        Title = $"{word} Manager";
        FontSize = 14;
        Width = 900;
        Height = 460;
        MinWidth = 760;
        MinHeight = 320;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var intro = new TextBlock
        {
            Text = kind == StoreKind.Workspace
                ? "Workspaces are sets of folders you saved by hand. Select one and choose what to do."
                : "Sessions are the open tabs, remembered all the time. Select one and choose what to do.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetColumnSpan(intro, 2);
        grid.Children.Add(intro);

        // Name only, until "Advanced" is checked (then Tabs and Last Changed too); see ApplyAdvanced.
        _nameColumn.DisplayMemberBinding    = new System.Windows.Data.Binding(nameof(Row.Display));
        _tabsColumn.DisplayMemberBinding    = new System.Windows.Data.Binding(nameof(Row.Tabs));
        _changedColumn.DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Modified));
        var view = new GridView();
        view.Columns.Add(_nameColumn);
        _list.View = view;

        var listHost = new Grid();
        listHost.Children.Add(_list);
        listHost.Children.Add(_empty);
        Grid.SetRow(listHost, 1);
        grid.Children.Add(listHost);

        var side = new StackPanel { Margin = new Thickness(12, 0, 0, 0), Width = ButtonWidth };
        foreach (var b in new[] { _open, _renameButton, _deleteButton, _shortcut, _moveButton })
        {
            b.Margin = new Thickness(0, 0, 0, 8);
            side.Children.Add(b);
        }
        Grid.SetRow(side, 1);
        Grid.SetColumn(side, 1);
        grid.Children.Add(side);

        // Lower left: the Advanced checkbox and, to the right of it (shown only when it is checked), Open Folder: it is about the
        // folder of the files, not about the row that is selected, so it does not sit with the buttons on the right.
        var bottomLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 14, 0, 0) };
        bottomLeft.Children.Add(_advanced);
        bottomLeft.Children.Add(_folder);
        Grid.SetRow(bottomLeft, 2);
        grid.Children.Add(bottomLeft);

        var close = new Button { Content = "Close", Width = ButtonWidth, Padding = new Thickness(12, 6, 12, 6), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 14, 0, 0) };
        Grid.SetRow(close, 2);
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);
        Content = grid;

        _folder.ToolTip = "Open the folder that holds the saved files in a new tab";
        _advanced.ToolTip = "Show more columns and the Open Folder button";
        var settings = AppSettingsStore.Current;
        _advanced.IsChecked = kind == StoreKind.Workspace ? settings.WorkspaceManagerAdvanced : settings.SessionManagerAdvanced;
        ApplyAdvanced();
        _advanced.Click += (_, _) =>
        {
            ApplyAdvanced();
            if (_kind == StoreKind.Workspace) settings.WorkspaceManagerAdvanced = _advanced.IsChecked == true;
            else settings.SessionManagerAdvanced = _advanced.IsChecked == true;
            AppSettingsStore.Save(settings);   // remembered from one time to the next, separately for workspaces and sessions
        };

        _empty.Text = kind == StoreKind.Workspace
            ? "No workspaces saved yet. Use Workspace > Save Current Tabs as Workspace."
            : "No sessions yet. Use Session > Start New Session.";

        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += (_, e) => { if (e.OriginalSource is FrameworkElement { DataContext: Row }) Finish(ManagerAction.Open); };
        _open.Click += (_, _) => Finish(ManagerAction.Open);
        _shortcut.Click += (_, _) => Finish(ManagerAction.Shortcut);
        _folder.Click += (_, _) => { SelectedName ??= ""; Action = ManagerAction.ShowFolder; DialogResult = true; };
        _renameButton.Click += (_, _) => DoRename();
        _deleteButton.Click += (_, _) => DoDelete();
        _moveButton.ToolTip = "Move the file to a folder on any drive. Paths on that drive are then kept without the drive letter, so the drive can have another letter on another computer.";
        _moveButton.Click += (_, _) => DoMove();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F2 && Selected != null) { DoRename(); e.Handled = true; }
            else if (e.Key == Key.Delete && Selected != null) { DoDelete(); e.Handled = true; }
            else if (e.Key == Key.Enter && Selected != null && !(Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase)) { Finish(ManagerAction.Open); e.Handled = true; }
        };
        Loaded += (_, _) => { Reload(null); _list.Focus(); };
    }

    // The Fluent button does not stretch in a StackPanel by itself, so the width is set: every button is the same width.
    private static Button Make(string text) => new() { Content = text, Width = ButtonWidth, Padding = new Thickness(12, 6, 12, 6), HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };

    /// <summary>Advanced unchecked: only the Name column and no Open Folder button. Checked: Tabs and Last Changed too, and Open Folder.</summary>
    private void ApplyAdvanced()
    {
        bool advanced = _advanced.IsChecked == true;
        var columns = ((GridView)_list.View).Columns;

        columns.Remove(_tabsColumn);
        columns.Remove(_changedColumn);
        if (advanced)
        {
            columns.Add(_tabsColumn);
            columns.Add(_changedColumn);
        }
        _nameColumn.Width = advanced ? 400 : 560;
        _folder.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
    }

    private string? Selected => (_list.SelectedItem as Row)?.Name;

    private void UpdateButtons()
    {
        bool has = Selected != null;
        _open.IsEnabled = _renameButton.IsEnabled = _deleteButton.IsEnabled = _shortcut.IsEnabled = _moveButton.IsEnabled = has;
        _folder.IsEnabled = true;
    }

    private void Finish(ManagerAction action)
    {
        if (Selected == null) return;
        SelectedName = Selected;
        Action = action;
        DialogResult = true;
    }

    private void DoRename()
    {
        var name = Selected;
        if (name == null) return;
        var newName = _rename(this, name);
        if (newName != null) Reload(newName);
    }

    private void DoMove()
    {
        var name = Selected;
        if (name == null) return;
        int index = _list.SelectedIndex;
        if (!_move(this, name)) return;   // the file is no longer in the program's own folder: it leaves the list
        Reload(null);
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(index, _list.Items.Count - 1);
    }

    private void DoDelete()
    {
        var name = Selected;
        if (name == null) return;
        int index = _list.SelectedIndex;
        if (!_delete(this, name)) return;
        Reload(null);
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(index, _list.Items.Count - 1);
    }

    /// <summary>Reads the folder again; <paramref name="select"/> is the name to select afterwards (the first row if null).</summary>
    private void Reload(string? select)
    {
        var inUse = _inUse();
        var rows = new List<Row>();
        foreach (var name in WorkspaceStore.Names(_kind))
        {
            var file = WorkspaceStore.PathFor(_kind, name);
            string tabs = "", modified = "";
            try { modified = File.GetLastWriteTime(file).ToString("g", System.Globalization.CultureInfo.CurrentCulture); } catch { }
            var data = WorkspaceStore.Read(file, out _, out _);
            if (data != null) tabs = data.Tabs.Count.ToString();
            bool current = string.Equals(name, inUse, StringComparison.OrdinalIgnoreCase);
            rows.Add(new Row(name, current ? $"{name} (in use)" : name, tabs, modified, current));
        }

        _list.ItemsSource = rows;
        _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var pick = rows.FirstOrDefault(r => select != null && string.Equals(r.Name, select, StringComparison.OrdinalIgnoreCase))
                   ?? rows.FirstOrDefault(r => r.InUse)
                   ?? rows.FirstOrDefault();
        if (pick != null) _list.SelectedItem = pick;
        UpdateButtons();
    }
}
