using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Reads where the poster of a movie is from its .nfo file (the small XML file Kodi and the scrapers write next to a video),
/// and fetches it. Two layouts are known:
///   &lt;thumb aspect="poster" preview="small address"&gt;address&lt;/thumb&gt;      (the address is the full picture; preview a smaller one)
///   &lt;art&gt;&lt;poster&gt;address&lt;/poster&gt;&lt;/art&gt;
/// and the old plain &lt;thumb&gt;address&lt;/thumb&gt; (no aspect), all directly under the root element only: an actor's &lt;thumb&gt; is a
/// portrait, not a poster. An address is a web address, or a path of a file (full, or from the folder of the .nfo file).
/// Fanart, season posters and other aspects (banner, landscape) are not posters and are not used.
/// The full pictures are tried first, in the order they are written, then the previews.
/// </summary>
public static class NfoPoster
{
    /// <summary>The .nfo files that may belong to a video: &lt;name&gt;.nfo and &lt;name with extension&gt;.nfo (Movie.mkv.nfo).</summary>
    public static IEnumerable<string> NfoFilesOf(string mediaPath)
    {
        // A folder (a TV show, or a movie that has a folder of its own): tvshow.nfo / movie.nfo inside it, the names Kodi and Jellyfin use.
        if (Directory.Exists(mediaPath))
        {
            yield return Path.Combine(mediaPath, "tvshow.nfo");
            yield return Path.Combine(mediaPath, "movie.nfo");
            yield break;
        }

        var folder = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrEmpty(folder)) yield break;

        yield return Path.Combine(folder, Path.GetFileNameWithoutExtension(mediaPath) + ".nfo");
        yield return Path.Combine(folder, Path.GetFileName(mediaPath) + ".nfo");
    }

    /// <summary>The poster addresses of an .nfo file, the full pictures first and then the previews; empty if there are none or it cannot be read.</summary>
    public static List<string> Addresses(string nfoPath)
    {
        var full = new List<string>();
        var previews = new List<string>();
        try
        {
            var text = TextFiles.ReadAllText(nfoPath);
            var doc = TryParse(text);

            // Only the picture of the video itself counts. A &lt;thumb&gt; inside &lt;actor&gt; is the actor's portrait, one inside
            // &lt;fanart&gt; is a backdrop, a season poster belongs to a season: none of them are the poster (found with Jellyfin's
            // .nfo files, where every actor has a &lt;thumb&gt; and the poster is in &lt;art&gt;&lt;poster&gt;).
            // Order: &lt;thumb aspect="poster"&gt;, then &lt;art&gt;&lt;poster&gt;, then an old plain &lt;thumb&gt; with no aspect.
            var postersThumbs = new List<string>(); var postersArt = new List<string>(); var plainThumbs = new List<string>();

            if (doc?.Root != null)
            {
                foreach (var thumb in doc.Root.Elements("thumb"))
                {
                    if (thumb.Attribute("season") != null) continue;   // the poster of a season

                    var aspect = (string?)thumb.Attribute("aspect") ?? "";
                    if (aspect.Length != 0 && !aspect.Equals("poster", StringComparison.OrdinalIgnoreCase)) continue;

                    Add(aspect.Length == 0 ? plainThumbs : postersThumbs, thumb.Value);
                    Add(previews, (string?)thumb.Attribute("preview"));
                }
                foreach (var poster in doc.Root.Elements("art").Elements("poster"))
                    Add(postersArt, poster.Value);
            }
            else
            {
                // Not valid XML (a web address above it, a stray character ...): the same tags found in the text, with what
                // belongs to actors, fanart and seasons cut out first.
                var top = Regex.Replace(text, @"<(actor|fanart|namedseason|set|resume)\b[^>]*>.*?</\1>", "",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                foreach (Match m in Regex.Matches(top, @"<thumb(?<attrs>[^>]*)>(?<value>[^<]*)</thumb>", RegexOptions.IgnoreCase))
                {
                    var attrs = m.Groups["attrs"].Value;
                    if (Regex.IsMatch(attrs, @"\bseason\s*=", RegexOptions.IgnoreCase)) continue;

                    var aspect = Regex.Match(attrs, @"aspect\s*=\s*""(?<a>[^""]*)""", RegexOptions.IgnoreCase).Groups["a"].Value;
                    if (aspect.Length != 0 && !aspect.Equals("poster", StringComparison.OrdinalIgnoreCase)) continue;

                    Add(aspect.Length == 0 ? plainThumbs : postersThumbs, m.Groups["value"].Value);
                    Add(previews, Regex.Match(attrs, @"preview\s*=\s*""(?<p>[^""]*)""", RegexOptions.IgnoreCase).Groups["p"].Value);
                }
                foreach (Match m in Regex.Matches(top, @"<art>.*?<poster>(?<value>[^<]*)</poster>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                    Add(postersArt, m.Groups["value"].Value);
            }

            full.AddRange(postersThumbs);
            full.AddRange(postersArt);
            full.AddRange(plainThumbs);
        }
        catch
        {
            // an .nfo that cannot be read has no poster for us
        }

        return full.Concat(previews).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Add(List<string> list, string? value)
    {
        var v = System.Net.WebUtility.HtmlDecode((value ?? "").Trim());
        if (v.Length > 0) list.Add(v);
    }

    // The XML may have something before it (Kodi allows a web address on a line above): start at the first '<' of a tag.
    private static XDocument? TryParse(string text)
    {
        try
        {
            int start = text.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
            if (start < 0) start = text.IndexOf('<');
            if (start < 0) return null;
            return XDocument.Parse(text[start..]);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The plot of the video from its .nfo file (<c>&lt;plot&gt;</c>, else the short <c>&lt;outline&gt;</c>), for the movie and for a TV
    /// episode alike; null if there is no .nfo or it says none. Line breaks inside the text are kept, other spaces tidied.
    /// </summary>
    public static string? Plot(string mediaPath)
    {
        foreach (var nfo in NfoFilesOf(mediaPath))
        {
            try
            {
                if (!File.Exists(nfo)) continue;

                var info = new FileInfo(nfo);
                if (info.Length is 0 or > 2 * 1024 * 1024) continue;   // an .nfo is small

                var text = TextFiles.ReadAllText(nfo);
                var doc = TryParse(text);

                string? plot = null;
                if (doc != null)
                {
                    plot = doc.Descendants("plot").Select(e => e.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                        ?? doc.Descendants("outline").Select(e => e.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                }
                else
                {
                    // Not valid XML: the same tags found in the text.
                    foreach (var tag in new[] { "plot", "outline" })
                    {
                        var m = Regex.Match(text, $@"<{tag}>(?<v>.*?)</{tag}>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        if (m.Success && !string.IsNullOrWhiteSpace(m.Groups["v"].Value)) { plot = System.Net.WebUtility.HtmlDecode(m.Groups["v"].Value); break; }
                    }
                }

                var tidy = Tidy(plot);
                if (tidy != null) return tidy;
            }
            catch
            {
                // an .nfo that cannot be read has no plot for us
            }
        }
        return null;
    }

    /// <summary>
    /// The title and the year of the video from its .nfo file (<c>&lt;title&gt;</c> directly under the root; <c>&lt;year&gt;</c>, else the first four
    /// digits of <c>&lt;premiered&gt;</c>, <c>&lt;releasedate&gt;</c> or <c>&lt;aired&gt;</c>). Empty strings for what the file does not say or when there is no .nfo.
    /// </summary>
    public static (string Title, string Year) TitleAndYear(string mediaPath)
    {
        foreach (var nfo in NfoFilesOf(mediaPath))
        {
            try
            {
                if (!File.Exists(nfo)) continue;
                var info = new FileInfo(nfo);
                if (info.Length is 0 or > 2 * 1024 * 1024) continue;

                var text = TextFiles.ReadAllText(nfo);
                var doc = TryParse(text);

                string Value(string tag)
                {
                    if (doc?.Root != null)
                        return System.Net.WebUtility.HtmlDecode(doc.Root.Element(tag)?.Value ?? "").Trim();
                    var m = Regex.Match(text, $@"<{tag}>(?<v>.*?)</{tag}>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups["v"].Value).Trim() : "";
                }

                string title = Regex.Replace(Value("title"), @"\s+", " ");
                string year = Regex.Match(Value("year"), @"^\d{4}$").Value;
                if (year.Length == 0)
                    foreach (var tag in new[] { "premiered", "releasedate", "aired" })
                    {
                        var m = Regex.Match(Value(tag), @"^\s*(\d{4})");
                        if (m.Success) { year = m.Groups[1].Value; break; }
                    }

                if (title.Length > 0 || year.Length > 0) return (title, year);
            }
            catch
            {
                // an .nfo that cannot be read says nothing
            }
        }
        return ("", "");
    }

    // Each line trimmed, runs of spaces made one, no empty lines at the ends and no more than one in a row.
    private static string? Tidy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = Regex.Replace(raw.Trim(), @"[ \t]+", " ");
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0)) continue;
            lines.Add(line);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>True for an address on the web (not a file path).</summary>
    public static bool IsWebAddress(string address) =>
        address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file an address names on the disk (full path, or from the folder of the .nfo file), if it exists.</summary>
    public static string? LocalFile(string address, string nfoFolder)
    {
        try
        {
            if (address.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) address = new Uri(address).LocalPath;
            var path = Path.IsPathRooted(address) ? address : Path.Combine(nfoFolder, address);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The extension to save the poster under, from what it really is: .png or .jpg when it is one of those (they are the names
    /// the preview finder looks for); another picture type is saved as a PNG, converted. Null if the bytes are no picture.
    /// </summary>
    public static (string Extension, byte[] Bytes)? ForSaving(byte[] bytes)
    {
        var ext = DroppedImage.ExtensionOf(bytes);
        if (ext is ".png" or ".jpg") return (ext, bytes);

        try
        {
            using var stream = new MemoryStream(bytes);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var png = new MemoryStream();
            encoder.Save(png);
            return (".png", png.ToArray());
        }
        catch
        {
            return null;
        }
    }
}
