using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// "Send to VirusTotal" (2026-11-03). Step 1: the SHA-256 of the file is worked out here and VirusTotal's page for that hash is
/// opened in the browser: nothing is uploaded, no API key is needed, and VirusTotal says there whether it knows the file.
/// </summary>
public static class VirusTotal
{
    private static ImageSource? _icon;

    /// <summary>VirusTotal's own favicon (16 px, embedded as a resource), kept after the first lookup.</summary>
    public static ImageSource? Icon
    {
        get
        {
            if (_icon != null) return _icon;
            try
            {
                var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/SwewolfFileExplorer;component/Resourses/virustotal.ico"),
                                                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                frame.Freeze();
                _icon = frame;
            }
            catch { }
            return _icon;
        }
    }

    /// <summary>The page of VirusTotal for a file with this SHA-256 (lower case hex).</summary>
    public static string PageFor(string sha256) => "https://www.virustotal.com/gui/file/" + sha256;

    /// <summary>The SHA-256 of a file as lower-case hex, read in 1 MB blocks so a big file is never held in memory.</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true);
        using var sha = SHA256.Create();
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
            sha.TransformBlock(buffer, 0, read, null, 0);
        sha.TransformFinalBlock(buffer, 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}
