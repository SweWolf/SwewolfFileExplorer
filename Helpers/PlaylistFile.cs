using System.IO;
using System.Text;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// One line of a playlist: the file it names (a full path), whether that file is there, and the <c>#EXTINF</c> line that came just before
/// it (kept so that saving the playlist does not lose it; null if there was none).
/// </summary>
public sealed record PlaylistEntry(string Path, bool Exists, string? ExtInf = null);

/// <summary>
/// M3U / M3U8 playlists. A playlist is shown like a folder: in the tree after the folders, and its files in the grid in the order of the
/// list. Lines that start with # (#EXTM3U, #EXTINF ...) are comments and are skipped (an #EXTINF line is kept with the entry after it);
/// so are empty lines and web addresses (streams). A relative path starts in the folder of the playlist file.
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
        string? extInf = null;

        foreach (var raw in TextFiles.ReadAllText(playlistPath).Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;
            if (line[0] == '#')
            {
                if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase)) extInf = line;
                continue;
            }

            var info = extInf;
            extInf = null;
            if (line.Contains("://", StringComparison.Ordinal) && !line.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) continue;   // a stream

            if (line.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                line = Uri.UnescapeDataString(line[8..]).Replace('/', '\\');

            try
            {
                var full = Path.GetFullPath(Path.IsPathRooted(line) ? line : Path.Combine(folder, line));
                entries.Add(new PlaylistEntry(full, File.Exists(full), info));
            }
            catch (Exception)
            {
                // not a path (illegal characters): the line is skipped
            }
        }

        return entries;
    }

    /// <summary>
    /// Writes the playlist (UTF-8, CRLF, "#EXTM3U" first). A file in the folder of the playlist (or below it) is written with a path from
    /// that folder, so the playlist and its files can be moved together; any other file with its full path. Written to a temporary file
    /// first and swapped in, so a failure leaves the old playlist as it was. Throws if it cannot be written.
    /// </summary>
    public static void Write(string playlistPath, IEnumerable<PlaylistEntry> entries)
    {
        var full   = Path.GetFullPath(playlistPath);
        var folder = (Path.GetDirectoryName(full) ?? "").TrimEnd('\\') + "\\";

        var text = new StringBuilder("#EXTM3U\r\n");
        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.ExtInf)) text.Append(entry.ExtInf).Append("\r\n");
            text.Append(entry.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? entry.Path[folder.Length..] : entry.Path).Append("\r\n");
        }

        var temp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".swewolf-part";
        try
        {
            File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
            if (File.Exists(full)) File.Replace(temp, full, null);
            else File.Move(temp, full);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* left behind: harmless */ }
        }
    }
}
