using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// "New File from Clipboard": the text on the clipboard becomes a .txt file (UTF-8 with a byte order mark), a picture becomes a .png.
/// A picture wins when both are there (a picture copied from a web page also carries its address as text).
/// </summary>
public static class ClipboardFile
{
    public enum Kind { None, Image, Text }

    /// <summary>What the clipboard holds that a file can be made of. The clipboard may be held by another program for a moment: tried a few times.</summary>
    public static Kind Detect()
    {
        for (int i = 0; i < 4; i++)
        {
            try
            {
                if (Clipboard.ContainsImage() || Clipboard.ContainsData("PNG")) return Kind.Image;
                if (Clipboard.ContainsText()) return Kind.Text;
                return Kind.None;
            }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(30); }
        }
        return Kind.None;
    }

    /// <summary>The suggested name: Clipboard 2026-10-26 14.30.05.txt (the seconds make it practically unique; a taken name gets (2), (3) ...).</summary>
    public static string SuggestName(string folder, Kind kind, DateTime? now = null)
    {
        var ext   = kind == Kind.Image ? ".png" : ".txt";
        var stem  = "Clipboard " + (now ?? DateTime.Now).ToString("yyyy-MM-dd HH.mm.ss");
        var name  = stem + ext;
        for (int i = 2; File.Exists(Path.Combine(folder, name)) || Directory.Exists(Path.Combine(folder, name)); i++)
            name = $"{stem} ({i}){ext}";
        return name;
    }

    /// <summary>
    /// Makes the file in <paramref name="folder"/> and returns its path; null with <paramref name="error"/> set if the clipboard holds
    /// nothing usable or the file cannot be written. Never replaces a file (CreateNew).
    /// </summary>
    public static string? Create(string folder, out string error)
    {
        error = "";
        var kind = Detect();
        if (kind == Kind.None)
        {
            error = "The clipboard holds no text and no picture.";
            return null;
        }

        try
        {
            byte[] bytes = kind == Kind.Image ? GetPngBytes() : new UTF8Encoding(true).GetPreamble()
                .Concat(Encoding.UTF8.GetBytes(Clipboard.GetText(TextDataFormat.UnicodeText))).ToArray();

            var path = Path.Combine(folder, SuggestName(folder, kind));
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                fs.Write(bytes, 0, bytes.Length);
            return path;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>The PNG the program offered itself (keeps transparency and the original quality), else the bitmap encoded as PNG.</summary>
    private static byte[] GetPngBytes()
    {
        if (Clipboard.GetData("PNG") is Stream png)
        {
            using var ms = new MemoryStream();
            png.CopyTo(ms);
            if (ms.Length > 8) return ms.ToArray();
        }

        var image = Clipboard.GetImage() ?? throw new InvalidOperationException("The picture on the clipboard could not be read.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
