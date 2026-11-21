using System.IO;
using System.Text;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// What a disc image (.iso) holds, found from its folder layout. Only a video DVD (a VIDEO_TS folder in the root) is recognized for now:
/// SweWolf Media Player plays such an image (its own dvdnav reader). A Blu-ray image (BDMV) is not handed on: the player has no reader for it.
/// </summary>
public static class DiscImage
{
    // History:
    //   2026-11-20  First version: IsDvdVideo (ISO 9660 root directory first, 7-Zip's listing as the fallback for a UDF-only image).

    public static bool IsIsoName(string name) => Path.GetExtension(name).Equals(".iso", StringComparison.OrdinalIgnoreCase);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> VideoDiscCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A video DVD image: for the places that treat it as a video (the plot, the preview picture, the details, the files that belong to it).
    /// The answer is kept per file (path, size and date), so the image is only looked into once.
    /// </summary>
    public static bool IsVideoDisc(string path)
    {
        if (!IsIsoName(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            return VideoDiscCache.GetOrAdd($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}", _ => IsDvdVideo(path));
        }
        catch { return false; }
    }

    /// <summary>True when the image has a VIDEO_TS folder in its root. Never throws; an unreadable file is "no".</summary>
    public static bool IsDvdVideo(string path)
    {
        try
        {
            switch (RootHas(path, "VIDEO_TS"))
            {
                case true: return true;
                case false: return false;
            }
            // No ISO 9660 part (a UDF-only image): ask 7-Zip, if it is installed. Unknown = not a DVD for us.
            if (SevenZipRunner.Find() == null) return false;
            foreach (var entry in SevenZipRunner.List(path))
            {
                var name = entry.FullName.Replace('/', '\\');
                if (name.StartsWith("VIDEO_TS\\", StringComparison.OrdinalIgnoreCase) || name.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Reads the ISO 9660 root directory: true / false for "has this name", null when the image has no ISO 9660 part.</summary>
    private static bool? RootHas(string path, string name)
    {
        const int Sector = 2048;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var volume = new byte[Sector];
        // The volume descriptors start at sector 16; the primary one (type 1, "CD001") holds the root directory record at offset 156.
        for (int sector = 16; sector < 32; sector++)
        {
            stream.Position = (long)sector * Sector;
            if (stream.Read(volume, 0, Sector) < Sector) return null;
            if (volume[0] == 255) return null;   // end of the descriptors
            if (volume[0] != 1 || Encoding.ASCII.GetString(volume, 1, 5) != "CD001") continue;

            int extent = BitConverter.ToInt32(volume, 156 + 2);
            int length = BitConverter.ToInt32(volume, 156 + 10);
            if (extent <= 0 || length <= 0 || length > 4 * 1024 * 1024) return null;

            var directory = new byte[length];
            stream.Position = (long)extent * Sector;
            if (stream.Read(directory, 0, length) < length) return null;

            int position = 0;
            while (position < length)
            {
                int record = directory[position];
                if (record == 0) { position = (position / Sector + 1) * Sector; continue; }   // records never cross a sector
                if (position + record > length) break;
                int nameLength = directory[position + 32];
                if (position + 33 + nameLength <= length)
                {
                    var entry = Encoding.ASCII.GetString(directory, position + 33, nameLength);
                    int version = entry.IndexOf(';');
                    if (version >= 0) entry = entry[..version];
                    if (entry.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
                }
                position += record;
            }
            return false;
        }
        return null;
    }
}
