using System.Collections;
using System.Runtime.InteropServices;
using MyFileExplorer.ViewModels;

namespace MyFileExplorer.Helpers;

public enum SortColumn { Name, DateModified, DateCreated, Type, Size }

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
            SortColumn.Type       => StrCmpLogicalW(a.TypeName, b.TypeName),
            SortColumn.Size         => a.IsDirectory ? 0 : a.Size.CompareTo(b.Size),
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
