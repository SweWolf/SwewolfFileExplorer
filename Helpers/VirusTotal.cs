using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;

namespace SwewolfFileExplorer.Helpers;

/// <summary>What VirusTotal's scanners said about a file.</summary>
/// <param name="Malicious">Scanners that call it malicious.</param>
/// <param name="Suspicious">Scanners that call it suspicious.</param>
/// <param name="Harmless">Scanners that call it harmless.</param>
/// <param name="Undetected">Scanners that found nothing.</param>
/// <param name="Scanned">When the scan was made, if VirusTotal says.</param>
public sealed record VirusTotalReport(int Malicious, int Suspicious, int Harmless, int Undetected, DateTime? Scanned)
{
    /// <summary>The scanners that gave an answer (VirusTotal's "x / 70": time-outs and unsupported types are not counted).</summary>
    public int Total => Malicious + Suspicious + Harmless + Undetected;
}

/// <summary>A problem VirusTotal reported (bad key, too many requests ...), with a text the user can read.</summary>
public sealed class VirusTotalException : Exception
{
    public bool BadKey { get; }
    public VirusTotalException(string message, bool badKey = false) : base(message) => BadKey = badKey;
}

/// <summary>
/// "Send to VirusTotal" (2026-11-03). Step 1: the SHA-256 of the file is worked out here and VirusTotal's page for that hash is opened
/// in the browser (nothing uploaded, no key). Step 2 (the user's own free API key, stored with Windows DPAPI): the v3 API is asked
/// directly, and a file VirusTotal does not know can be uploaded (only after the user said yes) and its scan waited for.
/// </summary>
public static class VirusTotal
{
    /// <summary>The API's address; a test points it at a local server.</summary>
    public static string ApiBase { get; set; } = "https://www.virustotal.com/api/v3";

    /// <summary>The biggest file the plain upload takes; above it an upload address is asked for first.</summary>
    public const long PlainUploadLimit = 32L * 1024 * 1024;
    /// <summary>The biggest file VirusTotal takes at all.</summary>
    public const long UploadLimit = 650L * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };   // each call has its own cancel token
    private static ImageSource? _icon;

    /// <summary>
    /// VirusTotal's own favicon (https://www.virustotal.com/gui/images/favicon.svg, one path, 100 x 89), drawn as a vector so it is sharp
    /// at any size and DPI; the menu shows it in a 16 x 16 box.
    /// </summary>
    public static ImageSource? Icon
    {
        get
        {
            if (_icon != null) return _icon;
            var geometry = Geometry.Parse("F0 M45.292,44.5 L0,89 H100 V0 H0 L45.292,44.5 Z M90,80 H22 L57.987,44.8 L22,9 H90 V80 Z");   // F0 = even-odd
            var brush = new SolidColorBrush(Color.FromRgb(0x0b, 0x4d, 0xda));
            var drawing = new GeometryDrawing(brush, null, geometry);
            drawing.Freeze();
            var image = new DrawingImage(drawing);
            image.Freeze();
            return _icon = image;
        }
    }

    /// <summary>The page of VirusTotal for a file with this SHA-256 (lower case hex).</summary>
    public static string PageFor(string sha256) => "https://www.virustotal.com/gui/file/" + sha256;

    /// <summary>Where the user gets a free key.</summary>
    public const string KeyPage = "https://www.virustotal.com/gui/my-apikey";

    // ── The API key: encrypted with DPAPI for this Windows user, never plain text in settings.json ───────────────

    /// <summary>The user's API key, or null when none is saved (or it cannot be read on this account).</summary>
    public static string? GetApiKey()
    {
        var stored = AppSettingsStore.Current.VirusTotalApiKeyProtected;
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(bytes).Trim();
            return key.Length == 0 ? null : key;
        }
        catch { return null; }
    }

    /// <summary>Saves the key (encrypted), or removes it when empty. The caller saves the settings.</summary>
    public static void SetApiKey(string? key)
    {
        key = key?.Trim();
        AppSettingsStore.Current.VirusTotalApiKeyProtected = string.IsNullOrEmpty(key)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
    }

    /// <summary>A VirusTotal key is 64 letters and digits.</summary>
    public static bool IsValidKeyFormat(string key) => key.Length == 64 && key.All(char.IsAsciiLetterOrDigit);

    // ── The hash ──────────────────────────────────────────────────────────────────────────────────────────────────

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

    // ── The API (v3) ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What VirusTotal knows about the file with this hash: null when it has never seen it (or never scanned it).</summary>
    public static async Task<VirusTotalReport?> LookupAsync(string sha256, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/files/{sha256}");
        request.Headers.Add("x-apikey", apiKey);
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await CheckAsync(response, ct);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var attributes = json.RootElement.GetProperty("data").GetProperty("attributes");
        if (!attributes.TryGetProperty("last_analysis_stats", out var stats)) return null;
        DateTime? scanned = attributes.TryGetProperty("last_analysis_date", out var d) && d.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(d.GetInt64()).LocalDateTime : null;
        var report = ReadStats(stats, scanned);
        return report.Total == 0 ? null : report;
    }

    /// <summary>Uploads the file and returns the id of its analysis. A file over 32 MB first asks for a special upload address.</summary>
    public static async Task<string> UploadAsync(string path, string apiKey, CancellationToken ct)
    {
        long size = new FileInfo(path).Length;
        if (size > UploadLimit) throw new VirusTotalException("VirusTotal does not take files bigger than 650 MB.");

        string url = $"{ApiBase}/files";
        if (size > PlainUploadLimit)
        {
            using var ask = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/files/upload_url");
            ask.Headers.Add("x-apikey", apiKey);
            using var asked = await SendAsync(ask, ct);
            await CheckAsync(asked, ct);
            using var addr = JsonDocument.Parse(await asked.Content.ReadAsStringAsync(ct));
            url = addr.RootElement.GetProperty("data").GetString() ?? throw new VirusTotalException("VirusTotal gave no upload address.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true);
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", Path.GetFileName(path));
        using var post = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        post.Headers.Add("x-apikey", apiKey);
        using var response = await SendAsync(post, ct);
        await CheckAsync(response, ct);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("data").GetProperty("id").GetString()
               ?? throw new VirusTotalException("VirusTotal gave no analysis number.");
    }

    /// <summary>Waits until the analysis is done (asking every <paramref name="pollSeconds"/> seconds) and returns its result.</summary>
    public static async Task<VirusTotalReport> WaitForAnalysisAsync(string analysisId, string apiKey, CancellationToken ct,
                                                                    double pollSeconds = 10, double giveUpMinutes = 10)
    {
        var until = DateTime.UtcNow.AddMinutes(giveUpMinutes);
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/analyses/{analysisId}");
            request.Headers.Add("x-apikey", apiKey);
            using var response = await SendAsync(request, ct);
            await CheckAsync(response, ct);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var attributes = json.RootElement.GetProperty("data").GetProperty("attributes");
            if (attributes.GetProperty("status").GetString() == "completed")
                return ReadStats(attributes.GetProperty("stats"), DateTime.Now);

            if (DateTime.UtcNow > until)
                throw new VirusTotalException("The scan is taking too long. Try Send to VirusTotal again in a few minutes.");
            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), ct);
        }
    }

    private static VirusTotalReport ReadStats(JsonElement stats, DateTime? scanned)
    {
        int Get(string name) => stats.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        return new VirusTotalReport(Get("malicious"), Get("suspicious"), Get("harmless"), Get("undetected"), scanned);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try { return await Http.SendAsync(request, ct); }
        catch (HttpRequestException ex)
        {
            throw new VirusTotalException("VirusTotal could not be reached. Is the computer online?\n\n" + ex.Message);
        }
    }

    /// <summary>Turns an error answer into an exception with a readable text.</summary>
    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string detail = "";
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m)) detail = m.GetString() ?? "";
        }
        catch { }

        switch ((int)response.StatusCode)
        {
            case 401 or 403:
                throw new VirusTotalException("VirusTotal did not accept the API key. Check it in Tools > Settings.", badKey: true);
            case 429:
                throw new VirusTotalException("VirusTotal says there have been too many requests. A free key allows 4 a minute and 500 a day. Try again in a minute.");
            case 413:
                throw new VirusTotalException("The file is too big for VirusTotal.");
            default:
                throw new VirusTotalException($"VirusTotal answered {(int)response.StatusCode} {response.ReasonPhrase}." + (detail.Length > 0 ? "\n\n" + detail : ""));
        }
    }
}
