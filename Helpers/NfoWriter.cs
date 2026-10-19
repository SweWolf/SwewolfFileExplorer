using System.IO;
using System.Text;
using System.Xml.Linq;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Changes the plot in a video's .nfo file (see <see cref="NfoPoster"/>, which reads it), or makes the .nfo file when there is none.
/// An existing file is changed carefully, as the user's Jellyfin / Kodi files are worth keeping: only the <c>&lt;plot&gt;</c> element
/// changes (everything else stays as it was: the other elements, the spaces and line breaks, the byte order mark, the line
/// ending style, the attributes of the file), the file is written whole to a temporary file and swapped in, and a file that is
/// not valid XML is refused, never repaired. Every failure is an <see cref="InvalidOperationException"/> with a message to show.
/// </summary>
public static class NfoWriter
{
    /// <summary>The .nfo file that holds (or will hold) the plot of the video: an existing one, else a new &lt;name&gt;.nfo.</summary>
    public static string TargetFile(string mediaPath, out bool exists)
    {
        foreach (var nfo in NfoPoster.NfoFilesOf(mediaPath))
        {
            if (File.Exists(nfo)) { exists = true; return nfo; }
        }
        exists = false;
        return Path.Combine(Path.GetDirectoryName(mediaPath)!, Path.GetFileNameWithoutExtension(mediaPath) + ".nfo");
    }

    /// <summary>The plot as it is now (what the edit box starts with); empty if there is none.</summary>
    public static string CurrentPlot(string mediaPath) => NfoPoster.Plot(mediaPath) ?? "";

    /// <summary>Sets the plot of the video; an empty text removes it. Makes the .nfo file if there is none.</summary>
    public static void SetPlot(string mediaPath, string plot)
    {
        plot = (plot ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

        var file = TargetFile(mediaPath, out bool exists);
        try
        {
            if (!exists) CreateNew(file, plot);
            else Change(file, plot);
        }
        catch (InvalidOperationException) { throw; }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"\"{Path.GetFileName(file)}\" cannot be written: access is denied (is the file or the folder write-protected?).");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"\"{Path.GetFileName(file)}\" cannot be written: {ex.Message}");
        }
    }

    // A new file: a movie with only its plot (nothing is made up), UTF-8 with a byte order mark and Windows line endings.
    private static void CreateNew(string file, string plot)
    {
        if (plot.Length == 0) return;   // nothing to say: no file is made for it

        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<movie>\n  "
                + new XElement("plot", plot).ToString(SaveOptions.DisableFormatting)
                + "\n</movie>\n";
        xml = xml.Replace("\r\n", "\n").Replace("\n", "\r\n");

        using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);   // never replaces a file
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(xml)).ToArray();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void Change(string file, string plot)
    {
        var original = File.ReadAllBytes(file);
        var (text, encoding, kind) = Decode(original);

        // The file may have something above the XML (a web address line, as Kodi allows): it is kept as it is.
        int start = text.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
        if (start < 0) start = text.IndexOf('<');
        if (start < 0) throw new InvalidOperationException($"\"{Path.GetFileName(file)}\" is not an XML file, so the plot cannot be changed in it.");

        XDocument doc;
        try { doc = XDocument.Parse(text[start..], LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidOperationException($"\"{Path.GetFileName(file)}\" is not valid XML ({ex.Message.TrimEnd('.')}), so the plot cannot be changed in it safely.");
        }
        var root = doc.Root ?? throw new InvalidOperationException($"\"{Path.GetFileName(file)}\" has no content.");

        var element = root.Elements("plot").FirstOrDefault();
        if (plot.Length == 0)
        {
            if (element == null) return;   // nothing to remove
            if (element.PreviousNode is XText space && string.IsNullOrWhiteSpace(space.Value)) space.Remove();
            element.Remove();
        }
        else if (element != null)
        {
            element.Value = plot;
        }
        else
        {
            element = new XElement("plot", plot);
            root.AddFirst(element);
            element.AddBeforeSelf(new XText("\n  "));   // the old first space now follows it, so it stands on a line of its own
        }

        // Back to text: the part above the XML, the declaration, the document.
        bool crlf = text.Contains("\r\n");
        string Build(bool declareUtf8)
        {
            var sb = new StringBuilder(text[..start]);
            if (doc.Declaration != null)
            {
                var declaration = new XDeclaration(doc.Declaration);
                if (declareUtf8) declaration.Encoding = "utf-8";
                sb.Append(declaration).Append('\n');
            }
            sb.Append(root.ToString(SaveOptions.DisableFormatting));
            if (text.EndsWith('\n')) sb.Append('\n');

            var s = sb.ToString();
            return crlf ? s.Replace("\r\n", "\n").Replace("\n", "\r\n") : s;   // the file used Windows line endings: so does it now
        }

        byte[] bytes;
        if (kind == EncodingKind.Other && !FitsLatin1(Build(false)))
            bytes = new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(Build(true))).ToArray();   // the file becomes UTF-8
        else
            bytes = Encode(Build(false), encoding, kind);

        Swap(file, bytes);
    }

    private static bool FitsLatin1(string text) => text.All(c => c <= 0xFF);

    private enum EncodingKind { Utf8Bom, Utf8, Utf16LE, Utf16BE, Other }

    private static (string Text, Encoding Encoding, EncodingKind Kind) Decode(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (Encoding.Unicode.GetString(b, 2, b.Length - 2), Encoding.Unicode, EncodingKind.Utf16LE);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2), Encoding.BigEndianUnicode, EncodingKind.Utf16BE);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (Encoding.UTF8.GetString(b, 3, b.Length - 3), Encoding.UTF8, EncodingKind.Utf8Bom);

        try { return (new UTF8Encoding(false, true).GetString(b), Encoding.UTF8, EncodingKind.Utf8); }
        catch (DecoderFallbackException) { return (Encoding.Latin1.GetString(b), Encoding.Latin1, EncodingKind.Other); }
    }

    // The same encoding as before, with the same byte order mark (or none). An old ANSI file is kept ANSI if the new text fits
    // in it (the caller checks, and otherwise makes it UTF-8 with the declaration changed to say so, as characters would be lost).
    private static byte[] Encode(string text, Encoding original, EncodingKind kind) => kind switch
    {
        EncodingKind.Utf16LE => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray(),
        EncodingKind.Utf16BE => Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray(),
        EncodingKind.Utf8Bom => new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(text)).ToArray(),
        EncodingKind.Utf8    => new UTF8Encoding(false).GetBytes(text),
        _                    => Encoding.Latin1.GetBytes(text),
    };

    // Writes the new content to a temporary file next to the old one and swaps it in (File.Replace keeps the old file's
    // attributes, hidden or not); if that is not possible the file is written in place.
    private static void Swap(string file, byte[] bytes)
    {
        var temp = file + ".tmp" + Environment.ProcessId;
        try
        {
            File.WriteAllBytes(temp, bytes);
            try { File.Replace(temp, file, null); }
            catch (IOException) { File.Copy(temp, file, overwrite: true); File.Delete(temp); }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
