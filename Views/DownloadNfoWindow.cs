using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.Views;

/// <summary>
/// "Download NFO File": asks for the name, the year and the type (Movie / TV Show / Not Sure) to look up on TMDB. It starts with
/// what the file name and its folders say (<see cref="Tmdb.Guess"/>). Built in code; OK / Cancel.
/// </summary>
public sealed class DownloadNfoWindow : Window
{
    private readonly TextBox _name = new();
    private readonly TextBox _year = new();
    private readonly RadioButton _movie = new() { Content = "Movie", GroupName = "kind", Margin = new Thickness(0, 0, 18, 0) };
    private readonly RadioButton _tv = new() { Content = "TV Show", GroupName = "kind", Margin = new Thickness(0, 0, 18, 0) };
    private readonly RadioButton _unknown = new() { Content = "Not Sure (Search Both)", GroupName = "kind" };

    private readonly TextBox _id = new();
    private readonly CheckBox _includeAdult = new() { Content = "Include Adult", ToolTip = "Also search adult titles (TMDB leaves them out otherwise)" };

    public bool IncludeAdult => _includeAdult.IsChecked == true;

    /// <summary>The TMDB number the user typed (it overrides the name and year), or null.</summary>
    public int? Id { get; private set; }

    /// <summary>The IMDb ID (tt0137523) the user typed, or null; it also overrides the name and year.</summary>
    public string? ImdbId { get; private set; }
    public string ShowName => _name.Text.Trim();
    public string Year => _year.Text.Trim();
    public Tmdb.Kind Kind => _tv.IsChecked == true ? Tmdb.Kind.Tv : _unknown.IsChecked == true ? Tmdb.Kind.Unknown : Tmdb.Kind.Movie;

    public DownloadNfoWindow(string mediaPath)
    {
        Title = "Download NFO File";
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        UseLayoutRounding = true;

        var (name, year, kind) = Tmdb.Guess(mediaPath);
        _name.Text = name; _year.Text = year;
        (kind == Tmdb.Kind.Tv ? _tv : _movie).IsChecked = true;

        // Radio buttons in a group need contiguous tab stops: the three radios come together, the fields before them.
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = $"Look up \"{Path.GetFileName(mediaPath)}\" on The Movie Database.", TextWrapping = TextWrapping.Wrap });

        panel.Children.Add(new TextBlock { Text = "Name:", Margin = new Thickness(0, 10, 0, 3) });
        panel.Children.Add(_name);
        panel.Children.Add(new TextBlock { Text = "Year (optional):", Margin = new Thickness(0, 10, 0, 3) });
        _year.Width = 90; _year.HorizontalAlignment = HorizontalAlignment.Left; _year.MaxLength = 4;
        panel.Children.Add(_year);
        _includeAdult.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_includeAdult);

        panel.Children.Add(new TextBlock { Text = "Type:", Margin = new Thickness(0, 10, 0, 3) });
        var kinds = new StackPanel { Orientation = Orientation.Horizontal };
        kinds.Children.Add(_movie); kinds.Children.Add(_tv); kinds.Children.Add(_unknown);
        panel.Children.Add(kinds);

        // Right after the radios (the type decides which numbering the ID belongs to). Name and year are greyed out while an ID is typed.
        panel.Children.Add(new TextBlock { Text = "TMDB ID or IMDb ID (optional):", Margin = new Thickness(0, 10, 0, 3) });
        _id.Width = 220; _id.HorizontalAlignment = HorizontalAlignment.Left; _id.MaxLength = 80;
        _id.TextChanged += (_, _) => { bool byId = _id.Text.Trim().Length > 0; _name.IsEnabled = !byId; _year.IsEnabled = !byId; _includeAdult.IsEnabled = !byId; };
        panel.Children.Add(_id);
        panel.Children.Add(new TextBlock
        {
            Text = "Instead of the name, you can enter the number from the address of the page on themoviedb.org (themoviedb.org/movie/550 or themoviedb.org/tv/1396), "
                 + "or an IMDb ID (like tt0137523). It is used as it is, and the name and year are then ignored. "
                 + "TMDB's movies and TV shows have separate numbers, so the type above matters for a TMDB number: Movie and Not Sure mean a movie. An IMDb ID finds either one.",
            Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "The file is saved as a hidden .nfo file with the same name, next to the video.",
            Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75,
        });

        var ok = new Button { Content = "OK", Width = 115, Margin = new Thickness(0, 0, 8, 0), IsDefault = true, BorderThickness = new Thickness(2) };
        ok.SetResourceReference(Control.BorderBrushProperty, SystemColors.AccentColorBrushKey);
        ok.Click += (_, _) =>
        {
            if (_id.Text.Trim().Length > 0)
            {
                ImdbId = Tmdb.ParseImdbId(_id.Text);
                if (ImdbId == null && (!int.TryParse(_id.Text.Trim(), out int id) || id <= 0))
                { MessageBox.Show(this, "A TMDB ID is a whole number, like 550, and an IMDb ID looks like tt0137523. Leave the box empty to search by name.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information); _id.Focus(); return; }
                if (ImdbId == null) Id = int.Parse(_id.Text.Trim());
                DialogResult = true;
                return;
            }
            if (ShowName.Length == 0) { MessageBox.Show(this, "Enter the name to look up, or a TMDB ID.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information); _name.Focus(); return; }
            if (Year.Length > 0 && (Year.Length != 4 || !Year.All(char.IsAsciiDigit)))
            { MessageBox.Show(this, "The year must have four digits, or be empty.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information); _year.Focus(); return; }
            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", Width = 115, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }
}

/// <summary>Which of several TMDB hits is the right one: a list with title, year, type and the start of the description.</summary>
public sealed class PickTmdbResultWindow : Window
{
    private readonly ListBox _list = new() { MinHeight = 200 };
    private readonly List<Tmdb.Result> _results;

    public Tmdb.Result? Chosen => _list.SelectedIndex >= 0 ? _results[_list.SelectedIndex] : null;

    public PickTmdbResultWindow(List<Tmdb.Result> results, string? heading = null)
    {
        _results = results;
        Title = "Download NFO File";
        Width = 680; Height = 440; MinWidth = 420; MinHeight = 280; ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        UseLayoutRounding = true;

        foreach (var r in results)
        {
            var overview = r.Overview.Length > 140 ? r.Overview[..140].TrimEnd() + "..." : r.Overview;
            var item = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
            item.Children.Add(new TextBlock
            {
                Text = $"{r.Title}{(r.Year != null ? $" ({r.Year})" : "")}  -  {(r.IsTv ? "TV Show" : "Movie")}",
                FontWeight = FontWeights.SemiBold,
            });
            if (overview.Length > 0) item.Children.Add(new TextBlock { Text = overview, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12 });
            _list.Items.Add(item);
        }
        _list.SelectedIndex = 0;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.MouseDoubleClick += (_, e) => { if (_list.SelectedIndex >= 0 && e.OriginalSource is not System.Windows.Controls.Primitives.ScrollBar) DialogResult = true; };

        var ok = new Button { Content = "OK", Width = 115, Margin = new Thickness(0, 0, 8, 0), IsDefault = true, BorderThickness = new Thickness(2) };
        ok.SetResourceReference(Control.BorderBrushProperty, SystemColors.AccentColorBrushKey);
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", Width = 115, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);

        var grid = new Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var headingText = new TextBlock { Text = heading ?? "Which one is it?", Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(_list, 1); Grid.SetRow(buttons, 2);
        grid.Children.Add(headingText); grid.Children.Add(_list); grid.Children.Add(buttons);
        Content = grid;
        Loaded += (_, _) => (_list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
    }
}
