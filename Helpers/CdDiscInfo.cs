// CdDiscInfo.cs: the artist, title, album and cover of the tracks of an audio CD, for the File Explorer (and, copied, SweWolf Media Player).
//
// A CD track is a 44-byte .cda stub in the drive's root (Track01.cda ...): no tags, no picture. Windows gives each disc a volume serial number
// made from its track layout, which is the key here ("<disc id>", e.g. 0129A09F). The stubs hold the table of contents (track number, start,
// length), from which the MusicBrainz disc id is worked out; MusicBrainz names the release (titles, artists) and the Cover Art Archive has
// the front picture. Only the disc id goes out, nothing about the user. What was found is kept on this computer, so a disc is looked up once:
//   %LocalAppData%\SweWolfSoftware\CdInfo\<disc id>.json   the tracks   (a cache the program can recreate, hence Local, not Roaming)
//   %LocalAppData%\SweWolfSoftware\CdInfo\<disc id>.jpg    the front cover
// A disc that is not complete (not in MusicBrainz, or no cover) is asked for again when the user uses it again: after every start of the program, or
// after a day if the program keeps running (10 minutes after a failed attempt). Nothing is kept about unknown discs, there is no list that is checked.
// The folder is shared by the apps of the family: whichever sees a disc first fills it for the others. The Media Player's older folder
// (SwewolfMediaPlayer\CdInfo, titles from LibVLC's CDDB lookup) is still read, only when the shared folder has nothing for the disc.
//
// The lookup runs in the background, once per disc per run (a failure is tried again after 10 minutes), and quietly: offline is no error.
// When it has found something, <see cref="Updated"/> is raised (on a background thread) with the disc id, so the lists can read their tags again.
// A future setting "Look Up Audio CD Information" (on by default) connects to <see cref="Enabled"/>.
// Plan and decisions: D:\Dev\dev-notes\AudioCD-titles-and-covers-shared-plan.md.
//
// History:
//   2026-11-14  First version for the File Explorer (MusicBrainz titles and artists with inc=recordings+artist-credits, cover, shared folder).
//   2026-11-14  No .nodisc / .nocover files and no 30-day wait: an incomplete disc is tried again after every start and after a day (NextTry, in memory only).
//
// To copy to another app: change the namespace, the User-Agent text and, if wanted, nothing else (self-contained; the App name below is only used for the User-Agent).

using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SwewolfFileExplorer.Helpers;

/// <summary>What is known about one track of a disc (the track number is the key in <see cref="CdDisc.Tracks"/>).</summary>
public sealed class CdTrackInfo
{
    public string? Artist { get; set; }
    public string? Title { get; set; }
    public string? Album { get; set; }
}

/// <summary>What is kept per disc: one entry per track ("1", "2" ...), the MusicBrainz release it came from.</summary>
public sealed class CdDisc
{
    public Dictionary<string, CdTrackInfo> Tracks { get; set; } = new();
    public string? AlbumArtist { get; set; }
    public string? ReleaseId { get; set; }
    public string? MusicBrainzDiscId { get; set; }
    public string? Source { get; set; }
}

public static class CdDiscInfo
{
    /// <summary>Switches the online lookup off (a future Settings box, "Look Up Audio CD Information", on by default). What is in the cache is still shown.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Raised on a background thread when a lookup has stored something new for a disc (its disc id).</summary>
    public static event Action<string>? Updated;

    private const string AppName = "SweWolfFileExplorer";   // only for the User-Agent that MusicBrainz asks for
    private const int BitrateKbps = 1411;                    // CD audio: 44.1 kHz, 16 bit, stereo

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ── Is it a CD track, which disc ─────────────────────────────────────

    /// <summary>A .cda file on a CD / DVD drive (a .cda copied to a hard disk is only a stub of no use).</summary>
    public static bool IsCdTrack(string path)
    {
        try
        {
            if (!string.Equals(Path.GetExtension(path), ".cda", StringComparison.OrdinalIgnoreCase)) return false;
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.CDRom;
        }
        catch { return false; }
    }

    /// <summary>The folder shown is the root of a CD drive that holds audio tracks (.cda files): the list then uses the audio CD template
    /// (Artist, Title, Track Number, sorted by track number), whatever the user chose before. Reads the disc's root: not for the UI thread's hot paths.</summary>
    public static bool IsAudioCdFolder(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || !string.Equals(full.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return false;
            return new DriveInfo(root).DriveType == DriveType.CDRom && Directory.EnumerateFiles(root, "*.cda").Any();
        }
        catch { return false; }
    }

    /// <summary>
    /// An audio CD whose titles are known (found by the lookup, or kept by an earlier one): only then the list uses the audio CD template. A disc
    /// that is not known (offline, not in MusicBrainz, the lookup not finished yet) is shown like any folder, with the file names; the template
    /// takes over by itself when the lookup is done (<see cref="Updated"/>).
    /// </summary>
    public static bool IsKnownAudioCd(string? path) =>
        IsAudioCdFolder(path) && DiscId(path!) is { } id && HasTitles(id);

    /// <summary>True if the kept information of the disc has a real title for at least one track (not only "Track 5").</summary>
    public static bool HasTitles(string discId) =>
        Load(discId)?.Tracks.Values.Any(t => !string.IsNullOrWhiteSpace(t.Title) && !IsGenericTitle(t.Title)) == true;

    private static bool IsGenericTitle(string title) =>
        System.Text.RegularExpressions.Regex.IsMatch(title.Trim(), @"^(Track|Sp.r)\s*\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string root, char[]? name, int nameSize, out uint serial, out uint maxComponent,
        out uint flags, char[]? fileSystem, int fileSystemSize);

    /// <summary>The disc id: the volume serial number Windows makes from the track layout (null if the drive cannot be read).</summary>
    public static string? DiscId(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
            return GetVolumeInformationW(root, null, 0, out uint serial, out _, out _, null, 0) && serial != 0 ? serial.ToString("X8") : null;
        }
        catch { return null; }
    }

    // ── The stub: track number and length ────────────────────────────────

    private static (int Number, int Start, int Length)? ReadStub(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var b = new byte[44];
            int read = 0;
            while (read < b.Length) { int n = stream.Read(b, read, b.Length - read); if (n <= 0) break; read += n; }
            if (read < 36) return null;
            return (BitConverter.ToUInt16(b, 22), BitConverter.ToInt32(b, 28), BitConverter.ToInt32(b, 32));
        }
        catch { return null; }
    }

    /// <summary>The track number of a .cda file: from the stub, else from the name (Track07.cda, Spår 07.cda).</summary>
    public static int TrackNumber(string path)
    {
        if (ReadStub(path) is { Number: >= 1 and <= 99 } stub) return stub.Number;
        var digits = new string(Path.GetFileNameWithoutExtension(path).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    // ── The cache ────────────────────────────────────────────────────────

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SweWolfSoftware", "CdInfo");

    private static string OldPlayerFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SweWolfSoftware", "SwewolfMediaPlayer", "CdInfo");

    private static readonly ConcurrentDictionary<string, CdDisc?> Loaded = new();

    /// <summary>The kept titles of a disc: the shared folder, else the Media Player's older folder; null if neither has them.</summary>
    public static CdDisc? Load(string discId)
    {
        if (Loaded.TryGetValue(discId, out var cached)) return cached;
        CdDisc? disc = null;
        foreach (var folder in new[] { Folder, OldPlayerFolder })
        {
            try
            {
                var file = Path.Combine(folder, discId + ".json");
                if (!File.Exists(file)) continue;
                disc = JsonSerializer.Deserialize<CdDisc>(File.ReadAllText(file));
                if (disc != null) break;
            }
            catch { /* damaged: the next place, or a new lookup */ }
        }
        if (disc == null) return null;   // not kept: a later lookup may fill it in, so "nothing" is not remembered here
        Loaded[discId] = disc;
        return disc;
    }

    /// <summary>The front cover kept for a disc (the shared folder, else the Media Player's older folder); null if there is none.</summary>
    public static string? CoverFile(string discId)
    {
        foreach (var folder in new[] { Folder, OldPlayerFolder })
        {
            var file = Path.Combine(folder, discId + ".jpg");
            if (File.Exists(file)) return file;
        }
        return null;
    }

    // ── What the lists use ───────────────────────────────────────────────

    /// <summary>
    /// Artist, title, track number, length and bitrate of a CD track for the list's columns and the details. The title and artist are empty
    /// until the disc is known (the lookup is started in the background and <see cref="Updated"/> says when it is done).
    /// </summary>
    public static AudioTags? ReadTags(string path)
    {
        if (!IsCdTrack(path)) return null;

        var stub   = ReadStub(path);
        int number = stub is { Number: >= 1 and <= 99 } s ? s.Number : TrackNumber(path);
        var length = stub is { } st && st.Length > 0 ? TimeSpan.FromSeconds(st.Length / 75.0) : default;   // 75 sectors a second

        string artist = "", title = "";
        var discId = DiscId(path);
        if (discId != null)
        {
            if (Load(discId)?.Tracks.TryGetValue(number.ToString(), out var info) == true)
            {
                artist = info.Artist?.Trim() ?? "";
                title  = info.Title?.Trim() ?? "";
                // CDDB (the Media Player's older lookup) names a track "Track 5" when it knows no title: that is not a title.
                if (IsGenericTitle(title)) title = "";
            }
            RequestLookup(path);   // only does something while the shared folder has nothing for the disc
        }
        return new AudioTags(artist, title, (uint)number, length, BitrateKbps);
    }

    /// <summary>
    /// The name of the disc in a CD drive, "Album Artist - Album" ("Ava Max - Heaven & Hell"), for the drive's node in the tree and its line
    /// on This PC; null when the drive holds no audio CD or the disc is not known (yet: a lookup is started, and <see cref="Updated"/> says when
    /// it is done). Without an album artist (an older cache) the artist the tracks share is used, else "Various Artists".
    /// </summary>
    public static string? DriveTitle(string root)
    {
        try
        {
            if (new DriveInfo(root).DriveType != DriveType.CDRom || !Directory.EnumerateFiles(root, "*.cda").Any()) return null;

            var discId = DiscId(root);
            if (discId == null) return null;
            RequestLookup(root);   // does nothing when the shared folder has the disc already

            var disc = Load(discId);
            var album = disc?.Tracks.Values.Select(t => t.Album?.Trim()).FirstOrDefault(a => !string.IsNullOrEmpty(a));
            if (disc == null || string.IsNullOrEmpty(album)) return null;

            var artist = disc.AlbumArtist?.Trim();
            if (string.IsNullOrEmpty(artist))
            {
                var artists = disc.Tracks.Values.Select(t => t.Artist?.Trim()).Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                artist = artists.Count == 1 ? artists[0] : artists.Count > 1 ? "Various Artists" : null;
            }
            return string.IsNullOrEmpty(artist) ? album : $"{artist} - {album}";
        }
        catch { return null; }
    }

    /// <summary>The front cover of the disc a CD track is on, as the bytes of a picture; null if none is known (a lookup is started if the disc is new).</summary>
    public static byte[]? ReadCover(string path)
    {
        if (!IsCdTrack(path)) return null;
        var discId = DiscId(path);
        if (discId == null) return null;
        RequestLookup(path);
        try { return CoverFile(discId) is { } file ? File.ReadAllBytes(file) : null; }
        catch { return null; }
    }

    // ── The online lookup ────────────────────────────────────────────────

    /// <summary>When a disc may be looked up again (this run only: after a restart every disc that is not complete is tried again).</summary>
    private static readonly ConcurrentDictionary<string, DateTime> NextTry = new();
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppName}/1.0 (+https://github.com/SweWolf/{AppName})");   // MusicBrainz asks for a name, no personal details
        return client;
    }

    /// <summary>
    /// Starts the lookup of the disc a CD track is on, in the background, unless the setting is off or the shared folder already has the
    /// titles and the cover. A disc that is not complete (MusicBrainz does not know it, or has no cover) is looked up again whenever the user
    /// uses it again: once after every start of the program, and again after a day if the program keeps running (a failed attempt, such as
    /// being offline, after 10 minutes). Nothing is remembered between runs and there is no list of unknown discs. One lookup at a time
    /// (MusicBrainz allows about one request a second).
    /// </summary>
    public static void RequestLookup(string path)
    {
        if (!Enabled) return;
        var discId = DiscId(path);
        if (discId == null) return;
        if (File.Exists(Path.Combine(Folder, discId + ".json")) && File.Exists(Path.Combine(Folder, discId + ".jpg"))) return;   // complete

        var now = DateTime.UtcNow;
        if (NextTry.TryGetValue(discId, out var next) && now < next) return;
        NextTry[discId] = now.AddMinutes(10);   // a failure (offline, 503 ...): tried again after 10 minutes; a clear answer sets a day below

        var root = Path.GetPathRoot(Path.GetFullPath(path))!;
        _ = Task.Run(() => LookupAsync(root, discId));
    }

    private static async Task LookupAsync(string root, string discId)
    {
        await OneAtATime.WaitAsync();
        try
        {
            var mbId = MusicBrainzDiscId(root);
            if (mbId == null) return;

            using var response = await Http.GetAsync($"https://musicbrainz.org/ws/2/discid/{mbId}?fmt=json&inc=recordings+artist-credits");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                NextTry[discId] = DateTime.UtcNow.AddDays(1);   // MusicBrainz does not know this disc: asked again after a restart, or after a day
                return;
            }
            if (!response.IsSuccessStatusCode) return;   // 503 (too many requests) and the like: tried again after 10 minutes

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) return;

            // The first release (of at most five) that has the disc among its media and a front cover, else the first that has the disc.
            JsonElement? chosen = null, chosenMedium = null;
            bool chosenHasCover = false;
            foreach (var release in releases.EnumerateArray().Take(5))
            {
                var medium = FindMedium(release, mbId);
                if (medium == null) continue;
                bool cover = release.TryGetProperty("cover-art-archive", out var archive) && archive.TryGetProperty("front", out var front) && front.ValueKind == JsonValueKind.True;
                if (chosen == null || (cover && !chosenHasCover)) { chosen = release; chosenMedium = medium; chosenHasCover = cover; }
                if (cover) break;
            }
            if (chosen is not { } rel || chosenMedium is not { } medium2) return;

            var disc = new CdDisc { AlbumArtist = ArtistOf(rel), MusicBrainzDiscId = mbId, ReleaseId = rel.TryGetProperty("id", out var rid) ? rid.GetString() : null, Source = "MusicBrainz" };
            var album = rel.TryGetProperty("title", out var at) ? at.GetString() : null;
            if (medium2.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
            {
                foreach (var track in tracks.EnumerateArray())
                {
                    int position = track.TryGetProperty("position", out var p) && p.TryGetInt32(out var pn) ? pn : 0;
                    if (position < 1) continue;
                    disc.Tracks[position.ToString()] = new CdTrackInfo
                    {
                        Title  = track.TryGetProperty("title", out var tt) ? tt.GetString() : null,
                        Artist = ArtistOf(track) ?? ArtistOf(rel),
                        Album  = album,
                    };
                }
            }

            Directory.CreateDirectory(Folder);

            // The cover first, so a list that is told about the titles can show the picture at once.
            if (chosenHasCover && disc.ReleaseId != null)
            {
                try
                {
                    var picture = await Http.GetByteArrayAsync($"https://coverartarchive.org/release/{disc.ReleaseId}/front-500");
                    File.WriteAllBytes(Path.Combine(Folder, discId + ".jpg"), picture);
                }
                catch { /* no picture this time */ }
            }
            else
                NextTry[discId] = DateTime.UtcNow.AddDays(1);   // the disc is known but has no cover (yet): asked again after a restart, or after a day

            var target = Path.Combine(Folder, discId + ".json");
            var temp = target + ".part";
            File.WriteAllText(temp, JsonSerializer.Serialize(disc, Json));
            File.Move(temp, target, overwrite: true);
            Loaded.TryRemove(discId, out _);
            Updated?.Invoke(discId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Offline, too slow, a server problem or a disk problem: nothing is shown, no message, and it is tried again later.
        }
        catch (Exception ex)
        {
            // A surprise (the answer looked different from what was expected): once in the log, so it can be looked at (Help > Open Log File).
            try { LogFiles.AppendError($"Audio CD lookup failed: {ex.Message}"); } catch { }
        }
        finally { OneAtATime.Release(); }
    }

    private static JsonElement? FindMedium(JsonElement release, string mbDiscId)
    {
        if (!release.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array) return null;
        foreach (var medium in media.EnumerateArray())
        {
            if (!medium.TryGetProperty("discs", out var discs) || discs.ValueKind != JsonValueKind.Array) continue;
            foreach (var disc in discs.EnumerateArray())
                if (disc.TryGetProperty("id", out var id) && id.GetString() == mbDiscId) return medium;
        }
        return null;
    }

    /// <summary>The artist credit written out ("Ava Max", "A feat. B": the names with their join phrases).</summary>
    private static string? ArtistOf(JsonElement element)
    {
        if (!element.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array) return null;
        var text = string.Concat(credits.EnumerateArray().Select(c =>
            (c.TryGetProperty("name", out var n) ? n.GetString() : "") + (c.TryGetProperty("joinphrase", out var j) ? j.GetString() : ""))).Trim();
        return text.Length == 0 ? null : text;
    }

    // ── The MusicBrainz disc id ──────────────────────────────────────────

    /// <summary>
    /// The MusicBrainz disc id of the disc in a drive, worked out from the .cda stubs: first and last track and the offset of every
    /// track (start + 150, the lead-in) with the lead-out in place 0, as a SHA-1 in MusicBrainz's base64. A disc with a data track too
    /// (mixed mode) would need the real table of contents: not handled. Null if the stubs cannot be read.
    /// </summary>
    public static string? MusicBrainzDiscId(string root)
    {
        try
        {
            var tracks = new List<(int Number, int Start, int Length)>();
            foreach (var file in Directory.GetFiles(root, "*.cda"))
                if (ReadStub(file) is { } stub) tracks.Add(stub);
            if (tracks.Count == 0) return null;

            tracks.Sort((a, b) => a.Number.CompareTo(b.Number));
            var offsets = new int[100];
            foreach (var t in tracks.Where(t => t.Number is >= 1 and <= 99)) offsets[t.Number] = t.Start + 150;
            var last = tracks[^1];
            offsets[0] = last.Start + last.Length + 150;

            var text = new System.Text.StringBuilder();
            text.Append(tracks[0].Number.ToString("X2")).Append(last.Number.ToString("X2"));
            foreach (var offset in offsets) text.Append(offset.ToString("X8"));
            var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.ASCII.GetBytes(text.ToString()));
            return Convert.ToBase64String(hash).Replace('+', '.').Replace('/', '_').Replace('=', '-');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
