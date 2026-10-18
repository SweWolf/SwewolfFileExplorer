using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// A picture dragged out of a web browser (Chrome, Edge, Firefox) and dropped on a folder: it becomes a file there.
///
/// What the browser puts on the drag: a "virtual file" (the name, in <c>FileGroupDescriptorW</c>, and the bytes, in <c>FileContents</c>:
/// read through the raw COM data object, as WPF cannot) and the address of the picture (text / <c>UniformResourceLocator</c>). The bytes
/// are used when the browser gave them (the picture exactly as it was); otherwise the picture is downloaded from its address.
/// The data object is only valid while the drop is handled, so <see cref="Read"/> takes everything out at once; <see cref="SaveAsync"/>
/// may then take its time. A file that is already there is never overwritten: the new one gets " (2)", " (3)" ... in its name.
/// </summary>
public static class DroppedImage
{
    /// <summary>What was taken from the drop: a name, and the picture itself or where to get it.</summary>
    public sealed record Payload(string Name, byte[]? Bytes, string? Url);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".webp", ".bmp", ".avif", ".ico", ".svg", ".tif", ".tiff",
    };

    private const long MaxBytes = 200L * 1024 * 1024;   // a picture bigger than this is not what was meant

    // ── Is it a picture from a browser? ──────────────────────────────────────

    /// <summary>True if the drag carries a picture from a browser (not files from Explorer, which are another matter).</summary>
    public static bool CanAccept(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop)) return false;   // files from Explorer

            if (data.GetDataPresent("FileGroupDescriptorW"))
            {
                var name = ReadDescriptorName(data);
                return string.IsNullOrEmpty(name) || ImageExtensions.Contains(Path.GetExtension(name));
            }

            // No virtual file: an address of a picture is enough.
            var url = ReadUrl(data);
            if (url == null) return false;
            return url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
                || ImageExtensions.Contains(Path.GetExtension(UrlPath(url)))
                || (data.GetDataPresent("HTML Format") && (data.GetData("HTML Format") as string ?? "").Contains("<img", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    // ── Taking it out of the drop ────────────────────────────────────────────

    /// <summary>Takes the picture (or its address) out of the drop. Null if there is nothing usable. Call it in the Drop handler itself.</summary>
    public static Payload? Read(IDataObject data)
    {
        string? name = ReadDescriptorName(data);
        string? url  = ReadUrl(data);

        byte[]? bytes = null;
        try { bytes = ReadFileContents(data, 0); } catch { /* the address is the other way */ }
        if (bytes is { Length: 0 }) bytes = null;

        if (bytes == null && url == null) return null;
        return new Payload(name ?? "", bytes, url);
    }

    private static string? ReadDescriptorName(IDataObject data)
    {
        try
        {
            if (data.GetData("FileGroupDescriptorW") is not MemoryStream stream) return null;
            var bytes = stream.ToArray();
            // FILEGROUPDESCRIPTORW: a count (4 bytes), then FILEDESCRIPTORW items of 592 bytes; the name starts 72 bytes into the first.
            if (bytes.Length < 4 + 72 + 2 || BitConverter.ToInt32(bytes, 0) < 1) return null;
            int length = Math.Min(520, bytes.Length - (4 + 72));
            var name = Encoding.Unicode.GetString(bytes, 4 + 72, length).Split('\0')[0];
            return name.Length == 0 ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadUrl(IDataObject data)
    {
        foreach (var format in new[] { DataFormats.UnicodeText, DataFormats.Text, "UniformResourceLocatorW" })
        {
            try
            {
                if (!data.GetDataPresent(format)) continue;

                string? text = data.GetData(format) switch
                {
                    string s => s,
                    MemoryStream m => Encoding.Unicode.GetString(m.ToArray()),
                    _ => null,
                };
                text = text?.Split('\0')[0].Trim().Split('\r', '\n')[0].Trim();
                if (!string.IsNullOrEmpty(text) &&
                    (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                     text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)))
                    return text;
            }
            catch { /* the next format */ }
        }
        return null;
    }

    /// <summary>The bytes of the virtual file <paramref name="index"/> of the drag (WPF's wrapper cannot ask for an index, COM can).</summary>
    private static byte[]? ReadFileContents(IDataObject data, int index)
    {
        if (data is not ComTypes.IDataObject com) return null;

        var format = new ComTypes.FORMATETC
        {
            cfFormat = unchecked((short)RegisterClipboardFormat("FileContents")),
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex   = index,
            ptd      = IntPtr.Zero,
            tymed    = ComTypes.TYMED.TYMED_ISTREAM | ComTypes.TYMED.TYMED_HGLOBAL,
        };

        com.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed == ComTypes.TYMED.TYMED_ISTREAM)
            {
                var stream = (ComTypes.IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                stream.Seek(0, 0, IntPtr.Zero);

                using var result = new MemoryStream();
                var chunk = new byte[64 * 1024];
                var readPtr = Marshal.AllocCoTaskMem(sizeof(int));
                try
                {
                    while (true)
                    {
                        stream.Read(chunk, chunk.Length, readPtr);
                        int read = Marshal.ReadInt32(readPtr);
                        if (read <= 0) break;
                        result.Write(chunk, 0, read);
                        if (result.Length > MaxBytes) throw new InvalidOperationException("The picture is too big.");
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(readPtr);
                }
                return result.ToArray();
            }

            if (medium.tymed == ComTypes.TYMED.TYMED_HGLOBAL)
            {
                long size = (long)GlobalSize(medium.unionmember);
                if (size <= 0 || size > MaxBytes) return null;

                var pointer = GlobalLock(medium.unionmember);
                if (pointer == IntPtr.Zero) return null;
                try
                {
                    var bytes = new byte[size];
                    Marshal.Copy(pointer, bytes, 0, (int)size);
                    return bytes;
                }
                finally
                {
                    GlobalUnlock(medium.unionmember);
                }
            }
            return null;
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    // ── Saving ───────────────────────────────────────────────────────────────

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Writes the picture into <paramref name="folder"/> as a new file and returns its path. The name is the original one when there is one;
    /// a name that is taken gets " (2)" ... Never overwrites. Throws with a message fit to show when it cannot.
    /// </summary>
    public static async Task<string> SaveAsync(Payload payload, string folder)
    {
        byte[]? bytes = payload.Bytes;
        string? contentType = null;

        if (bytes == null && payload.Url != null)
            (bytes, contentType) = await GetFromAddressAsync(payload.Url);
        if (bytes == null || bytes.Length == 0)
            throw new InvalidOperationException("The picture could not be read from the drop.");

        string name = FileName(payload, bytes, contentType);
        return WriteNew(folder, name, bytes);
    }

    private static async Task<(byte[] Bytes, string? ContentType)> GetFromAddressAsync(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // data:image/png;base64,....
            int comma = url.IndexOf(',');
            if (comma < 0) throw new InvalidOperationException("The picture address is not valid.");

            string header = url[5..comma];
            string body   = url[(comma + 1)..];
            string? mime  = header.Split(';')[0];
            var data = header.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(body)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(body));
            return (data, mime);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SweWolfFileExplorer");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > MaxBytes)
                throw new InvalidOperationException("The picture is too big.");

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBytes) throw new InvalidOperationException("The picture is too big.");
            }
            return (buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"The picture could not be downloaded: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("The picture could not be downloaded in time.");
        }
    }

    /// <summary>The file name: the original, or the one in the address, or "image"; with an extension that fits what the picture really is.</summary>
    private static string FileName(Payload payload, byte[] bytes, string? contentType)
    {
        string name = payload.Name;
        if (string.IsNullOrWhiteSpace(name) && payload.Url != null && !payload.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            name = Uri.UnescapeDataString(Path.GetFileName(UrlPath(payload.Url)));

        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length == 0) name = "image";

        // The extension must say what the file is (a picture address often has none, or none that fits).
        string current = Path.GetExtension(name);
        string? real = ExtensionOf(bytes) ?? ExtensionOfContentType(contentType);
        if (!ImageExtensions.Contains(current))
            name += real ?? ".jpg";

        if (name.Length > 150)
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            name = stem[..Math.Min(140, stem.Length)] + Path.GetExtension(name);
        }
        return name;
    }

    /// <summary>Creates a new file and writes the bytes. If the name is taken: " (2)", " (3)" ... (CreateNew: never replaces a file, even one that appears meanwhile).</summary>
    private static string WriteNew(string folder, string name, byte[] bytes)
    {
        string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);

        for (int n = 1; n < 10000; n++)
        {
            string candidate = Path.Combine(folder, n == 1 ? name : $"{stem} ({n}){extension}");
            try
            {
                using var file = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(bytes, 0, bytes.Length);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // taken: the next number
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException($"The picture cannot be saved here: {ex.Message}");
            }
        }
        throw new InvalidOperationException("No free file name was found.");
    }

    // ── What kind of picture ─────────────────────────────────────────────────

    private static string UrlPath(string url)
    {
        try { return new Uri(url).AbsolutePath; }
        catch { return url.Split('?', '#')[0]; }
    }

    /// <summary>The extension from the first bytes of the file (null if they are not a known picture).</summary>
    private static string? ExtensionOf(byte[] b)
    {
        static bool Starts(byte[] data, params byte[] prefix) => data.Length >= prefix.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);

        if (Starts(b, 0x89, 0x50, 0x4E, 0x47)) return ".png";
        if (Starts(b, 0xFF, 0xD8, 0xFF)) return ".jpg";
        if (Starts(b, (byte)'G', (byte)'I', (byte)'F', (byte)'8')) return ".gif";
        if (b.Length > 12 && Starts(b, (byte)'R', (byte)'I', (byte)'F', (byte)'F') && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return ".webp";
        if (Starts(b, (byte)'B', (byte)'M')) return ".bmp";
        if (b.Length > 12 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p' && b[8] == 'a' && b[9] == 'v' && b[10] == 'i') return ".avif";
        if (Starts(b, 0x00, 0x00, 0x01, 0x00)) return ".ico";
        if (Starts(b, 0x49, 0x49, 0x2A, 0x00) || Starts(b, 0x4D, 0x4D, 0x00, 0x2A)) return ".tif";

        var head = Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 512));
        if (head.Contains("<svg", StringComparison.OrdinalIgnoreCase)) return ".svg";
        return null;
    }

    private static string? ExtensionOfContentType(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/avif" => ".avif",
        "image/svg+xml" => ".svg",
        "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
        "image/tiff" => ".tif",
        _ => null,
    };

    // ── Windows ──────────────────────────────────────────────────────────────

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);
}
