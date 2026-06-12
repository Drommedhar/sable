using System.Buffers.Binary;
using System.Text;

namespace Sable.Tools;

/// <summary>
/// Photoshop .abr brush importer (improvement plan §2). Supports the legacy v1/v2
/// computed + sampled brushes and the modern v6/v7/v10 container ("8BIM" blocks:
/// <c>samp</c> = sampled tip bitmaps, <c>desc</c> = a PS descriptor with the mappable
/// params). Maps what Sable's engine supports — name, diameter, spacing, angle,
/// roundness, hardness, sampled tip — and reports skipped PS-engine dynamics.
/// Tips larger than 512px are downscaled (box filter).
/// </summary>
public static class AbrReader
{
    private const int MaxTip = 512;

    public static List<BrushPreset> Load(string path, out List<string> notes)
        => Load(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path), out notes);

    public static List<BrushPreset> Load(byte[] bytes, string baseName, out List<string> notes)
    {
        notes = new List<string>();
        var r = new Reader(bytes);
        if (bytes.Length < 4) throw new InvalidDataException("Not an .abr file (too short).");
        int version = r.U16();
        int sub = r.U16();

        if (version is 1 or 2)
            return LoadV12(ref r, version, sub /* count in v1/2 */, baseName, notes);

        if (version is not (6 or 7 or 10))
            throw new InvalidDataException($"Unsupported .abr version {version}.");
        if (sub is not (1 or 2))
            throw new InvalidDataException($"Unsupported .abr v{version} subversion {sub}.");

        // modern container: 8BIM blocks
        var tips = new List<(string id, byte[] tip, int w, int h)>();
        PsDescriptor? desc = null;
        while (r.Pos + 12 <= bytes.Length)
        {
            if (r.ReadAscii(4) != "8BIM") break;
            string key = r.ReadAscii(4);
            long len = r.U32();
            long next = r.Pos + len + ((len & 3) != 0 ? 4 - (len & 3) : 0);
            switch (key)
            {
                case "samp": ReadSamp(ref r, r.Pos + len, sub, tips, notes); break;
                case "desc":
                    try { desc = PsDescriptor.Parse(ref r); }
                    catch { notes.Add("brush parameter block unreadable — defaults used."); }
                    break;
            }
            if (next > bytes.Length) break;
            r.Pos = next;
        }

        var presets = BuildPresets(tips, desc, baseName, notes);
        if (presets.Count == 0) throw new InvalidDataException("No importable brushes found in this .abr.");
        return presets;
    }

    // --------------------------------------------------------------- v1/v2

    private static List<BrushPreset> LoadV12(ref Reader r, int version, int count, string baseName, List<string> notes)
    {
        var presets = new List<BrushPreset>();
        for (int i = 0; i < count && r.Pos + 6 <= r.Bytes.Length; i++)
        {
            int type = r.U16();
            long size = r.U32();
            long end = r.Pos + size;
            try
            {
                if (type == 1)        // computed round brush
                {
                    r.Skip(4);        // misc
                    int spacing = r.U16();
                    string name = version == 2 ? ReadUnicode(ref r) : "";
                    r.Skip(1);        // antialiasing
                    int diameter = r.U16();
                    int hardness = r.U16();
                    int angle = (short)r.U16();
                    int roundness = r.U16();
                    presets.Add(new BrushPreset
                    {
                        Name = name.Length > 0 ? name : $"{baseName} {presets.Count + 1}",
                        Radius = Math.Max(1, diameter) / 2f,
                        Hardness = Math.Clamp(hardness / 100f, 0f, 1f),
                        Spacing = Math.Clamp(spacing / 100f, 0f, 10f),
                        Angle = angle,
                        Roundness = Math.Clamp(roundness / 100f, 0.025f, 1f),
                    });
                }
                else if (type == 2)   // sampled brush
                {
                    r.Skip(4);        // misc
                    int spacing = r.U16();
                    string name = version == 2 ? ReadUnicode(ref r) : "";
                    r.Skip(1);        // antialiasing
                    r.Skip(8);        // short bounds
                    long top = r.U32(), left = r.U32(), bottom = r.U32(), right = r.U32();
                    int depth = r.U16();
                    int w = (int)(right - left), h = (int)(bottom - top);
                    var tip = ReadTipRows(ref r, w, h, depth, compressed: r.U8() != 0);
                    var (dt, dw, dh) = Downscale(tip, w, h);
                    presets.Add(new BrushPreset
                    {
                        Name = name.Length > 0 ? name : $"{baseName} {presets.Count + 1}",
                        Radius = Math.Max(w, h) / 2f,
                        Spacing = Math.Clamp(spacing / 100f, 0f, 10f),
                        Tip = dt, TipW = dw, TipH = dh,
                    });
                }
                else notes.Add($"brush {i + 1}: unknown type {type} skipped.");
            }
            catch { notes.Add($"brush {i + 1}: unreadable, skipped."); }
            r.Pos = end;
        }
        if (presets.Count == 0) throw new InvalidDataException("No importable brushes found in this .abr.");
        return presets;
    }

    // --------------------------------------------------------------- v6 samp

    private static void ReadSamp(ref Reader r, long end, int sub, List<(string, byte[], int, int)> tips, List<string> notes)
    {
        int n = 0;
        while (r.Pos + 4 < end)
        {
            long size = r.U32();
            long brushEnd = r.Pos + size + ((size & 3) != 0 ? 4 - (size & 3) : 0);
            n++;
            try
            {
                int idLen = r.U8();
                string id = r.ReadAscii(idLen);
                r.Skip(sub == 1 ? 10 : 264);   // unknown header (subversion 2 carries an extra block)
                long top = r.U32(), left = r.U32(), bottom = r.U32(), right = r.U32();
                int depth = r.U16();
                bool compressed = r.U8() != 0;
                int w = (int)(right - left), h = (int)(bottom - top);
                if (w <= 0 || h <= 0 || w > 20000 || h > 20000)
                    throw new InvalidDataException("bad tip bounds");
                var tip = ReadTipRows(ref r, w, h, depth, compressed);
                var (dt, dw, dh) = Downscale(tip, w, h);
                tips.Add((id, dt, dw, dh));
            }
            catch { notes.Add($"sampled tip {n}: unreadable, skipped."); }
            if (brushEnd <= r.Pos || brushEnd > end) break;
            r.Pos = brushEnd;
        }
    }

    /// <summary>Greyscale tip rows: raw, or PackBits RLE with per-row u16 byte counts (8/16-bit).</summary>
    private static byte[] ReadTipRows(ref Reader r, int w, int h, int depth, bool compressed)
    {
        int bpp = depth == 16 ? 2 : 1;
        int rowBytes = w * bpp;
        var raw = new byte[rowBytes * h];
        if (!compressed)
        {
            r.ReadInto(raw, Math.Min(raw.Length, (int)(r.Bytes.Length - r.Pos)));
        }
        else
        {
            var counts = new int[h];
            for (int y = 0; y < h; y++) counts[y] = r.U16();
            for (int y = 0; y < h; y++)
            {
                long rend = r.Pos + counts[y];
                var dst = raw.AsSpan(y * rowBytes, rowBytes);
                int o = 0;
                while (r.Pos < rend && o < dst.Length)
                {
                    sbyte c = (sbyte)r.U8();
                    if (c >= 0) { int l = Math.Min(c + 1, dst.Length - o); for (int k = 0; k < l; k++) dst[o++] = r.U8(); }
                    else if (c != -128) { byte v = r.U8(); int l = Math.Min(1 - c, dst.Length - o); for (int k = 0; k < l; k++) dst[o++] = v; }
                }
                r.Pos = rend;
            }
        }
        if (bpp == 1) return raw;
        var plane = new byte[w * h];
        for (int i = 0; i < plane.Length; i++) plane[i] = raw[i * 2];
        return plane;
    }

    private static (byte[] tip, int w, int h) Downscale(byte[] tip, int w, int h)
    {
        if (Math.Max(w, h) <= MaxTip) return (tip, w, h);
        float k = MaxTip / (float)Math.Max(w, h);
        int nw = Math.Max(1, (int)(w * k)), nh = Math.Max(1, (int)(h * k));
        var dst = new byte[nw * nh];
        for (int y = 0; y < nh; y++)
        {
            int sy0 = y * h / nh, sy1 = Math.Max(sy0 + 1, (y + 1) * h / nh);
            for (int x = 0; x < nw; x++)
            {
                int sx0 = x * w / nw, sx1 = Math.Max(sx0 + 1, (x + 1) * w / nw);
                int sum = 0, cnt = 0;
                for (int sy = sy0; sy < sy1; sy++)
                for (int sx = sx0; sx < sx1; sx++) { sum += tip[sy * w + sx]; cnt++; }
                dst[y * nw + x] = (byte)(sum / cnt);
            }
        }
        return (dst, nw, nh);
    }

    // --------------------------------------------------------------- presets from samp + desc

    private static List<BrushPreset> BuildPresets(
        List<(string id, byte[] tip, int w, int h)> tips, PsDescriptor? desc, string baseName, List<string> notes)
    {
        var presets = new List<BrushPreset>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (desc?.Get("Brsh") is List<object> list)
        {
            foreach (var item in list)
            {
                if (item is not PsDescriptor b) continue;
                var p = new BrushPreset { Name = b.GetString("Nm  ") ?? $"{baseName} {presets.Count + 1}" };
                // tip params live in the nested 'Brsh' descriptor
                var tipDesc = b.Get("Brsh") as PsDescriptor ?? b;
                if (tipDesc.GetNumber("Dmtr") is { } dia) p.Radius = (float)Math.Max(0.5, dia / 2);
                if (tipDesc.GetNumber("Angl") is { } ang) p.Angle = (float)ang;
                if (tipDesc.GetNumber("Rndn") is { } rnd) p.Roundness = Math.Clamp((float)(rnd / 100), 0.025f, 1f);
                if (tipDesc.GetNumber("Spcn") is { } spc) p.Spacing = Math.Clamp((float)(spc / 100), 0f, 10f);
                if (tipDesc.GetNumber("Hrdn") is { } hrd) p.Hardness = Math.Clamp((float)(hrd / 100), 0f, 1f);

                if (tipDesc.GetString("sampledData") is { } id)
                {
                    var t = tips.FirstOrDefault(x => x.id.Trim() == id.Trim());
                    if (t.tip is null && tips.Count > 0)
                        t = tips.Count > presets.Count ? tips[presets.Count] : tips[0];   // order fallback
                    if (t.tip is not null)
                    {
                        p.Tip = t.tip; p.TipW = t.w; p.TipH = t.h;
                        if (p.Radius <= 0.5f) p.Radius = Math.Max(t.w, t.h) / 2f;
                        used.Add(t.id);
                    }
                }
                if (b.Has("useTipDynamics") || b.Has("useScatter") || b.Has("dualBrush") || b.Has("useTexture"))
                    notes.Add($"\"{p.Name}\": PS-engine dynamics (scatter/texture/dual brush curves) not imported.");
                presets.Add(p);
            }
        }

        // tips with no descriptor entry (or no/unreadable desc block): import with defaults
        int extra = 0;
        foreach (var (id, tip, w, h) in tips)
        {
            if (used.Contains(id)) continue;
            if (desc is not null && presets.Any(p => ReferenceEquals(p.Tip, tip))) continue;
            extra++;
            presets.Add(new BrushPreset
            {
                Name = $"{baseName} tip {extra}",
                Radius = Math.Max(w, h) / 2f,
                Spacing = 0.25f,
                Tip = tip, TipW = w, TipH = h,
            });
        }
        return presets;
    }

    private static string ReadUnicode(ref Reader r)
    {
        int n = (int)r.U32();
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++) sb.Append((char)r.U16());
        return sb.ToString().TrimEnd('\0');
    }

    // --------------------------------------------------------------- PS descriptor (subset)

    /// <summary>Minimal Photoshop descriptor parser: keys → values (double / string / bool /
    /// nested descriptor / list). Enough to pull brush params out of the 'desc' block.</summary>
    private sealed class PsDescriptor
    {
        private readonly Dictionary<string, object?> _items = new();

        public object? Get(string key) => _items.GetValueOrDefault(key);
        public bool Has(string key) => _items.ContainsKey(key);
        public string? GetString(string key) => _items.GetValueOrDefault(key) as string;
        public double? GetNumber(string key) => _items.GetValueOrDefault(key) switch
        {
            double d => d, int i => i, long l => l, _ => null,
        };

        public static PsDescriptor Parse(ref Reader r)
        {
            r.Skip(4);   // descriptor version (16)
            return ReadDescriptor(ref r);
        }

        private static PsDescriptor ReadDescriptor(ref Reader r)
        {
            ReadUnicodeStr(ref r);   // class name
            ReadKey(ref r);          // class id
            var d = new PsDescriptor();
            int count = (int)r.U32();
            for (int i = 0; i < count; i++)
            {
                string key = ReadKey(ref r);
                d._items[key] = ReadValue(ref r);
            }
            return d;
        }

        private static object? ReadValue(ref Reader r)
        {
            string type = r.ReadAscii(4);
            switch (type)
            {
                case "Objc": case "GlbO": return ReadDescriptor(ref r);
                case "VlLs":
                {
                    int n = (int)r.U32();
                    var list = new List<object>(n);
                    for (int i = 0; i < n; i++) if (ReadValue(ref r) is { } v) list.Add(v);
                    return list;
                }
                case "doub": return r.F64();
                case "UntF": r.Skip(4); return r.F64();
                case "TEXT": return ReadUnicodeStr(ref r);
                case "enum": ReadKey(ref r); return ReadKey(ref r);
                case "long": return (int)r.U32();
                case "comp": { long hi = r.U32(); return (long)((ulong)hi << 32 | r.U32()); }
                case "bool": return r.U8() != 0;
                case "type": case "GlbC": ReadUnicodeStr(ref r); ReadKey(ref r); return null;
                case "alis": case "tdta": { long n = r.U32(); r.Skip((int)n); return null; }
                case "obj ":
                {
                    int n = (int)r.U32();
                    for (int i = 0; i < n; i++) ReadReferenceItem(ref r);
                    return null;
                }
                default: throw new InvalidDataException($"Unknown descriptor type '{type}'.");
            }
        }

        private static void ReadReferenceItem(ref Reader r)
        {
            string t = r.ReadAscii(4);
            switch (t)
            {
                case "prop": ReadUnicodeStr(ref r); ReadKey(ref r); ReadKey(ref r); break;
                case "Clss": ReadUnicodeStr(ref r); ReadKey(ref r); break;
                case "Enmr": ReadUnicodeStr(ref r); ReadKey(ref r); ReadKey(ref r); ReadKey(ref r); break;
                case "rele": ReadUnicodeStr(ref r); ReadKey(ref r); r.Skip(4); break;
                case "Idnt": case "indx": r.Skip(4); break;
                case "name": ReadUnicodeStr(ref r); ReadKey(ref r); ReadUnicodeStr(ref r); break;
                default: throw new InvalidDataException($"Unknown reference type '{t}'.");
            }
        }

        private static string ReadKey(ref Reader r)
        {
            int len = (int)r.U32();
            return r.ReadAscii(len == 0 ? 4 : len);
        }

        private static string ReadUnicodeStr(ref Reader r)
        {
            int n = (int)r.U32();
            var sb = new StringBuilder(n);
            for (int i = 0; i < n; i++) sb.Append((char)r.U16());
            return sb.ToString().TrimEnd('\0');
        }
    }

    // --------------------------------------------------------------- byte reader

    internal struct Reader
    {
        public readonly byte[] Bytes;
        public long Pos;

        public Reader(byte[] bytes) { Bytes = bytes; Pos = 0; }

        public byte U8() => Bytes[Pos++];
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan((int)Pos)); Pos += 2; return v; }
        public uint U32() { var v = BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)Pos)); Pos += 4; return v; }
        public double F64() { var v = BinaryPrimitives.ReadDoubleBigEndian(Bytes.AsSpan((int)Pos)); Pos += 8; return v; }
        public void Skip(int n) => Pos += n;

        public string ReadAscii(int n)
        {
            var s = Encoding.ASCII.GetString(Bytes, (int)Pos, n);
            Pos += n;
            return s;
        }

        public void ReadInto(byte[] dst, int n)
        {
            Array.Copy(Bytes, Pos, dst, 0, n);
            Pos += n;
        }
    }
}
