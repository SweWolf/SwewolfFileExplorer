// CdDiscInfo.Explorer.cs: the part of CdDiscInfo that only the File Explorer needs (its list columns and details are AudioTags).
// The shared part (CdDiscInfo.cs) is identical in SweWolf Media Player; this file is not copied there.

namespace SwewolfFileExplorer.Helpers;

public static partial class CdDiscInfo
{
    private const int BitrateKbps = 1411;   // CD audio: 44.1 kHz, 16 bit, stereo

    /// <summary>
    /// Artist, title, track number, length and bitrate of a CD track for the list's columns and the details. The title and artist are empty
    /// until the disc is known (the lookup is started in the background and <see cref="Updated"/> says when it is done).
    /// </summary>
    public static AudioTags? ReadTags(string path)
    {
        if (!IsCdTrack(path)) return null;

        var info = InfoOf(path);   // also starts the lookup of a new disc, or the daily refresh of an old one
        return new AudioTags(info?.Artist?.Trim() ?? "", info?.Title?.Trim() ?? "", (uint)TrackNumber(path), TrackLength(path), BitrateKbps);
    }
}
