using System.IO;
using System.Windows.Media.Imaging;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The preview picture of a video or music file: the picture and where it came from, so that a click on it can show it big.
/// <see cref="File"/> is a picture file (opened as it is); <see cref="Bytes"/> is a picture that was inside the file (saved to
/// the temporary folder first); neither is set for a frame Windows made (<see cref="Frame"/>), which is saved the same way.
/// </summary>
public sealed record PreviewPicture(BitmapSource Image, string? File, byte[]? Bytes, bool Frame);

/// <summary>
/// Finds the preview picture of a video or music file, first match wins:
///   1. what the user chose in the library database (a picture, or "none": then there is no preview picture at all),
///   2. a picture built into the file (the cover of a music file; the cover of an mp4 / mkv),
///   3. a picture file in the same folder: &lt;name&gt;-poster.png / .jpg / .jpeg, then &lt;name&gt;.png / .jpg / .jpeg
///      (TeraTool looked for the same names), and the poster named in its .nfo file;
///      for a VIDEO steps 3 comes before step 2 (the picture inside a downloaded video is often an advertisement),
///   4. for a video: the thumbnail Windows makes (<see cref="ShellThumbnail"/>); where Windows has none, a frame made with
///      ffmpeg at 10 % of the length (<see cref="FfmpegFrame"/>, kept in a cache folder, never beside the video).
/// Runs on a background thread; never writes anything and never keeps a file open.
/// </summary>
public static class PreviewPictureFinder
{
    private static readonly string[] SideNames = { "-poster.png", "-poster.jpg", "-poster.jpeg", ".png", ".jpg", ".jpeg" };

    /// <summary>The picture files next to <paramref name="path"/> that count, in the order they are tried.</summary>
    public static IEnumerable<string> SideFiles(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder)) yield break;

        var baseName = Path.GetFileNameWithoutExtension(path);
        foreach (var suffix in SideNames)
            yield return Path.Combine(folder, baseName + suffix);
    }

    private const long PosterMaxBytes = 30 * 1024 * 1024;

    // Addresses that failed in this run (not asked again at every selection: an offline computer would wait for each one),
    // and the .nfo files already logged as a failure.
    private static readonly HashSet<string> FailedAddresses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The poster an .nfo file names (see <see cref="NfoPoster"/>), full picture first, then the preview. A file on the disk is
    /// used as it is. A web address is downloaded and kept beside the video as a hidden file <c>&lt;name&gt;-poster.png/.jpg</c>, which
    /// step 3 then finds by itself next time (the saved file is never overwritten; if it cannot be saved the picture is still
    /// shown, from memory, and downloaded again next time). A failed address is tried once per run and written to the error log.
    /// </summary>
    private static PreviewPicture? FromNfo(string path, int decodeWidth)
    {
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder)) return null;

        foreach (var nfo in NfoPoster.NfoFilesOf(path))
        {
            if (!File.Exists(nfo)) continue;

            foreach (var address in NfoPoster.Addresses(nfo))
            {
                if (!NfoPoster.IsWebAddress(address))
                {
                    if (NfoPoster.LocalFile(address, folder) is { } local && ThumbnailLoader.Load(local, decodeWidth) is { } fromDisk)
                        return new PreviewPicture(fromDisk, local, null, false);
                    continue;
                }

                if (!AppSettingsStore.Current.DownloadPosterFromNfo) continue;
                lock (FailedAddresses) { if (FailedAddresses.Contains(address)) continue; }

                try
                {
                    var download = FileDownloader.GetBytesAsync(address, PosterMaxBytes, timeoutSeconds: 15, what: "poster").GetAwaiter().GetResult();
                    var image = ThumbnailLoader.LoadBytes(download.Bytes, decodeWidth)
                        ?? throw new InvalidOperationException("It is not a picture.");

                    // Kept beside the video as a hidden poster file; not if the folder cannot be written to or the name is taken.
                    if (NfoPoster.ForSaving(download.Bytes) is { } save)
                    {
                        var target = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "-poster" + save.Extension);
                        try
                        {
                            FileDownloader.WriteNew(target, save.Bytes, hidden: true);
                            return new PreviewPicture(image, target, null, false);
                        }
                        catch { /* not saved: shown from memory */ }
                    }
                    return new PreviewPicture(image, null, download.Bytes, false);
                }
                catch (Exception ex)
                {
                    lock (FailedAddresses) FailedAddresses.Add(address);
                    LogFiles.AppendError($"Could not get the poster \"{address}\" named in \"{nfo}\": {ex.Message}");
                }
            }
        }
        return null;
    }

    // A picture file next to the file, else the poster named in the .nfo file beside it (a file on the disk, or downloaded and kept as a
    // hidden poster file).
    private static PreviewPicture? Beside(string path, int decodeWidth)
    {
        foreach (var side in SideFiles(path))
            if (File.Exists(side) && ThumbnailLoader.Load(side, decodeWidth) is { } picture)
                return new PreviewPicture(picture, side, null, false);

        return FromNfo(path, decodeWidth);
    }

    public static PreviewPicture? Find(string path, int decodeWidth)
    {
        bool isVideo = MediaPlayerLauncher.IsVideo(path);

        // 1. The user's own choice.
        var choice = LibraryStore.GetPreviewChoice(path);
        if (choice is { None: true }) return null;
        if (choice is { Picture: { } chosen } && File.Exists(chosen) && ThumbnailLoader.Load(chosen, decodeWidth) is { } picked)
            return new PreviewPicture(picked, chosen, null, false);   // a chosen file that is gone counts as not chosen

        // For a VIDEO a picture or .nfo put beside it comes before the picture built into the file: that one is often an advertisement
        // of the site the file came from (found with Bula (2022).mkv: a "MKVKING.COM" logo inside the mkv hid the poster of its .nfo).
        // For a song the cover inside the file comes first, as before.
        if (isVideo && Beside(path, decodeWidth) is { } wanted) return wanted;

        // 2. A picture inside the file.
        if (AudioTagReader.ReadCover(path) is { } cover && ThumbnailLoader.LoadBytes(cover, decodeWidth) is { } inside)
            return new PreviewPicture(inside, null, cover, false);

        // 3. A picture file next to it, 3b. the poster named in the .nfo file beside it (a song's, here).
        if (!isVideo && Beside(path, decodeWidth) is { } beside) return beside;

        // 4. A frame of the video: the thumbnail Windows makes, else (it has no codec) one made with ffmpeg at 10 % of the length.
        if (isVideo)
        {
            if (ShellThumbnail.Get(path, decodeWidth) is { } frame)
                return new PreviewPicture(frame, null, null, true);
            if (FfmpegFrame.Get(path, decodeWidth) is { } jpeg && ThumbnailLoader.LoadBytes(jpeg, decodeWidth) is { } made)
                return new PreviewPicture(made, null, null, true);
        }

        return null;
    }
}
