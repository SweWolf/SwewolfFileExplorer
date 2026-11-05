using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// What Windows' "Mark of the Web" says about a file: the zone it came from and, when the browser (or other program) that saved it
/// wrote them, the address it was downloaded from and the page that linked to it.
/// </summary>
internal static class WebAddresses
{
    /// <summary>
    /// A web address worth showing and keeping: http or https, and not "about:internet" (Edge, when it does not know the address) or this
    /// computer's own server (localhost, 127.x.x.x, ::1: a local program such as Pinokio, not a download from the internet).
    /// </summary>
    public static bool IsUseful(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
        var host = uri.Host;
        return !(host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                 || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip)));
    }
}

public sealed record WebOrigin(int? ZoneId, string? HostUrl, string? ReferrerUrl)
{
    /// <summary>The zone number with its Windows name: "3 - Internet".</summary>
    public string? ZoneText => ZoneId switch
    {
        null => null,
        0 => "0 - My Computer",
        1 => "1 - Local Intranet",
        2 => "2 - Trusted Sites",
        3 => "3 - Internet",
        4 => "4 - Restricted Sites",
        var other => other.ToString(),
    };
}

/// <summary>
/// Reads the hidden NTFS stream "Zone.Identifier" that browsers (Chrome, Edge, Brave ...) attach to a downloaded file, an INI text:
///   [ZoneTransfer]
///   ZoneId=3
///   ReferrerUrl=https://page.example/       (the page that linked to the download; not always there)
///   HostUrl=https://files.example/a.jpg     (the address of the file, or only of the site; not always there)
/// The stream is part of the file: it goes when the file is copied to a drive that is not NTFS, when a cloud service strips it,
/// or when the user clicks Unblock. Nothing is written. A file with no stream (the usual case), a drive with no streams, a network
/// share or a folder gives null.
/// History:
///   2026-10-24  First version (SweWolf File Explorer only).
///   2026-11-04  A NUL byte at the end of the stream (a Suno download: `HostUrl=https://suno.com/` + 00) is removed from the values;
///               it made the address a broken link.
/// </summary>
public static class MarkOfTheWeb
{
    public static WebOrigin? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

            int? zone = null;
            string? host = null, referrer = null;
            bool inSection = false;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim().Trim('\0');   // some browsers end the stream with a NUL byte: Trim() leaves it on the last value
                if (line.StartsWith('[')) { inSection = line.Equals("[ZoneTransfer]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inSection) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim().Trim('\0').Trim();
                if (value.Length == 0) continue;

                if (key.Equals("ZoneId", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var id)) zone = id;
                else if (key.Equals("HostUrl", StringComparison.OrdinalIgnoreCase)) host = value;
                else if (key.Equals("ReferrerUrl", StringComparison.OrdinalIgnoreCase)) referrer = value;
            }

            return zone == null && host == null && referrer == null ? null : new WebOrigin(zone, host, referrer);
        }
        catch
        {
            return null;   // no stream, not NTFS, no access, gone: nothing to show
        }
    }
}
