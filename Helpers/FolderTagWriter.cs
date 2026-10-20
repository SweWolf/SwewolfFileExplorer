using System.IO;
using System.Text;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Sets the tags of a folder where Windows and TeraTool keep them: the folder's own <c>desktop.ini</c>, in the group of the
/// "summary information" property set, as <c>Prop5=31,tag one; tag two</c> (see <see cref="FolderTagReader"/>, which reads it).
///
/// Written carefully, because a <c>desktop.ini</c> may hold other things (an icon, a localized name): only the one line changes, the
/// rest is kept as it was, with its line endings and its encoding (ANSI, UTF-8 or UTF-16, with or without a byte order mark). A
/// file that is not there is made, as UTF-16 with a byte order mark (what Windows writes) and with the Hidden and System attributes
/// every <c>desktop.ini</c> has; one that has no lines left after the tags were removed is deleted. The folder itself is not changed.
/// </summary>
public static class FolderTagWriter
{
    private const string Section = "{F29F85E0-4FF9-1068-AB91-08002B27B3D9}";
    private const string Key = "Prop5";

    private enum Kind { Utf16, Utf16BigEndian, Utf8Bom, Utf8, Ansi }

    /// <summary>
    /// Sets the tags of <paramref name="folder"/> (an empty text removes them). Returns false with a message when it cannot be done
    /// (a write-protected drive or folder, a file in use ...); nothing is changed then.
    /// </summary>
    public static bool TrySet(string folder, string tags, out string error)
    {
        error = "";
        try
        {
            if (!Directory.Exists(folder)) { error = "The folder is not on the disk."; return false; }

            // One line: line breaks in the text would break the file.
            tags = string.Join(" ", tags.Split('\r', '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();

            var file = Path.Combine(folder, "desktop.ini");
            bool exists = File.Exists(file);
            if (!exists && tags.Length == 0) return true;   // nothing to remove: no file is made

            string text = "";
            var kind = Kind.Utf16;   // a new file
            if (exists) (text, kind) = Decode(File.ReadAllBytes(file));

            var newText = Edit(text, tags);

            if (string.IsNullOrWhiteSpace(newText))
            {
                if (exists) { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
                return true;
            }

            // A desktop.ini in ANSI cannot hold characters outside Latin-1: it becomes UTF-16 then.
            if (kind == Kind.Ansi && newText.Any(c => c > 255)) kind = Kind.Utf16;

            var bytes = Encode(newText, kind);
            if (exists) File.SetAttributes(file, FileAttributes.Normal);   // a hidden + system file cannot be written as it is
            try { File.WriteAllBytes(file, bytes); }
            finally
            {
                try { File.SetAttributes(file, FileAttributes.Hidden | FileAttributes.System); } catch { /* the tags are saved all the same */ }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex is UnauthorizedAccessException ? "Access is denied (is the drive or the folder write-protected?)." : ex.Message;
            return false;
        }
    }

    /// <summary>The text of a desktop.ini with the tags set (for tests too); empty text when nothing is left in it.</summary>
    public static string Edit(string text, string tags)
    {
        string eol = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : "\r\n";
        var lines = text.Length == 0 ? new List<string>() : text.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);   // blank lines at the end

        // Where the section is, and where its Prop5 line is.
        int start = -1, end = lines.Count, prop = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim().TrimStart('﻿');
            if (line.Length > 0 && line[0] == '[')
            {
                if (start >= 0) { end = i; break; }
                if (line.Equals($"[{Section}]", StringComparison.OrdinalIgnoreCase)) start = i;
                continue;
            }
            if (start < 0) continue;

            int equals = line.IndexOf('=');
            if (equals > 0 && line[..equals].Trim().Equals(Key, StringComparison.OrdinalIgnoreCase) && prop < 0) prop = i;
        }

        var value = $"{Key}=31,{tags}";

        if (tags.Length > 0)
        {
            if (prop >= 0) lines[prop] = value;
            else if (start >= 0) lines.Insert(start + 1, value);
            else
            {
                if (lines.Count > 0) lines.Add("");
                lines.Add($"[{Section}]");
                lines.Add(value);
            }
        }
        else if (prop >= 0)
        {
            lines.RemoveAt(prop);
            end--;

            // The group has nothing left in it: it goes too (with the blank line before it).
            bool empty = true;
            for (int i = start + 1; i < end && i < lines.Count; i++) if (lines[i].Trim().Length > 0) { empty = false; break; }
            if (empty)
            {
                lines.RemoveRange(start, Math.Min(end, lines.Count) - start);
                if (start > 0 && start - 1 < lines.Count && lines[start - 1].Trim().Length == 0) lines.RemoveAt(start - 1);
            }
        }

        if (lines.All(l => l.Trim().Length == 0)) return "";
        return string.Join(eol, lines) + eol;
    }

    // ── Encodings ─────────────────────────────────────────────────────────────

    private static (string Text, Kind Kind) Decode(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (Encoding.Unicode.GetString(b, 2, b.Length - 2), Kind.Utf16);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2), Kind.Utf16BigEndian);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (Encoding.UTF8.GetString(b, 3, b.Length - 3), Kind.Utf8Bom);

        try { return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b), Kind.Utf8); }
        catch (DecoderFallbackException) { return (Encoding.Latin1.GetString(b), Kind.Ansi); }
    }

    private static byte[] Encode(string text, Kind kind) => kind switch
    {
        Kind.Utf16          => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray(),
        Kind.Utf16BigEndian => Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray(),
        Kind.Utf8Bom        => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray(),
        Kind.Utf8           => new UTF8Encoding(false).GetBytes(text),
        _                   => Encoding.Latin1.GetBytes(text),
    };
}
