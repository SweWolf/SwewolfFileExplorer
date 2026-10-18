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
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public string TypeName { get; set; } = "";

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

    public void SetTags(AudioTags tags)
    {
        Artist      = tags.Artist;
        Title       = tags.Title;
        TrackNumber = tags.TrackNumber;
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TrackNumber));
        OnPropertyChanged(nameof(TrackText));
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

    public void LoadIconAsync()
    {
        // The icon of the type of file (kept per extension, no disk access) for everything inside an archive and for every file
        // that has no icon of its own: at once. Only a folder, and a file with its own icon (exe, ico, shortcut), is read from
        // the disk, in the background: hundreds of icon reads at once held up a cloud drive for seconds.
        if (IsInArchive || (!IsDirectory && !IconHelper.HasOwnIcon(Name)))
        {
            if (IconHelper.TryGetCachedIconForName(Name, IsDirectory, out var known))
            {
                Icon = known;   // this kind of file was looked up before: no waiting
                return;
            }

            // The first of its kind: one lookup in the background for all files of that kind, then the icon is set.
            var dispatcher = Application.Current?.Dispatcher;
            _ = IconHelper.GetIconForNameAsync(Name, IsDirectory).ContinueWith(
                t => dispatcher?.InvokeAsync(() => Icon = t.Result),
                TaskContinuationOptions.OnlyOnRanToCompletion);
            return;
        }

        Task.Run(() =>
        {
            var icon = IsInArchive ? IconHelper.GetIconForName(Name, IsDirectory) : IconHelper.GetIcon(FullPath);
            Application.Current?.Dispatcher.InvokeAsync(() => Icon = icon);
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
