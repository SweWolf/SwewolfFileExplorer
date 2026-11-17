using System.Collections;
using System.Runtime.InteropServices;
using SwewolfFileExplorer.ViewModels;

namespace SwewolfFileExplorer.Helpers;

/// <summary>The column the list is sorted by. None: not sorted, the items stay in the order they were read (the order inside a zip).</summary>
public enum SortColumn { Name, DateModified, DateCreated, Type, Size, Artist, Title, TrackNumber, Length, Bitrate, Dimensions, ProductName, ProductVersion, Comment, Rating, Expectation, DateTaken, Location, None }

/// <summary>
/// Sorts the file list like Windows Explorer: folders always stay above files,
/// names compare "naturally" (file2 before file10), and ties fall back to the name.
/// </summary>
public class FileItemComparer : IComparer
{
    private readonly SortColumn _column;
    private readonly bool _ascending;

    public FileItemComparer(SortColumn column, bool ascending)
    {
        _column    = column;
        _ascending = ascending;
    }

    public int Compare(object? x, object? y)
    {
        if (x is not FileItemViewModel a || y is not FileItemViewModel b) return 0;

        if (a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;

        int result = _column switch
        {
            SortColumn.DateModified => a.DateModified.CompareTo(b.DateModified),
            SortColumn.DateCreated  => a.DateCreated.CompareTo(b.DateCreated),
            SortColumn.DateTaken    => (a.DateTaken ?? DateTime.MinValue).CompareTo(b.DateTaken ?? DateTime.MinValue),   // no date counts as the oldest
            SortColumn.Type       => StrCmpLogicalW(a.TypeName, b.TypeName),
            SortColumn.Size         => a.IsDirectory ? 0 : a.Size.CompareTo(b.Size),
            SortColumn.Artist       => StrCmpLogicalW(a.Artist, b.Artist),
            SortColumn.Title        => StrCmpLogicalW(a.Title, b.Title),
            SortColumn.TrackNumber  => a.TrackNumber.CompareTo(b.TrackNumber),
            SortColumn.Length       => a.Duration.CompareTo(b.Duration),
            SortColumn.Bitrate      => a.BitrateKbps.CompareTo(b.BitrateKbps),
            SortColumn.Dimensions   => a.Pixels.CompareTo(b.Pixels),   // by the number of pixels
            SortColumn.Location       => StrCmpLogicalW(a.Location, b.Location),
            SortColumn.Comment        =>StrCmpLogicalW(a.Comment, b.Comment),
            SortColumn.ProductName    => StrCmpLogicalW(a.ProductName, b.ProductName),
            SortColumn.ProductVersion => StrCmpLogicalW(a.ProductVersion, b.ProductVersion),   // 1.10 after 1.9
            // By number (10 is more than 9); no value counts as zero.
            SortColumn.Rating       => (a.Rating ?? 0).CompareTo(b.Rating ?? 0),
            SortColumn.Expectation  => (a.Expectation ?? 0).CompareTo(b.Expectation ?? 0),
            _                       => StrCmpLogicalW(a.Name, b.Name)
        };

        if (!_ascending) result = -result;

        // Ties are always broken by name, ascending, so the order is stable.
        return result != 0 ? result : StrCmpLogicalW(a.Name, b.Name);
    }

    /// <summary>Compares two names the way the list does: "file2" before "file10".</summary>
    public static int NaturalCompare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);
}
