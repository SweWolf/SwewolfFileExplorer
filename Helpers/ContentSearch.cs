using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SwewolfFileExplorer.Helpers;

// ContentSearch: is a word (or several) INSIDE a text file? Built 2026-11-27 for the Search window's Content box (plan:
// D:\Dev\dev-notes\FileExplorer-content-search-plan.md). First step: text files only (tags, prompts and Office text come later; no raw scan of
// binary files; no PDF). Only FileSearch.Simplify / SplitTerms (the same word rules as the name search) is shared.
//
// How a file is read: the first 8 KB decide the encoding (byte order mark; UTF-16 without one by its zero bytes; the charset named in an XML
// declaration or an HTML meta tag; valid UTF-8; else ANSI) and whether it is text at all (a zero byte, or more than 1 % control characters, says
// no). The file is then read in chunks of 64K characters, never whole, and a window of the end of the last chunk goes first in the next one, so a
// word cut by a chunk border is still found. Each window is turned into plain text before it is searched: HTML / XML entities (&aring; &#229;
// &#xE5;) and, for .json, \u00e5 are decoded; letters written as a + combining ring become one character; with approximate matching the window
// goes through FileSearch.Simplify. A file stops being read as soon as the words are found.
//
// Words: all words must be in the file (anywhere); a capital OR on its own gives another way to match; "quoted words" are one word. * and ? are
// ordinary characters here (they are only wildcards in a name).
//
// History
//   2026-11-27  First version.

public enum ContentResult { NoMatch, Match, TooBig, NotText, NotOnThisComputer }

public sealed class ContentMatcher
{
    /// <summary>Files bigger than this are not read (reported as TooBig).</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    private const int HeadBytes = 8192;
    private const int ChunkChars = 65536;

    private readonly string[] _needles;       // every word once, ready to look for (normalized, simplified when approximate)
    private readonly int[][] _groups;         // for each OR-alternative: the indexes in _needles that must all be found
    private readonly bool _approximate;
    private readonly int _carry;

    private ContentMatcher(string[] needles, int[][] groups, bool approximate)
    {
        _needles = needles; _groups = groups; _approximate = approximate;
        _carry = Math.Clamp(needles.Max(n => n.Length) * 4 + 64, 512, 8192);
    }

    /// <summary>The matcher for a Content box text, or null when it holds nothing to look for.</summary>
    public static ContentMatcher? Create(string text, bool approximate)
    {
        var needles = new List<string>();
        var groups = new List<List<int>> { new() };
        foreach (var (term, quoted) in FileSearch.SplitTerms(text))
        {
            if (!quoted && term == "OR") { groups.Add(new()); continue; }
            string n = term.Normalize(NormalizationForm.FormC);
            if (approximate) n = FileSearch.Simplify(n);
            if (n.Length == 0) continue;
            int i = needles.IndexOf(n);
            if (i < 0) { needles.Add(n); i = needles.Count - 1; }
            if (!groups[^1].Contains(i)) groups[^1].Add(i);
        }
        groups.RemoveAll(g => g.Count == 0);
        if (groups.Count == 0) return null;
        return new ContentMatcher(needles.ToArray(), groups.Select(g => g.ToArray()).ToArray(), approximate);
    }

    /// <summary>Extensions of files that are searched when the user has not named a file type: the plain-text kinds.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "markdown", "log", "cs", "vb", "c", "cpp", "h", "hpp", "js", "jsx", "ts", "tsx", "json", "jsonc", "xml", "xaml", "html", "htm",
        "xhtml", "css", "scss", "csv", "tsv", "ini", "cfg", "conf", "config", "yml", "yaml", "toml", "sql", "srt", "sub", "vtt", "ass", "ssa", "nfo",
        "bat", "cmd", "ps1", "psm1", "py", "java", "kt", "go", "rs", "php", "rb", "sh", "lua", "tex", "svg", "url", "m3u", "m3u8", "pls", "cue", "lrc",
        "csproj", "vbproj", "sln", "props", "targets", "resx", "xsd", "xsl", "xslt", "plist", "reg", "inf", "diz", "sfv", "rss", "atom", "kml", "gpx",
        "opf", "ncx", "gitignore", "gitattributes", "editorconfig", "vbs", "wsf", "hta", "manifest"
    };

    public static bool IsKnownTextFile(string name) => TextExtensions.Contains(Path.GetExtension(name).TrimStart('.'));

    /// <summary>Extensions whose text has XML / HTML entities in it.</summary>
    private static readonly HashSet<string> MarkupExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "html", "htm", "xhtml", "xml", "xaml", "svg", "nfo", "rss", "atom", "xsd", "xsl", "xslt", "plist", "resx", "csproj", "vbproj", "props",
        "targets", "config", "manifest", "kml", "gpx", "opf", "ncx", "hta", "wsf"
    };

    private static readonly Regex JsonEscape = new(@"\\u([0-9a-fA-F]{4})", RegexOptions.Compiled);
    private static readonly Regex XmlCharset = new(@"encoding\s*=\s*[""']([\w\-]+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlCharset = new(@"charset\s*=\s*[""']?([\w\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Looks in the file. Never throws: a file that cannot be read (locked, gone, no access) is NoMatch.</summary>
    public ContentResult Check(string path, long length, FileAttributes attributes, CancellationToken ct)
    {
        // A cloud file that is not on the disk would be downloaded by reading it: left alone.
        if ((attributes & (FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0) return ContentResult.NotOnThisComputer;
        if (length == 0) return ContentResult.NoMatch;
        if (length > MaxFileBytes) return ContentResult.TooBig;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var head = new byte[HeadBytes];
            int n = stream.Read(head, 0, head.Length);
            var enc = DetectEncoding(head, n, out int bom);
            if (enc == null) return ContentResult.NotText;

            stream.Position = bom;
            using var reader = new StreamReader(stream, enc, detectEncodingFromByteOrderMarks: false, bufferSize: 65536);
            bool markup = MarkupExtensions.Contains(Path.GetExtension(path).TrimStart('.')) || LooksLikeMarkup(head, n, enc);
            bool json = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase);
            return Scan(reader, markup, json, ct) ? ContentResult.Match : ContentResult.NoMatch;
        }
        catch { return ContentResult.NoMatch; }
    }

    private bool Scan(StreamReader reader, bool markup, bool json, CancellationToken ct)
    {
        var found = new bool[_needles.Length];
        var buffer = new char[ChunkChars];
        string carry = "";
        while (!ct.IsCancellationRequested)
        {
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            string raw = carry.Length == 0 ? new string(buffer, 0, read) : carry + new string(buffer, 0, read);

            string w = raw;
            if (markup && w.Contains('&')) w = WebUtility.HtmlDecode(w);
            if (json && w.Contains("\\u")) w = JsonEscape.Replace(w, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            if (!IsAscii(w))
            {
                try { w = w.Normalize(NormalizationForm.FormC); } catch (ArgumentException) { }   // a broken surrogate: searched as it is
            }
            if (_approximate) w = FileSearch.Simplify(w);

            for (int i = 0; i < _needles.Length; i++)
                if (!found[i] && w.Contains(_needles[i], _approximate ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) found[i] = true;

            foreach (var g in _groups)
            {
                bool all = true;
                foreach (var i in g) if (!found[i]) { all = false; break; }
                if (all) return true;
            }
            carry = raw.Length > _carry ? raw[^_carry..] : raw;
        }
        return false;
    }

    private static bool IsAscii(string s)
    {
        foreach (var ch in s) if (ch > 127) return false;
        return true;
    }

    private static bool LooksLikeMarkup(byte[] head, int n, Encoding enc)
    {
        try
        {
            var s = enc.GetString(head, 0, Math.Min(n, 256)).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            return s.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || s.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>The encoding of a text file from its first bytes, or null when it is not text. <paramref name="bom"/> is the length of the byte order mark to skip.</summary>
    internal static Encoding? DetectEncoding(byte[] b, int n, out int bom)
    {
        bom = 0;
        if (n == 0) return null;
        if (n >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { bom = 3; return new UTF8Encoding(false); }
        if (n >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0) { bom = 4; return new UTF32Encoding(false, false); }
        if (n >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF) { bom = 4; return new UTF32Encoding(true, false); }
        if (n >= 2 && b[0] == 0xFF && b[1] == 0xFE) { bom = 2; return Encoding.Unicode; }
        if (n >= 2 && b[0] == 0xFE && b[1] == 0xFF) { bom = 2; return Encoding.BigEndianUnicode; }

        int zeros = 0, evenZeros = 0, oddZeros = 0, control = 0;
        for (int i = 0; i < n; i++)
        {
            byte x = b[i];
            if (x == 0) { zeros++; if ((i & 1) == 0) evenZeros++; else oddZeros++; }
            else if (x < 32 && x != 9 && x != 10 && x != 12 && x != 13 && x != 27) control++;
        }
        if (zeros > 0)
        {
            // UTF-16 with no byte order mark: the zero bytes sit on every second place.
            int half = n / 2;
            if (n >= 4 && oddZeros * 4 > half && evenZeros * 20 < half) return Encoding.Unicode;
            if (n >= 4 && evenZeros * 4 > half && oddZeros * 20 < half) return Encoding.BigEndianUnicode;
            return null;   // a zero byte in an 8-bit file: binary
        }
        if (control * 100 > n) return null;

        // The charset the file names itself (first 1 KB, plain letters only).
        var text = Encoding.Latin1.GetString(b, 0, Math.Min(n, 1024));
        var m = XmlCharset.Match(text);
        if (!m.Success) m = HtmlCharset.Match(text);
        if (m.Success)
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            if (name is "utf-8" or "utf8") return new UTF8Encoding(false);
            if (name is "iso-8859-1" or "latin1" or "latin-1" or "windows-1252" or "cp1252" or "us-ascii" or "ascii") return Encoding.Latin1;
        }

        // Valid UTF-8 (a cut at the end of the 8 KB is allowed), else ANSI.
        if (IsValidUtf8(b, n)) return new UTF8Encoding(false);
        return Encoding.Latin1;
    }

    private static bool IsValidUtf8(byte[] b, int n)
    {
        for (int cut = 0; cut <= 3 && cut < n; cut++)
        {
            try { new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b, 0, n - cut); return true; }
            catch (DecoderFallbackException) { if (n < HeadBytes) return false; }   // a whole short file must be valid; a cut head may lose its last bytes
        }
        return false;
    }
}
