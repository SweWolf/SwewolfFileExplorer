using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// A folder on a web server that shows an "Index of /..." page (nginx autoindex, Apache, IIS and look-alikes), read like a folder:
/// <c>http://98.71.240.223/file/Burda</c> in the address bar. Read-only. Everything about web folders is in this file; the rest of
/// the program only asks <see cref="ArchiveSupport"/>, which hands a web address over to the methods here (a web folder is shown
/// the way a packed file is: a folder that cannot be changed, whose files are copied out to a temporary place to be opened).
///
/// A path of a web folder is the address with forward slashes, never a closing slash, names written as they read (spaces, not %20);
/// <see cref="RequestUri"/> makes the real address (every name is escaped there). A query string is not supported.
/// </summary>
public static class WebFolder
{
    private static readonly CookieContainer Cookies = new();
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = Cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };   // each call has its own limit
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SweWolfFileExplorer/1.0");
        return client;
    }

    /// <summary>How long a listing may take.</summary>
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(20);

    // ── Addresses ────────────────────────────────────────────────────────────

    public static bool IsWebPath(string? path) =>
        path != null && (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static readonly Regex BareAddress = new(@"^(\d{1,3}(\.\d{1,3}){3}|localhost)(:\d+)?(/.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Text typed without "http://" that is clearly an address (a number address such as 98.71.240.223/file, or localhost) gets it.</summary>
    public static string Complete(string text) =>
        !string.IsNullOrEmpty(text) && BareAddress.IsMatch(text) ? "http://" + text : text;

    /// <summary>The path in its one form: forward slashes, no closing slash, "http://host" for the top of a server.</summary>
    public static string Normalize(string path)
    {
        path = path.Trim().Replace('\\', '/');
        int q = path.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) path = path[..q];
        return path.TrimEnd('/');
    }

    private static (string Origin, string[] Segments) Split(string path)
    {
        path = Normalize(path);
        int schemeEnd = path.IndexOf("://", StringComparison.Ordinal) + 3;
        int slash = path.IndexOf('/', schemeEnd);
        string origin = slash < 0 ? path : path[..slash];
        var segments = slash < 0 ? Array.Empty<string>() : path[(slash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (origin, segments);
    }

    /// <summary>The real address of a path (every name escaped); a folder ends with a slash.</summary>
    public static Uri RequestUri(string path, bool folder)
    {
        var (origin, segments) = Split(path);
        var sb = new StringBuilder(origin).Append('/');
        sb.Append(string.Join("/", segments.Select(Uri.EscapeDataString)));
        if (folder && segments.Length > 0) sb.Append('/');
        return new Uri(sb.ToString());
    }

    /// <summary>The path of the folder above, or null at the top of the server.</summary>
    public static string? Parent(string path)
    {
        var (origin, segments) = Split(path);
        if (segments.Length == 0) return null;
        return segments.Length == 1 ? origin : origin + "/" + string.Join("/", segments[..^1]);
    }

    /// <summary>The last name of the path, or the server's name at the top.</summary>
    public static string NameOf(string path)
    {
        var (origin, segments) = Split(path);
        return segments.Length > 0 ? segments[^1] : origin[(origin.IndexOf("://", StringComparison.Ordinal) + 3)..];
    }

    public static string Combine(string folder, string name) => Normalize(folder) + "/" + name;

    // ── Listing ──────────────────────────────────────────────────────────────

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (DateTime At, List<VirtualEntry> Entries)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(60);

    /// <summary>Forgets what was read of a folder (F5, Refresh): the next listing asks the server again.</summary>
    public static void Invalidate(string path)
    {
        var key = Normalize(path);
        lock (CacheLock)
            foreach (var k in Cache.Keys.Where(k => k.StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToList())
                Cache.Remove(k);
    }

    /// <summary>What is in the folder. Throws <see cref="ArchiveException"/> with a readable text when the server cannot be reached or says no.</summary>
    public static List<VirtualEntry> List(string folderPath)
    {
        string key = Normalize(folderPath);
        lock (CacheLock)
            if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheTime) return hit.Entries;

        var entries = Run(() => ListAsync(key));
        lock (CacheLock)
        {
            if (Cache.Count > 200) Cache.Clear();
            Cache[key] = (DateTime.UtcNow, entries);
        }
        return entries;
    }

    public static bool TryList(string folderPath, out string? error)
    {
        try { List(folderPath); error = null; return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>The entry at a path: its size and date, null if the folder above does not have it.</summary>
    public static VirtualEntry? GetEntry(string path)
    {
        var parent = Parent(path);
        if (parent == null) return null;
        try
        {
            string name = NameOf(path);
            return List(parent).FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    private static async Task<List<VirtualEntry>> ListAsync(string folderPath)
    {
        var uri = RequestUri(folderPath, folder: true);
        using var cts = new CancellationTokenSource(ListTimeout);
        string html = "", finalPath = uri.AbsolutePath;
        try
        {
            // Twice at most: a host with a JavaScript check in front (see SolveChallenge) answers the first time with a script, not the listing.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new ArchiveException(StatusText(response.StatusCode, uri));
                finalPath = response.RequestMessage?.RequestUri?.AbsolutePath ?? uri.AbsolutePath;

                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (type.Length > 0 && !type.Contains("html", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                    throw new ArchiveException($"{uri} is a file, not a folder.");

                var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                html = Decode(bytes, response.Content.Headers.ContentType?.CharSet);
                if (attempt == 0 && SolveChallenge(html, uri)) continue;   // the cookie is set: ask again
                break;
            }
        }
        catch (OperationCanceledException)
        {
            throw new ArchiveException($"The server did not answer in {(int)ListTimeout.TotalSeconds} seconds: {uri.Host}.");
        }
        catch (HttpRequestException ex)
        {
            throw new ArchiveException($"Cannot reach {uri.Host}: {(ex.InnerException?.Message ?? ex.Message)}");
        }

        return Parse(html, folderPath, new Uri(uri, finalPath));
    }

    private static readonly Regex ChallengeNumbers = new(@"toNumbers\(""([0-9a-f]{32})""\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Some free hosts (InfinityFree, "great-site.net" and others) answer a program with a small page whose script calculates a cookie
    /// (<c>__test</c>: AES-128 CBC, three hex values in the page: key, IV, data) and reloads; a browser does that by itself and then gets
    /// the real page. This does the same calculation and keeps the cookie, so such a site works as any other. Nothing is guessed or
    /// bypassed: the values are in the page and the calculation is the one the page's own script makes. Returns true if the cookie was set.
    /// </summary>
    private static bool SolveChallenge(string html, Uri uri)
    {
        if (!html.Contains("slowAES", StringComparison.Ordinal) || !html.Contains("__test", StringComparison.Ordinal)) return false;
        var numbers = ChallengeNumbers.Matches(html);
        if (numbers.Count < 3) return false;

        try
        {
            byte[] key = Convert.FromHexString(numbers[0].Groups[1].Value);
            byte[] iv = Convert.FromHexString(numbers[1].Groups[1].Value);
            byte[] data = Convert.FromHexString(numbers[2].Groups[1].Value);
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = key;
            string value = Convert.ToHexString(aes.DecryptCbc(data, iv, System.Security.Cryptography.PaddingMode.None)).ToLowerInvariant();
            Cookies.Add(new Uri(uri.GetLeftPart(UriPartial.Authority)), new Cookie("__test", value, "/"));
            return true;
        }
        catch { return false; }
    }

    private static string StatusText(HttpStatusCode code, Uri uri) => code switch
    {
        HttpStatusCode.NotFound  => $"The folder was not found on the server: {uri.AbsolutePath}",
        HttpStatusCode.Forbidden => $"The server does not allow listing this folder: {uri.AbsolutePath}",
        HttpStatusCode.Unauthorized => $"The server asks for a user name and password, which this program does not support yet: {uri.Host}",
        _ => $"The server answered {(int)code} {code} for {uri.AbsolutePath}"
    };

    private static string Decode(byte[] bytes, string? charset)
    {
        try
        {
            if (!string.IsNullOrEmpty(charset)) return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes);
        }
        catch { /* unknown name: guessed below */ }

        // A valid UTF-8 page is UTF-8; otherwise the old Western code page.
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch { return Encoding.Latin1.GetString(bytes); }
    }

    // ── Reading the page ─────────────────────────────────────────────────────

    private static readonly Regex Anchor = new(@"<a\s[^>]*?href\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex CellEnd = new(@"</t[dh]\s*>|<br\s*/?>|</tr\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd H:mm", "yyyy-MM-dd",
        "dd-MMM-yyyy HH:mm", "dd-MMM-yyyy HH:mm:ss", "d-MMM-yyyy HH:mm",
        "dd-MMM-yy HH:mm", "d-MMM-yyyy",
        "M/d/yyyy h:mm tt", "M/d/yyyy h:mm:ss tt", "M/d/yyyy HH:mm", "d/M/yyyy HH:mm",
        "yyyy/MM/dd HH:mm", "yyyy/MM/dd HH:mm:ss",
    };

    private static readonly Regex DateText = new(
        @"\d{4}[-/]\d{1,2}[-/]\d{1,2}(?:\s+\d{1,2}:\d{2}(?::\d{2})?)?|\d{1,2}-[A-Za-z]{3}-\d{2,4}(?:\s+\d{1,2}:\d{2}(?::\d{2})?)?|\d{1,2}/\d{1,2}/\d{4}(?:,?\s+\d{1,2}:\d{2}(?::\d{2})?(?:\s*[AaPp][Mm])?)?",
        RegexOptions.Compiled);

    private static readonly Regex SizeText = new(@"(?<![\w.\-/])(\d+(?:[.,]\d+)?)\s*([KMGT]i?B?|B|bytes)?(?![\w.\-/:])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SlashDate = new(@"(?<!\d)(\d{1,2})/(\d{1,2})/\d{4}(?!\d)", RegexOptions.Compiled);

    private static string Flatten(string html)
    {
        var text = WebUtility.HtmlDecode(Tag.Replace(CellEnd.Replace(html, " | "), " "));
        int eol = text.IndexOfAny(new[] { '\r', '\n' });
        return eol >= 0 ? text[..eol] : text;   // one record is one line
    }

    /// <summary>Takes the folders and files out of an index page. Public so a test can feed it pages of different servers.</summary>
    public static List<VirtualEntry> Parse(string html, string folderPath, Uri pageUri)
    {
        var result = new List<VirtualEntry>();
        var known = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // name -> place in result
        string basePath = pageUri.AbsolutePath;
        if (!basePath.EndsWith('/')) basePath += "/";

        // 21/02/2023 is day first, 02/21/2023 month first; when no date in the page settles it, month first (the American default).
        bool dayFirst = false, monthFirst = false;
        foreach (Match d in SlashDate.Matches(html))
        {
            if (int.Parse(d.Groups[1].Value) > 12) dayFirst = true;
            if (int.Parse(d.Groups[2].Value) > 12) monthFirst = true;
        }
        var formats = dayFirst && !monthFirst
            ? DateFormats.Where(f => f.StartsWith("d/M")).Concat(DateFormats.Where(f => !f.StartsWith("M/d") && !f.StartsWith("d/M"))).ToArray()
            : DateFormats;

        var matches = Anchor.Matches(html);
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            string href = WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim();
            if (href.Length == 0 || href[0] == '#' || href[0] == '?' || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;

            // The address as the page's own folder sees it; only a direct child counts (not the parent, not another server).
            if (!Uri.TryCreate(pageUri, href, out var target)) continue;
            if (!target.Host.Equals(pageUri.Host, StringComparison.OrdinalIgnoreCase) || target.Port != pageUri.Port) continue;
            string targetPath = target.AbsolutePath;
            bool endsWithSlash = targetPath.EndsWith('/');
            string asFolder = endsWithSlash ? targetPath : targetPath + "/";
            if (!asFolder.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) || asFolder.Length == basePath.Length) continue;
            string rest = targetPath[basePath.Length..].TrimEnd('/');
            if (rest.Length == 0 || rest.Contains('/')) continue;
            string name = Uri.UnescapeDataString(rest);
            if (name.Length == 0) continue;

            // The date and the size: most servers write them after the link (nginx, Apache: in the next cells of the row),
            // Microsoft IIS writes them before it ("21/02/2023  19:36  526 <a>"). The side that has a date is the entry's.
            int from = m.Index + m.Length;
            int to = i + 1 < matches.Count ? matches[i + 1].Index : html.Length;
            string after = html[from..to];
            int closing = after.IndexOf("</a>", StringComparison.OrdinalIgnoreCase);
            if (closing >= 0) after = after[(closing + 4)..];
            int endOfRecord = after.IndexOf("</pre>", StringComparison.OrdinalIgnoreCase);
            if (endOfRecord >= 0) after = after[..endOfRecord];
            int br = after.IndexOf("<br", StringComparison.OrdinalIgnoreCase);
            if (br >= 0) after = after[..br];
            string text = Flatten(after);

            if (!DateText.IsMatch(text) && i > 0)
            {
                // Before the link: from the end of the line (or the last line break tag) that ends the entry above.
                int prevEnd = html.IndexOf("</a>", matches[i - 1].Index, StringComparison.OrdinalIgnoreCase);
                prevEnd = prevEnd < 0 || prevEnd > m.Index ? matches[i - 1].Index + matches[i - 1].Length : prevEnd + 4;
                string before = html[prevEnd..m.Index];
                int cut = Math.Max(before.LastIndexOf('\n'), Math.Max(before.LastIndexOf("<br", StringComparison.OrdinalIgnoreCase), before.LastIndexOf("<tr", StringComparison.OrdinalIgnoreCase)));
                if (cut >= 0) before = before[(before.IndexOf('>', cut) >= 0 && before[cut] == '<' ? before.IndexOf('>', cut) + 1 : cut + 1)..];
                var beforeText = Flatten(before);
                if (DateText.IsMatch(beforeText)) text = beforeText;
            }
            else if (!DateText.IsMatch(text) && i == 0)
            {
                int start = Math.Max(0, html.LastIndexOf('\n', Math.Max(0, m.Index - 1)));
                var beforeText = Flatten(html[start..m.Index]);
                if (DateText.IsMatch(beforeText)) text = beforeText;
            }

            DateTime modified = DateTime.MinValue;
            var dm = DateText.Match(text);
            if (dm.Success)
            {
                var d = dm.Value.Replace(",", "").Trim();
                if (DateTime.TryParseExact(d, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var parsed)
                    || DateTime.TryParse(d, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out parsed))
                    modified = parsed;
                text = text.Remove(dm.Index, dm.Length);
            }

            bool isDir = endsWithSlash || text.Contains("<dir>", StringComparison.OrdinalIgnoreCase) || text.Contains("[DIR]", StringComparison.OrdinalIgnoreCase);
            long size = -1;
            if (!isDir)
            {
                var sm = SizeText.Match(text);
                if (sm.Success && double.TryParse(sm.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
                {
                    char unit = sm.Groups[2].Success && sm.Groups[2].Value.Length > 0 ? char.ToUpperInvariant(sm.Groups[2].Value[0]) : 'B';
                    double factor = unit switch { 'K' => 1024d, 'M' => 1024d * 1024, 'G' => 1024d * 1024 * 1024, 'T' => 1024d * 1024 * 1024 * 1024, _ => 1 };
                    size = (long)(number * factor);
                }
            }

            // Apache tables link the icon and the name to the same address: the entry is one, and the link that has the date is the name's.
            var entry = new VirtualEntry(name, Combine(folderPath, name), isDir, isDir ? -1 : size, modified);
            if (known.TryGetValue(name, out var at))
            {
                if (result[at].Modified == DateTime.MinValue && modified != DateTime.MinValue) result[at] = entry;
                else if (result[at].Size < 0 && size >= 0 && !result[at].IsDirectory) result[at] = entry;
                continue;
            }
            known[name] = result.Count;
            result.Add(entry);
        }

        return result;
    }

    // ── Taking files out ─────────────────────────────────────────────────────

    /// <summary>Copies a file, or a whole folder, to <paramref name="destination"/> (which must not exist yet).</summary>
    public static void ExtractTo(string path, string destination)
    {
        var entry = GetEntry(path);
        bool isFolder = entry != null ? entry.IsDirectory : Run(() => LooksLikeFolderAsync(path));

        if (!isFolder)
        {
            try { Run(() => DownloadAsync(path, destination)); }
            catch
            {
                try { File.Delete(destination); } catch { /* a half file may be left */ }
                throw;
            }
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var child in List(path))
            ExtractTo(child.FullPath, Path.Combine(destination, SafeName(child.Name)));
    }

    /// <summary>The file as a stream (in memory up to 200 MB, else a temporary file that goes away when it is closed).</summary>
    public static Stream OpenRead(string path)
    {
        var entry = GetEntry(path);
        if (entry != null && entry.Size > 200L * 1024 * 1024)
        {
            string temp = Path.Combine(Path.GetTempPath(), "SwewolfFileExplorer-web-" + Guid.NewGuid().ToString("N"));
            Run(() => DownloadAsync(path, temp));
            return new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose);
        }

        var memory = new MemoryStream();
        Run(async () => { await DownloadToAsync(path, memory).ConfigureAwait(false); return 0; });
        memory.Position = 0;
        return memory;
    }

    private static async Task<int> DownloadAsync(string path, string destination)
    {
        using (var file = File.Create(destination))
            await DownloadToAsync(path, file).ConfigureAwait(false);
        return 0;
    }

    private static async Task DownloadToAsync(string path, Stream target, CancellationToken cancel = default, Action<int>? onRead = null)
    {
        var uri = RequestUri(path, folder: false);
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new ArchiveException(StatusText(response.StatusCode, uri));
            using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            // A stall of 60 s with no data ends the download.
            var buffer = new byte[81920];
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                stall.CancelAfter(TimeSpan.FromSeconds(60));
                int read;
                try { read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new ArchiveException($"The server stopped sending '{NameOf(path)}'."); }
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                onRead?.Invoke(read);
            }
        }
        catch (HttpRequestException ex) { throw new ArchiveException($"Cannot download '{NameOf(path)}': {ex.InnerException?.Message ?? ex.Message}"); }
    }

    // ── Copying in the background, with progress and Cancel ─────────────────

    /// <summary>
    /// Downloads files and folders to the temporary folder (the same places <see cref="ArchiveSupport.ExtractToTemp"/> uses) on the calling
    /// thread's task, with <paramref name="progress"/> (bytes and files) and Cancel. An item that fails does not stop the others:
    /// the result has what was done (web path to temporary path) and what failed with a readable reason.
    /// </summary>
    public static async Task<TransferResult> DownloadManyAsync(List<string> paths, TransferProgress progress, CancellationToken cancel)
    {
        var done = new List<(string From, string To)>();
        var failed = new List<(string Name, string Reason)>();
        progress.ItemWord = "File";
        progress.Preparing = true;

        // First what there is: every file of every folder, so the bar and "File 3 of 12" are true.
        var files = new List<(string Path, long Size)>();
        var plan = new List<(string Root, string Target, bool Folder)>();
        foreach (var root in paths)
        {
            try
            {
                cancel.ThrowIfCancellationRequested();
                var entry = GetEntry(root);
                bool folder = entry != null ? entry.IsDirectory : await LooksLikeFolderAsync(root).ConfigureAwait(false);
                string target = ArchiveSupport.NewTempTarget(root);
                plan.Add((root, target, folder));
                if (folder) await CollectAsync(root, files, cancel).ConfigureAwait(false);
                else files.Add((root, entry?.Size ?? 0));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { failed.Add((NameOf(root), ex.Message)); }
        }
        progress.FileTotal = files.Count;
        progress.BytesTotal = files.Sum(f => Math.Max(0, f.Size));
        progress.Preparing = false;

        foreach (var (root, target, folder) in plan)
        {
            if (cancel.IsCancellationRequested) break;
            try
            {
                if (folder) await DownloadFolderAsync(root, target, progress, cancel).ConfigureAwait(false);
                else await DownloadFileAsync(root, target, progress, cancel).ConfigureAwait(false);
                ArchiveSupport.RememberExtracted(root, target);
                done.Add((root, target));
            }
            catch (OperationCanceledException) { RemoveQuietly(target); break; }
            catch (Exception ex)
            {
                RemoveQuietly(target);
                failed.Add((NameOf(root), ex.Message));
            }
        }

        // What was started and not finished is gone: no half copies are left in the temporary folder.
        if (cancel.IsCancellationRequested) foreach (var (_, target, _) in plan) if (!done.Any(d => d.To == target)) RemoveQuietly(target);
        return new TransferResult(done, failed, cancel.IsCancellationRequested, paths.Count);
    }

    /// <summary>The folder above does not list this path (the server does not list it, or it is the top): it is a folder if it lists itself.</summary>
    private static async Task<bool> LooksLikeFolderAsync(string path)
    {
        if (Parent(path) == null) return true;
        try { await ListCachedAsync(path).ConfigureAwait(false); return true; }
        catch { return false; }
    }

    private static async Task CollectAsync(string folder, List<(string Path, long Size)> files, CancellationToken cancel)
    {
        foreach (var child in await ListCachedAsync(folder).ConfigureAwait(false))
        {
            cancel.ThrowIfCancellationRequested();
            if (child.IsDirectory) await CollectAsync(child.FullPath, files, cancel).ConfigureAwait(false);
            else files.Add((child.FullPath, child.Size));
        }
    }

    private static async Task DownloadFolderAsync(string folder, string target, TransferProgress progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(target);
        foreach (var child in await ListCachedAsync(folder).ConfigureAwait(false))
        {
            cancel.ThrowIfCancellationRequested();
            string to = Path.Combine(target, SafeName(child.Name));
            if (child.IsDirectory) await DownloadFolderAsync(child.FullPath, to, progress, cancel).ConfigureAwait(false);
            else await DownloadFileAsync(child.FullPath, to, progress, cancel).ConfigureAwait(false);
        }
    }

    private static async Task DownloadFileAsync(string path, string target, TransferProgress progress, CancellationToken cancel)
    {
        progress.CurrentName = NameOf(path);
        progress.FileIndex++;
        using var file = File.Create(target);
        await DownloadToAsync(path, file, cancel, n => progress.BytesDone += n).ConfigureAwait(false);
    }

    private static void RemoveQuietly(string target)
    {
        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            else if (File.Exists(target)) File.Delete(target);
        }
        catch { /* the temporary folder is cleaned when the program ends */ }
    }

    private static async Task<List<VirtualEntry>> ListCachedAsync(string folderPath)
    {
        string key = Normalize(folderPath);
        lock (CacheLock)
            if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheTime) return hit.Entries;

        var entries = await ListAsync(key).ConfigureAwait(false);
        lock (CacheLock)
        {
            if (Cache.Count > 200) Cache.Clear();
            Cache[key] = (DateTime.UtcNow, entries);
        }
        return entries;
    }

    private static string SafeName(string name)
    {
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        return name.Trim().Length == 0 || name is "." or ".." ? "_" : name;
    }

    /// <summary>Runs the network work on a thread-pool thread and waits for it, with the wait cursor when the window thread is the one waiting.</summary>
    private static T Run<T>(Func<Task<T>> work)
    {
        var app = System.Windows.Application.Current;
        bool onUi = app != null && app.Dispatcher.CheckAccess();
        System.Windows.Input.Cursor? before = null;
        try
        {
            if (onUi)
            {
                before = System.Windows.Input.Mouse.OverrideCursor;
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            }
        }
        catch { onUi = false; }   // not a thread that can show a cursor
        try { return Task.Run(work).GetAwaiter().GetResult(); }
        finally { if (onUi) System.Windows.Input.Mouse.OverrideCursor = before; }
    }
}
