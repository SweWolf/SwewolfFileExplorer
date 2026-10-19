using System.IO;
using System.Text;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Reads small text files whose encoding is not known (an .nfo from Kodi, Jellyfin or an old tool): UTF-16 and UTF-8 with a byte
/// order mark are recognized by it; without one the file is UTF-8 if it is valid UTF-8, otherwise ANSI (Latin-1: å ä ö).
/// <c>File.ReadAllText</c> alone would show an ANSI file's å ä ö as replacement characters.
/// </summary>
public static class TextFiles
{
    public static string ReadAllText(string path) => Decode(File.ReadAllBytes(path));

    public static string Decode(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);

        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(b); }
    }
}
