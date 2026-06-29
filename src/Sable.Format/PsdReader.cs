using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Sable.Core;
using Sable.Engine;
using Sable.Engine.Layers;

namespace Sable.Format;

/// <summary>
/// Focused Photoshop PSD importer (improvement plan §9). Maps what Sable's model
/// supports — layers, groups (incl. pass-through), layer masks, blend modes, opacity,
/// fill opacity, visibility, clipping masks, per-layer bounds/offsets — and rasterises
/// or skips the rest, reporting every lossy mapping in <c>warnings</c>.
/// Supports 8/16-bit RGB and Grayscale, compression raw/RLE/zip/zip-prediction,
/// 16-bit layer data in the Lr16 tagged block. PSB (version 2), CMYK/Lab/Indexed and
/// 32-bit are rejected with a clear error.
/// </summary>
public static class PsdReader
{
    /// <summary>Open a .psd file as a Sable document. Lossy/skipped features are reported in
    /// <paramref name="warnings"/>; <paramref name="fonts"/> lists the PostScript font names
    /// used by (rasterised) text layers so the app can flag missing ones.</summary>
    public static Document Load(string path, out List<string> warnings, out List<string> fonts)
        => Load(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path), out warnings, out fonts);

    public static Document Load(byte[] bytes, string docName, out List<string> warnings)
        => Load(bytes, docName, out warnings, out _);

    /// <summary>Total-pixel import budget — layers are monolithic CPU float buffers, so a huge canvas would
    /// OOM. ~80 MP ≈ 1.3 GB per RGBA32F layer. Lifted once tiled CPU storage lands (PSB_FEASIBILITY.md).</summary>
    private const long MaxImportPixels = 80_000_000;

    /// <summary>Additional-layer-info keys whose length field is 8 bytes in PSB (large document format).</summary>
    private static bool Psb8ByteKey(string key) => key is "LMsk" or "Lr16" or "Lr32" or "Layr" or "Mt16"
        or "Mt32" or "Mtrn" or "Alph" or "FMsk" or "lnk2" or "FEid" or "FXid" or "PxSD" or "cinf";

    public static Document Load(byte[] bytes, string docName, out List<string> warnings, out List<string> fonts)
    {
        warnings = new List<string>();
        var fontSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        fonts = new List<string>();
        var r = new Reader(bytes);

        // ---- header ----
        if (bytes.Length < 26 || r.ReadAscii(4) != "8BPS")
            throw new InvalidDataException("Not a PSD file (missing 8BPS signature).");
        int version = r.U16();
        if (version != 1 && version != 2)
            throw new InvalidDataException($"Unsupported PSD/PSB version {version}.");
        bool psb = version == 2;   // PSB = large document format: 64-bit section/channel lengths (PSB_FEASIBILITY.md)
        r.Skip(6);
        int headerChannels = r.U16();
        int height = (int)r.U32();
        int width = (int)r.U32();
        int depth = r.U16();
        int colorMode = r.U16();

        int maxDim = psb ? 300_000 : 65_535;
        if (width <= 0 || height <= 0 || width > maxDim || height > maxDim)
            throw new InvalidDataException($"Unsupported canvas size {width}x{height}.");
        // Memory guard: layers are monolithic CPU float buffers (W·H·16 B), so cap total pixels until
        // tiled CPU storage lands (PSB_FEASIBILITY.md Tier 2). Protects against OOM on huge PSBs.
        if ((long)width * height > MaxImportPixels)
            throw new InvalidDataException(
                $"Document too large to import ({width}×{height} = {(long)width * height / 1_000_000} MP). " +
                $"Sable's in-memory limit is {MaxImportPixels / 1_000_000} MP — large-document support needs tiled CPU storage.");
        if (colorMode != 3 && colorMode != 1)
            throw new InvalidDataException($"Unsupported colour mode {ModeName(colorMode)} — only RGB and Grayscale PSDs import.");
        if (depth != 8 && depth != 16)
            throw new InvalidDataException($"Unsupported bit depth {depth} — only 8- and 16-bit PSDs import.");
        if (depth == 16)
            warnings.Add("16-bit document converted to 8-bit.");

        // ---- colour mode data: skip ----
        r.Skip((int)r.U32());
        // ---- image resources: scan for the embedded ICC profile (resource 1039), skip the rest ----
        byte[]? icc = ParseImageResources(ref r, warnings);

        // ---- layer and mask info ---- (PSB: section + layer-info lengths are 8-byte)
        long lmiLen = r.Len(psb);
        long lmiEnd = r.Pos + lmiLen;
        List<LayerRecord> records = new();
        if (lmiLen > 0)
        {
            long liLen = r.Len(psb);
            long liEnd = r.Pos + liLen;
            if (liLen > 0)
                records = ParseLayerInfo(ref r, depth, colorMode, warnings, fontSet, width, height, psb);
            r.Pos = liEnd;

            // 16-bit files keep their layers in the Lr16 global tagged block instead.
            if (records.Count == 0 && r.Pos < lmiEnd)
            {
                if (r.Pos + 4 <= lmiEnd) r.Skip((int)r.U32());   // global layer mask info (always 4-byte)
                while (r.Pos + 12 <= lmiEnd)
                {
                    string sig = r.ReadAscii(4);
                    if (sig != "8BIM" && sig != "8B64") break;
                    string key = r.ReadAscii(4);
                    long len = psb && Psb8ByteKey(key) ? (long)r.U64() : r.U32();
                    long next = r.Pos + len + ((len & 3) != 0 ? 4 - (len & 3) : 0);   // global blocks pad to 4
                    if (key is "Lr16" or "Lr32")
                    {
                        if (key == "Lr32")
                            throw new InvalidDataException("32-bit PSD layers are not supported — only 8- and 16-bit.");
                        records = ParseLayerInfo(ref r, depth, colorMode, warnings, fontSet, width, height, psb);
                    }
                    if (next > lmiEnd) break;
                    r.Pos = next;
                }
            }
        }
        r.Pos = lmiEnd;

        var doc = new Document(width, height);
        if (icc is not null)
        {
            doc.IccProfile = icc;
            doc.IccProfileName = IccDescription(icc);
        }

        if (records.Count > 0)
        {
            BuildTree(records, doc.Layers, width, height, colorMode, warnings);
            if (doc.Layers.Count == 0)
                warnings.Add("No importable layers — document is empty.");
        }
        else
        {
            // flattened file (or no layer info): import the merged composite as one layer
            var px = ReadComposite(ref r, width, height, depth, headerChannels, colorMode);
            var layer = new PixelLayer(width, height, docName);
            layer.SetBufferFromBytes(width, height, px);   // RGBA8 → RGBA32F (bit-depth pipeline)
            doc.Layers.Add(layer);
        }

        fonts.AddRange(fontSet);
        return doc;
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>Scan the image-resources section (a length-prefixed sequence of "8BIM" blocks) for
    /// the embedded ICC profile (resource ID 1039 / 0x040F) and return its raw bytes; advances the
    /// reader past the whole section. Every other resource is skipped. Robust to truncation.</summary>
    private static byte[]? ParseImageResources(ref Reader r, List<string> warnings)
    {
        long len = r.U32();
        long end = r.Pos + len;
        if (len <= 0 || end > r.Bytes.Length) { r.Pos = Math.Min(end, r.Bytes.Length); return null; }
        byte[]? icc = null;
        while (r.Pos + 12 <= end)
        {
            if (r.ReadAscii(4) != "8BIM") break;       // out of sync → stop scanning
            int id = r.U16();
            // Pascal name: 1 length byte + bytes, padded so the (name+1) span is even.
            int nameLen = r.U8();
            r.Skip(nameLen);
            if (((nameLen + 1) & 1) != 0) r.Skip(1);   // pad name field to even
            long size = r.U32();
            long dataStart = r.Pos;
            long next = dataStart + size + (size & 1);   // data padded to even
            if (next > end) break;
            if (id == 0x040F && size > 0 && dataStart + size <= r.Bytes.Length)   // 1039 = ICC profile
            {
                icc = new byte[size];
                Array.Copy(r.Bytes, dataStart, icc, 0, size);
            }
            r.Pos = next;
        }
        r.Pos = end;
        if (icc is not null && icc.Length < 128) { warnings.Add("embedded colour profile too small — ignored."); icc = null; }
        return icc;
    }

    /// <summary>Best-effort ICC profile name: the 'desc' tag's ASCII text (ICC profiles store a
    /// human-readable description). Returns null when the tag table can't be read.</summary>
    private static string? IccDescription(byte[] icc)
    {
        try
        {
            if (icc.Length < 132) return null;
            int tagCount = (int)BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(128));
            int p = 132;
            for (int i = 0; i < tagCount && p + 12 <= icc.Length; i++, p += 12)
            {
                string sig = Encoding.ASCII.GetString(icc, p, 4);
                int off = (int)BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(p + 4));
                int size = (int)BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(p + 8));
                if (sig != "desc" || off <= 0 || off + 12 > icc.Length) continue;
                string type = Encoding.ASCII.GetString(icc, off, 4);
                if (type == "desc")   // ICCv2 textDescriptionType: count(4) at off+8, ASCII at off+12
                {
                    int n = (int)BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(off + 8));
                    n = Math.Max(0, Math.Min(n, Math.Min(size, icc.Length - off - 12)));
                    return Encoding.ASCII.GetString(icc, off + 12, n).TrimEnd('\0', ' ');
                }
                if (type == "mluc" && off + 28 <= icc.Length)   // ICCv4 multiLocalizedUnicodeType (UTF-16BE)
                {
                    int recs = (int)BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(off + 8));
                    if (recs <= 0) return null;
                    int sLen = (int)BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(off + 20));
                    int sOff = (int)BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(off + 24));
                    if (sOff <= 0 || off + sOff + sLen > icc.Length) return null;
                    return Encoding.BigEndianUnicode.GetString(icc, off + sOff, sLen).TrimEnd('\0', ' ');
                }
            }
        }
        catch { /* malformed profile → no name */ }
        return null;
    }

    private sealed class ChannelData
    {
        public int Id;
        public long RawLength;
        public byte[]? Plane;   // decoded, 8-bit, rect-sized
    }

    private sealed class LayerRecord
    {
        public int Top, Left, Bottom, Right;
        public List<ChannelData> Channels = new();
        public string BlendKey = "norm";
        public byte Opacity = 255;
        public byte FillOpacity = 255;
        public bool Clipping;
        public bool Visible = true;
        public string Name = "Layer";
        public int SectionType;               // lsct: 0 none, 1 open folder, 2 closed folder, 3 bounding divider
        public bool HasMask, MaskDisabled;
        public int MaskTop, MaskLeft, MaskBottom, MaskRight;
        public byte MaskDefault = 255;
        public string? UnmappableKey;         // adjustment / fill-layer key when the layer has no raster content
        public PsDesc? AdjustmentDesc;        // parsed descriptor for a mapped adjustment key (brit/levl/…)
        public List<string> Notes = new();    // per-layer rasterisation notes
        public List<LayerEffect> Effects = new();                     // mapped lfx2 layer effects
        public List<List<(double X, double Y)>>? VectorContours;      // vmsk/vsms bezier path, doc px (flattened polyline)
        public List<VectorContour>? VectorKnots;                       // vmsk/vsms bezier knots (handles preserved) for the ShapeLayer bridge
        public (byte r, byte g, byte b)? SoCoColor;                   // solid-colour fill layer
        public PsdTextInfo? TextInfo;                                 // TySh → editable TextLayer
        public Sable.Engine.Layers.SmartObjectInfo? SmartObject;      // SoLd/SoLE → captured placement + identity (Tier 1)
        public bool HasLrFx, HasLfx2;
        public int W => Math.Max(0, Right - Left);
        public int H => Math.Max(0, Bottom - Top);
    }

    private static List<LayerRecord> ParseLayerInfo(ref Reader r, int depth, int colorMode, List<string> warnings, SortedSet<string> fonts, int docW, int docH, bool psb = false)
    {
        int count = (short)r.U16();
        if (count < 0) count = -count;        // negative = composite has transparency; layer parsing identical
        var records = new List<LayerRecord>(count);

        for (int i = 0; i < count; i++)
        {
            var rec = new LayerRecord
            {
                Top = (int)r.U32(), Left = (int)r.U32(), Bottom = (int)r.U32(), Right = (int)r.U32(),
            };
            int chCount = r.U16();
            for (int c = 0; c < chCount; c++)
                rec.Channels.Add(new ChannelData { Id = (short)r.U16(), RawLength = r.Len(psb) });   // PSB: 8-byte channel length

            if (r.ReadAscii(4) != "8BIM") throw new InvalidDataException("Corrupt PSD: bad blend-mode signature.");
            rec.BlendKey = r.ReadAscii(4);
            rec.Opacity = r.U8();
            rec.Clipping = r.U8() != 0;
            byte flags = r.U8();
            rec.Visible = (flags & 0x02) == 0;
            r.Skip(1);

            long extraLen = r.U32();
            long extraEnd = r.Pos + extraLen;

            // layer mask / adjustment-layer data
            long maskSize = r.U32();
            long maskEnd = r.Pos + maskSize;
            if (maskSize >= 20)
            {
                rec.HasMask = true;
                rec.MaskTop = (int)r.U32(); rec.MaskLeft = (int)r.U32();
                rec.MaskBottom = (int)r.U32(); rec.MaskRight = (int)r.U32();
                rec.MaskDefault = r.U8();
                byte mflags = r.U8();
                rec.MaskDisabled = (mflags & 0x02) != 0;
            }
            r.Pos = maskEnd;

            r.Skip((int)r.U32());             // blending ranges

            int nameLen = r.U8();             // Pascal name, (1+len) padded to 4
            rec.Name = r.ReadAscii(nameLen);
            int pad = (1 + nameLen) % 4;
            if (pad != 0) r.Skip(4 - pad);

            // tagged blocks (padded to 2)
            while (r.Pos + 12 <= extraEnd)
            {
                string sig = r.ReadAscii(4);
                if (sig != "8BIM" && sig != "8B64") break;
                string key = r.ReadAscii(4);
                long len = psb && Psb8ByteKey(key) ? (long)r.U64() : r.U32();   // PSB: 8-byte length for the large-doc key set
                long next = r.Pos + len + (len & 1);
                if (next > extraEnd) { break; }

                switch (key)
                {
                    case "lsct":
                        if (len >= 4) rec.SectionType = (int)r.U32();
                        break;
                    case "luni":
                        if (len >= 4)
                        {
                            int n = (int)r.U32();
                            var sb = new StringBuilder(n);
                            for (int k = 0; k < n && r.Pos + 2 <= next; k++) sb.Append((char)r.U16());
                            if (sb.Length > 0) rec.Name = sb.ToString();
                        }
                        break;
                    case "iOpa":
                        if (len >= 1) rec.FillOpacity = r.U8();
                        break;
                    case "TySh":
                        ExtractFonts(r.Bytes.AsSpan((int)r.Pos, (int)Math.Min(len, r.Bytes.Length - r.Pos)), fonts);
                        try
                        {
                            rec.TextInfo = ParseTySh(ref r);
                            rec.Notes.Add("text layer converted to editable text");
                            foreach (var n in rec.TextInfo.Notes) rec.Notes.Add(n);
                        }
                        catch
                        {
                            rec.TextInfo = null;
                            rec.Notes.Add("text layer rasterised (style data unreadable)");
                        }
                        break;
                    case "SoLd" or "SoLE":
                        try
                        {
                            rec.SmartObject = ParseSmartObject(ref r);
                            string id = rec.SmartObject.Identity;
                            rec.Notes.Add(string.IsNullOrEmpty(id)
                                ? "smart object rasterised (placement preserved; embedded-source editing pending)"
                                : $"smart object '{id}' rasterised (placement + identity preserved; embedded-source editing pending)");
                        }
                        catch { rec.Notes.Add("smart object rasterised"); }
                        break;
                    case "PlLd":
                        rec.Notes.Add("smart object rasterised");   // legacy placed-layer format (non-descriptor) — Tier 1 metadata pending
                        break;
                    case "vmsk" or "vsms":
                        try
                        {
                            rec.VectorKnots = ParseVectorMaskKnots(ref r, next - (len & 1), docW, docH);
                            rec.VectorContours = rec.VectorKnots is not null ? KnotsToPolylines(rec.VectorKnots) : null;
                            if (rec.VectorContours is not null) rec.Notes.Add("vector mask rasterised");
                        }
                        catch { rec.Notes.Add("vector mask unreadable"); }
                        break;
                    case "lrFX":
                        rec.HasLrFx = true;
                        break;
                    case "lfx2":
                        try
                        {
                            rec.Effects = ParseLfx2(ref r, rec);
                            rec.HasLfx2 = true;
                        }
                        catch { rec.Notes.Add("layer effects unreadable"); }
                        break;
                    case "SoCo":
                        try
                        {
                            r.Skip(4);   // descriptor version
                            rec.SoCoColor = DescColor(PsDesc.Parse(ref r).Obj("Clr "));
                            if (rec.SoCoColor is null) rec.UnmappableKey = key;
                        }
                        catch { rec.UnmappableKey = key; }   // unparseable → skip-with-note path
                        break;
                    default:
                        if (MappableAdjustmentKeys.Contains(key))
                        {
                            try
                            {
                                r.Skip(4);   // descriptor version
                                rec.AdjustmentDesc = PsDesc.Parse(ref r);
                                rec.UnmappableKey = key;   // marks the layer as an adjustment (not skipped now)
                            }
                            catch { rec.Notes.Add("adjustment layer unreadable"); }
                        }
                        else if (AdjustmentKeys.Contains(key)) rec.UnmappableKey = key;
                        break;
                }
                r.Pos = next;
            }
            r.Pos = extraEnd;
            if (rec.HasLrFx && !rec.HasLfx2) rec.Notes.Add("legacy layer effects not imported");
            records.Add(rec);
        }

        // channel image data follows, in the same layer order
        foreach (var rec in records)
        {
            foreach (var ch in rec.Channels)
            {
                long chEnd = r.Pos + ch.RawLength;
                int w = ch.Id == -2 ? Math.Max(0, rec.MaskRight - rec.MaskLeft) : rec.W;
                int h = ch.Id == -2 ? Math.Max(0, rec.MaskBottom - rec.MaskTop) : rec.H;
                if (ch.Id == -3) { r.Pos = chEnd; continue; }   // real (vector) mask composite — ignored
                try
                {
                    if (ch.RawLength >= 2 && w > 0 && h > 0)
                        ch.Plane = DecodeChannel(ref r, w, h, depth, chEnd);
                }
                catch
                {
                    rec.Notes.Add("channel data unreadable");
                }
                r.Pos = chEnd;
            }
        }
        return records;
    }

    /// <summary>
    /// Best-effort font-name extraction from a TySh (type tool) block. The text engine
    /// data is a PostScript-style blob whose <c>/FontSet</c> array lists every font as
    /// <c>/Name (…)</c> (UTF-16BE with BOM, or ASCII). We scan each FontSet region with a
    /// tiny escape-aware tokenizer — names elsewhere (style sheets) are NOT collected.
    /// </summary>
    public static void ExtractFonts(ReadOnlySpan<byte> data, ISet<string> fonts)
    {
        ReadOnlySpan<byte> marker = "/FontSet"u8;
        int from = 0;
        while (true)
        {
            int hit = data[from..].IndexOf(marker);
            if (hit < 0) return;
            int i = from + hit + marker.Length;

            // expect whitespace then '[' — scan that array, skipping strings, until its ']'
            while (i < data.Length && data[i] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t') i++;
            if (i >= data.Length || data[i] != (byte)'[') { from = i; continue; }
            i++;
            int level = 1;
            while (i < data.Length && level > 0)
            {
                byte b = data[i];
                if (b == (byte)'(')
                {
                    // a parenthesised string; check whether it follows "/Name"
                    bool isName = data[Math.Max(0, i - 12)..i].IndexOf("/Name"u8) >= 0;
                    int s = ++i;
                    var raw = new List<byte>(32);
                    while (i < data.Length && data[i] != (byte)')')
                    {
                        if (data[i] == (byte)'\\' && i + 1 < data.Length) i++;   // escaped char
                        raw.Add(data[i]);
                        i++;
                    }
                    i++;   // past ')'
                    if (isName && raw.Count > 0)
                    {
                        string name = raw.Count >= 2 && raw[0] == 0xFE && raw[1] == 0xFF
                            ? Encoding.BigEndianUnicode.GetString(raw.ToArray(), 2, raw.Count - 2)
                            : Encoding.ASCII.GetString(raw.ToArray());
                        name = name.Trim('\0', ' ');
                        // AdobeInvisFont is Photoshop's internal placeholder — never a real font
                        if (name.Length > 0 && !name.Equals("AdobeInvisFont", StringComparison.OrdinalIgnoreCase))
                            fonts.Add(name);
                    }
                    continue;
                }
                if (b == (byte)'[') level++;
                else if (b == (byte)']') level--;
                i++;
            }
            from = i;
            if (from >= data.Length) return;
        }
    }

    // ------------------------------------------------------------------ text layers (TySh)

    /// <summary>Style data pulled from a TySh block (descriptor + EngineData) — enough to
    /// rebuild the text as an editable Sable <see cref="TextLayer"/>.</summary>
    public sealed class PsdTextInfo
    {
        public string Text = "";
        public string PsFontName = "";
        public double Size = 24, Tracking, Leading;     // tracking in 1/1000 em, leading in pt
        public byte R, G, B;
        public bool Bold, Italic, Underline, Strike;
        public int Justification;                        // PS: 0 left, 1 right, 2 center, 3+ justified
        public double BoxW;                              // 0 = point text
        public double ScaleX = 1, ScaleY = 1;            // baked text transform (PS scales text via the matrix)
        public double RotationDeg;                       // baked text rotation (from the same matrix)
        public List<string> Notes = new();              // fidelity warnings (multi-style, warp, …)
    }

    /// <summary>TySh: version, 6×f64 transform, text version, descriptor version, then the text
    /// descriptor ('Txt ' string + 'EngineData' styling blob). Warp/bounds after it are skipped.</summary>
    private static PsdTextInfo ParseTySh(ref Reader r)
    {
        r.Skip(2);   // version (1)
        Span<double> xf = stackalloc double[6];
        for (int i = 0; i < 6; i++) xf[i] = r.F64();
        r.Skip(2);   // text version (50)
        r.Skip(4);   // descriptor version (16)
        var d = PsDesc.Parse(ref r);

        // decompose the (xx xy yx yy) matrix: scale + rotation (PS bakes both into it)
        double sx = Math.Sqrt(xf[0] * xf[0] + xf[1] * xf[1]);
        double sy = Math.Sqrt(xf[2] * xf[2] + xf[3] * xf[3]);
        var info = new PsdTextInfo
        {
            ScaleX = sx > 1e-6 ? sx : 1.0,
            ScaleY = sy > 1e-6 ? sy : 1.0,
            RotationDeg = Math.Atan2(xf[1], xf[0]) * 180.0 / Math.PI,
            Text = (d.Get("Txt ") as string ?? "").Replace("\r\n", "\n").Replace('\r', '\n'),
        };
        if (info.Text.Length == 0) throw new InvalidDataException("empty text");
        if (d.Get("EngineData") is byte[] ed) ApplyEngineData(ed, info);
        return info;
    }

    /// <summary>Walk the parsed EngineData for the FIRST style/paragraph run (Sable text layers
    /// are single-style): font, size, colour, tracking, leading, faux styles, justification, box.</summary>
    private static void ApplyEngineData(byte[] engineData, PsdTextInfo info)
    {
        var root = EngineData.Parse(engineData);

        // font set (PostScript names)
        var fontSet = Walk(root, "ResourceDict", "FontSet") as List<object>;

        // first character style run (fallback: the resource default style sheet)
        var style = Walk(root, "EngineDict", "StyleRun", "RunArray", 0, "StyleSheet", "StyleSheetData") as Dictionary<string, object>
                 ?? Walk(root, "ResourceDict", "StyleSheetSet", 0, "StyleSheetData") as Dictionary<string, object>;
        if (style is not null)
        {
            if (style.TryGetValue("Font", out var fi) && fi is double fidx && fontSet is not null
                && (int)fidx >= 0 && (int)fidx < fontSet.Count
                && Walk(fontSet[(int)fidx], "Name") is string fname)
                info.PsFontName = fname;
            if (style.TryGetValue("FontSize", out var fs) && fs is double size) info.Size = size;
            if (style.TryGetValue("Tracking", out var tr) && tr is double track) info.Tracking = track;
            if (style.TryGetValue("Leading", out var ld) && ld is double lead) info.Leading = lead;
            if (style.TryGetValue("FauxBold", out var fb) && fb is bool b1) info.Bold = b1;
            if (style.TryGetValue("FauxItalic", out var fi2) && fi2 is bool b2) info.Italic = b2;
            if (style.TryGetValue("Underline", out var ul) && ul is bool b3) info.Underline = b3;
            if (style.TryGetValue("Strikethrough", out var st) && st is bool b4) info.Strike = b4;
            if (Walk(style, "FillColor", "Values") is List<object> { Count: >= 4 } v
                && v[1] is double cr && v[2] is double cg && v[3] is double cb)
            {
                info.R = (byte)Math.Clamp(cr * 255, 0, 255);
                info.G = (byte)Math.Clamp(cg * 255, 0, 255);
                info.B = (byte)Math.Clamp(cb * 255, 0, 255);
            }
        }

        if (Walk(root, "EngineDict", "ParagraphRun", "RunArray", 0, "ParagraphSheet", "Properties", "Justification") is double j)
            info.Justification = (int)j;

        // multi-style runs: Sable TextLayer is single-style → flatten to the first run + warn
        if (Walk(root, "EngineDict", "StyleRun", "RunArray") is List<object> { Count: > 1 } runs)
            info.Notes.Add($"text layer has {runs.Count} style runs — flattened to first style.");

        // box (paragraph) text: ShapeType 1 carries BoxBounds [x y w h]
        var shape = Walk(root, "EngineDict", "Rendered", "Shapes", "Children", 0) as Dictionary<string, object>;
        if (shape is not null
            && Walk(shape, "ShapeType") is double stp && (int)stp == 1
            && Walk(shape, "Cookie", "Photoshop", "BoxBounds") is List<object> { Count: >= 4 } bb
            && bb[2] is double bw)
            info.BoxW = bw;

        // warp: PS stores a warp descriptor in the TySh block after EngineData (we skip the bytes,
        // but the EngineData Rendered.Shapes also carries a warp flag). Detect + warn.
        if (Walk(root, "EngineDict", "Rendered", "Shapes", "WarpData") is not null
            || Walk(root, "EngineDict", "Warp") is not null)
            info.Notes.Add("text warp not imported (flattened to un-warped text).");

        // vertical text (orientation flag in EngineDict)
        if (Walk(root, "EngineDict", "Orientation") is double ori && (int)ori == 2)
            info.Notes.Add("vertical text not imported (flattened to horizontal).");

        // OpenType features / baseline shift / super-sub / caps — inspect the first style run.
        if (style is not null)
        {
            if (style.TryGetValue("Baseline", out var bs) && bs is double bsv && Math.Abs(bsv) > 1e-6)
                info.Notes.Add("baseline shift not imported.");
            if (style.TryGetValue("Superscript", out var sup) && sup is bool supb && supb)
                info.Notes.Add("superscript not imported.");
            if (style.TryGetValue("Subscript", out var sub) && sub is bool subb && subb)
                info.Notes.Add("subscript not imported.");
            if (style.TryGetValue("AllCaps", out var ac) && ac is bool acb && acb)
                info.Notes.Add("all-caps not imported.");
            if (style.TryGetValue("SmallCaps", out var sc) && sc is bool scb && scb)
                info.Notes.Add("small-caps not imported.");
            if (style.TryGetValue("OpenType", out var ot) && ot is Dictionary<string, object> { Count: > 0 })
                info.Notes.Add("OpenType features not imported.");
        }
    }

    /// <summary>Path walk over the EngineData object model (string key → dict, int → list index).</summary>
    private static object? Walk(object? node, params object[] path)
    {
        foreach (var step in path)
        {
            node = step switch
            {
                string key when node is Dictionary<string, object> d => d.GetValueOrDefault(key),
                int idx when node is List<object> l && idx >= 0 && idx < l.Count => l[idx],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>Map a PostScript font name ("OpenSans-Bold") to an installed family + style flags.
    /// Falls back to a camel-case split of the base name when nothing matches (the app's
    /// missing-font toast still warns the user).</summary>
    internal static (string family, bool bold, bool italic) MapPsFont(string psName)
    {
        var (bold, italic) = FontMatcher.StyleFlags(psName);
        try { return (FontMatcher.Resolve(psName, Sable.Imaging.TextRaster.Families(), out _), bold, italic); }
        catch { return (FontMatcher.Humanize(psName), bold, italic); }   // no font system (headless)
    }

    /// <summary>EngineData: Photoshop's PostScript-style text blob
    /// (<c>&lt;&lt; /Key value &gt;&gt;</c>, arrays, numbers, bools, UTF-16BE strings).</summary>
    public static class EngineData
    {
        public static Dictionary<string, object> Parse(byte[] b)
        {
            int pos = 0;
            return ReadValue(b, ref pos) as Dictionary<string, object> ?? new();
        }

        private static object? ReadValue(byte[] b, ref int pos)
        {
            SkipWs(b, ref pos);
            if (pos >= b.Length) return null;
            char c = (char)b[pos];

            if (c == '<' && pos + 1 < b.Length && b[pos + 1] == '<')
            {
                pos += 2;
                var dict = new Dictionary<string, object>();
                while (true)
                {
                    SkipWs(b, ref pos);
                    if (pos + 1 < b.Length && b[pos] == '>' && b[pos + 1] == '>') { pos += 2; break; }
                    if (pos >= b.Length || b[pos] != '/') { pos++; continue; }
                    string key = ReadName(b, ref pos);
                    if (ReadValue(b, ref pos) is { } v) dict[key] = v;
                }
                return dict;
            }
            if (c == '[')
            {
                pos++;
                var list = new List<object>();
                while (true)
                {
                    SkipWs(b, ref pos);
                    if (pos >= b.Length) break;
                    if (b[pos] == ']') { pos++; break; }
                    if (ReadValue(b, ref pos) is { } v) list.Add(v);
                    else pos++;
                }
                return list;
            }
            if (c == '/') return ReadName(b, ref pos);      // name used as a value
            if (c == '(') return ReadString(b, ref pos);
            if (c == 't' && Match(b, pos, "true")) { pos += 4; return true; }
            if (c == 'f' && Match(b, pos, "false")) { pos += 5; return false; }
            return ReadNumber(b, ref pos);
        }

        private static void SkipWs(byte[] b, ref int pos)
        {
            while (pos < b.Length && (b[pos] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t')) pos++;
        }

        private static bool Match(byte[] b, int pos, string word)
        {
            if (pos + word.Length > b.Length) return false;
            for (int i = 0; i < word.Length; i++) if (b[pos + i] != word[i]) return false;
            return true;
        }

        private static string ReadName(byte[] b, ref int pos)
        {
            pos++;   // '/'
            int start = pos;
            while (pos < b.Length && (char.IsLetterOrDigit((char)b[pos]) || b[pos] is (byte)'.' or (byte)'_')) pos++;
            return Encoding.ASCII.GetString(b, start, pos - start);
        }

        private static object? ReadNumber(byte[] b, ref int pos)
        {
            int start = pos;
            while (pos < b.Length && ((char)b[pos] is '-' or '+' or '.' or 'e' or 'E' or (>= '0' and <= '9'))) pos++;
            if (pos == start) { pos++; return null; }
            return double.TryParse(Encoding.ASCII.GetString(b, start, pos - start),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? d : null;
        }

        private static string ReadString(byte[] b, ref int pos)
        {
            pos++;   // '('
            var raw = new List<byte>(64);
            while (pos < b.Length && b[pos] != (byte)')')
            {
                if (b[pos] == (byte)'\\' && pos + 1 < b.Length) pos++;   // escaped char
                raw.Add(b[pos]);
                pos++;
            }
            pos++;   // ')'
            if (raw.Count >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(raw.ToArray(), 2, raw.Count - 2);
            return Encoding.ASCII.GetString(raw.ToArray());
        }
    }

    // ------------------------------------------------------------------ layer effects (lfx2)

    /// <summary>Map the lfx2 effects descriptor onto Sable's <see cref="LayerEffect"/> set —
    /// drop shadow / inner shadow / outer+inner glow / colour overlay / stroke / gradient
    /// overlay / bevel. PS-only params (contour curves, noise, textures) are dropped.</summary>
    private static List<LayerEffect> ParseLfx2(ref Reader r, LayerRecord rec)
    {
        var fx = new List<LayerEffect>();
        r.Skip(4);   // object version (0)
        r.Skip(4);   // descriptor version (16)
        var d = PsDesc.Parse(ref r);
        double scale = d.Num("Scl ", 100) / 100.0;
        bool master = d.Get("masterFXSwitch") is not bool m || m;

        void Common(LayerEffect e, PsDesc o)
        {
            e.Enabled = master && o.Flag("enab");
            e.Opacity = (float)Math.Clamp(o.Num("Opct", 100) / 100.0, 0, 1);
            e.BlendMode = MapFxBlend(o.EnumVal("Md  "));
            if (DescColor(o.Obj("Clr ")) is { } c) { e.R = c.r / 255f; e.G = c.g / 255f; e.B = c.b / 255f; }
        }
        void Shadow(LayerEffectKind kind, PsDesc o)
        {
            var e = LayerEffect.Create(kind);
            Common(e, o);
            double dist = o.Num("Dstn", 5) * scale;
            double ang = o.Num("lagl", 120) * Math.PI / 180.0;
            e.OffsetX = (float)(-Math.Cos(ang) * dist);
            e.OffsetY = (float)(Math.Sin(ang) * dist);
            e.Radius = (float)Math.Max(0, o.Num("blur", 5) * scale);
            fx.Add(e);
        }
        void Glow(LayerEffectKind kind, PsDesc o)
        {
            var e = LayerEffect.Create(kind);
            Common(e, o);
            e.Radius = (float)Math.Max(0, o.Num("blur", 5) * scale);
            fx.Add(e);
        }

        // CC-era files store effects as "...Multi" VlLs lists (multiple instances per kind);
        // older files use the singular Objc. Prefer the multi list when present.
        IEnumerable<PsDesc> Entries(string single, string multi)
        {
            if (d.Items(multi) is { } list)
            {
                foreach (var o in list)
                    if (o is PsDesc pd) yield return pd;
                yield break;
            }
            if (d.Obj(single) is { } s) yield return s;
        }

        foreach (var dr in Entries("DrSh", "dropShadowMulti")) Shadow(LayerEffectKind.DropShadow, dr);
        foreach (var ir in Entries("IrSh", "innerShadowMulti")) Shadow(LayerEffectKind.InnerShadow, ir);
        foreach (var og in Entries("OrGl", "outerGlowMulti")) Glow(LayerEffectKind.OuterGlow, og);
        foreach (var ig in Entries("IrGl", "innerGlowMulti")) Glow(LayerEffectKind.InnerGlow, ig);
        foreach (var so in Entries("SoFi", "solidFillMulti"))
        {
            var e = LayerEffect.Create(LayerEffectKind.ColorOverlay);
            Common(e, so);
            fx.Add(e);
        }
        foreach (var fr in Entries("FrFX", "frameFXMulti"))
        {
            var e = LayerEffect.Create(LayerEffectKind.Stroke);
            Common(e, fr);
            e.Size = (float)Math.Max(0.5, fr.Num("Sz  ", 3) * scale);
            e.StrokePos = fr.EnumVal("Styl") switch
            {
                "InsF" => StrokePosition.Inside,
                "CtrF" => StrokePosition.Center,
                _ => StrokePosition.Outside,
            };
            fx.Add(e);
        }
        foreach (var gf in Entries("GrFl", "gradientFillMulti"))
        {
            var e = LayerEffect.Create(LayerEffectKind.GradientOverlay);
            e.Enabled = master && gf.Flag("enab");
            e.Opacity = (float)Math.Clamp(gf.Num("Opct", 100) / 100.0, 0, 1);
            e.BlendMode = MapFxBlend(gf.EnumVal("Md  "));
            e.Angle = (float)gf.Num("Angl", 90);
            // first/last gradient stop → start/end colour (the engine renders a 2-colour ramp)
            if (gf.Obj("Grad")?.Items("Clrs") is { Count: > 0 } stops)
            {
                if (stops.Count > 2)
                    rec.Notes.Add($"gradient overlay has {stops.Count} stops — flattened to first/last.");
                if ((stops[0] as PsDesc)?.Obj("Clr ") is { } c0 && DescColor(c0) is { } s0)
                { e.R = s0.r / 255f; e.G = s0.g / 255f; e.B = s0.b / 255f; }
                if ((stops[^1] as PsDesc)?.Obj("Clr ") is { } c1 && DescColor(c1) is { } s1)
                { e.R2 = s1.r / 255f; e.G2 = s1.g / 255f; e.B2 = s1.b / 255f; }
            }
            fx.Add(e);
        }
        if (d.Obj("ebbl") is { } bv)
        {
            var e = LayerEffect.Create(LayerEffectKind.Bevel);
            e.Enabled = master && bv.Flag("enab");
            e.Opacity = (float)Math.Clamp(bv.Num("hglO", 75) / 100.0, 0, 1);
            if (DescColor(bv.Obj("hglC")) is { } hc) { e.R = hc.r / 255f; e.G = hc.g / 255f; e.B = hc.b / 255f; }
            if (DescColor(bv.Obj("sdwC")) is { } sc) { e.R2 = sc.r / 255f; e.G2 = sc.g / 255f; e.B2 = sc.b / 255f; }
            e.Angle = (float)bv.Num("lagl", 135);
            e.Size = (float)Math.Max(0.5, bv.Num("blur", 4) * scale);
            e.Depth = (float)Math.Clamp(bv.Num("srgR", 100) / 100.0, 0.1, 4.0);
            if (bv.Obj("TrnS") is not null || bv.Get("TrnS") is not null)
                rec.Notes.Add("bevel/emboss contour curve not imported.");
            if (bv.Get("texture") is not null || bv.Obj("TxtC") is not null)
                rec.Notes.Add("bevel/emboss texture not imported.");
            fx.Add(e);
        }
        return fx;
    }

    /// <summary>PS blend enum key (e.g. 'Mltp', 'linearDodge') → Sable BlendMode.</summary>
    private static BlendMode MapFxBlend(string? key) => key switch
    {
        "Mltp" => BlendMode.Multiply,
        "Scrn" => BlendMode.Screen,
        "Ovrl" => BlendMode.Overlay,
        "Drkn" => BlendMode.Darken,
        "Lghn" => BlendMode.Lighten,
        "CBrn" => BlendMode.ColorBurn,
        "CDdg" => BlendMode.ColorDodge,
        "linearBurn" => BlendMode.LinearBurn,
        "linearDodge" => BlendMode.Add,
        "darkerColor" => BlendMode.DarkerColor,
        "lighterColor" => BlendMode.LighterColor,
        "SftL" => BlendMode.SoftLight,
        "HrdL" => BlendMode.HardLight,
        "vividLight" => BlendMode.VividLight,
        "linearLight" => BlendMode.LinearLight,
        "pinLight" => BlendMode.PinLight,
        "hardMix" => BlendMode.HardMix,
        "Dfrn" => BlendMode.Difference,
        "Xclu" => BlendMode.Exclusion,
        "blendSubtraction" => BlendMode.Subtract,
        "blendDivide" => BlendMode.Divide,
        "H   " => BlendMode.Hue,
        "Strt" => BlendMode.Saturation,
        "Clr " => BlendMode.Color,
        "Lmns" => BlendMode.Luminosity,
        _ => BlendMode.Normal,
    };

    /// <summary>'Clr ' descriptor ('Rd  '/'Grn '/'Bl  ' 0..255 doubles) → bytes; null if absent.</summary>
    private static (byte r, byte g, byte b)? DescColor(PsDesc? clr)
    {
        if (clr is null) return null;
        return ((byte)Math.Clamp(clr.Num("Rd  "), 0, 255),
                (byte)Math.Clamp(clr.Num("Grn "), 0, 255),
                (byte)Math.Clamp(clr.Num("Bl  "), 0, 255));
    }

    // ------------------------------------------------------------------ vector mask (vmsk/vsms)

    /// <summary>Parse the bezier path records of a vector mask and flatten to doc-px contours.
    /// Knot coordinates are 8.24 fixed-point fractions of the canvas (vertical first).</summary>
    private static List<List<(double X, double Y)>>? ParseVectorMask(ref Reader r, long end, int docW, int docH)
        => ParseVectorMaskKnots(ref r, end, docW, docH) is { } ks ? KnotsToPolylines(ks) : null;

    /// <summary>A parsed vector-mask sub-path: its bezier knots (in/anchor/out handles, doc px) + closed flag.</summary>
    public sealed class VectorContour
    {
        public List<(double Ix, double Iy, double Ax, double Ay, double Ox, double Oy)> Knots = new();
        public bool Closed = true;
    }

    /// <summary>Parse the vector mask into per-contour bezier knots (preserving handles), so a
    /// single closed contour can be rebuilt as an editable <see cref="PathLayer"/>.</summary>
    private static List<VectorContour>? ParseVectorMaskKnots(ref Reader r, long end, int docW, int docH)
    {
        r.Skip(4);   // version
        r.Skip(4);   // flags
        var contours = new List<VectorContour>();
        VectorContour? cur = null;

        while (r.Pos + 26 <= end)
        {
            int sel = r.U16();
            if (sel is 0 or 3)        // subpath length record (0 = closed, 3 = open)
            {
                cur = new VectorContour { Closed = sel == 0 };
                contours.Add(cur);
                r.Skip(24);
            }
            else if (sel is 1 or 2 or 4 or 5 && cur is not null)   // knot: in-ctl, anchor, out-ctl
            {
                // 8.24 fixed-point fractions of the canvas, VERTICAL first
                double iy = (int)r.U32() / 16777216.0 * docH;
                double ix = (int)r.U32() / 16777216.0 * docW;
                double ay = (int)r.U32() / 16777216.0 * docH;
                double ax = (int)r.U32() / 16777216.0 * docW;
                double oy = (int)r.U32() / 16777216.0 * docH;
                double ox = (int)r.U32() / 16777216.0 * docW;
                cur.Knots.Add((ix, iy, ax, ay, ox, oy));
            }
            else r.Skip(24);
        }
        // drop contours with <2 knots (can't form a segment)
        contours.RemoveAll(c => c.Knots.Count < 2);
        return contours.Count > 0 ? contours : null;
    }

    /// <summary>Flatten bezier knots to doc-px polylines (16 steps per segment) — the rasterisation path.</summary>
    private static List<List<(double X, double Y)>> KnotsToPolylines(List<VectorContour> contours)
    {
        var result = new List<List<(double X, double Y)>>();
        foreach (var c in contours)
        {
            var pts = new List<(double X, double Y)>();
            int n = c.Knots.Count;
            int segs = c.Closed ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                var a = c.Knots[i];
                var b = c.Knots[(i + 1) % n];
                for (int t = 0; t < 16; t++)
                {
                    double f = t / 16.0, g = 1 - f;
                    double x = g * g * g * a.Ax + 3 * g * g * f * a.Ox + 3 * g * f * f * b.Ix + f * f * f * b.Ax;
                    double y = g * g * g * a.Ay + 3 * g * g * f * a.Oy + 3 * g * f * f * b.Iy + f * f * f * b.Ay;
                    pts.Add((x, y));
                }
            }
            if (!c.Closed) pts.Add((c.Knots[^1].Ax, c.Knots[^1].Ay));
            if (pts.Count >= 3) result.Add(pts);
        }
        return result;
    }

    /// <summary>Rasterise doc-px contours into a buffer-aligned coverage plane (0..255).</summary>
    private static byte[] RasterizeCoverage(List<List<(double X, double Y)>> contours, int w, int h, int offX, int offY)
    {
        var shifted = new List<IReadOnlyList<(double X, double Y)>>(contours.Count);
        foreach (var c in contours)
        {
            var s = new List<(double X, double Y)>(c.Count);
            foreach (var (x, y) in c) s.Add((x - offX, y - offY));
            shifted.Add(s);
        }
        var rgba = new byte[w * h * 4];
        VectorRaster.FillMulti(rgba, w, h, shifted, 255, 255, 255, 255);
        var cov = new byte[w * h];
        for (int i = 0; i < cov.Length; i++) cov[i] = rgba[i * 4 + 3];
        return cov;
    }

    private static readonly HashSet<string> AdjustmentKeys = new()
    {
        "SoCo", "GdFl", "PtFl",                                   // fill layers
        "brit", "levl", "curv", "expA", "vibA", "hue2", "hue ",   // adjustments
        "blnc", "blwh", "phfl", "mixr", "clrL", "nvrt", "post",
        "thrs", "grdm", "selc",
    };

    /// <summary>Adjustment keys Sable can map to an editable <see cref="AdjustmentLayer"/>.
    /// The rest (<c>selc</c> selective color, <c>GdFl</c>/<c>PtFl</c> fill layers) stay in
    /// <see cref="AdjustmentKeys"/> and are skipped with a warning. <c>phfl</c> photo filter maps
    /// approximately to White Balance (temperature/tint); <c>clrL</c> legacy channel mixer maps to
    /// Channel Mixer like <c>mixr</c>.</summary>
    private static readonly HashSet<string> MappableAdjustmentKeys = new()
    {
        "brit", "levl", "curv", "expA", "vibA", "hue2", "hue ",
        "blnc", "blwh", "mixr", "clrL", "nvrt", "post", "thrs", "grdm", "phfl",
    };

    /// <summary>Decode one channel's data (compression code + payload) to an 8-bit plane.</summary>
    private static byte[] DecodeChannel(ref Reader r, int w, int h, int depth, long end)
    {
        int comp = r.U16();
        int bpp = depth / 8;
        int rowBytes = w * bpp;
        var raw = new byte[rowBytes * h];

        switch (comp)
        {
            case 0:
                r.ReadInto(raw, Math.Min(raw.Length, (int)(end - r.Pos)));
                break;
            case 1:
            {
                var counts = new int[h];
                for (int y = 0; y < h; y++) counts[y] = r.U16();
                for (int y = 0; y < h; y++)
                    UnpackBits(ref r, counts[y], raw.AsSpan(y * rowBytes, rowBytes));
                break;
            }
            case 2:
            case 3:
            {
                using var ms = new MemoryStream(r.Bytes, (int)r.Pos, (int)(end - r.Pos), writable: false);
                using var z = new ZLibStream(ms, CompressionMode.Decompress);
                int got = 0;
                while (got < raw.Length)
                {
                    int n = z.Read(raw, got, raw.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (comp == 3) UndoPrediction(raw, w, h, depth);
                break;
            }
            default:
                throw new InvalidDataException($"Unknown channel compression {comp}.");
        }

        if (depth == 8) return raw;
        var plane = new byte[w * h];          // 16-bit big-endian → high byte
        for (int i = 0; i < plane.Length; i++) plane[i] = raw[i * 2];
        return plane;
    }

    private static void UnpackBits(ref Reader r, int count, Span<byte> dst)
    {
        long end = r.Pos + count;
        int o = 0;
        while (r.Pos < end && o < dst.Length)
        {
            sbyte n = (sbyte)r.U8();
            if (n >= 0)
            {
                int len = Math.Min(n + 1, dst.Length - o);
                for (int i = 0; i < len; i++) dst[o++] = r.U8();
            }
            else if (n != -128)
            {
                byte v = r.U8();
                int len = Math.Min(1 - n, dst.Length - o);
                for (int i = 0; i < len; i++) dst[o++] = v;
            }
        }
        r.Pos = end;
    }

    /// <summary>Zip-with-prediction stores per-row deltas (byte for 8-bit, big-endian u16 for 16-bit).</summary>
    private static void UndoPrediction(byte[] raw, int w, int h, int depth)
    {
        if (depth == 8)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 1; x < w; x++) raw[row + x] += raw[row + x - 1];
            }
        }
        else
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 2;
                for (int x = 1; x < w; x++)
                {
                    ushort prev = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(row + (x - 1) * 2));
                    ushort cur = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(row + x * 2));
                    BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(row + x * 2), (ushort)(prev + cur));
                }
            }
        }
    }

    // ------------------------------------------------------------------ tree building

    private static void BuildTree(List<LayerRecord> records, List<Layer> rootOut, int docW, int docH, int colorMode, List<string> warnings)
    {
        // records are stored bottom→top; a group = bounding divider (type 3, bottom)
        // … children … folder layer (type 1/2, top, carries the group's own props).
        var stack = new Stack<List<Layer>>();
        var current = rootOut;

        foreach (var rec in records)
        {
            if (rec.SectionType == 3)
            {
                stack.Push(current);
                current = new List<Layer>();
                continue;
            }
            if (rec.SectionType is 1 or 2)
            {
                var g = new GroupLayer(rec.Name)
                {
                    Opacity = rec.Opacity / 255f,
                    FillOpacity = rec.FillOpacity / 255f,
                    Visible = rec.Visible,
                    BlendMode = MapBlend(rec.BlendKey, rec.Name, warnings),
                    PassThrough = rec.BlendKey == "pass",
                    ClipToBelow = rec.Clipping,
                };
                ApplyMask(g, rec, docW, docH, warnings);
                g.Effects.AddRange(rec.Effects);
                g.Children.AddRange(current);
                current = stack.Count > 0 ? stack.Pop() : rootOut;
                current.Add(g);
                foreach (var note in rec.Notes.Distinct())
                    warnings.Add($"\"{rec.Name}\": {note}.");
                continue;
            }

            Layer layer;
            if (rec.TextInfo is { } ti)
            {
                layer = BuildTextLayer(rec, ti);
            }
            else if ((rec.W == 0 || rec.H == 0) && rec.SoCoColor is { } fillCol)
            {
                // solid-colour fill layer: a shape when it carries a vector mask, else canvas-wide
                layer = BuildSoCoLayer(rec, fillCol, docW, docH);
                rec.VectorContours = null;   // coverage already baked into the layer alpha
            }
            else if (rec.AdjustmentDesc is { } adjDesc && rec.UnmappableKey is { } adjKey && (rec.W == 0 || rec.H == 0))
            {
                var adj = BuildAdjustmentLayer(rec, adjKey, adjDesc, warnings);
                if (adj is null) continue;   // mapping failed → already warned, skip the layer
                layer = adj;
            }
            else if (rec.UnmappableKey is not null && (rec.W == 0 || rec.H == 0))
            {
                warnings.Add($"\"{rec.Name}\": {DescribeKey(rec.UnmappableKey)} skipped (no raster content).");
                continue;
            }
            else
            {
                layer = BuildPixelLayer(rec, colorMode);
            }

            ApplyMask(layer, rec, docW, docH, warnings);
            if (layer is PixelLayer pxl) ApplyVectorMask(pxl, rec);
            layer.BlendMode = MapBlend(rec.BlendKey, rec.Name, warnings);
            layer.Effects.AddRange(rec.Effects);
            foreach (var note in rec.Notes.Distinct())
                warnings.Add($"\"{rec.Name}\": {note}.");
            current.Add(layer);
        }

        // unbalanced groups (corrupt/truncated file): flush whatever was collected
        while (stack.Count > 0)
        {
            var partial = current;
            current = stack.Pop();
            current.AddRange(partial);
            warnings.Add("Unbalanced group markers — group structure flattened.");
        }
    }

    /// <summary>SoLd/SoLE: <c>soLD</c> sig + version + descriptor version, then the smart-object
    /// descriptor. Captures identity (<c>Idnt</c>), placement quad (<c>Trnf</c>, 8 doubles), source
    /// size (<c>Sz</c>), and type for staged Smart Object import (Tier 1, plans/SMART_OBJECTS.md).
    /// Pixels stay rasterised — this only preserves the metadata.</summary>
    private static Sable.Engine.Layers.SmartObjectInfo ParseSmartObject(ref Reader r)
    {
        // Layout varies: some writers prefix the descriptor with a 'soLD'/'soLE' signature (4) + version (4);
        // others start at the descriptor version. Read the first 4 bytes and branch — if they spell the
        // signature, skip the version too; otherwise they WERE the descriptor version (already consumed).
        string sig = r.ReadAscii(4);
        if (sig is "soLD" or "soLE") { r.Skip(4); r.Skip(4); }   // version + descriptor version
        var d = PsDesc.Parse(ref r);
        var so = new Sable.Engine.Layers.SmartObjectInfo
        {
            Identity = d.EnumVal("Idnt") ?? "",
            SourceType = (int)d.Num("Type"),
        };
        if (d.Items("Trnf") is { Count: 8 } t)
        {
            var q = new float[8];
            for (int i = 0; i < 8; i++) q[i] = (float)System.Convert.ToDouble(t[i]);
            so.Placement = q;
        }
        if (d.Obj("Sz  ") is { } sz)
        {
            so.SourceWidth = (int)sz.Num("Wdth");
            so.SourceHeight = (int)sz.Num("Hght");
        }
        return so;
    }

    private static PixelLayer BuildPixelLayer(LayerRecord rec, int colorMode)
    {
        int w = Math.Max(1, rec.W), h = Math.Max(1, rec.H);
        var layer = new PixelLayer(w, h, rec.Name)
        {
            Opacity = rec.Opacity / 255f,
            FillOpacity = rec.FillOpacity / 255f,
            Visible = rec.Visible,
            ClipToBelow = rec.Clipping,
            OffsetX = rec.Left,
            OffsetY = rec.Top,
            SmartObject = rec.SmartObject,   // captured Smart Object placement/identity (Tier 1)
        };
        if (rec.W == 0 || rec.H == 0) return layer;   // empty raster (e.g. brand-new layer)

        var px = layer.Pixels;   // RGBA32F: 8-bit PSD planes (0..255) map to 0..1
        for (int i = 3; i < px.Length; i += 4) px[i] = 1f;

        foreach (var ch in rec.Channels)
        {
            if (ch.Plane is not { } plane || plane.Length < w * h) continue;
            int off = ch.Id switch
            {
                0 => 0,
                1 when colorMode == 3 => 1,
                2 when colorMode == 3 => 2,
                -1 => 3,
                _ => -1,
            };
            if (off < 0) continue;
            if (colorMode == 1 && ch.Id == 0)
            {
                for (int i = 0; i < w * h; i++) { px[i * 4] = plane[i] / 255f; px[i * 4 + 1] = plane[i] / 255f; px[i * 4 + 2] = plane[i] / 255f; }
            }
            else
            {
                for (int i = 0; i < w * h; i++) px[i * 4 + off] = plane[i] / 255f;
            }
        }
        return layer;
    }

    /// <summary>Editable text layer from a TySh block: string + first style run mapped onto
    /// Sable's single-style <see cref="TextLayer"/>. Positioned at the PSD raster bbox top-left
    /// (closest match for Sable's top-left text anchor); the matrix scale folds into the size.</summary>
    private static TextLayer BuildTextLayer(LayerRecord rec, PsdTextInfo ti)
    {
        float size = (float)Math.Clamp(ti.Size * ti.ScaleY, 1, 1000);
        var (family, psBold, psItalic) = MapPsFont(ti.PsFontName);
        var t = new TextLayer(ti.Text, rec.Left, rec.Top, size, ti.R, ti.G, ti.B)
        {
            Name = rec.Name,
            Opacity = rec.Opacity / 255f,
            FillOpacity = rec.FillOpacity / 255f,
            Visible = rec.Visible,
            ClipToBelow = rec.Clipping,
            FontFamily = family,
            Bold = psBold || ti.Bold,
            Italic = psItalic || ti.Italic,
            Underline = ti.Underline,
            Strikethrough = ti.Strike,
            Align = ti.Justification switch { 1 => TextAlign.Right, 2 => TextAlign.Center, _ => TextAlign.Left },
            Tracking = (float)(ti.Tracking / 1000.0 * size),
            BoxWidth = (float)Math.Max(0, ti.BoxW * ti.ScaleX),
        };
        if (ti.Leading > 0 && size > 0)
            t.LineSpacing = Math.Clamp((float)(ti.Leading * ti.ScaleY) / size, 0.5f, 4f);
        // baked rotation → the layer's non-destructive rotation (about the text-block centre;
        // a few px of anchor drift vs PS for large angles is accepted)
        if (Math.Abs(ti.RotationDeg) > 0.05)
            t.Rotation = (float)ti.RotationDeg;
        return t;
    }

    /// <summary>A solid-colour fill layer: with a single closed vector-mask contour = an editable
    /// <see cref="PathLayer"/> (fill colour × bezier path); with multiple/open contours or no
    /// preserved knots = a rasterised <see cref="PixelLayer"/> shaped to the path bbox; without
    /// any vector mask = a canvas-covering solid.</summary>
    private static Layer BuildSoCoLayer(LayerRecord rec, (byte r, byte g, byte b) c, int docW, int docH)
    {
        // Bridge to an editable PathLayer when the vector mask is a single closed contour.
        if (rec.VectorKnots is { Count: 1 } knots && knots[0].Closed && knots[0].Knots.Count >= 2)
        {
            var path = new PathLayer(knots[0].Knots.Select(k => new PathNode(
                (float)k.Ax, (float)k.Ay,
                (float)k.Ix, (float)k.Iy,
                (float)k.Ox, (float)k.Oy)), true, c.r, c.g, c.b)
            {
                Name = rec.Name,
                Opacity = rec.Opacity / 255f,
                FillOpacity = rec.FillOpacity / 255f,
                Visible = rec.Visible,
                ClipToBelow = rec.Clipping,
            };
            rec.Notes.Clear();
            rec.Notes.Add("solid-colour fill layer imported as editable shape");
            return path;
        }

        // Multi-contour: when every contour is closed, bridge to a PathLayer whose primary
        // sub-path is the first contour and the rest become ExtraContours (even-odd fill → holes).
        if (rec.VectorKnots is { Count: > 1 } all && all.All(k => k.Closed) && all[0].Knots.Count >= 2)
        {
            var path = new PathLayer(all[0].Knots.Select(k => new PathNode(
                (float)k.Ax, (float)k.Ay,
                (float)k.Ix, (float)k.Iy,
                (float)k.Ox, (float)k.Oy)), true, c.r, c.g, c.b)
            {
                Name = rec.Name,
                Opacity = rec.Opacity / 255f,
                FillOpacity = rec.FillOpacity / 255f,
                Visible = rec.Visible,
                ClipToBelow = rec.Clipping,
            };
            for (int i = 1; i < all.Count; i++)
            {
                path.ExtraContours.Add((all[i].Knots.Select(k => new PathNode(
                    (float)k.Ax, (float)k.Ay,
                    (float)k.Ix, (float)k.Iy,
                    (float)k.Ox, (float)k.Oy)).ToList(), true));
            }
            rec.Notes.Clear();
            rec.Notes.Add("solid-colour fill layer imported as editable shape (multi-contour)");
            return path;
        }

        if (rec.VectorContours is { } vc)
        {
            double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
            foreach (var contour in vc)
                foreach (var (x, y) in contour)
                {
                    minx = Math.Min(minx, x); maxx = Math.Max(maxx, x);
                    miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
                }
            // sanity-clamp (corrupt fixed-point data must not allocate gigabytes)
            minx = Math.Clamp(minx, -2.0 * docW, 2.0 * docW);
            maxx = Math.Clamp(maxx, -2.0 * docW, 2.0 * docW);
            miny = Math.Clamp(miny, -2.0 * docH, 2.0 * docH);
            maxy = Math.Clamp(maxy, -2.0 * docH, 2.0 * docH);
            int x0 = (int)Math.Floor(minx), y0 = (int)Math.Floor(miny);
            int w = Math.Max(1, (int)Math.Ceiling(maxx) - x0 + 1);
            int h = Math.Max(1, (int)Math.Ceiling(maxy) - y0 + 1);

            var shape = new PixelLayer(w, h, rec.Name)
            {
                Opacity = rec.Opacity / 255f,
                FillOpacity = rec.FillOpacity / 255f,
                Visible = rec.Visible,
                ClipToBelow = rec.Clipping,
                OffsetX = x0,
                OffsetY = y0,
            };
            var cov = RasterizeCoverage(vc, w, h, x0, y0);
            var px = shape.Pixels;
            for (int i = 0; i < cov.Length; i++)
            {
                px[i * 4] = c.r / 255f; px[i * 4 + 1] = c.g / 255f; px[i * 4 + 2] = c.b / 255f;
                px[i * 4 + 3] = cov[i] / 255f;
            }
            return shape;
        }

        rec.Notes.Add("fill layer rasterised");
        var solid = new PixelLayer(docW, docH, rec.Name)
        {
            Opacity = rec.Opacity / 255f,
            FillOpacity = rec.FillOpacity / 255f,
            Visible = rec.Visible,
            ClipToBelow = rec.Clipping,
        };
        var sp = solid.Pixels;
        for (int i = 0; i < sp.Length; i += 4)
        {
            sp[i] = c.r / 255f; sp[i + 1] = c.g / 255f; sp[i + 2] = c.b / 255f; sp[i + 3] = 1f;
        }
        return solid;
    }

    /// <summary>Map a PSD adjustment-layer descriptor onto an editable Sable
    /// <see cref="AdjustmentLayer"/>. Returns null (with a warning) when the mapping fails or the
    /// kind is not in <see cref="MappableAdjustmentKeys"/>. The layer keeps the PSD opacity /
    /// visibility / clipping / mask like any other layer.</summary>
    private static Layer? BuildAdjustmentLayer(LayerRecord rec, string key, PsDesc d, List<string> warnings)
    {
        AdjustmentLayer adj;
        try
        {
            adj = key switch
            {
                "brit" => BuildBrightnessContrast(d),
                "levl" => BuildLevels(d),
                "curv" => BuildCurves(d),
                "expA" => BuildExposure(d),
                "vibA" => BuildVibrance(d),
                "hue2" or "hue " => BuildHsl(d),
                "blnc" => BuildColorBalance(d),
                "blwh" => BuildBlackWhite(d),
                "mixr" or "clrL" => BuildChannelMixer(d),
                "nvrt" => new AdjustmentLayer(AdjustmentKind.Invert),
                "post" => BuildPosterize(d),
                "thrs" => BuildThreshold(d),
                "grdm" => BuildGradientMap(d),
                "phfl" => BuildPhotoFilter(d),
                _ => null!,
            };
        }
        catch
        {
            warnings.Add($"\"{rec.Name}\": {DescribeKey(key)} unreadable — skipped.");
            return null;
        }
        if (adj is null)
        {
            warnings.Add($"\"{rec.Name}\": {DescribeKey(key)} skipped (no mapping).");
            return null;
        }

        adj.Name = rec.Name;
        adj.Opacity = rec.Opacity / 255f;
        adj.FillOpacity = rec.FillOpacity / 255f;
        adj.Visible = rec.Visible;
        adj.ClipToBelow = rec.Clipping;
        rec.Notes.Add("adjustment layer imported as editable");
        return adj;
    }

    // ---- per-kind adjustment mappers (PS descriptor → Sable AdjustmentLayer params) ----

    private static AdjustmentLayer BuildBrightnessContrast(PsDesc d)
    {
        // PS 'brit': Brightness (-100..100), Contrast (-100..100), useLegacy flag (we ignore legacy).
        var a = new AdjustmentLayer(AdjustmentKind.BrightnessContrast)
        {
            Brightness = (float)d.Num("Brgh") / 100f,
            Contrast = 1f + (float)d.Num("Cntr") / 100f,
        };
        return a;
    }

    private static AdjustmentLayer BuildLevels(PsDesc d)
    {
        // PS 'levl': Adjs list of channel descriptors; channel 0 = composite. InputBlack/White/Gamma.
        var a = new AdjustmentLayer(AdjustmentKind.Levels);
        var adjs = d.Items("Adjs");
        var comp = adjs?.FirstOrDefault(o => o is PsDesc) as PsDesc;
        if (comp is not null)
        {
            a.InBlack = (float)(comp.Num("Inpt") / 255.0);
            a.InWhite = (float)(comp.Num("Whtp") / 255.0);
            a.Gamma = (float)comp.Num("Gmm ", 1.0);
        }
        return a;
    }

    private static AdjustmentLayer BuildCurves(PsDesc d)
    {
        // PS 'curv': Adjs list, each with a channel id + a curve point list. Sable has 4 channels
        // (0=RGB, 1=R, 2=G, 3=B); PS channel ids: 0=composite, 1=R, 2=G, 3=B.
        var a = new AdjustmentLayer(AdjustmentKind.Curves);
        var adjs = d.Items("Adjs");
        if (adjs is null) return a;
        foreach (var o in adjs)
        {
            if (o is not PsDesc ch) continue;
            int id = (int)ch.Num("Chnl");
            if (id is < 0 or > 3) continue;
            if (ch.Items("Crv ") is not { } pts || pts.Count == 0) continue;
            var curve = a.Curves[id];
            curve.Clear();
            foreach (var p in pts)
            {
                if (p is not PsDesc pt) continue;
                // PS curve points: Hrz (0..255 input), Vrtc (0..255 output) → 0..1
                curve.Add(((float)(pt.Num("Hrz") / 255.0), (float)(pt.Num("Vrtc") / 255.0)));
            }
            curve.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        }
        return a;
    }

    private static AdjustmentLayer BuildExposure(PsDesc d)
    {
        // PS 'expA': exposure (stops), offset (small), gammaCorrection. Sable uses stops only.
        return new AdjustmentLayer(AdjustmentKind.Exposure)
        {
            Exposure = (float)d.Num("Exps"),
        };
    }

    private static AdjustmentLayer BuildVibrance(PsDesc d)
    {
        // PS 'vibA': Vibrance (-100..100), Saturation (-100..100).
        return new AdjustmentLayer(AdjustmentKind.Vibrance)
        {
            Vibrance = (float)d.Num("Vibr") / 100f,
        };
    }

    private static AdjustmentLayer BuildHsl(PsDesc d)
    {
        // PS 'hue2' (newer) / 'hue ' (legacy): Colorization + adjustment values. Hue is in degrees,
        // Sat/Lightness in -100..100. Sable: HueShift in turns, Saturation 0..2, Lightness -1..1.
        var a = new AdjustmentLayer(AdjustmentKind.Hsl);
        // 'hue2' stores adjustments in an "Adjs" VlLs of channel descriptors; the composite (ch 0)
        // carries H/S/L. 'hue ' stores them directly.
        double h = 0, s = 0, l = 0;
        if (d.Items("Adjs") is { } adjs)
        {
            var comp = adjs.FirstOrDefault(o => o is PsDesc) as PsDesc;
            if (comp is not null) { h = comp.Num("H   "); s = comp.Num("Strt"); l = comp.Num("Lght"); }
        }
        else { h = d.Num("H   "); s = d.Num("Strt"); l = d.Num("Lght"); }
        a.HueShift = (float)(h / 360.0);
        a.Saturation = 1f + (float)(s / 100.0);
        a.Lightness = (float)(l / 100.0);
        return a;
    }

    private static AdjustmentLayer BuildColorBalance(PsDesc d)
    {
        // PS 'blnc': shadow/midtone/highlight R/G/B shifts (-100..100). Sable stores 9 floats
        // (shadow R,G,B, mid R,G,B, highlight R,G,B) in -1..1.
        var a = new AdjustmentLayer(AdjustmentKind.ColorBalance);
        void Fill(int offset, PsDesc? range)
        {
            if (range is null) return;
            a.ColorBalance[offset + 0] = (float)(range.Num("Rd  ") / 100.0);
            a.ColorBalance[offset + 1] = (float)(range.Num("Grn ") / 100.0);
            a.ColorBalance[offset + 2] = (float)(range.Num("Bl  ") / 100.0);
        }
        Fill(0, d.Obj("Sdw "));
        Fill(3, d.Obj("Mdt "));
        Fill(6, d.Obj("Hgh "));
        return a;
    }

    private static AdjustmentLayer BuildBlackWhite(PsDesc d)
    {
        // PS 'blwh': per-channel weights (RdYlw, Grn, Cyn, Bl, Mgnt) + useTint/tint color.
        // Sable uses R/G/B luminance weights; map the RGB ones directly.
        return new AdjustmentLayer(AdjustmentKind.BlackWhite)
        {
            BwR = (float)(d.Num("Rd  ", 0.3) / 100.0),
            BwG = (float)(d.Num("Grn ", 0.59) / 100.0),
            BwB = (float)(d.Num("Bl  ", 0.11) / 100.0),
        };
    }

    private static AdjustmentLayer BuildChannelMixer(PsDesc d)
    {
        // PS 'mixr': per-output-channel descriptors (R, G, B) each with R/G/B source percentages.
        // Sable stores a 3×3 row-major matrix (outR = row0·rgb).
        var a = new AdjustmentLayer(AdjustmentKind.ChannelMixer);
        void Row(int row, string key)
        {
            if (d.Obj(key) is not { } ch) return;
            a.ChannelMix[row * 3 + 0] = (float)(ch.Num("Rd  ") / 100.0);
            a.ChannelMix[row * 3 + 1] = (float)(ch.Num("Grn ") / 100.0);
            a.ChannelMix[row * 3 + 2] = (float)(ch.Num("Bl  ") / 100.0);
        }
        Row(0, "Rd  "); Row(1, "Grn "); Row(2, "Bl  ");
        return a;
    }

    private static AdjustmentLayer BuildPosterize(PsDesc d)
    {
        // PS 'post': Levels (2..255).
        return new AdjustmentLayer(AdjustmentKind.Posterize)
        {
            Posterize = (float)Math.Clamp(d.Num("Lvls", 6), 2, 255),
        };
    }

    private static AdjustmentLayer BuildThreshold(PsDesc d)
    {
        // PS 'thrs': Level (1..255). Sable: 0..1 luminance cut.
        return new AdjustmentLayer(AdjustmentKind.Threshold)
        {
            Threshold = (float)Math.Clamp(d.Num("Lvl ", 128), 0, 255) / 255f,
        };
    }

    private static AdjustmentLayer BuildGradientMap(PsDesc d)
    {
        // PS 'grdm': a gradient descriptor with 'Clrs' stops (Color + Lctn 0..4096).
        // Sable: GradientStops (Pos 0..1, R/G/B bytes).
        var a = new AdjustmentLayer(AdjustmentKind.GradientMap);
        if (d.Obj("Grad")?.Items("Clrs") is { } stops && stops.Count > 0)
        {
            a.GradientStops.Clear();
            foreach (var s in stops)
            {
                if (s is not PsDesc st) continue;
                if (st.Obj("Clr ") is not { } clr || DescColor(clr) is not { } rgb) continue;
                double pos = st.Num("Lctn") / 4096.0;
                a.GradientStops.Add(((float)pos, rgb.r, rgb.g, rgb.b));
            }
            a.GradientStops.Sort((x, y) => x.Pos.CompareTo(y.Pos));
        }
        return a;
    }

    /// <summary>PS 'phfl' (Photo Filter): a warming/cooling colour cast with a density. Sable has
    /// no direct photo-filter kind, so it maps approximately to White Balance — the filter colour
    /// hue drives temperature (warm=+temp, cool=−temp) and density scales the strength. Preserved
    /// as an editable <see cref="AdjustmentKind.WhiteBalance"/> node.</summary>
    private static AdjustmentLayer BuildPhotoFilter(PsDesc d)
    {
        // density 0..1, preserveLuminosity ignored (Sable WB has no Luma option).
        double density = d.Num("Dens ", 0.25);
        bool warm = true;
        if (d.Obj("Clr ") is { } clr && DescColor(clr) is { } rgb)
        {
            // warm filters (R>B) → +temp; cool (B>R) → −temp. Tint from green bias.
            warm = rgb.r >= rgb.b;
        }
        else if (d.EnumVal("FrgC") is { } preset)
        {
            warm = !preset.Contains("Cool") && !preset.Contains("cool");
        }
        float temp = (float)(warm ? density : -density);   // -1..1 (cool..warm)
        float tint = 0f;
        if (d.Obj("Clr ") is { } c2 && DescColor(c2) is { } rgb2)
            tint = (float)Math.Clamp((rgb2.g - (rgb2.r + rgb2.b) / 2.0) / 128.0, -1, 1);
        return new AdjustmentLayer(AdjustmentKind.WhiteBalance)
        {
            Temperature = temp,
            Tint = tint,
        };
    }

    /// <summary>Multiply the rasterised vector-mask coverage into the layer's mask (creating one
    /// when the layer has no raster mask).</summary>
    private static void ApplyVectorMask(PixelLayer layer, LayerRecord rec)
    {
        if (rec.VectorContours is not { } vc) return;
        var cov = RasterizeCoverage(vc, layer.Width, layer.Height, rec.Left, rec.Top);
        if (layer.Mask is { } m)
        {
            for (int i = 0; i < cov.Length; i++)
            {
                byte b = (byte)(m[i * 4] * cov[i] / 255);
                m[i * 4] = m[i * 4 + 1] = m[i * 4 + 2] = m[i * 4 + 3] = b;
            }
        }
        else
        {
            var mask = new byte[layer.Width * layer.Height * 4];
            for (int i = 0; i < cov.Length; i++)
                mask[i * 4] = mask[i * 4 + 1] = mask[i * 4 + 2] = mask[i * 4 + 3] = cov[i];
            layer.Mask = mask;
        }
        layer.MaskDirty = true;
    }

    /// <summary>PSD masks have their own rect + default colour; Sable masks are layer-aligned.
    /// Fill with the default, blit the overlapping region.</summary>
    private static void ApplyMask(Layer layer, LayerRecord rec, int docW, int docH, List<string> warnings)
    {
        if (!rec.HasMask) return;
        if (rec.MaskDisabled) { warnings.Add($"\"{rec.Name}\": disabled layer mask dropped."); return; }
        var ch = rec.Channels.FirstOrDefault(c => c.Id == -2);

        // Sable masks are layer-aligned: pixel-layer masks share the layer buffer's rect,
        // group masks are document-sized.
        int lw, lh, lx, ly;
        if (layer is PixelLayer p) { lw = p.Width; lh = p.Height; lx = rec.Left; ly = rec.Top; }
        else { lw = docW; lh = docH; lx = 0; ly = 0; }

        var mask = new byte[lw * lh * 4];
        Array.Fill(mask, rec.MaskDefault);

        int mw = rec.MaskRight - rec.MaskLeft, mh = rec.MaskBottom - rec.MaskTop;
        if (ch?.Plane is { } plane && mw > 0 && mh > 0)
        {
            for (int y = 0; y < mh; y++)
            {
                int dy = rec.MaskTop + y - ly;
                if (dy < 0 || dy >= lh) continue;
                for (int x = 0; x < mw; x++)
                {
                    int dx = rec.MaskLeft + x - lx;
                    if (dx < 0 || dx >= lw) continue;
                    byte v = plane[y * mw + x];
                    int i = (dy * lw + dx) * 4;
                    mask[i] = v; mask[i + 1] = v; mask[i + 2] = v; mask[i + 3] = v;
                }
            }
        }
        layer.Mask = mask;
        layer.MaskDirty = true;
    }

    private static BlendMode MapBlend(string key, string layerName, List<string> warnings)
    {
        switch (key)
        {
            case "norm": case "pass": return BlendMode.Normal;
            case "diss": warnings.Add($"\"{layerName}\": Dissolve blend mapped to Normal."); return BlendMode.Normal;
            case "dark": return BlendMode.Darken;
            case "mul ": return BlendMode.Multiply;
            case "idiv": return BlendMode.ColorBurn;
            case "lbrn": return BlendMode.LinearBurn;
            case "dkCl": return BlendMode.DarkerColor;
            case "lite": return BlendMode.Lighten;
            case "scrn": return BlendMode.Screen;
            case "div ": return BlendMode.ColorDodge;
            case "lddg": return BlendMode.Add;
            case "lgCl": return BlendMode.LighterColor;
            case "over": return BlendMode.Overlay;
            case "sLit": return BlendMode.SoftLight;
            case "hLit": return BlendMode.HardLight;
            case "vLit": return BlendMode.VividLight;
            case "lLit": return BlendMode.LinearLight;
            case "pLit": return BlendMode.PinLight;
            case "hMix": return BlendMode.HardMix;
            case "diff": return BlendMode.Difference;
            case "smud": return BlendMode.Exclusion;
            case "fsub": return BlendMode.Subtract;
            case "fdiv": return BlendMode.Divide;
            case "hue ": return BlendMode.Hue;
            case "sat ": return BlendMode.Saturation;
            case "colr": return BlendMode.Color;
            case "lum ": return BlendMode.Luminosity;
            default:
                warnings.Add($"\"{layerName}\": unknown blend mode '{key}' mapped to Normal.");
                return BlendMode.Normal;
        }
    }

    private static string DescribeKey(string key) => key switch
    {
        "SoCo" => "solid-colour fill layer",
        "GdFl" => "gradient fill layer",
        "PtFl" => "pattern fill layer",
        _ => "adjustment layer",
    };

    private static string ModeName(int m) => m switch
    {
        0 => "Bitmap", 1 => "Grayscale", 2 => "Indexed", 3 => "RGB",
        4 => "CMYK", 7 => "Multichannel", 8 => "Duotone", 9 => "Lab", _ => $"mode {m}",
    };

    // ------------------------------------------------------------------ merged composite

    private static byte[] ReadComposite(ref Reader r, int w, int h, int depth, int channels, int colorMode)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        if (r.Pos + 2 > r.Bytes.Length) return rgba;

        int comp = r.U16();
        int bpp = depth / 8;
        int rowBytes = w * bpp;
        int useCh = Math.Min(channels, colorMode == 1 ? 2 : 4);

        int[]? counts = null;
        if (comp == 1)
        {
            counts = new int[channels * h];
            for (int i = 0; i < counts.Length; i++) counts[i] = r.U16();
        }

        for (int c = 0; c < channels; c++)
        {
            var raw = new byte[rowBytes * h];
            if (comp == 0) r.ReadInto(raw, Math.Min(raw.Length, r.Bytes.Length - (int)r.Pos));
            else if (comp == 1)
            {
                for (int y = 0; y < h; y++)
                    UnpackBits(ref r, counts![c * h + y], raw.AsSpan(y * rowBytes, rowBytes));
            }
            else throw new InvalidDataException($"Unsupported composite compression {comp}.");

            if (c >= useCh) continue;
            int off = colorMode == 1 ? (c == 1 ? 3 : 0) : c;     // gray: ch0=gray, ch1=alpha
            for (int i = 0; i < w * h; i++)
            {
                byte v = depth == 8 ? raw[i] : raw[i * 2];
                if (colorMode == 1 && c == 0)
                {
                    rgba[i * 4] = v; rgba[i * 4 + 1] = v; rgba[i * 4 + 2] = v;
                }
                else rgba[i * 4 + off] = v;
            }
        }
        return rgba;
    }

    // ------------------------------------------------------------------ PS descriptor (subset)

    /// <summary>Minimal Photoshop descriptor parser (keys → double/string/bool/nested/list) —
    /// enough for lfx2 layer effects and SoCo fill colours. Mirrors the .abr 'desc' parser.</summary>
    private sealed class PsDesc
    {
        private readonly Dictionary<string, object?> _items = new();

        public object? Get(string key) => _items.GetValueOrDefault(key);
        public PsDesc? Obj(string key) => Get(key) as PsDesc;
        public List<object>? Items(string key) => Get(key) as List<object>;
        public string? EnumVal(string key) => Get(key) as string;
        public bool Flag(string key, bool def = true) => Get(key) is bool b ? b : def;
        public double Num(string key, double def = 0) => Get(key) switch
        {
            double d => d, int i => i, long l => l, _ => def,
        };

        public static PsDesc Parse(ref Reader r)
        {
            ReadUni(ref r);   // class name
            ReadKey(ref r);   // class id
            var d = new PsDesc();
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
                case "Objc": case "GlbO": return Parse(ref r);
                case "VlLs":
                {
                    int n = (int)r.U32();
                    var list = new List<object>(n);
                    for (int i = 0; i < n; i++) if (ReadValue(ref r) is { } v) list.Add(v);
                    return list;
                }
                case "doub": return r.F64();
                case "UntF": r.Skip(4); return r.F64();
                case "TEXT": return ReadUni(ref r);
                case "enum": ReadKey(ref r); return ReadKey(ref r);
                case "long": return (int)r.U32();
                case "comp": { long hi = r.U32(); return (long)((ulong)hi << 32 | r.U32()); }
                case "bool": return r.U8() != 0;
                case "type": case "GlbC": ReadUni(ref r); ReadKey(ref r); return null;
                case "alis": { long n = r.U32(); r.Skip((int)n); return null; }
                case "tdta": { long n = r.U32(); var raw = new byte[n]; r.ReadInto(raw, (int)n); return raw; }   // EngineData
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
                case "prop": ReadUni(ref r); ReadKey(ref r); ReadKey(ref r); break;
                case "Clss": ReadUni(ref r); ReadKey(ref r); break;
                case "Enmr": ReadUni(ref r); ReadKey(ref r); ReadKey(ref r); ReadKey(ref r); break;
                case "rele": ReadUni(ref r); ReadKey(ref r); r.Skip(4); break;
                case "Idnt": case "indx": r.Skip(4); break;
                case "name": ReadUni(ref r); ReadKey(ref r); ReadUni(ref r); break;
                default: throw new InvalidDataException($"Unknown reference type '{t}'.");
            }
        }

        private static string ReadKey(ref Reader r)
        {
            int len = (int)r.U32();
            return r.ReadAscii(len == 0 ? 4 : len);
        }

        private static string ReadUni(ref Reader r)
        {
            int n = (int)r.U32();
            var sb = new StringBuilder(n);
            for (int i = 0; i < n; i++) sb.Append((char)r.U16());
            return sb.ToString().TrimEnd('\0');
        }
    }

    // ------------------------------------------------------------------ byte reader

    private struct Reader
    {
        public readonly byte[] Bytes;
        public long Pos;

        public Reader(byte[] bytes) { Bytes = bytes; Pos = 0; }

        public byte U8() => Bytes[Pos++];
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan((int)Pos)); Pos += 2; return v; }
        public uint U32() { var v = BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)Pos)); Pos += 4; return v; }
        public ulong U64() { var v = BinaryPrimitives.ReadUInt64BigEndian(Bytes.AsSpan((int)Pos)); Pos += 8; return v; }
        /// <summary>Read a length field: 8 bytes for PSB (large document format), 4 for PSD.</summary>
        public long Len(bool psb) => psb ? (long)U64() : U32();
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
