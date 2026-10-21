// PlayerLink.cs: the explorer's side of the named pipe to a running SweWolf Media Player.
//
// One pipe per Windows user (the name has the user's id). Messages are one line of JSON each (camelCase). The explorer sends commands (play a file,
// pause, resume, control, release, front, close); the player sends events (hello, ended, closed, error). The explorer decides everything about a
// playlist; the player only plays what it is told and says when a file has ended.
// The same record is in the player (PlayerPipe.cs): change both together, and raise Version when the messages change.
//
// History:
//   2026-10-20  First version (protocol 1).

using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SwewolfFileExplorer.Helpers;

/// <summary>A command to the player. Names are in camelCase in the JSON ("cmd", "path", "libraryPath", ...).</summary>
public sealed record PipeCommand(string Cmd, string? Path = null, string? LibraryPath = null, long StartMs = 0,
                                 bool NoResume = false, bool Front = false, bool Playlist = false);

public sealed class PlayerLink : IDisposable
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The pipe's name. SWEWOLF_PLAYER_PIPE gives a test a pipe of its own, so it never talks to the user's real player.</summary>
    public static string Name =>
        Environment.GetEnvironmentVariable("SWEWOLF_PLAYER_PIPE") is { Length: > 0 } custom
            ? custom
            : "SweWolfMediaPlayer." + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

    /// <summary>A file played to its end (the path as the player knows it). Raised on a background thread.</summary>
    public event Action<string?>? Ended;

    /// <summary>The player's window closed, or the connection was lost. Raised on a background thread.</summary>
    public event Action? Closed;

    /// <summary>The player could not play a file (path, message). Raised on a background thread.</summary>
    public event Action<string?, string?>? Error;

    private NamedPipeClientStream? _client;
    private readonly object _writeGate = new();
    private volatile bool _connected;

    public bool Connected => _connected && _client is { IsConnected: true };

    /// <summary>Connects to a running player. False if none answers within the time.</summary>
    public async Task<bool> ConnectAsync(int timeoutMs, CancellationToken cancel = default)
    {
        if (Connected) return true;

        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!cancel.IsCancellationRequested)
        {
            int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
            if (left <= 0) break;

            NamedPipeClientStream? client = null;
            try
            {
                client = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                await client.ConnectAsync(Math.Min(left, 500), cancel);
                _client = client;
                _connected = true;
                _ = Task.Run(() => ReadLoop(client));
                return true;
            }
            catch (OperationCanceledException) { client?.Dispose(); break; }
            catch (Exception)
            {
                client?.Dispose();
                try { await Task.Delay(300, cancel); } catch (OperationCanceledException) { break; }
            }
        }

        return false;
    }

    private async Task ReadLoop(NamedPipeClientStream client)
    {
        try
        {
            using var reader = new StreamReader(client, new UTF8Encoding(false), leaveOpen: true);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                JsonElement root;
                try { root = JsonDocument.Parse(line).RootElement.Clone(); }
                catch (JsonException) { continue; }

                string? path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
                switch (root.TryGetProperty("event", out var e) ? e.GetString() : null)
                {
                    case "ended":  Ended?.Invoke(path); break;
                    case "closed": Closed?.Invoke(); break;
                    case "error":  Error?.Invoke(path, root.TryGetProperty("message", out var m) ? m.GetString() : null); break;
                }
            }
        }
        catch (Exception) { /* the connection is gone */ }
        finally
        {
            _connected = false;
            Closed?.Invoke();
        }
    }

    /// <summary>Sends a command. False if the player is not there any more.</summary>
    public bool Send(PipeCommand command)
    {
        try
        {
            if (!Connected) return false;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, Json) + "\n");
            lock (_writeGate)
            {
                _client!.Write(bytes, 0, bytes.Length);
                _client.Flush();
            }
            return true;
        }
        catch (Exception)
        {
            _connected = false;
            return false;
        }
    }

    public void Dispose()
    {
        _connected = false;
        try { _client?.Dispose(); } catch { }
        _client = null;
    }
}
