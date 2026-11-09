using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwewolfFileExplorer.Helpers;

/// <summary>What an AI picture or video says about how it was made: the prompt, the negative prompt and the seed. A value is null when it was not found.</summary>
public sealed record AiInfo(string PromptLabel, string? Prompt, string? Negative, string? Seed)
{
    /// <summary>Nothing was found: the file is not from a program this reader knows.</summary>
    public bool IsEmpty => Prompt == null && Negative == null && Seed == null;
}

/// <summary>
/// The prompt, negative prompt and seed of a file made by an AI program, for the info panel: a PNG from ComfyUI (<see cref="ComfyPromptReader"/>)
/// or from Automatic1111 / Forge / SD.Next (the "parameters" text chunk, <see cref="Automatic1111Reader"/>), or an MP4 from Wan2GP
/// (<see cref="Wan2GpReader"/>). Null for any other file, so nothing is shown for it.
/// File Info Viewer has the ComfyUI and Wan2GP readers too (its template 1, "Prompt and Seed"): see their History lists.
/// </summary>
public static class AiInfoReader
{
    /// <summary>Which files can hold such information (decided by the extension only; nothing is read yet).</summary>
    public static bool CanHave(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".mp4" or ".m4v" or ".mov";

    public static AiInfo? Read(string path)
    {
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png":
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var chunks = new Dictionary<string, string>();
                        ComfyPromptReader.ReadPngTextChunks(stream, chunks);
                        return ComfyPromptReader.FromChunks(chunks)
                            ?? (chunks.TryGetValue("parameters", out var parameters)
                                ? (FooocusReader.Parse(parameters) ?? Automatic1111Reader.Parse(parameters))
                                : null);
                    }
                case ".mp4" or ".m4v" or ".mov":
                    return Wan2GpReader.Read(path);
                default:
                    return null;
            }
        }
        catch
        {
            return null;   // not readable, damaged, locked or gone: nothing is shown
        }
    }
}

/// <summary>
/// The "parameters" text chunk of a PNG from Fooocus with "Save Metadata to Images" on and the scheme "fooocus": one JSON object
/// (a second chunk, "fooocus_scheme", says "fooocus"). Keys used: prompt, negative_prompt, seed (a number or a text); the rest
/// (models, sampler, styles, the expanded prompts ...) is not shown. Text that is not a JSON object gives null, so the
/// Automatic1111 reader gets its turn (Fooocus' "a1111" scheme).
/// History:
///   2026-11-09  First version (SweWolf File Explorer only).
/// </summary>
public static class FooocusReader
{
    public static AiInfo? Parse(string text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? Text(string name)
            {
                if (!root.TryGetProperty(name, out var v)) return null;
                var s = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetRawText(),
                    _ => null,
                };
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }

            var info = new AiInfo("Prompt", Text("prompt"), Text("negative_prompt"), Text("seed"));
            return info.IsEmpty ? null : info;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The "parameters" text chunk that Automatic1111's web UI (and Forge, SD.Next ...) writes into a PNG:
///   the prompt (any number of lines)
///   Negative prompt: the negative prompt (any number of lines)
///   Steps: 20, Sampler: Euler a, CFG scale: 7, Seed: 1234, Size: 512x512, Model hash: ...
/// The settings line is the last line that starts with "Steps: "; the seed is read from it.
/// History:
///   2026-10-24  First version (SweWolf File Explorer only).
/// </summary>
public static class Automatic1111Reader
{
    private static readonly Regex SeedPattern = new(@"(?:^|,\s*)Seed:\s*(\d+)", RegexOptions.Compiled);

    public static AiInfo? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r\n", "\n").Split('\n');

        // The settings line: the last one that starts with "Steps: " (a prompt may mention "Steps:" higher up).
        int settings = -1;
        for (int i = lines.Length - 1; i >= 0; i--)
            if (lines[i].StartsWith("Steps: ", StringComparison.Ordinal)) { settings = i; break; }

        int bodyEnd = settings >= 0 ? settings : lines.Length;
        int negativeAt = -1;
        for (int i = 0; i < bodyEnd; i++)
            if (lines[i].StartsWith("Negative prompt:", StringComparison.Ordinal)) { negativeAt = i; break; }

        string Join(int from, int to) => string.Join("\n", lines.Skip(from).Take(to - from)).Trim();

        string prompt = Join(0, negativeAt >= 0 ? negativeAt : bodyEnd);
        string? negative = null;
        if (negativeAt >= 0)
        {
            var first = lines[negativeAt]["Negative prompt:".Length..].TrimStart();
            negative = (first + "\n" + Join(negativeAt + 1, bodyEnd)).Trim();
        }

        string? seed = null;
        if (settings >= 0 && SeedPattern.Match(lines[settings]) is { Success: true } m) seed = m.Groups[1].Value;

        var info = new AiInfo("Prompt", prompt, negative, seed);
        return info.IsEmpty ? null : info;
    }
}

/// <summary>
/// An MP4 / MOV file made by Wan2GP (WanGP): the generation settings are stored as JSON in the iTunes-style "comment" atom
/// (moov/udta/meta/ilst/©cmt). The JSON has the keys prompt, negative_prompt (only for models that have one) and seed.
/// Copied from File Info Viewer's Services/Wan2GpReader.cs; the apps share no code, so a fix in one may belong in the other.
/// When copying: change the namespace and the result type (here <see cref="AiInfo"/>).
/// History:
///   2026-10-24  First copy into SweWolf File Explorer (it returns null instead of an error text; the file is opened for sharing).
/// </summary>
public static class Wan2GpReader
{
    private const long MaxMoovSize = 64 * 1024 * 1024;

    public static AiInfo? Read(string filePath)
    {
        try
        {
            var json = ReadCommentAtom(filePath);
            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var info = new AiInfo("Prompt", GetValue(root, "prompt"), GetValue(root, "negative_prompt"), GetValue(root, "seed"));
            return info.IsEmpty ? null : info;
        }
        catch
        {
            return null;   // not readable, or the comment is some other tool's text and not Wan2GP's JSON
        }
    }

    private static string? GetValue(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    /// <summary>Returns the text of the ©cmt atom, or null when the file has none.</summary>
    private static string? ReadCommentAtom(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[16];

        // Top-level boxes: skip everything (the huge mdat in particular) except moov
        while (fs.Position + 8 <= fs.Length)
        {
            var start = fs.Position;
            fs.ReadExactly(header, 0, 8);
            long size = ReadUInt32(header, 0);
            var type = Encoding.Latin1.GetString(header, 4, 4);
            var headerSize = 8;
            if (size == 1)
            {
                fs.ReadExactly(header, 8, 8);
                size = (long)(((ulong)ReadUInt32(header, 8) << 32) | ReadUInt32(header, 12));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = fs.Length - start;
            }
            if (size < headerSize) return null;

            if (type == "moov")
            {
                var bodySize = size - headerSize;
                if (bodySize > MaxMoovSize || start + size > fs.Length) return null;
                var body = new byte[bodySize];
                fs.ReadExactly(body);
                return FindComment(body, 0, body.Length, 0);
            }
            fs.Position = start + size;
        }
        return null;
    }

    private static string? FindComment(byte[] b, int start, int end, int depth)
    {
        if (depth > 6) return null;
        var p = start;
        while (p + 8 <= end)
        {
            long size = ReadUInt32(b, p);
            if (size < 8 || p + size > end) return null;
            var type = Encoding.Latin1.GetString(b, p + 4, 4);
            var bodyStart = p + 8;
            var bodyEnd = (int)(p + size);

            switch (type)
            {
                case "udta" or "ilst":
                    if (FindComment(b, bodyStart, bodyEnd, depth + 1) is { } inner) return inner;
                    break;
                case "meta":
                    // meta is a "full box": 4 bytes of version and flags come before its children
                    if (FindComment(b, bodyStart + 4, bodyEnd, depth + 1) is { } meta) return meta;
                    break;
                case "©cmt":
                    return ReadDataAtom(b, bodyStart, bodyEnd);
            }
            p = bodyEnd;
        }
        return null;
    }

    /// <summary>The "data" child of an ilst item: size, "data", type (4), locale (4), then the value.</summary>
    private static string? ReadDataAtom(byte[] b, int start, int end)
    {
        if (start + 16 > end || Encoding.Latin1.GetString(b, start + 4, 4) != "data") return null;
        var valueStart = start + 16;
        var valueEnd = (int)Math.Min(end, start + ReadUInt32(b, start));
        return valueEnd > valueStart ? Encoding.UTF8.GetString(b, valueStart, valueEnd - valueStart) : null;
    }

    private static uint ReadUInt32(byte[] b, int offset) =>
        (uint)((b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);
}
