using System.IO;
using System.Text;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The tags of a folder, read from the folder's own <c>desktop.ini</c>, where Windows keeps the "Tags" (keywords) of a folder:
/// <code>
/// [{F29F85E0-4FF9-1068-AB91-08002B27B3D9}]
/// Prop5=31,tag one; tag two
/// </code>
/// (the group is the "summary information" property set, Prop5 is its Keywords, 31 says "text"). TeraTool's "Set Tags" wrote the
/// same place, so the tags it made are found. <b>Read only</b>: nothing is ever written and no file is created. Windows writes
/// <c>desktop.ini</c> as ANSI, UTF-8 or UTF-16 depending on the characters, so all three are understood.
/// </summary>
public static class FolderTagReader
{
    private const string Section = "{F29F85E0-4FF9-1068-AB91-08002B27B3D9}";
    private const string Key = "Prop5";

    /// <summary>The tags of the folder as written (words separated as the user typed them), or null if it has none or cannot be read.</summary>
    public static string? Read(string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(folder)) return null;

            var file = Path.Combine(folder, "desktop.ini");
            if (!File.Exists(file)) return null;   // also false for a folder inside an archive

            var info = new FileInfo(file);
            if (info.Length is 0 or > 256 * 1024) return null;   // a desktop.ini is tiny

            return Parse(Decode(File.ReadAllBytes(file)));
        }
        catch
        {
            return null;   // locked, no access, gone meanwhile
        }
    }

    /// <summary>The Keywords value of the text of a desktop.ini (for tests too); null if there is none.</summary>
    public static string? Parse(string text)
    {
        bool inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] == ';') continue;

            if (line[0] == '[')
            {
                inSection = line.TrimEnd().Equals($"[{Section}]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;

            int equals = line.IndexOf('=');
            if (equals <= 0 || !line[..equals].Trim().Equals(Key, StringComparison.OrdinalIgnoreCase)) continue;

            var value = line[(equals + 1)..].Trim();

            // "31,text": a number (the kind of value; 31 = text) and a comma come first.
            int comma = value.IndexOf(',');
            if (comma > 0 && value[..comma].All(char.IsDigit)) value = value[(comma + 1)..];

            value = value.Trim();
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    // UTF-16 and UTF-8 files start with a byte order mark; without one it is UTF-8 if it is valid UTF-8, else ANSI (Latin-1: å ä ö).
    private static string Decode(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);

        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(b); }
    }
}
