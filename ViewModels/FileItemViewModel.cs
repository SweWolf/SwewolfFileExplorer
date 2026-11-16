using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using SwewolfFileExplorer.Helpers;

namespace SwewolfFileExplorer.ViewModels;

public class FileItemViewModel : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public string Name { get; set; } = "";

    /// <summary>What the list shows: the name, without the extension of a special shell file type (.lnk ...) when the setting hides it.</summary>
    public string DisplayName => IsDirectory ? Name : ShellExtensions.DisplayName(Name);

    /// <summary>The setting that hides extensions changed: the name shown is worked out again.</summary>
    public void RefreshDisplayName() => OnPropertyChanged(nameof(DisplayName));
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    private string _typeName = "";
    public string TypeName { get => _typeName; set { _typeName = value; OnPropertyChanged(); } }

    // These can change while the item is on show (the Properties window edits a file): the cells follow.
    private DateTime _dateModified, _dateCreated;
    private long _size;

    public DateTime DateModified { get => _dateModified; set { _dateModified = value; OnPropertyChanged(); } }
    public DateTime DateCreated  { get => _dateCreated;  set { _dateCreated  = value; OnPropertyChanged(); } }
    public long Size             { get => _size;         set { _size         = value; OnPropertyChanged(); } }   // -1 for directories

    /// <summary>Reads the size and dates of a file again. False if the file is not there any more.</summary>
    public bool ReloadFileInfo()
    {
        try
        {
            var file = new System.IO.FileInfo(FullPath);
            if (!file.Exists) return false;

            Size         = file.Length;
            DateModified = file.LastWriteTime;
            DateCreated  = file.CreationTime;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // The tags of a music file (the Artist, Title and Track Number columns). Empty until they are read.
    public string Artist { get; private set; } = "";
    public string Title { get; private set; } = "";
    public uint TrackNumber { get; private set; }

    /// <summary>The track number for the list: nothing when the file has none.</summary>
    public string TrackText => TrackNumber == 0 ? "" : TrackNumber.ToString();

    /// <summary>The tags of this file were asked for already (they are read once, in the background).</summary>
    public bool TagsRequested { get; set; }

    // What is known about the file itself (the Length, Bitrate and Dimensions columns). Zero until read, or when the file has none.
    public TimeSpan Duration { get; private set; }
    public int BitrateKbps { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    // Of a program or library: from its version information (the Product name and Product version columns).
    public string ProductName { get; private set; } = "";
    public string ProductVersion { get; private set; } = "";

    /// <summary>"3:45", or "1:02:03" from an hour up.</summary>
    public string LengthText => Duration < TimeSpan.FromSeconds(0.5) ? ""
        : Duration.TotalHours >= 1 ? $"{(int)Duration.TotalHours}:{Duration.Minutes:00}:{Duration.Seconds:00}"
        : $"{Duration.Minutes}:{Duration.Seconds:00}";

    public string BitrateText => BitrateKbps > 0 ? $"{BitrateKbps} kbps" : "";

    /// <summary>When a picture was taken (the EXIF date inside the file); null when it has none or is not read yet.</summary>
    public DateTime? DateTaken { get; private set; }

    public string DateTakenText => DateTaken is { } taken ? Converters.DateTimeConverter.Format(taken) : "";

    public string DimensionsText => Width > 0 && Height > 0 ? $"{Width} x {Height}" : "";

    /// <summary>For sorting by dimensions: the number of pixels.</summary>
    public long Pixels => (long)Width * Height;

    public void SetTags(AudioTags tags)
    {
        Artist      = tags.Artist;
        Title       = tags.Title;
        TrackNumber = tags.TrackNumber;
        Duration    = tags.Duration;
        BitrateKbps = tags.BitrateKbps;
        Width       = tags.Width;
        Height      = tags.Height;
        ProductName    = tags.ProductName;
        ProductVersion = tags.ProductVersion;
        DateTaken      = tags.DateTaken;
        OnPropertyChanged(nameof(DateTakenText));
        OnPropertyChanged(nameof(ProductName));
        OnPropertyChanged(nameof(ProductVersion));
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TrackNumber));
        OnPropertyChanged(nameof(TrackText));
        OnPropertyChanged(nameof(LengthText));
        OnPropertyChanged(nameof(BitrateText));
        OnPropertyChanged(nameof(DimensionsText));
    }

    // The user's own rating and expectation (1-10; kept in the library). None until set; the columns show them.
    public int? Rating { get; private set; }
    public double? Expectation { get; private set; }

    public string RatingText      => RatingRules.Format(Rating);
    public string ExpectationText => RatingRules.Format(Expectation);

    public void SetMarks(int? rating, double? expectation)
    {
        if (Rating != rating)
        {
            Rating = rating;
            OnPropertyChanged(nameof(Rating));
            OnPropertyChanged(nameof(RatingText));
        }
        if (Expectation != expectation)
        {
            Expectation = expectation;
            OnPropertyChanged(nameof(Expectation));
            OnPropertyChanged(nameof(ExpectationText));
        }
    }

    /// <summary>The #EXTINF line that came before this entry in the playlist file (kept for saving it again); null if none.</summary>
    public string? PlaylistInfo { get; set; }

    /// <summary>A line of a playlist whose file is not there: shown dimmed, never played.</summary>
    public bool IsMissing { get; set; }

    /// <summary>A hidden or system item, shown (dimmed) only when the user asked for the hidden items.</summary>
    public bool IsHidden { get; set; }

    /// <summary>
    /// A file that was never opened in the app (the list draws it bold and/or italic). False until the library has answered,
    /// and for folders and packed files, which have a state of their own.
    /// </summary>
    public bool IsUnread { get; private set; }

    public void SetUnread(bool value)
    {
        if (IsUnread == value) return;
        IsUnread = value;
        OnPropertyChanged(nameof(IsUnread));
    }

    /// <summary>The item is inside an archive: it is not on the disk, so its icon and type come from its name.</summary>
    public bool IsInArchive { get; set; }

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); }
    }

    // ── The Icons view ────────────────────────────────────────────────────────

    private ImageSource? _thumb;

    /// <summary>The picture of the item in the Icons view: its icon first, then the thumbnail (see <see cref="ThumbnailService"/>).</summary>
    public ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; OnPropertyChanged(); }
    }

    /// <summary>The item is on screen in the Icons view (set and cleared by the panel as it makes and drops the item's tile).</summary>
    public volatile bool ThumbWanted;

    /// <summary>The pixel size the thumbnail was asked for at (0 = not asked): the panel asks at every layout, this stops it asking twice.</summary>
    public int ThumbDoneAt { get; set; }

    public void LoadIconAsync()
    {
        // The icon of the type of file (kept per extension, no disk access) for everything inside an archive and for every file
        // that has no icon of its own: at once. Only a folder, and a file with its own icon (exe, ico, shortcut), is read from
        // the disk, in the background: hundreds of icon reads at once held up a cloud drive for seconds.
        int size = IconHelper.ListIconSize;   // follows the list's font size
        if (IsInArchive || (!IsDirectory && !IconHelper.HasOwnIcon(Name)))
        {
            if (IconHelper.TryGetCachedIconForName(Name, IsDirectory, out var known, size))
            {
                Icon = known;   // this kind of file was looked up before: no waiting
                return;
            }

            // The first of its kind: one lookup in the background for all files of that kind, then the icon is set.
            var dispatcher = Application.Current?.Dispatcher;
            _ = IconHelper.GetIconForNameAsync(Name, IsDirectory, size).ContinueWith(
                t => dispatcher?.InvokeAsync(() => Icon = t.Result),
                TaskContinuationOptions.OnlyOnRanToCompletion);
            return;
        }

        Task.Run(() =>
        {
            var icon = IsInArchive ? IconHelper.GetIconForName(Name, IsDirectory, size) : IconHelper.GetIcon(FullPath, true, size);
            Application.Current?.Dispatcher.InvokeAsync(() => Icon = icon);
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
