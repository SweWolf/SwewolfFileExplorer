using System.IO;
using System.Windows;
using System.Windows.Media;
using SwewolfFileExplorer.ViewModels;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Gives the tiles of the Icons view their pictures. The panel says which items are on screen (<see cref="Wanted"/> / <see cref="Unwanted"/>);
/// nothing else is ever read, so a folder with thousands of files only costs what is visible.
///
/// How it works:
///   • The item gets its icon at once (the icon of its kind, kept per extension), so a tile is never empty.
///   • The real thumbnail is made on a few background threads. The work is a stack: the tile asked for last (what the user
///     scrolled to) is served first, and an item that went off screen before its turn is skipped.
///   • Pictures are decoded by <see cref="ThumbnailLoader"/> (EXIF turn, PSD, SVG, RAW, JPEG XL ...); music files show their cover;
///     videos and documents show the thumbnail Windows itself makes (<see cref="ShellThumbnail"/>, which Windows keeps in its own
///     thumbnail cache on the disk), videos Windows has no codec for a frame made with ffmpeg (kept in the PreviewCache folder).
///   • Finished pictures are kept in a memory cache (the most recent ones), so scrolling back or changing the size inside one step is instant.
/// </summary>
public static class ThumbnailService
{
    private const int WorkerCount = 4;
    private const int MemoryCacheEntries = 1500;

    private static readonly object Gate = new();
    private static readonly Stack<FileItemViewModel> Pending = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static bool _started;

    // The memory cache: most recently used at the front of the list.
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, ImageSource>>> Cache = new();
    private static readonly LinkedList<KeyValuePair<string, ImageSource>> CacheOrder = new();

    /// <summary>The pixel size the thumbnails are made at: the tile size times the screen's scale, rounded up to a multiple of 64 (so a small change of the size needs no new pictures).</summary>
    public static int PixelSize { get; private set; } = 128;

    /// <summary>Works out the pixel size for a tile size in device-independent pixels. True if it changed.</summary>
    public static bool SetTileSize(double tileSize, double dpiScale)
    {
        int pixels = (int)Math.Ceiling(tileSize * Math.Max(1, dpiScale) / 64.0) * 64;
        pixels = Math.Max(64, Math.Min(256, pixels));
        if (pixels == PixelSize) return false;
        PixelSize = pixels;
        return true;
    }

    private static string KeyOf(FileItemViewModel item, int pixels) =>
        $"{item.FullPath}|{item.DateModified.Ticks}|{item.Size}|{pixels}";

    /// <summary>The item's tile is on screen (UI thread).</summary>
    public static void Wanted(FileItemViewModel item)
    {
        item.ThumbWanted = true;
        int pixels = PixelSize;
        if (item.ThumbDoneAt == pixels) return;   // asked for at this size already (the panel calls this at every layout)
        item.ThumbDoneAt = pixels;

        if (TryCache(KeyOf(item, pixels), out var cached))
        {
            item.Thumb = cached;
            return;
        }

        lock (Gate)
        {
            Pending.Push(item);
            if (!_started) Start();
        }
        Signal.Release();
    }

    /// <summary>The item's tile left the screen (UI thread): its picture is let go (the memory cache may keep it).</summary>
    public static void Unwanted(FileItemViewModel item)
    {
        item.ThumbWanted = false;
        item.Thumb = null;
        item.ThumbDoneAt = 0;
    }

    private static void Start()
    {
        _started = true;
        for (int i = 0; i < WorkerCount; i++)
        {
            new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Thumbnails " + i }.Start();
        }
    }

    private static void Work()
    {
        while (true)
        {
            Signal.Wait();

            FileItemViewModel? item = null;
            lock (Gate)
            {
                while (Pending.Count > 0)
                {
                    var candidate = Pending.Pop();   // the newest request first: that is what is on screen now
                    if (candidate.ThumbWanted) { item = candidate; break; }
                }
            }
            if (item == null) continue;

            try { Make(item); }
            catch { /* a file that cannot be read keeps its icon */ }
        }
    }

    private static void Make(FileItemViewModel item)
    {
        int pixels = PixelSize;
        string path = item.FullPath, name = item.Name;
        bool isDir = item.IsDirectory, inArchive = item.IsInArchive;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        // 1. The icon, at once (also what a file without a thumbnail keeps).
        if (item.Thumb == null)
        {
            ImageSource? icon = inArchive || (!isDir && !IconHelper.HasOwnIcon(name))
                ? IconHelper.GetIconForName(name, isDir, pixels)
                : IconHelper.GetIcon(path, true, pixels);
            if (icon != null)
                dispatcher.InvokeAsync(() => { if (item.ThumbWanted && item.Thumb == null) item.Thumb = icon; });
        }

        if (isDir || !item.ThumbWanted) return;

        // 2. The real thumbnail, where there is one.
        var thumb = Create(path, name, inArchive, pixels, item);
        if (thumb == null) return;

        string key = $"{path}|{item.DateModified.Ticks}|{item.Size}|{pixels}";
        AddToCache(key, thumb);
        dispatcher.InvokeAsync(() =>
        {
            if (!item.ThumbWanted || PixelSize != pixels) return;
            item.Thumb = thumb;
        });
    }

    private static ImageSource? Create(string path, string name, bool inArchive, int pixels, FileItemViewModel item)
    {
        if (ThumbnailLoader.IsImage(path)) return ThumbnailLoader.Load(path, pixels);
        if (inArchive) return null;   // a file in a packed file: its icon

        // Video and music files: exactly the picture the info panel shows (the user's choice, the picture inside the file, a poster
        // file beside it, the poster of its .nfo, then a frame from Windows or ffmpeg): one logic for both places.
        if (MediaPlayerLauncher.IsMedia(path))
            return PreviewPictureFinder.Find(path, pixels)?.Image;

        // Documents ...: what Windows shows in its own thumbnails (cached by Windows on the disk).
        if (ShellThumbnail.Get(path, pixels) is { } fromWindows) return fromWindows;

        if (MediaPlayerLauncher.IsVideo(path) && FfmpegFrame.Get(path, pixels) is { } jpeg)
            return ThumbnailLoader.LoadBytes(jpeg, pixels);
        return null;
    }

    private static bool TryCache(string key, out ImageSource? image)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var node))
            {
                CacheOrder.Remove(node);
                CacheOrder.AddFirst(node);
                image = node.Value.Value;
                return true;
            }
        }
        image = null;
        return false;
    }

    private static void AddToCache(string key, ImageSource image)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var old)) CacheOrder.Remove(old);
            Cache[key] = CacheOrder.AddFirst(new KeyValuePair<string, ImageSource>(key, image));
            while (CacheOrder.Count > MemoryCacheEntries)
            {
                Cache.Remove(CacheOrder.Last!.Value.Key);
                CacheOrder.RemoveLast();
            }
        }
    }
}
