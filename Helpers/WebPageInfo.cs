using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace SwewolfFileExplorer.Helpers;

/// <summary>What a web page calls itself: its title and its small icon (favicon), for a bookmark of a web address.</summary>
public sealed record WebPageInfo(string? Title, byte[]? IconIco);

/// <summary>
/// Reads the title and the favicon of a web page (the only reason this program goes to a page the user pasted: it is asked for by
/// "paste a web address in the Bookmarks folder"). Everything is under one time limit and a cancel token; whatever was found when
/// time is up is returned, never an error. The icon is returned as the bytes of an .ico file (a PNG icon is wrapped in one, as
/// Windows reads that), because a .url shortcut can only point to an .ico.
/// </summary>
public static class WebPageInfoReader
{
    private const long MaxPageBytes = 2_000_000;
    private const long MaxIconBytes = 400_000;

    public static async Task<WebPageInfo> ReadAsync(string url, int timeoutSeconds, CancellationToken cancel)
    {
        using var all = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        all.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var token = all.Token;

        string? title = null;
        var iconUrls = new List<string>();

        try
        {
            var page = await FileDownloader.GetBytesAsync(url, MaxPageBytes, timeoutSeconds, "web page", token);
            var html = Decode(page.Bytes);
            title = FindTitle(html);
            iconUrls.AddRange(FindIconUrls(html, url));
        }
        catch { /* no page: the icon may still be found at the usual place */ }

        if (!token.IsCancellationRequested)
        {
            try
            {
                var fallback = new Uri(new Uri(url), "/favicon.ico").AbsoluteUri;
                if (!iconUrls.Contains(fallback, StringComparer.OrdinalIgnoreCase)) iconUrls.Add(fallback);
            }
            catch { }
        }

        byte[]? ico = null;
        foreach (var iconUrl in iconUrls.Take(4))
        {
            if (token.IsCancellationRequested) break;
            try
            {
                var download = await FileDownloader.GetBytesAsync(iconUrl, MaxIconBytes, timeoutSeconds, "icon", token);
                ico = ToIco(download.Bytes);
                if (ico != null) break;
            }
            catch { /* the next one */ }
        }

        return new WebPageInfo(title, ico);
    }

    // ── The page ──────────────────────────────────────────────────────────────

    /// <summary>The page's text: a byte order mark, a charset named in the first bytes, else UTF-8 if it is valid, else Windows-1252.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var m = Regex.Match(head, @"charset\s*=\s*[""']?\s*([\w\-]+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            try { return Encoding.GetEncoding(m.Groups[1].Value).GetString(bytes); } catch { /* an encoding this computer does not have */ }
        }

        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch { return Encoding.Latin1.GetString(bytes); }
    }

    /// <summary>The page's title, tidied (entities decoded, spaces collapsed, at most 150 characters); null if there is none.</summary>
    public static string? FindTitle(string html)
    {
        var m = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;

        var title = Regex.Replace(WebUtility.HtmlDecode(m.Groups[1].Value), @"\s+", " ").Trim();
        if (title.Length == 0) return null;
        return title.Length > 150 ? title[..150].TrimEnd() : title;
    }

    /// <summary>The icons the page names (<c>&lt;link rel="icon" href="..."&gt;</c> and the touch icons), best first, as full web addresses.</summary>
    public static List<string> FindIconUrls(string html, string pageUrl)
    {
        var found = new List<(int Rank, string Url)>();
        Uri baseUri;
        try { baseUri = new Uri(pageUrl); } catch { return new(); }

        foreach (Match tag in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var rel  = Attribute(tag.Value, "rel")?.ToLowerInvariant();
            var href = Attribute(tag.Value, "href");
            if (rel == null || href == null || !rel.Contains("icon") || rel.Contains("mask-icon")) continue;

            try
            {
                var full = new Uri(baseUri, WebUtility.HtmlDecode(href));
                if (full.Scheme != Uri.UriSchemeHttp && full.Scheme != Uri.UriSchemeHttps) continue;   // data: addresses are not fetched
                int rank = rel.Contains("apple") ? 2 : 1;                                              // the plain icon first
                found.Add((rank, full.AbsoluteUri));
            }
            catch { }
        }

        return found.OrderBy(f => f.Rank).Select(f => f.Url).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? Attribute(string tag, string name)
    {
        var m = Regex.Match(tag, name + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : null;
    }

    // ── The icon ──────────────────────────────────────────────────────────────

    /// <summary>The bytes of an .ico file for a downloaded icon (an .ico as it is; a PNG wrapped in one); null for anything else.</summary>
    public static byte[]? ToIco(byte[] bytes)
    {
        if (bytes.Length < 16) return null;

        // An .ico file: 00 00 01 00 and at least one picture.
        if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0 && (bytes[4] | bytes[5] << 8) > 0) return bytes;

        // A PNG: made at most 128 px (a touch icon is 180 px) and put in an .ico, which Windows reads since Vista.
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            try
            {
                var decoder = new PngBitmapDecoder(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapSource frame = decoder.Frames[0];
                // Always a 32-bit picture with transparency in the .ico: a palette or gray PNG inside an .ico is refused by WPF's icon
                // decoder (found with dn.se's favicon).
                frame = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                if (frame.PixelWidth > 128 || frame.PixelHeight > 128)
                {
                    double scale = 128.0 / Math.Max(frame.PixelWidth, frame.PixelHeight);
                    frame = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
                }

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(frame));
                using var png = new MemoryStream();
                encoder.Save(png);

                using var ico = new MemoryStream();
                using var w = new BinaryWriter(ico);
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);                  // header: one picture
                w.Write((byte)(frame.PixelWidth  >= 256 ? 0 : frame.PixelWidth));
                w.Write((byte)(frame.PixelHeight >= 256 ? 0 : frame.PixelHeight));
                w.Write((byte)0); w.Write((byte)0);                                          // palette, reserved
                w.Write((ushort)1); w.Write((ushort)32);                                     // planes, bits
                w.Write((uint)png.Length); w.Write((uint)22);                                // size, offset
                w.Write(png.ToArray());
                return ico.ToArray();
            }
            catch { return null; }
        }

        return null;   // a gif, jpg, svg ...: not used
    }
}
