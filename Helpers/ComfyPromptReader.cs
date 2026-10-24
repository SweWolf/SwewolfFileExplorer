using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Reads the prompt, negative prompt and seed that ComfyUI stores in a PNG file (the info panel shows them to the right of the preview).
/// The values come from the PNG text chunk "prompt" (node id to {class_type, inputs}). Node ids and class names differ between workflows,
/// so nothing is looked up by id: the sampler node is found by its seed input and its positive / negative links are followed to the text node.
///
/// Copied from File Info Viewer's Services/ComfyPromptReader.cs (its template 1, "Prompt and Seed"); the apps share no code, so a fix in one
/// may belong in the other. When copying: change the namespace, and the result type (here <see cref="AiInfo"/>).
/// History:
///   2026-10-24  First copy into SweWolf File Explorer. Reads from a stream (not a file name), a compressed iTXt chunk is understood
///               (File Info Viewer skips the compression flag), and the PNG chunk reader is part of this file.
///   2026-10-24  The result type is AiInfo (AiInfoReader.cs), shared with the Automatic1111 and Wan2GP readers; FromChunks and the
///               public PNG chunk reader let AiInfoReader read a file once.
/// </summary>
public static class ComfyPromptReader
{
    private const int MaxDepth = 8;

    // Input names that can hold the text, tried in this order (Text Encode, CLIPTextEncode, string/primitive nodes,
    // and "source" for Preview Any, which only passes its input on)
    private static readonly string[] TextKeys = ["prompt", "text", "string", "value", "source"];
    private static readonly string[] SeedKeys = ["seed", "noise_seed", "value"];

    /// <summary>Notes what the prompt passed through on its way to the sampler.</summary>
    private sealed class Trace
    {
        /// <summary>True when a text-generation (prompt enhancer) node sits in between: the text found is its input, not the final prompt.</summary>
        public bool Enhanced;
    }

    /// <summary>The ComfyUI data of a PNG file; null if the file is not a PNG, cannot be read or has none.</summary>
    public static AiInfo? Read(string path)
    {
        try
        {
            if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The ComfyUI data of a PNG stream (it must be able to seek); null if it has none.</summary>
    public static AiInfo? Read(Stream stream)
    {
        try
        {
            var chunks = new Dictionary<string, string>();
            ReadPngTextChunks(stream, chunks);
            return FromChunks(chunks);
        }
        catch
        {
            return null;   // not a PNG, damaged, or the prompt data is not JSON
        }
    }

    /// <summary>The ComfyUI data from the text chunks of a PNG (the keyword "prompt" holds it); null if there is none.</summary>
    public static AiInfo? FromChunks(Dictionary<string, string> chunks)
    {
        try
        {
            if (!chunks.TryGetValue("prompt", out var json)) return null;
            using var doc = JsonDocument.Parse(json);
            var info = Extract(doc.RootElement);
            return info.IsEmpty ? null : info;
        }
        catch (JsonException)
        {
            return null;   // the prompt data is not JSON
        }
    }

    /// <summary>Reads all tEXt / iTXt chunks of a PNG into <paramref name="chunks"/> (keyword to text); the picture data is skipped with seeks.</summary>
    public static void ReadPngTextChunks(Stream fs, Dictionary<string, string> chunks)
    {
        Span<byte> hdr = stackalloc byte[8];
        fs.ReadExactly(hdr);   // PNG signature
        if (hdr[0] != 0x89 || hdr[1] != (byte)'P' || hdr[2] != (byte)'N' || hdr[3] != (byte)'G') return;

        Span<byte> buf4 = stackalloc byte[4];
        while (fs.Position <= fs.Length - 12)
        {
            fs.ReadExactly(buf4);
            int length = (buf4[0] << 24) | (buf4[1] << 16) | (buf4[2] << 8) | buf4[3];
            if (length < 0 || length > 50 * 1024 * 1024) break;

            fs.ReadExactly(buf4);
            var type = Encoding.ASCII.GetString(buf4);

            if (type is "tEXt" or "iTXt")
            {
                var data = new byte[length];
                fs.ReadExactly(data);
                fs.Seek(4, SeekOrigin.Current);   // skip CRC

                int nullPos = Array.IndexOf(data, (byte)0);
                if (nullPos < 0) continue;

                var keyword = Encoding.UTF8.GetString(data, 0, nullPos);
                string text;

                if (type == "tEXt")
                {
                    text = Encoding.UTF8.GetString(data, nullPos + 1, length - nullPos - 1);
                }
                else   // iTXt: keyword\0compFlag\0compMethod\0lang\0translatedKw\0text
                {
                    bool compressed = nullPos + 1 < length && data[nullPos + 1] == 1;
                    int pos = nullPos + 3;   // skip compFlag, compMethod
                    while (pos < length && data[pos] != 0) pos++;   // skip lang
                    pos++;
                    while (pos < length && data[pos] != 0) pos++;   // skip translated keyword
                    pos++;
                    if (pos >= length) text = "";
                    else if (!compressed) text = Encoding.UTF8.GetString(data, pos, length - pos);
                    else
                    {
                        using var zlib = new ZLibStream(new MemoryStream(data, pos, length - pos), CompressionMode.Decompress);
                        using var reader = new StreamReader(zlib, Encoding.UTF8);
                        text = reader.ReadToEnd();
                    }
                }

                if (!string.IsNullOrWhiteSpace(text))
                    chunks[keyword] = text;
            }
            else if (type == "IEND")
            {
                break;
            }
            else
            {
                fs.Seek(length + 4, SeekOrigin.Current);   // skip data + CRC
            }
        }
    }

    private static AiInfo Extract(JsonElement root)
    {
        string? prompt = null, negative = null, seed = null;
        var trace = new Trace();

        if (root.ValueKind == JsonValueKind.Object && FindSampler(root) is { } sampler)
        {
            if (ConditioningNode(root, sampler, negative: false) is { } pos) prompt = ReadText(root, pos, false, 0, trace);
            if (ConditioningNode(root, sampler, negative: true) is { } neg) negative = ReadText(root, neg, true, 0, new Trace());
            seed = ResolveScalar(root, sampler, SeedKeys, 0);
            if (seed == null && sampler.TryGetProperty("noise", out var noiseLink)
                && TryFollow(root, noiseLink, out var noiseNode) && TryInputs(noiseNode, out var noiseInputs))
                seed = ResolveScalar(root, noiseInputs, SeedKeys, 1);
        }

        return new AiInfo(trace.Enhanced ? "Prompt (Before Enhancement)" : "Prompt", prompt, negative, seed);
    }

    /// <summary>Finds the inputs of the sampler node: the first one with a seed and prompt links, else the first with any seed.</summary>
    private static JsonElement? FindSampler(JsonElement root)
    {
        JsonElement? withSeedOnly = null;
        foreach (var node in root.EnumerateObject())
        {
            if (!TryInputs(node.Value, out var inputs)) continue;
            if (!inputs.TryGetProperty("seed", out _) && !inputs.TryGetProperty("noise_seed", out _)
                && !inputs.TryGetProperty("noise", out _)) continue;
            if (inputs.TryGetProperty("positive", out _) || inputs.TryGetProperty("guider", out _)) return inputs;
            withSeedOnly ??= inputs;
        }
        return withSeedOnly;
    }

    /// <summary>Follows the sampler's positive/negative link (or, for guider-based samplers, the guider's) to the conditioning node.</summary>
    private static JsonElement? ConditioningNode(JsonElement root, JsonElement samplerInputs, bool negative)
    {
        string[] keys = negative ? ["negative"] : ["positive", "conditioning"];

        if (samplerInputs.TryGetProperty(negative ? "negative" : "positive", out var link) && TryFollow(root, link, out var node))
            return node;

        if (samplerInputs.TryGetProperty("guider", out var guiderLink) && TryFollow(root, guiderLink, out var guider)
            && TryInputs(guider, out var guiderInputs))
        {
            foreach (var key in keys)
                if (guiderInputs.TryGetProperty(key, out var l) && TryFollow(root, l, out var n))
                    return n;
        }
        return null;
    }

    private static string? ReadText(JsonElement root, JsonElement node, bool negative, int depth, Trace trace)
    {
        if (depth > MaxDepth || !TryInputs(node, out var inputs)) return null;

        // "Zero out" is how a workflow says "no negative prompt" without typing an empty one
        if (node.TryGetProperty("class_type", out var classType) && classType.GetString() == "ConditioningZeroOut")
            return "";

        // A node that holds both texts (Text Encode Qwen Image): which one is wanted depends on the role of the link
        if (inputs.TryGetProperty("negative_prompt", out _))
            return ResolveScalar(root, inputs, [negative ? "negative_prompt" : "prompt"], depth, trace);

        var text = ResolveScalar(root, inputs, TextKeys, depth, trace);
        if (text != null) return text;

        // Pass-through nodes (e.g. FluxGuidance) keep the text one step further back
        if (inputs.TryGetProperty("conditioning", out var link) && TryFollow(root, link, out var next))
            return ReadText(root, next, negative, depth + 1, trace);
        return null;
    }

    /// <summary>Returns the first of <paramref name="keys"/> that has a text or number value, following links to string/primitive nodes.</summary>
    private static string? ResolveScalar(JsonElement root, JsonElement inputs, string[] keys, int depth, Trace? trace = null)
    {
        foreach (var key in keys)
        {
            if (!inputs.TryGetProperty(key, out var value)) continue;
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                    return value.GetRawText();
                case JsonValueKind.Array when depth < MaxDepth && TryFollow(root, value, out var target):
                    var resolved = ResolveNode(root, target, keys, depth + 1, trace);
                    if (resolved != null) return resolved;
                    break;
            }
        }
        return null;
    }

    /// <summary>Resolves a linked node. A switch node (ComfySwitchNode) passes on only the branch its switch selects.</summary>
    private static string? ResolveNode(JsonElement root, JsonElement node, string[] keys, int depth, Trace? trace)
    {
        if (!TryInputs(node, out var inputs)) return null;

        if (inputs.TryGetProperty("switch", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            var branch = on.GetBoolean() ? "on_true" : "on_false";
            return inputs.TryGetProperty(branch, out var link) && depth < MaxDepth && TryFollow(root, link, out var next)
                ? ResolveNode(root, next, keys, depth + 1, trace)
                : null;
        }

        var result = ResolveScalar(root, inputs, keys, depth, trace);
        if (result != null && trace != null && node.TryGetProperty("class_type", out var classType)
            && classType.GetString() == "TextGenerate")
            trace.Enhanced = true;
        return result;
    }

    /// <summary>A link in the prompt JSON is ["node id", output index].</summary>
    private static bool TryFollow(JsonElement root, JsonElement link, out JsonElement node)
    {
        node = default;
        if (link.ValueKind != JsonValueKind.Array || link.GetArrayLength() != 2) return false;
        var idElement = link[0];
        var id = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : idElement.GetRawText();
        return id != null && root.TryGetProperty(id, out node) && node.ValueKind == JsonValueKind.Object;
    }

    private static bool TryInputs(JsonElement node, out JsonElement inputs)
    {
        inputs = default;
        return node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("inputs", out inputs) && inputs.ValueKind == JsonValueKind.Object;
    }
}
