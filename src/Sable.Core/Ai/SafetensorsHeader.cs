using System.Buffers.Binary;
using System.IO;
using System.Text.Json;

namespace Sable.Core.Ai;

/// <summary>
/// Header-only sniff of a <c>.safetensors</c> file to refine architecture detection beyond the filename
/// (PHASE8_AI_SIDECAR §2.4). The format is: 8-byte little-endian u64 header length N, then N bytes of JSON
/// (tensor index + an optional <c>__metadata__</c> map). We read ONLY the header — never the weights — and
/// look for a recorded architecture (e.g. <c>modelspec.architecture</c>) or a LoRA base-model tag. The
/// byte→length and json→arch steps are pure and unit-tested; <see cref="TryReadArch(string)"/> adds the file IO.
/// </summary>
public static class SafetensorsHeader
{
    /// <summary>Max header we'll read (sane cap; real headers are KBs–low MBs).</summary>
    public const long MaxHeaderBytes = 64L * 1024 * 1024;

    /// <summary>Decode the 8-byte little-endian header length; -1 if fewer than 8 bytes.</summary>
    public static long ReadHeaderLength(ReadOnlySpan<byte> first8)
    {
        if (first8.Length < 8) return -1;
        ulong n = BinaryPrimitives.ReadUInt64LittleEndian(first8);
        return n > (ulong)MaxHeaderBytes ? -1 : (long)n;
    }

    /// <summary>
    /// Map a safetensors header JSON to a Sable architecture string (SD1.5/SD2/SDXL/SD3/Flux), or null.
    /// Checks <c>__metadata__</c> tags first (modelspec / kohya LoRA), then falls back to tensor-key shape
    /// heuristics. Pure — feed it captured header JSON in tests.
    /// </summary>
    public static string? GuessArchFromHeaderJson(string headerJson)
    {
        if (string.IsNullOrWhiteSpace(headerJson)) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(headerJson); }
        catch { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("__metadata__", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                var fromMeta = ArchFromMetadata(meta);
                if (fromMeta is not null) return fromMeta;
            }

            return ArchFromTensorKeys(root);
        }
    }

    private static string? ArchFromMetadata(JsonElement meta)
    {
        // collect a few candidate metadata values
        foreach (var key in new[] { "modelspec.architecture", "ss_base_model_version", "modelspec.implementation" })
            if (meta.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var a = NormalizeArch(v.GetString());
                if (a is not null) return a;
            }
        return null;
    }

    private static string? ArchFromTensorKeys(JsonElement root)
    {
        // cheap structural tells without loading weights:
        //  - Flux/SD3 transformers use "...double_blocks..." / "...joint_blocks..." keys
        //  - SDXL UNet has the second text-embedding ("...add_embedding..." / label_emb) + add_embeds
        bool sawDouble = false, sawJoint = false, sawAddEmb = false;
        int seen = 0;
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals("__metadata__")) continue;
            var k = prop.Name;
            if (k.Contains("double_blocks") || k.Contains("single_blocks")) sawDouble = true;
            else if (k.Contains("joint_blocks") || k.Contains("context_embedder")) sawJoint = true;
            else if (k.Contains("add_embedding") || k.Contains("label_emb")) sawAddEmb = true;
            if (++seen > 4000) break;   // bounded scan
        }
        if (sawDouble) return "Flux";
        if (sawJoint) return "SD3";
        if (sawAddEmb) return "SDXL";
        return null;
    }

    /// <summary>Normalise a free-form arch/version string to a Sable family id.</summary>
    public static string? NormalizeArch(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var n = s.ToLowerInvariant();
        if (n.Contains("flux")) return "Flux";
        if (n.Contains("stable-diffusion-3") || n.Contains("sd3") || n.Contains("sd_3")) return "SD3";
        if (n.Contains("xl")) return "SDXL";
        if (n.Contains("v2") || n.Contains("768") || n.Contains("2.1") || n.Contains("2-1")) return "SD2";
        if (n.Contains("v1") || n.Contains("1.5") || n.Contains("1-5")) return "SD1.5";
        return null;
    }

    /// <summary>Read just the header of a real file and guess the arch; null on any IO/format failure.</summary>
    public static string? TryReadArch(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> len = stackalloc byte[8];
            if (fs.Read(len) < 8) return null;
            long n = ReadHeaderLength(len);
            if (n <= 0 || n > fs.Length - 8) return null;
            var buf = new byte[n];
            int read = 0;
            while (read < n)
            {
                int r = fs.Read(buf, read, (int)(n - read));
                if (r <= 0) break;
                read += r;
            }
            if (read < n) return null;
            return GuessArchFromHeaderJson(System.Text.Encoding.UTF8.GetString(buf));
        }
        catch { return null; }
    }
}
