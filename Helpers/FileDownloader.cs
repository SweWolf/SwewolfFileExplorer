using System.IO;
using System.Net.Http;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Downloads a file from a web address: the one place in the app that does it (a picture dragged in from a web browser,
/// the poster named in an .nfo file). Never keeps a half file, never goes over a size limit, and every failure is an
/// <see cref="InvalidOperationException"/> with a message fit to show or to log.
/// </summary>
public static class FileDownloader
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };   // each call has its own time limit

    /// <summary>The downloaded bytes and the content type the server gave (null if it gave none).</summary>
    public readonly record struct Download(byte[] Bytes, string? ContentType);

    /// <param name="what">What is downloaded, for the messages ("picture", "file").</param>
    /// <param name="cancel">Stops the download early (the caller's Cancel button, or a time limit of its own).</param>
    public static async Task<Download> GetBytesAsync(string url, long maxBytes, int timeoutSeconds = 60, string what = "file",
                                                     CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SweWolfFileExplorer");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > maxBytes)
                throw new InvalidOperationException($"The {what} is too big.");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes) throw new InvalidOperationException($"The {what} is too big.");
            }
            return new Download(buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"The {what} could not be downloaded: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException($"The {what} could not be downloaded in time.");
        }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> into a new file at <paramref name="path"/> and returns the path. Never replaces a file
    /// (an existing one is an error). The file is written whole or not at all. With <paramref name="hidden"/> it gets the Hidden
    /// attribute. The folder must exist.
    /// </summary>
    public static async Task<string> ToFileAsync(string url, string path, long maxBytes, bool hidden = false, int timeoutSeconds = 60)
    {
        var download = await GetBytesAsync(url, maxBytes, timeoutSeconds);
        WriteNew(path, download.Bytes, hidden);
        return path;
    }

    /// <summary>Writes bytes into a new file (CreateNew: never replaces one, even one that appears meanwhile), optionally hidden.</summary>
    public static void WriteNew(string path, byte[] bytes, bool hidden)
    {
        var temp = path + ".part" + Environment.ProcessId;
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path)) throw new IOException($"\"{Path.GetFileName(path)}\" already exists.");
            File.Move(temp, path);   // no overwrite: fails if the file appeared meanwhile
            if (hidden) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }
}
