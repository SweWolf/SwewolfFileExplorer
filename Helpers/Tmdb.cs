using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The Movie Database (TMDB): search for a movie or TV show with the user's own free API key and write a Kodi / Jellyfin style
/// .nfo file from the answer (<see cref="BuildNfo"/>). Used by "Download NFO File..." (right-click in the plot area).
/// A key is either the 32-character "API Key" (sent as a parameter) or the long "API Read Access Token" (sent as a Bearer header);
/// both work. The key is kept encrypted with DPAPI for this Windows user, never as plain text in settings.json.
/// Every failure is an <see cref="InvalidOperationException"/> with a message fit to show.
/// </summary>
public static class Tmdb
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };   // each call has its own cancel token

    public const string KeyPage = "https://www.themoviedb.org/settings/api";
    private const string Api = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p/";

    /// <summary>What the user is looking for. Unknown = search movies and TV shows both.</summary>
    public enum Kind { Unknown, Movie, Tv }

    /// <summary>One search hit. <see cref="IsTv"/> says which kind it is.</summary>
    public sealed record Result(bool IsTv, int Id, string Title, string? Year, string Overview);

    // ── The key ──────────────────────────────────────────────────────────────────────────────────────────────────

    public static string? GetApiKey()
    {
        var stored = AppSettingsStore.Current.TmdbApiKeyProtected;
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            var key = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser)).Trim();
            return key.Length == 0 ? null : key;
        }
        catch { return null; }
    }

    /// <summary>Saves the key (encrypted), or removes it when empty. The caller saves the settings.</summary>
    public static void SetApiKey(string? key)
    {
        key = key?.Trim();
        AppSettingsStore.Current.TmdbApiKeyProtected = string.IsNullOrEmpty(key)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
    }

    /// <summary>The API Key (32 hex digits) or the Read Access Token (a long text of letters, digits, '-', '_' and two dots).</summary>
    public static bool IsValidKeyFormat(string key) =>
        (key.Length == 32 && key.All(char.IsAsciiHexDigit))
        || (key.Length >= 100 && key.Count(c => c == '.') == 2 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'));

    // ── Reading the file name ────────────────────────────────────────────────────────────────────────────────────

    private static readonly Regex EpisodeRx = new(@"(?<![A-Za-z0-9])[sS](\d{1,2})[eE](\d{1,3})(?![0-9])", RegexOptions.Compiled);
    private static readonly Regex YearRx = new(@"(?<![0-9])(19|20)\d{2}(?![0-9p])", RegexOptions.Compiled);

    /// <summary>The season and episode in a file name (<c>S02E03</c>), or null.</summary>
    public static (int Season, int Episode)? EpisodeOf(string fileName)
    {
        var m = EpisodeRx.Match(fileName);
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    /// <summary>True when a parent folder (any level up to the root) is called "TV Shows" (also "TV Show", "TV Series", "TVShows").</summary>
    public static bool InTvShowsFolder(string path)
    {
        for (var dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            var name = Path.GetFileName(dir.TrimEnd('\\', '/')).Replace(" ", "").ToLowerInvariant();
            if (name is "tvshows" or "tvshow" or "tvseries") return true;
        }
        return false;
    }

    /// <summary>The name, year and kind suggested by a file: a TV show when a parent folder is "TV Shows" or the name has S02E03, else a movie.</summary>
    public static (string Name, string Year, Kind Kind) Guess(string path)
    {
        // A folder is taken as a TV show (its whole name counts: "Show.Name.2010" has no extension), and its year is the show's.
        bool isFolder = Directory.Exists(path);
        var baseName = isFolder ? Path.GetFileName(path.TrimEnd('\\', '/')) : Path.GetFileNameWithoutExtension(path);
        var episode = EpisodeRx.Match(baseName);
        bool tv = isFolder || episode.Success || InTvShowsFolder(path);

        string title = baseName;
        string year = "";

        // The last year that has text in front of it ("2001.A.Space.Odyssey.1968" gives 1968, "1917.2019" gives 2019).
        Match? yearMatch = null;
        foreach (Match m in YearRx.Matches(baseName)) if (m.Index > 0) yearMatch = m;

        int cut = baseName.Length;
        if (episode.Success) cut = Math.Min(cut, episode.Index);
        if (yearMatch != null) { cut = Math.Min(cut, yearMatch.Index); year = yearMatch.Value; }
        if (cut > 0) title = baseName[..cut];

        title = Regex.Replace(title.Replace('.', ' ').Replace('_', ' ').Trim(' ', '-', '(', '[', ')', ']'), @"\s+", " ");
        return (title, tv && !isFolder ? "" : year, tv ? Kind.Tv : Kind.Movie);
    }

    // ── The web ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<JsonElement> GetAsync(string path, string query, CancellationToken ct)
    {
        var key = GetApiKey() ?? throw new InvalidOperationException("There is no TMDB API key. Add it in Tools > Settings > Scrapers.");
        bool token = key.Contains('.');
        var url = $"{Api}{path}?language=en-US{(query.Length > 0 ? "&" + query : "")}{(token ? "" : "&api_key=" + Uri.EscapeDataString(key))}";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        // Every call is written to internet.log (what was asked, what came back, how long it took). The key is never in it:
        // the address that is logged is built without it.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string outcome = "no answer (canceled)";
        try
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (token) request.Headers.Authorization = new("Bearer", key);
                request.Headers.UserAgent.ParseAdd("SweWolfFileExplorer/1.0");
                using var response = await Http.SendAsync(request, timeout.Token);
                outcome = ((int)response.StatusCode).ToString();

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("TMDB did not accept the API key. Check it in Tools > Settings > Scrapers.");
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    throw new InvalidOperationException("TMDB has no entry for this.");
                if ((int)response.StatusCode == 429)
                    throw new InvalidOperationException("TMDB says there were too many requests. Try again in a moment.");
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                if (doc.RootElement.TryGetProperty("results", out var found) && found.ValueKind == JsonValueKind.Array)
                    outcome += $", {found.GetArrayLength()} results";
                return doc.RootElement.Clone();
            }
            catch (HttpRequestException ex) { outcome = "failed: " + ex.Message; throw new InvalidOperationException($"TMDB could not be reached: {ex.Message}"); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { outcome = "no answer within 20 s"; throw new InvalidOperationException("TMDB did not answer in time."); }
            catch (JsonException) { outcome += ", answer could not be read"; throw new InvalidOperationException("TMDB gave an answer that could not be read."); }
        }
        finally
        {
            LogFiles.AppendInternet($"GET\tthemoviedb.org\t{path}?language=en-US{(query.Length > 0 ? "&" + query : "")}\t{outcome}\t{watch.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>The hits, and whether the year had to be dropped to find any (then the list holds every year).</summary>
    public sealed record SearchOutcome(List<Result> Results, bool YearIgnored);

    public static async Task<SearchOutcome> SearchAsync(string name, string year, Kind kind, bool includeAdult, CancellationToken ct)
    {
        bool hasYear = year.Length == 4 && year.All(char.IsAsciiDigit);
        var results = await SearchOnceAsync(name, hasYear ? year : "", kind, includeAdult, ct);
        // The year in a file name is often a little wrong: when nothing fits it, try again without it.
        if (results.Count == 0 && hasYear)
            return new SearchOutcome(await SearchOnceAsync(name, "", kind, includeAdult, ct), true);
        return new SearchOutcome(results, false);
    }

    private static async Task<List<Result>> SearchOnceAsync(string name, string year, Kind kind, bool includeAdult, CancellationToken ct)
    {
        var results = new List<Result>();
        // include_video and sort_by are what the user's research suggested (TMDB's own pages do not say they matter for a search).
        string q = "query=" + Uri.EscapeDataString(name)
                 + "&include_adult=" + (includeAdult ? "true" : "false") + "&include_video=true&sort_by=popularity.desc";
        bool hasYear = year.Length == 4;

        if (kind != Kind.Tv)
        {
            // primary_release_year = the main release date only; "year" also matches any other release date (festivals, other countries).
            var json = await GetAsync("/search/movie", q + (hasYear ? "&primary_release_year=" + year : ""), ct);
            foreach (var r in json.GetProperty("results").EnumerateArray())
                results.Add(new Result(false, r.GetProperty("id").GetInt32(), Str(r, "title"), YearOf(Str(r, "release_date")), Str(r, "overview")));
        }
        if (kind != Kind.Movie)
        {
            var json = await GetAsync("/search/tv", q + (hasYear ? "&first_air_date_year=" + year : ""), ct);
            foreach (var r in json.GetProperty("results").EnumerateArray())
                results.Add(new Result(true, r.GetProperty("id").GetInt32(), Str(r, "name"), YearOf(Str(r, "first_air_date")), Str(r, "overview")));
        }
        return results;
    }

    private static readonly Regex ImdbRx = new(@"(?<![A-Za-z0-9])tt\d{5,10}(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>An IMDb ID (<c>tt0137523</c>) found in typed or pasted text (also in an address like imdb.com/title/tt0137523/), lower case; else null.</summary>
    public static string? ParseImdbId(string text)
    {
        var m = ImdbRx.Match(text);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// The movie or TV show that has this IMDb ID (TMDB's "find by external ID"). The type decides which one wins in the rare case that
    /// both exist; a missing one is answered by the other. Null when TMDB does not know the ID. The ID of a single TV episode is refused
    /// with a message (use the ID of the show).
    /// </summary>
    public static async Task<Result?> FindByImdbAsync(string imdbId, Kind kind, CancellationToken ct)
    {
        var json = await GetAsync($"/find/{imdbId}", "external_source=imdb_id", ct);

        Result? movie = null, tv = null;
        if (json.TryGetProperty("movie_results", out var movies) && movies.GetArrayLength() > 0)
        {
            var r = movies[0];
            movie = new Result(false, r.GetProperty("id").GetInt32(), Str(r, "title"), YearOf(Str(r, "release_date")), Str(r, "overview"));
        }
        if (json.TryGetProperty("tv_results", out var shows) && shows.GetArrayLength() > 0)
        {
            var r = shows[0];
            tv = new Result(true, r.GetProperty("id").GetInt32(), Str(r, "name"), YearOf(Str(r, "first_air_date")), Str(r, "overview"));
        }
        if (movie == null && tv == null && json.TryGetProperty("tv_episode_results", out var eps) && eps.GetArrayLength() > 0)
            throw new InvalidOperationException($"{imdbId} is the IMDb ID of a single TV episode. Enter the IMDb ID of the TV show instead.");

        return kind == Kind.Tv ? tv ?? movie : movie ?? tv;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static string? YearOf(string date) => date.Length >= 4 ? date[..4] : null;

    // ── The .nfo ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The .nfo text for a hit. A movie gives &lt;movie&gt;; a TV show gives &lt;episodedetails&gt; when the file has a season and episode
    /// (<paramref name="episode"/>), else &lt;tvshow&gt;. Same element names as Kodi and Jellyfin write.
    /// </summary>
    public static async Task<string> BuildNfoAsync(Result hit, (int Season, int Episode)? episode, CancellationToken ct)
    {
        XElement root;
        if (!hit.IsTv)
        {
            var m = await GetAsync($"/movie/{hit.Id}", "append_to_response=credits,external_ids,release_dates", ct);
            root = MovieNfo(m);
        }
        else
        {
            var show = await GetAsync($"/tv/{hit.Id}", "append_to_response=credits,external_ids,content_ratings", ct);
            if (episode is { } ep)
            {
                JsonElement e;
                try { e = await GetAsync($"/tv/{hit.Id}/season/{ep.Season}/episode/{ep.Episode}", "append_to_response=credits", ct); }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("TMDB has no entry"))
                { throw new InvalidOperationException($"TMDB has no season {ep.Season} episode {ep.Episode} of \"{hit.Title}\"."); }
                root = EpisodeNfo(show, e, ep);
            }
            else root = TvShowNfo(show);
        }

        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n" + root.ToString() + "\n";
        return xml.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    private static void Add(XElement parent, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) parent.Add(new XElement(name, value.Trim()));
    }

    private static string Num(JsonElement e, string name, string format = "0.#") =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDouble().ToString(format, System.Globalization.CultureInfo.InvariantCulture) : "";

    private static void AddNames(XElement parent, JsonElement e, string array, string element)
    {
        if (e.TryGetProperty(array, out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray()) Add(parent, element, Str(item, "name"));
    }

    private static void AddRating(XElement root, JsonElement e)
    {
        var value = Num(e, "vote_average", "0.0");
        if (value.Length == 0 || value == "0.0") return;
        root.Add(new XElement("ratings", new XElement("rating", new XAttribute("name", "themoviedb"), new XAttribute("max", "10"), new XAttribute("default", "true"),
            new XElement("value", value), new XElement("votes", Num(e, "vote_count", "0")))));
    }

    private static void AddArt(XElement root, JsonElement e)
    {
        var poster = Str(e, "poster_path");
        if (poster.Length > 0) root.Add(new XElement("thumb", new XAttribute("aspect", "poster"), ImageBase + "original" + poster));
        var back = Str(e, "backdrop_path");
        if (back.Length > 0) root.Add(new XElement("fanart", new XElement("thumb", ImageBase + "original" + back)));
    }

    private static void AddActors(XElement root, JsonElement credits, int max = 30)
    {
        if (!credits.TryGetProperty("cast", out var cast) || cast.ValueKind != JsonValueKind.Array) return;
        int order = 0;
        foreach (var c in cast.EnumerateArray())
        {
            if (order >= max) break;
            var actor = new XElement("actor", new XElement("name", Str(c, "name")));
            Add(actor, "role", Str(c, "character"));
            actor.Add(new XElement("order", order++));
            var profile = Str(c, "profile_path");
            if (profile.Length > 0) actor.Add(new XElement("thumb", ImageBase + "h632" + profile));
            root.Add(actor);
        }
    }

    private static void AddCrew(XElement root, JsonElement credits)
    {
        if (!credits.TryGetProperty("crew", out var crew) || crew.ValueKind != JsonValueKind.Array) return;
        foreach (var c in crew.EnumerateArray())
        {
            var job = Str(c, "job");
            if (job == "Director") Add(root, "director", Str(c, "name"));
            else if (job is "Writer" or "Screenplay" or "Story") Add(root, "credits", Str(c, "name"));
        }
    }

    // Elements both a movie and a TV show have: sort title, the user's own rating (0 = none), spoken languages (the original one first).
    private static void AddLanguages(XElement root, JsonElement e)
    {
        var original = Str(e, "original_language");
        var names = new List<(string Code, string Name)>();
        if (e.TryGetProperty("spoken_languages", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var l in list.EnumerateArray())
            {
                var name = Str(l, "english_name"); if (name.Length == 0) name = Str(l, "name");
                if (name.Length > 0) names.Add((Str(l, "iso_639_1"), name));
            }
        foreach (var (code, name) in names.OrderBy(n => n.Code == original ? 0 : 1)) Add(root, "language", name);
        if (names.Count == 0) Add(root, "language", original);
        Add(root, "originallanguage", original);
    }

    private static string? Rated(string? certification) => string.IsNullOrEmpty(certification) ? null : "Rated " + certification;

    private static XElement MovieNfo(JsonElement m)
    {
        var root = new XElement("movie");
        Add(root, "title", Str(m, "title"));
        var original = Str(m, "original_title");
        if (original != Str(m, "title")) Add(root, "originaltitle", original);
        Add(root, "sorttitle", Str(m, "title"));
        AddRating(root, m);
        root.Add(new XElement("userrating", 0));
        var release = Str(m, "release_date");
        Add(root, "year", YearOf(release));
        Add(root, "plot", Str(m, "overview"));
        Add(root, "outline", Str(m, "overview"));
        Add(root, "tagline", Str(m, "tagline"));
        var runtime = Num(m, "runtime", "0");
        if (runtime != "" && runtime != "0") Add(root, "runtime", runtime);
        AddArt(root, m);
        root.Add(new XElement("uniqueid", new XAttribute("type", "tmdb"), new XAttribute("default", "true"), m.GetProperty("id").GetInt32()));
        if (m.TryGetProperty("external_ids", out var ids)) Add(root, "id", Str(ids, "imdb_id"));
        if (m.TryGetProperty("external_ids", out ids) && Str(ids, "imdb_id").Length > 0)
            root.Add(new XElement("uniqueid", new XAttribute("type", "imdb"), Str(ids, "imdb_id")));
        Add(root, "premiered", release);
        AddNames(root, m, "genres", "genre");
        AddNames(root, m, "production_companies", "studio");
        AddNames(root, m, "production_countries", "country");
        AddLanguages(root, m);
        if (m.TryGetProperty("belongs_to_collection", out var set) && set.ValueKind == JsonValueKind.Object && Str(set, "name").Length > 0)
            root.Add(new XElement("set", new XElement("name", Str(set, "name"))));
        Add(root, "mpaa", Rated(MovieCertification(m)));
        if (m.TryGetProperty("credits", out var credits)) { AddCrew(root, credits); AddActors(root, credits); }
        return root;
    }

    private static string? MovieCertification(JsonElement m)
    {
        if (!m.TryGetProperty("release_dates", out var rd) || !rd.TryGetProperty("results", out var results)) return null;
        foreach (var country in results.EnumerateArray())
            if (Str(country, "iso_3166_1") == "US")
                foreach (var r in country.GetProperty("release_dates").EnumerateArray())
                    if (Str(r, "certification").Length > 0) return Str(r, "certification");
        return null;
    }

    private static XElement TvShowNfo(JsonElement s)
    {
        var root = new XElement("tvshow");
        Add(root, "title", Str(s, "name"));
        var original = Str(s, "original_name");
        if (original != Str(s, "name")) Add(root, "originaltitle", original);
        Add(root, "sorttitle", Str(s, "name"));
        AddRating(root, s);
        root.Add(new XElement("userrating", 0));
        var premiered = Str(s, "first_air_date");
        Add(root, "year", YearOf(premiered));
        Add(root, "plot", Str(s, "overview"));
        Add(root, "outline", Str(s, "overview"));
        if (s.TryGetProperty("episode_run_time", out var runs) && runs.ValueKind == JsonValueKind.Array && runs.GetArrayLength() > 0 && runs[0].ValueKind == JsonValueKind.Number)
            Add(root, "runtime", runs[0].GetInt32().ToString());
        AddArt(root, s);
        root.Add(new XElement("uniqueid", new XAttribute("type", "tmdb"), new XAttribute("default", "true"), s.GetProperty("id").GetInt32()));
        if (s.TryGetProperty("external_ids", out var ids) && Str(ids, "imdb_id").Length > 0)
            root.Add(new XElement("uniqueid", new XAttribute("type", "imdb"), Str(ids, "imdb_id")));
        if (s.TryGetProperty("external_ids", out ids) && ids.TryGetProperty("tvdb_id", out var tvdb) && tvdb.ValueKind == JsonValueKind.Number)
            root.Add(new XElement("uniqueid", new XAttribute("type", "tvdb"), tvdb.GetInt32()));
        Add(root, "premiered", premiered);
        Add(root, "status", Str(s, "status"));
        AddNames(root, s, "genres", "genre");
        AddNames(root, s, "production_countries", "country");
        AddLanguages(root, s);
        AddNames(root, s, "networks", "studio");
        AddNames(root, s, "created_by", "credits");
        if (s.TryGetProperty("content_ratings", out var cr) && cr.TryGetProperty("results", out var list))
            foreach (var c in list.EnumerateArray())
                if (Str(c, "iso_3166_1") == "US" && Str(c, "rating").Length > 0) { Add(root, "mpaa", Str(c, "rating")); break; }
        if (s.TryGetProperty("credits", out var credits)) AddActors(root, credits);
        return root;
    }

    private static XElement EpisodeNfo(JsonElement show, JsonElement e, (int Season, int Episode) ep)
    {
        var root = new XElement("episodedetails");
        Add(root, "title", Str(e, "name"));
        Add(root, "showtitle", Str(show, "name"));
        root.Add(new XElement("season", ep.Season), new XElement("episode", ep.Episode));
        AddRating(root, e);
        Add(root, "plot", Str(e, "overview"));
        var runtime = Num(e, "runtime", "0");
        if (runtime != "" && runtime != "0") Add(root, "runtime", runtime);
        var still = Str(e, "still_path");
        if (still.Length > 0) root.Add(new XElement("thumb", ImageBase + "original" + still));
        Add(root, "aired", Str(e, "air_date"));
        if (e.TryGetProperty("credits", out var credits)) { AddCrew(root, credits); AddActors(root, credits, 15); }
        if (e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
            root.Add(new XElement("uniqueid", new XAttribute("type", "tmdb"), new XAttribute("default", "true"), id.GetInt32()));
        return root;
    }

    /// <summary>Writes the text as a new hidden .nfo (UTF-8 with a byte order mark); an existing file is replaced only when <paramref name="replace"/>.</summary>
    public static void WriteNfo(string file, string xml, bool replace)
    {
        var temp = file + ".part" + Environment.ProcessId;
        try
        {
            File.WriteAllBytes(temp, new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(xml)).ToArray());
            if (File.Exists(file))
            {
                if (!replace) throw new InvalidOperationException($"\"{Path.GetFileName(file)}\" already exists.");
                File.SetAttributes(file, FileAttributes.Normal);   // a hidden or read-only file cannot be replaced
                File.Delete(file);
            }
            File.Move(temp, file);
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.Hidden);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"\"{Path.GetFileName(file)}\" cannot be written: access is denied (is the folder write-protected?).");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"\"{Path.GetFileName(file)}\" cannot be written: {ex.Message}");
        }
        finally { try { File.Delete(temp); } catch { } }
    }
}
