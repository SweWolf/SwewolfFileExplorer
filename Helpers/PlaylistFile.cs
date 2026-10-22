using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>One line of a playlist: the file it names (a full path) and whether that file is there.</summary>
public sealed record PlaylistEntry(string Path, bool Exists);

/// <summary>
/// M3U / M3U8 playlists, read only. A playlist is shown like a folder: in the tree after the folders, and its files in the grid
/// in the order of the list. Lines that start with # (#EXTM3U, #EXTINF ...) are comments and are skipped; so are empty lines and
/// web addresses (streams). A relative path starts in the folder of the playlist file.
/// </summary>
public static class PlaylistFile
{
    public static bool IsPlaylistName(string? name)
    {
        var ext = Path.GetExtension(name ?? "");
        return ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True if <paramref name="path"/> is a playlist file that is there.</summary>
    public static bool IsPlaylist(string? path) => IsPlaylistName(path) && File.Exists(path);

    /// <summary>The entries of the playlist, in order. Throws if the file cannot be read.</summary>
    public static List<PlaylistEntry> Read(string playlistPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(playlistPath)) ?? "";
        var entries = new List<PlaylistEntry>();

        foreach (var raw in TextFiles.ReadAllText(playlistPath).Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.Contains("://", StringComparison.Ordinal) && !line.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) continue;   // a stream

            if (line.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                line = Uri.UnescapeDataString(line[8..]).Replace('/', '\\');

            try
            {
                var full = Path.GetFullPath(Path.IsPathRooted(line) ? line : Path.Combine(folder, line));
                entries.Add(new PlaylistEntry(full, File.Exists(full)));
            }
            catch (Exception)
            {
                // not a path (illegal characters): the line is skipped
            }
        }

        return entries;
    }
}
