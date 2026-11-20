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

    // The two ways to use the window: one title (the name, year and ID below), or everything in the folder that has no .nfo file yet.
    private readonly RadioButton _single = new() { GroupName = "scope", Margin = new Thickness(0, 0, 0, 4) };
    private readonly RadioButton _all = new() { GroupName = "scope", Margin = new Thickness(0, 0, 0, 4) };
    private readonly StackPanel _nameYear = new();
    private readonly StackPanel _idPanel = new();
    private readonly TextBlock _note = new() { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
    private readonly bool _isFolder;

    public bool IncludeAdult => _includeAdult.IsChecked == true;

    /// <summary>True when the user chose to look up everything in the folder that has no .nfo file yet.</summary>
    public bool BatchAll => _all.IsChecked == true;

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
        _isFolder = Directory.Exists(mediaPath);

        var (name, year, kind) = Tmdb.Guess(mediaPath);
        _name.Text = name; _year.Text = year;
        (kind == Tmdb.Kind.Tv ? _tv : _movie).IsChecked = true;

        // Radio buttons in a group need contiguous tab stops: each group's radios come together, the fields around them.
        var panel = new StackPanel { Margin = new Thickness(14) };

        _single.Content = $"Look Up \"{Path.GetFileName(mediaPath)}\" on The Movie Database";
        _single.IsChecked = true;
        panel.Children.Add(_single);
        panel.Children.Add(_all);   // its text depends on the type below (UpdateBatchText)

        _nameYear.Children.Add(new TextBlock { Text = "Name:", Margin = new Thickness(0, 10, 0, 3) });
        _nameYear.Children.Add(_name);
        _nameYear.Children.Add(new TextBlock { Text = "Year (optional):", Margin = new Thickness(0, 10, 0, 3) });
        _year.Width = 90; _year.HorizontalAlignment = HorizontalAlignment.Left; _year.MaxLength = 4;
        _nameYear.Children.Add(_year);
        panel.Children.Add(_nameYear);

        _includeAdult.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_includeAdult);

        panel.Children.Add(new TextBlock { Text = "Type:", Margin = new Thickness(0, 10, 0, 3) });
        var kinds = new StackPanel { Orientation = Orientation.Horizontal };
        kinds.Children.Add(_movie); kinds.Children.Add(_tv); kinds.Children.Add(_unknown);
        panel.Children.Add(kinds);

        // Right after the radios (the type decides which numbering the ID belongs to). Name and year are greyed out while an ID is typed.
        _idPanel.Children.Add(new TextBlock { Text = "TMDB ID or IMDb ID (optional):", Margin = new Thickness(0, 10, 0, 3) });
        _id.Width = 220; _id.HorizontalAlignment = HorizontalAlignment.Left; _id.MaxLength = 80;
        _id.TextChanged += (_, _) => { bool byId = _id.Text.Trim().Length > 0; _name.IsEnabled = !byId; _year.IsEnabled = !byId; _includeAdult.IsEnabled = !byId; };
        _idPanel.Children.Add(_id);
        _idPanel.Children.Add(new TextBlock
        {
            Text = "Instead of the name, you can enter the number from the address of the page on themoviedb.org (themoviedb.org/movie/550 or themoviedb.org/tv/1396), "
                 + "or an IMDb ID (like tt0137523). It is used as it is, and the name and year are then ignored. "
                 + "TMDB's movies and TV shows have separate numbers, so the type above matters for a TMDB number: Movie and Not Sure mean a movie. An IMDb ID finds either one.",
            Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75,
        });
        panel.Children.Add(_idPanel);
        panel.Children.Add(_note);

        // Looking up everything: no name, year or ID (they are for one title); the type still decides what is searched for.
        void UpdateScope()
        {
            var one = _single.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            _nameYear.Visibility = one;
            _idPanel.Visibility = one;
            UpdateBatchText();
        }
        _single.Checked += (_, _) => UpdateScope();
        _all.Checked += (_, _) => UpdateScope();
        foreach (var radio in new[] { _movie, _tv, _unknown }) radio.Checked += (_, _) => UpdateBatchText();
        UpdateScope();

        var ok = new Button { Content = "OK", Width = 115, Margin = new Thickness(0, 0, 8, 0), IsDefault = true, BorderThickness = new Thickness(2) };
        ok.SetResourceReference(Control.BorderBrushProperty, SystemColors.AccentColorBrushKey);
        ok.Click += (_, _) =>
        {
            if (BatchAll) { DialogResult = true; return; }

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
        Loaded += (_, _) => { if (_single.IsChecked == true) { _name.Focus(); _name.SelectAll(); } };
    }

    // The second radio button and the note under the form say what the type will find in this folder.
    private void UpdateBatchText()
    {
        bool tv = _tv.IsChecked == true, movie = _movie.IsChecked == true;
        string what = !_isFolder ? "Video Files"
                    : tv ? "Folders"
                    : movie ? "Video Files"
                    : "Video Files and Folders";
        _all.Content = $"Look Up All {what} in This Folder That Have No .nfo File Yet";

        if (_all.IsChecked != true)
        {
            _note.Text = _isFolder
                ? "The details are saved as a hidden tvshow.nfo in this folder (movie.nfo for a movie), and the poster as a hidden poster.jpg, which the folder picture shows."
                : "The file is saved as a hidden .nfo file with the same name, next to the video.";
            return;
        }

        _note.Text = "Only a certain answer is saved: exactly one hit on TMDB must have the same title (and the same year, when the name has one). "
                   + "Everything else is skipped, and listed afterwards. Subfolders are not searched.";
    }
}

/// <summary>Which of several TMDB hits is the right one: a list with poster, title, year, type, original title and language, and the start of the description.</summary>
public sealed class PickTmdbResultWindow : Window
{
    private readonly ListBox _list = new() { MinHeight = 200 };
    private readonly List<Tmdb.Result> _results;
    private readonly double? _fileMinutes;
    private readonly List<Image> _posters = new();
    private readonly List<TextBlock> _lengths = new();
    private readonly CancellationTokenSource _cancel = new();
    private bool _programmatic;

    // A hit whose length is within this many minutes of the file's own length counts as the same movie.
    private const double LengthTolerance = 3;

    public Tmdb.Result? Chosen => _list.SelectedIndex >= 0 ? _results[_list.SelectedIndex] : null;

    /// <param name="fileMinutes">The length of the video file in minutes, when it is a file: the lengths of the movies are then fetched and compared with it.</param>
    public PickTmdbResultWindow(List<Tmdb.Result> results, string? heading = null, double? fileMinutes = null)
    {
        _results = results;
        _fileMinutes = fileMinutes;
        Title = "Download NFO File";
        Width = 700; Height = 520; MinWidth = 440; MinHeight = 300; ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        UseLayoutRounding = true;

        foreach (var r in results)
        {
            var overview = r.Overview.Length > 140 ? r.Overview[..140].TrimEnd() + "..." : r.Overview;

            var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            text.Children.Add(new TextBlock
            {
                Text = $"{r.Title}{(r.Year != null ? $" ({r.Year})" : "")}  -  {(r.IsTv ? "TV Show" : "Movie")}",
                FontWeight = FontWeights.SemiBold,
            });

            // The title it has in its own country, and its language: only when that tells something the title above does not.
            bool english = string.Equals(r.Language, "English", StringComparison.OrdinalIgnoreCase);
            string? original = r.OriginalTitle != null && !string.Equals(r.OriginalTitle, r.Title, StringComparison.OrdinalIgnoreCase)
                ? $"Original title: {r.OriginalTitle}{(r.Language != null ? $" ({r.Language})" : "")}"
                : r.Language != null && !english ? $"Language: {r.Language}" : null;
            if (original != null) text.Children.Add(new TextBlock { Text = original, FontSize = 12, Opacity = 0.85 });

            var length = new TextBlock { FontSize = 12, Visibility = Visibility.Collapsed };
            _lengths.Add(length);
            text.Children.Add(length);

            if (overview.Length > 0) text.Children.Add(new TextBlock { Text = overview, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12 });

            var poster = new Image { Width = 46, Height = 69, Stretch = System.Windows.Media.Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
            _posters.Add(poster);

            var row = new Grid { Margin = new Thickness(2, 4, 2, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(text, 1);
            row.Children.Add(poster);
            row.Children.Add(text);
            _list.Items.Add(row);
        }
        _list.SelectedIndex = 0;
        _list.SelectionChanged += (_, _) => { if (!_programmatic) _userChose = true; };
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

        Loaded += async (_, _) =>
        {
            (_list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            await LoadDetailsAsync();
        };
        Closed += (_, _) => _cancel.Cancel();
    }

    private bool _userChose;

    // The small posters and, for a movie file, the lengths: one hit after the other, so the first rows fill in first. The window never waits for it.
    private async Task LoadDetailsAsync()
    {
        var token = _cancel.Token;
        var close = new List<int>();
        try
        {
            for (int i = 0; i < _results.Count && i < 15; i++)
            {
                var hit = _results[i];

                var bytes = await Tmdb.SmallPosterAsync(hit, token);
                if (bytes != null)
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = new MemoryStream(bytes);
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _posters[i].Source = bitmap;
                }

                if (_fileMinutes is { } mine && !hit.IsTv && await Tmdb.RuntimeAsync(hit, token) is { } minutes)
                {
                    bool same = Math.Abs(minutes - mine) <= LengthTolerance;
                    _lengths[i].Text = $"Length: {minutes} min (your file: {Math.Round(mine)} min)" + (same ? "  -  the same length as your file" : "");
                    _lengths[i].FontWeight = same ? FontWeights.SemiBold : FontWeights.Normal;
                    _lengths[i].Opacity = same ? 1 : 0.85;
                    _lengths[i].Visibility = Visibility.Visible;
                    if (same) close.Add(i);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { return; }

        // Exactly one hit has the length of the file: it is the likely one, so the selection goes to it (unless the user already chose).
        if (close.Count == 1 && !_userChose && _list.SelectedIndex != close[0])
        {
            _programmatic = true;
            _list.SelectedIndex = close[0];
            _list.ScrollIntoView(_list.Items[close[0]]);
            _programmatic = false;
        }
    }
}
