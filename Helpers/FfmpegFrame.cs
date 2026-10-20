using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// A still frame of a video made with ffmpeg, for the preview picture when Windows has no thumbnail for the file (see
/// <see cref="PreviewPictureFinder"/>). The frame is taken at <see cref="PositionFraction"/> (10 %) of the length: a fixed time
/// like TeraTool's 10 s would be past the end of a short clip, and 0 s is often black.
///
/// ffmpeg is looked for next to this EXE, then in the PATH (as the sibling apps do); without it this gives nothing. Nothing is
/// written beside the video: the JPEG comes back through the standard output, and is kept in the program's own cache folder
/// (%LocalAppData%\SweWolfSoftware\SwewolfFileExplorer\PreviewCache), named after a hash of the path, size and date of the
/// file, so ffmpeg runs once per file and a changed file gets a new frame.
/// </summary>
public static class FfmpegFrame
{
    /// <summary>Where in the video the frame is taken (a part of its length).</summary>
    public const double PositionFraction = 0.10;

    private const int TimeoutMilliseconds = 15000;
    private const int CacheLimit = 2000;   // files; the oldest quarter is deleted when there are more

    private static readonly SemaphoreSlim Limit = new(2);   // never more than two ffmpeg at a time
    private static string? _ffmpeg;
    private static bool _searched;

    public static string CacheFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SweWolfSoftware", AppInfo.FolderName, "PreviewCache");

    /// <summary>Only for tests: use this ffmpeg instead of looking for one ("" = pretend there is none).</summary>
    public static string? FfmpegOverride { get; set; }

    /// <summary>The path of ffmpeg.exe: next to this EXE, then in the PATH; null if there is none.</summary>
    public static string? Find()
    {
        if (FfmpegOverride != null) return FfmpegOverride.Length == 0 ? null : FfmpegOverride;
        if (_searched) return _ffmpeg;

        _searched = true;
        var next = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(next)) return _ffmpeg = next;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate)) return _ffmpeg = candidate;
            }
            catch { /* a bad entry in the PATH */ }
        }
        return null;
    }

    private static string CacheFile(string path)
    {
        var info = new FileInfo(path);
        var key  = $"{Path.GetFullPath(path).ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(CacheFolder, hash + ".jpg");
    }

    /// <summary>
    /// The JPEG of a frame of the video, about <paramref name="width"/> pixels wide; null if there is no ffmpeg or it gave nothing.
    /// With <paramref name="width"/> 0 the frame has the size of the video, and the cache is neither read nor written (it holds
    /// the small preview frames): that is what Save Preview Picture As uses.
    /// </summary>
    public static byte[]? Get(string path, int width)
    {
        try
        {
            var ffmpeg = Find();
            if (ffmpeg == null || !File.Exists(path)) return null;

            var cache = CacheFile(path);
            if (width > 0 && File.Exists(cache))
            {
                var cached = File.ReadAllBytes(cache);
                if (cached.Length > 0) return cached;
            }

            Limit.Wait();
            byte[]? jpeg;
            try
            {
                var seconds = LengthSeconds(ffmpeg, path) * PositionFraction;
                jpeg = Grab(ffmpeg, path, seconds, width);
                if (jpeg == null && seconds > 0) jpeg = Grab(ffmpeg, path, 0, width);   // a file that cannot seek: its start
            }
            finally
            {
                Limit.Release();
            }

            if (jpeg != null && width > 0) SaveToCache(cache, jpeg);
            return jpeg;
        }
        catch
        {
            return null;
        }
    }

    // ffmpeg prints "Duration: 00:01:23.45" on its error output when it is given only an input (and then stops with an error).
    private static double LengthSeconds(string ffmpeg, string path)
    {
        var text = Run(ffmpeg, new[] { "-hide_banner", "-i", path }, captureError: true, out _);
        var m = Regex.Match(Encoding.UTF8.GetString(text ?? Array.Empty<byte>()), @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!m.Success) return 0;

        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
             + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
             + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    private static byte[]? Grab(string ffmpeg, string path, double seconds, int width)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),   // before -i: jumps instead of decoding up to there
            "-i", path,
            "-frames:v", "1",
        };
        if (width > 0) { args.Add("-vf"); args.Add($"scale={width}:-2"); }
        args.AddRange(new[] { "-f", "image2pipe", "-c:v", "mjpeg", "-q:v", width > 0 ? "3" : "1", "pipe:1" });
        var bytes = Run(ffmpeg, args, captureError: false, out _);
        return bytes is { Length: > 100 } ? bytes : null;
    }

    private static byte[]? Run(string exe, IEnumerable<string> args, bool captureError, out int exitCode)
    {
        exitCode = -1;
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,   // never waits for a keypress
        };
        foreach (var a in args) start.ArgumentList.Add(a);

        using var process = Process.Start(start);
        if (process == null) return null;
        process.StandardInput.Close();

        using var output = new MemoryStream();
        using var error  = new MemoryStream();
        var readOut = process.StandardOutput.BaseStream.CopyToAsync(output);
        var readErr = process.StandardError.BaseStream.CopyToAsync(error);

        if (!process.WaitForExit(TimeoutMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        Task.WaitAll(new[] { readOut, readErr }, 2000);

        exitCode = process.ExitCode;
        return (captureError ? error : output).ToArray();
    }

    private static void SaveToCache(string file, byte[] jpeg)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            var temp = file + ".tmp" + Environment.ProcessId;
            File.WriteAllBytes(temp, jpeg);
            File.Move(temp, file, overwrite: true);

            // Not for ever: when there are too many, the oldest quarter goes.
            var files = new DirectoryInfo(CacheFolder).GetFiles("*.jpg");
            if (files.Length > CacheLimit)
                foreach (var old in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length / 4))
                    try { old.Delete(); } catch { }
        }
        catch
        {
            // no cache is not an error: the next time ffmpeg runs again
        }
    }
}
