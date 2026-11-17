using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// "View NFO File as a Web Page": the details in a Kodi / Jellyfin <c>.nfo</c> file (movie, TV show, episode, music video, album ...)
/// written as one small, self-contained HTML page in the temp folder and opened in the default browser. The page is only a view:
/// the .nfo file is never changed. Every address in it (trailer, IMDb / TMDB ids, poster, any text that is a web address) is a real link.
/// A file that is not XML (a release's ASCII art, a web address on a line of its own) is shown as plain text with its addresses made links.
///
/// History: 2026-11-16 first version.
/// </summary>
public static class NfoPage
{
    /// <summary>Makes the page for <paramref name="nfoPath"/> and opens it in the default browser. Returns an error text, or null when it worked.</summary>
    public static string? Show(string nfoPath)
    {
        try
        {
            var page = Create(nfoPath);
            Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Writes the page to <c>%TEMP%\SwewolfFileExplorer\NfoPages</c> and returns its path (older pages, over a day old, are removed).</summary>
    public static string Create(string nfoPath)
    {
        var folder = Path.Combine(Path.GetTempPath(), "SwewolfFileExplorer", "NfoPages");
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var old in Directory.EnumerateFiles(folder, "*.html"))
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) File.Delete(old);
        }
        catch { /* a page that is open in a browser, or no access: not important */ }

        var hash = (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(nfoPath);
        var safe = string.Concat(Path.GetFileNameWithoutExtension(nfoPath).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (safe.Length > 60) safe = safe[..60];
        var page = Path.Combine(folder, $"{safe}-{hash:x8}.html");
        File.WriteAllText(page, BuildHtml(nfoPath), new UTF8Encoding(true));
        return page;
    }

    // ── The page ─────────────────────────────────────────────────────────────

    public static string BuildHtml(string nfoPath)
    {
        var text = TextFiles.ReadAllText(nfoPath);
        var nfoFolder = Path.GetDirectoryName(nfoPath) ?? "";
        var fileName = Path.GetFileName(nfoPath);

        var doc = TryParse(text, out var linesAbove);
        var body = new StringBuilder();
        string title = Path.GetFileNameWithoutExtension(nfoPath);

        if (doc?.Root == null)
        {
            body.Append("<h1>").Append(Enc(title)).Append("</h1>");
            body.Append("<p class=\"note\">This file is not an XML .nfo file, so it is shown as it is.</p>");
            body.Append("<pre>").Append(Linkify(text)).Append("</pre>");
        }
        else
        {
            var root = doc.Root;
            var kind = root.Name.LocalName.ToLowerInvariant();
            title = First(root, "title") ?? First(root, "showtitle") ?? First(root, "name") ?? title;
            body.Append(Header(root, kind, title, nfoFolder));
            body.Append(Details(root, kind));
            body.Append(PlotSection(root));
            body.Append(LinksSection(root, kind, linesAbove));
            body.Append(CastSection(root, nfoFolder));
            body.Append(FileInfoSection(root));
            body.Append(OtherSection(root));
        }

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>{Enc(title)}</title>
<style>{Css}</style>
</head>
<body>
<main>
{body}
<footer>Made by SweWolf File Explorer from <code>{Enc(fileName)}</code>. This page is only a view; the .nfo file is not changed.</footer>
</main>
</body>
</html>
";
    }

    private const string Css = @"
:root { --bg:#fff; --fg:#1d2330; --muted:#667085; --card:#f4f6fa; --line:#dfe3ea; --accent:#0b62c4; }
@media (prefers-color-scheme: dark) { :root { --bg:#14171c; --fg:#e6e9ef; --muted:#9aa3b2; --card:#1d2229; --line:#2c333d; --accent:#6db3ff; } }
* { box-sizing: border-box; }
body { margin:0; background:var(--bg); color:var(--fg); font:16px/1.55 'Segoe UI', system-ui, sans-serif; }
main { max-width: 980px; margin: 0 auto; padding: 28px 20px 40px; }
a { color: var(--accent); }
h1 { margin:0 0 4px; font-size: 2rem; line-height:1.2; }
h2 { font-size:1.05rem; text-transform:uppercase; letter-spacing:.06em; color:var(--muted); border-bottom:1px solid var(--line); padding-bottom:4px; margin:28px 0 10px; }
.sub { color:var(--muted); margin:0 0 14px; }
.top { display:flex; gap:24px; align-items:flex-start; }
.top img.poster { width:440px; max-width:45%; border-radius:8px; border:1px solid var(--line); }
.chips { display:flex; flex-wrap:wrap; gap:6px; margin:8px 0; }
.chip { background:var(--card); border:1px solid var(--line); border-radius:999px; padding:1px 11px; font-size:.9rem; }
.tagline { font-style:italic; color:var(--muted); margin:6px 0 10px; }
table { border-collapse:collapse; width:100%; }
th, td { text-align:left; vertical-align:top; padding:5px 12px 5px 0; border-bottom:1px solid var(--line); }
th { color:var(--muted); font-weight:600; white-space:nowrap; width:1%; }
.cast { display:grid; grid-template-columns:repeat(auto-fill, minmax(120px,1fr)); gap:14px; }
.cast .p { text-align:center; font-size:.9rem; }
.cast img { width:100%; aspect-ratio:2/3; object-fit:cover; border-radius:6px; border:1px solid var(--line); background:var(--card); }
.cast .ph { width:100%; aspect-ratio:2/3; border-radius:6px; background:var(--card); border:1px solid var(--line); }
.cast .role { color:var(--muted); }
img.fanart { width:100%; border-radius:8px; border:1px solid var(--line); margin-top:8px; }
pre { background:var(--card); border:1px solid var(--line); border-radius:8px; padding:14px; overflow:auto; white-space:pre-wrap; font:13px/1.35 Consolas, monospace; }
.note { color:var(--muted); }
ul.plain { margin:0; padding-left:1.1em; }
footer { margin-top:36px; color:var(--muted); font-size:.85rem; border-top:1px solid var(--line); padding-top:10px; }
@media (max-width:640px) { .top { flex-direction:column; } .top img.poster { width:60%; max-width:none; } }
";

    // ── Sections ─────────────────────────────────────────────────────────────

    private static readonly string[] Handled =
    {
        "title","originaltitle","sorttitle","showtitle","year","premiered","releasedate","aired","runtime","mpaa","certification",
        "tagline","outline","plot","genre","studio","director","credits","country","tag","set","status","season","episode",
        "rating","ratings","votes","userrating","top250","uniqueid","id","imdbid","tmdbid","tvdbid","trailer","thumb","art","fanart",
        "actor","fileinfo","artist","album","track","name","displayseason","displayepisode","dateadded","lastplayed",
    };

    private static string Header(XElement root, string kind, string title, string nfoFolder)
    {
        var sb = new StringBuilder();
        var poster = PosterOf(root, nfoFolder);

        sb.Append("<div class=\"top\">");
        if (poster != null) sb.Append("<a href=\"").Append(Attr(poster)).Append("\"><img class=\"poster\" alt=\"Poster\" src=\"").Append(Attr(poster)).Append("\"></a>");
        sb.Append("<div>");
        sb.Append("<h1>").Append(Enc(title)).Append("</h1>");

        var original = First(root, "originaltitle");
        var year = First(root, "year") ?? Year(First(root, "premiered") ?? First(root, "releasedate") ?? First(root, "aired"));
        var sub = new List<string>();
        if (!string.IsNullOrWhiteSpace(original) && !string.Equals(original, title, StringComparison.OrdinalIgnoreCase)) sub.Add(original!);
        sub.Add(KindName(kind));
        if (year != null) sub.Add(year);
        var show = First(root, "showtitle");
        if (kind == "episodedetails" && show != null)
        {
            var s = First(root, "season"); var e = First(root, "episode");
            sub.Insert(0, $"{show}" + (s != null && e != null ? $" - S{Pad(s)}E{Pad(e)}" : ""));
        }
        sb.Append("<p class=\"sub\">").Append(string.Join(" &middot; ", sub.Select(Enc))).Append("</p>");

        var tagline = First(root, "tagline");
        if (tagline != null) sb.Append("<p class=\"tagline\">").Append(Enc(tagline)).Append("</p>");

        var chips = new List<string>();
        chips.AddRange(All(root, "genre"));
        var mpaa = First(root, "mpaa") ?? First(root, "certification");
        if (mpaa != null) chips.Add(mpaa);
        var runtime = First(root, "runtime");
        if (runtime != null) chips.Add(Runtime(runtime));
        chips.AddRange(RatingTexts(root));
        if (chips.Count > 0)
            sb.Append("<div class=\"chips\">").Append(string.Concat(chips.Select(c => "<span class=\"chip\">" + Enc(c) + "</span>"))).Append("</div>");

        sb.Append("</div></div>");

        var fanart = FanartOf(root, nfoFolder);
        if (fanart != null) sb.Append("<img class=\"fanart\" alt=\"\" src=\"").Append(Attr(fanart)).Append("\">");
        return sb.ToString();
    }

    private static string Details(XElement root, string kind)
    {
        var rows = new List<(string, string)>();
        void Add(string label, IEnumerable<string> values, bool link = false)
        {
            var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (list.Count > 0) rows.Add((label, string.Join(", ", list.Select(v => link ? Linkify(v) : Enc(v)))));
        }

        Add("Premiered", Single(LocalDate(First(root, "premiered") ?? First(root, "releasedate") ?? First(root, "aired"))));
        Add("Season / Episode", kind == "episodedetails" ? Single(Combine(First(root, "season"), First(root, "episode"))) : Array.Empty<string>());
        Add("Status", Single(First(root, "status")));
        Add("Director", All(root, "director"));
        Add("Writer", All(root, "credits"));
        Add("Studio", All(root, "studio"));
        Add("Country", All(root, "country"));
        var set = root.Element("set");
        if (set != null) Add("Collection", Single(First(set, "name") ?? Clean(set.Value)));
        Add("Tags", All(root, "tag"));
        Add("Artist", All(root, "artist"));
        Add("Album", Single(First(root, "album")));
        Add("Track", Single(First(root, "track")));
        Add("Top 250", Single(First(root, "top250")));
        // Add("Your Rating", Single(First(root, "userrating")));   // left out for now (the user, 2026-11-16); remove the // to show it again
        // Add("Date Added", Single(LocalDate(First(root, "dateadded"))));   // left out (the user: data from his own disk, not about the film); remove the // to show it again
        // Add("Last Played", Single(LocalDate(First(root, "lastplayed"))));   // left out: when it was last played in the user's own Kodi / Jellyfin, not about the film; remove the // to show it again

        if (rows.Count == 0) return "";
        var sb = new StringBuilder("<h2>Details</h2><table>");
        foreach (var (k, v) in rows) sb.Append("<tr><th>").Append(Enc(k)).Append("</th><td>").Append(v).Append("</td></tr>");
        return sb.Append("</table>").ToString();
    }

    private static string PlotSection(XElement root)
    {
        var plot = Text(root, "plot");
        var outline = Text(root, "outline");
        var sb = new StringBuilder();
        if (plot != null) sb.Append("<h2>Plot</h2>").Append(Paragraphs(plot));
        if (outline != null && !string.Equals(outline, plot, StringComparison.Ordinal)) sb.Append("<h2>Outline</h2>").Append(Paragraphs(outline));
        return sb.ToString();
    }

    private static string LinksSection(XElement root, string kind, string? linesAbove)
    {
        var links = new List<(string Label, string Url)>();
        void Add(string label, string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (links.Any(l => string.Equals(l.Url, url, StringComparison.OrdinalIgnoreCase))) return;
            links.Add((label, url));
        }

        // The trailer: a web address, or a Kodi add-on address that names a YouTube video.
        foreach (var t in All(root, "trailer"))
        {
            var url = TrailerAddress(t);
            if (url != null) Add("Trailer", url);
        }

        bool tv = kind == "tvshow";
        var ids = new List<(string Type, string Id)>();
        foreach (var u in root.Elements().Where(e => e.Name.LocalName.Equals("uniqueid", StringComparison.OrdinalIgnoreCase)))
            ids.Add(((u.Attribute("type")?.Value ?? "").ToLowerInvariant(), Clean(u.Value) ?? ""));
        foreach (var (name, type) in new[] { ("imdbid", "imdb"), ("tmdbid", "tmdb"), ("tvdbid", "tvdb") })
            if (First(root, name) is { } v) ids.Add((type, v));
        var plainId = First(root, "id");
        if (plainId != null) ids.Add((plainId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? "imdb" : "", plainId));

        foreach (var (type, id) in ids.Where(i => i.Id.Length > 0))
        {
            var tmdbKind = tv ? "tv" : "movie";
            switch (type)
            {
                case "imdb" when id.StartsWith("tt", StringComparison.OrdinalIgnoreCase):
                    Add("IMDb", $"https://www.imdb.com/title/{id}/"); break;
                case "tmdb" when kind is "movie" or "tvshow":
                    Add("The Movie Database", $"https://www.themoviedb.org/{tmdbKind}/{id}"); break;
                case "tvdb" when tv:
                    Add("TheTVDB", $"https://thetvdb.com/dereferrer/series/{id}"); break;
            }
        }

        // Any other web address in the file's text values (a homepage, a review ...), and a web address on a line above the XML.
        foreach (var e in root.Descendants())
        {
            if (e.HasElements) continue;
            var v = Clean(e.Value);
            if (v != null && NfoPoster.IsWebAddress(v) && !IsImage(v)) Add(e.Name.LocalName == "trailer" ? "Trailer" : Capitalize(e.Name.LocalName), v);
        }
        if (!string.IsNullOrWhiteSpace(linesAbove))
            foreach (Match m in UrlRegex.Matches(linesAbove)) Add("Web address in the file", m.Value.TrimEnd('.', ',', ';', ')'));

        if (links.Count == 0) return "";
        var sb = new StringBuilder("<h2>Links</h2><table>");
        foreach (var (label, url) in links)
            sb.Append("<tr><th>").Append(Enc(label)).Append("</th><td><a href=\"").Append(Attr(url)).Append("\" target=\"_blank\" rel=\"noopener\">").Append(Enc(url)).Append("</a></td></tr>");
        return sb.Append("</table>").ToString();
    }

    private static string CastSection(XElement root, string nfoFolder)
    {
        var actors = root.Elements().Where(e => e.Name.LocalName.Equals("actor", StringComparison.OrdinalIgnoreCase)).ToList();
        if (actors.Count == 0) return "";

        var sb = new StringBuilder("<h2>Cast</h2><div class=\"cast\">");
        foreach (var a in actors)
        {
            var name = First(a, "name") ?? "";
            var role = First(a, "role");
            var thumb = ResolveImage(First(a, "thumb"), nfoFolder);
            sb.Append("<div class=\"p\">");
            sb.Append(thumb != null ? $"<img loading=\"lazy\" alt=\"\" src=\"{Attr(thumb)}\">" : "<div class=\"ph\"></div>");
            sb.Append("<div><strong>").Append(Enc(name)).Append("</strong></div>");
            if (role != null) sb.Append("<div class=\"role\">").Append(Enc(role)).Append("</div>");
            sb.Append("</div>");
        }
        return sb.Append("</div>").ToString();
    }

    private static string FileInfoSection(XElement root)
    {
        var details = root.Element("fileinfo")?.Element("streamdetails");
        if (details == null) return "";

        var rows = new List<(string, string)>();
        foreach (var v in details.Elements("video"))
        {
            var parts = new List<string>();
            if (First(v, "codec") is { } c) parts.Add(c.ToUpperInvariant());
            if (First(v, "width") is { } w && First(v, "height") is { } h) parts.Add($"{w} x {h}");
            if (First(v, "aspect") is { } ar) parts.Add("aspect " + ar);
            if (First(v, "durationinseconds") is { } d && int.TryParse(d, out var sec)) parts.Add(TimeSpan.FromSeconds(sec).ToString(sec >= 3600 ? @"h\:mm\:ss" : @"m\:ss"));
            if (First(v, "hdrtype") is { } hdr) parts.Add(hdr);
            rows.Add(("Video", string.Join(" &middot; ", parts.Select(Enc))));
        }
        foreach (var a in details.Elements("audio"))
        {
            var parts = new List<string>();
            if (First(a, "language") is { } l) parts.Add(l);
            if (First(a, "codec") is { } c) parts.Add(c.ToUpperInvariant());
            if (First(a, "channels") is { } ch) parts.Add(ch + " channels");
            rows.Add(("Audio", string.Join(" &middot; ", parts.Select(Enc))));
        }
        var subs = details.Elements("subtitle").Select(s => First(s, "language")).Where(l => l != null).Select(l => l!).ToList();
        if (subs.Count > 0) rows.Add(("Subtitles", string.Join(", ", subs.Select(Enc))));
        if (rows.Count == 0) return "";

        var sb = new StringBuilder("<h2>File Information</h2><table>");
        foreach (var (k, v) in rows) sb.Append("<tr><th>").Append(Enc(k)).Append("</th><td>").Append(v).Append("</td></tr>");
        return sb.Append("</table>").ToString();
    }

    // Everything the page has no place for, so nothing in the file is hidden from the reader.
    private static string OtherSection(XElement root)
    {
        var rows = new StringBuilder();
        foreach (var e in root.Elements())
        {
            var name = e.Name.LocalName;
            if (Handled.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var value = Render(e);
            if (value.Length == 0) continue;
            rows.Append("<tr><th>").Append(Enc(Capitalize(name))).Append("</th><td>").Append(value).Append("</td></tr>");
        }
        return rows.Length == 0 ? "" : "<h2>Other Information</h2><table>" + rows + "</table>";
    }

    private static string Render(XElement e)
    {
        if (!e.HasElements) return Clean(e.Value) is { } v ? Linkify(v) : "";
        var items = e.Elements().Select(c =>
        {
            var inner = Render(c);
            return inner.Length == 0 ? "" : $"<li><span class=\"note\">{Enc(Capitalize(c.Name.LocalName))}:</span> {inner}</li>";
        }).Where(s => s.Length > 0);
        var list = string.Concat(items);
        return list.Length == 0 ? "" : "<ul class=\"plain\">" + list + "</ul>";
    }

    // ── Pictures ─────────────────────────────────────────────────────────────

    private static string? PosterOf(XElement root, string nfoFolder)
    {
        var candidates = new List<string?>();
        candidates.AddRange(root.Elements("thumb").Where(t => string.Equals(t.Attribute("aspect")?.Value, "poster", StringComparison.OrdinalIgnoreCase)).Select(t => Clean(t.Value)));
        candidates.Add(First(root.Element("art"), "poster"));
        candidates.AddRange(root.Elements("thumb").Where(t => t.Attribute("aspect") == null && t.Attribute("season") == null).Select(t => Clean(t.Value)));
        foreach (var c in candidates)
            if (ResolveImage(c, nfoFolder) is { } found) return found;
        return null;
    }

    private static string? FanartOf(XElement root, string nfoFolder)
    {
        var candidates = new List<string?> { First(root.Element("art"), "fanart"), First(root.Element("fanart"), "thumb") };
        foreach (var c in candidates)
            if (ResolveImage(c, nfoFolder) is { } found) return found;
        return null;
    }

    // A web address stays as it is; a file on the disk (full, or beside the .nfo) becomes a file:// address.
    private static string? ResolveImage(string? address, string nfoFolder)
    {
        address = Clean(address);
        if (address == null) return null;
        if (NfoPoster.IsWebAddress(address)) return address;
        var local = NfoPoster.LocalFile(address, nfoFolder);
        return local == null ? null : new Uri(local).AbsoluteUri;
    }

    private static bool IsImage(string url)
    {
        var path = url.Split('?', '#')[0];
        return path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    // The XML may have something before it (Kodi allows a web address on a line above): start at the first '<' of a tag.
    private static XDocument? TryParse(string text, out string? linesAbove)
    {
        linesAbove = null;
        try
        {
            int start = text.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
            if (start < 0) start = text.IndexOf('<');
            if (start < 0) return null;
            linesAbove = text[..start];
            return XDocument.Parse(text[start..]);
        }
        catch
        {
            return null;
        }
    }

    private static string? Clean(string? s)
    {
        var v = WebUtility.HtmlDecode((s ?? "").Trim());
        return v.Length == 0 ? null : v;
    }

    private static string? First(XElement? parent, string name) =>
        parent?.Elements().Where(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase) && !e.HasElements)
            .Select(e => Clean(e.Value)).FirstOrDefault(v => v != null);

    private static List<string> All(XElement parent, string name) =>
        parent.Elements().Where(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase) && !e.HasElements)
            .Select(e => Clean(e.Value)).Where(v => v != null).Select(v => v!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // The text of an element with its line breaks kept (a plot).
    private static string? Text(XElement parent, string name)
    {
        var e = parent.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (e == null) return null;
        var lines = WebUtility.HtmlDecode(e.Value).Replace("\r\n", "\n").Split('\n').Select(l => Regex.Replace(l.Trim(), @"[ \t]+", " "));
        var text = string.Join("\n", lines).Trim('\n');
        return text.Length == 0 ? null : Regex.Replace(text, @"\n{3,}", "\n\n");
    }

    private static IEnumerable<string> Single(string? v) => v == null ? Array.Empty<string>() : new[] { v };

    private static string? Combine(string? season, string? episode) =>
        season == null && episode == null ? null : $"Season {season ?? "?"}, Episode {episode ?? "?"}";

    private static IEnumerable<string> RatingTexts(XElement root)
    {
        var result = new List<string>();
        foreach (var r in root.Descendants().Where(e => e.Name.LocalName.Equals("rating", StringComparison.OrdinalIgnoreCase)))
        {
            var value = r.HasElements ? First(r, "value") : Clean(r.Value);
            if (value == null) continue;
            var max = r.Attribute("max")?.Value ?? "10";
            var name = r.Attribute("name")?.Value;
            var votes = r.HasElements ? First(r, "votes") : First(root, "votes");
            var t = $"{value} / {max}";
            var extra = new List<string>();
            if (!string.IsNullOrEmpty(name)) extra.Add(name!);
            if (votes != null) extra.Add($"{votes} votes");
            if (extra.Count > 0) t += " (" + string.Join(", ", extra) + ")";
            result.Add("Rating " + t);
        }
        return result;
    }

    private static string Runtime(string runtime) => int.TryParse(runtime.Trim(), out var min) && min > 0
        ? (min >= 60 ? $"{min / 60} h {min % 60} min" : $"{min} min")
        : runtime;

    // A date as the file writes it (2021-10-22, or with a time) in the format this computer is set to (Windows' regional settings).
    // Text that is not a date (a year alone, a free text) is shown as it is.
    private static string? LocalDate(string? text)
    {
        if (text == null) return null;
        if (!DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)) return text;
        return text.Contains(':') ? date.ToString("g", System.Globalization.CultureInfo.CurrentCulture) : date.ToString("d", System.Globalization.CultureInfo.CurrentCulture);
    }

    private static string? Year(string? date) => date != null && date.Length >= 4 && date[..4].All(char.IsDigit) ? date[..4] : null;

    private static string Pad(string n) => n.Length == 1 ? "0" + n : n;

    private static string KindName(string kind) => kind switch
    {
        "movie" => "Movie",
        "tvshow" => "TV Show",
        "episodedetails" => "TV Episode",
        "musicvideo" => "Music Video",
        "album" => "Album",
        "artist" => "Artist",
        "season" => "Season",
        _ => Capitalize(kind),
    };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // A trailer address: a web address as it is; Kodi's YouTube add-on address (plugin://plugin.video.youtube/?action=play_video&videoid=ID) as a YouTube link.
    private static string? TrailerAddress(string value)
    {
        if (NfoPoster.IsWebAddress(value)) return value;
        var m = Regex.Match(value, @"videoid=([\w-]{6,})", RegexOptions.IgnoreCase);
        return m.Success && value.Contains("youtube", StringComparison.OrdinalIgnoreCase) ? "https://www.youtube.com/watch?v=" + m.Groups[1].Value : null;
    }

    // ── HTML text ────────────────────────────────────────────────────────────

    private static readonly Regex UrlRegex = new(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase);
    private static readonly Regex EncodedUrlRegex = new(@"https?://(?:(?!&quot;|&#39;|&lt;|&gt;)[^\s<>""])+", RegexOptions.IgnoreCase);

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    private static string Attr(string s) => WebUtility.HtmlEncode(s);

    /// <summary>Text made safe for HTML, with every web address in it a link.</summary>
    private static string Linkify(string raw) =>
        EncodedUrlRegex.Replace(Enc(raw), m =>
        {
            var url = m.Value;
            var tail = "";
            while (url.Length > 0 && ".,;:)!?".Contains(url[^1])) { tail = url[^1] + tail; url = url[..^1]; }
            return $"<a href=\"{url}\" target=\"_blank\" rel=\"noopener\">{url}</a>{tail}";
        });

    private static string Paragraphs(string text) =>
        string.Concat(text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => "<p>" + Linkify(p.Trim()).Replace("\n", "<br>") + "</p>"));
}
