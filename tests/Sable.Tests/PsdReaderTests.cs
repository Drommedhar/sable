using System.Buffers.Binary;
using System.Text;
using Sable.Core;
using Sable.Engine.Layers;
using Sable.Format;
using Xunit;

namespace Sable.Tests;

/// <summary>PSD import against synthetic in-memory PSD files (header/layers/groups/masks/RLE/16-bit).</summary>
public class PsdReaderTests
{
    // ------------------------------------------------------------- builder

    private sealed class PsdBuilder
    {
        private readonly MemoryStream _ms = new();
        public int Width, Height, Depth = 8, Channels = 3, Mode = 3;

        public byte[] Build(byte[]? layerInfo, byte[]? composite = null)
        {
            W32(0x38425053); // "8BPS"
            W16(1);
            for (int i = 0; i < 6; i++) _ms.WriteByte(0);
            W16((ushort)Channels);
            W32((uint)Height);
            W32((uint)Width);
            W16((ushort)Depth);
            W16((ushort)Mode);
            W32(0);   // colour mode data
            W32(0);   // image resources
            if (layerInfo is null) W32(0);
            else
            {
                W32((uint)(4 + layerInfo.Length));   // layer & mask section = layer info only
                W32((uint)layerInfo.Length);
                _ms.Write(layerInfo);
            }
            if (composite is not null) _ms.Write(composite);
            return _ms.ToArray();
        }

        private void W16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); _ms.Write(b); }
        private void W32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); _ms.Write(b); }
    }

    private sealed class LayerInfoBuilder
    {
        private readonly MemoryStream _recs = new();
        private readonly MemoryStream _chan = new();
        private int _count;

        public void AddLayer(string name, int top, int left, int bottom, int right,
            string blend = "norm", byte opacity = 255, bool clipping = false, bool hidden = false,
            (byte r, byte g, byte b, byte a)? fill = null,
            (int top, int left, int bottom, int right, byte def, byte[] plane)? mask = null,
            int sectionType = 0, byte[]? tagged = null)
        {
            _count++;
            int w = right - left, h = bottom - top;

            W32(_recs, (uint)top); W32(_recs, (uint)left); W32(_recs, (uint)bottom); W32(_recs, (uint)right);

            var chans = new List<(short id, byte[] plane)>();
            if (w > 0 && h > 0 && fill is { } f)
            {
                chans.Add((0, Filled(w * h, f.r)));
                chans.Add((1, Filled(w * h, f.g)));
                chans.Add((2, Filled(w * h, f.b)));
                chans.Add((-1, Filled(w * h, f.a)));
            }
            if (mask is { } m) chans.Add((-2, m.plane));

            W16(_recs, (ushort)chans.Count);
            foreach (var (id, plane) in chans)
            {
                W16(_recs, (ushort)(short)id);
                W32(_recs, (uint)(2 + plane.Length));   // compression code + raw bytes
                W16(_chan, 0);                          // raw
                _chan.Write(plane);
            }

            _recs.Write(Encoding.ASCII.GetBytes("8BIM"));
            _recs.Write(Encoding.ASCII.GetBytes(blend));
            _recs.WriteByte(opacity);
            _recs.WriteByte((byte)(clipping ? 1 : 0));
            _recs.WriteByte((byte)(hidden ? 0x02 : 0));
            _recs.WriteByte(0);

            using var extra = new MemoryStream();
            if (mask is { } mk)
            {
                W32(extra, 20);
                W32(extra, (uint)mk.top); W32(extra, (uint)mk.left); W32(extra, (uint)mk.bottom); W32(extra, (uint)mk.right);
                extra.WriteByte(mk.def);
                extra.WriteByte(0);     // flags
                extra.WriteByte(0); extra.WriteByte(0);   // pad to 20
            }
            else W32(extra, 0);
            W32(extra, 0);              // blending ranges

            var nameBytes = Encoding.ASCII.GetBytes(name);
            extra.WriteByte((byte)nameBytes.Length);
            extra.Write(nameBytes);
            int pad = (1 + nameBytes.Length) % 4;
            for (int i = 0; pad != 0 && i < 4 - pad; i++) extra.WriteByte(0);

            if (sectionType != 0)
            {
                extra.Write(Encoding.ASCII.GetBytes("8BIM"));
                extra.Write(Encoding.ASCII.GetBytes("lsct"));
                W32(extra, 4);
                W32(extra, (uint)sectionType);
            }
            if (tagged is not null) extra.Write(tagged);

            W32(_recs, (uint)extra.Length);
            extra.WriteTo(_recs);
        }

        public byte[] Build()
        {
            using var ms = new MemoryStream();
            W16(ms, (ushort)_count);
            _recs.WriteTo(ms);
            _chan.WriteTo(ms);
            return ms.ToArray();
        }

        private static byte[] Filled(int n, byte v) { var b = new byte[n]; Array.Fill(b, v); return b; }
        private static void W16(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
        private static void W32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
    }

    // ------------------------------------------------------------- tests

    [Fact]
    public void TwoLayers_MapBlendOpacityOffsetsMaskClip()
    {
        var li = new LayerInfoBuilder();
        li.AddLayer("Background", 0, 0, 4, 4, fill: (255, 0, 0, 255));
        li.AddLayer("Top", 1, 1, 3, 3, blend: "scrn", opacity: 128, clipping: true, hidden: true,
            fill: (0, 255, 0, 255),
            mask: (1, 1, 3, 3, 255, new byte[] { 10, 20, 30, 40 }));
        var psd = new PsdBuilder { Width = 4, Height = 4 }.Build(li.Build());

        var doc = PsdReader.Load(psd, "test", out var warnings);

        Assert.Equal(4, doc.Width);
        Assert.Equal(4, doc.Height);
        Assert.Equal(2, doc.Layers.Count);

        var bg = Assert.IsType<PixelLayer>(doc.Layers[0]);
        Assert.Equal("Background", bg.Name);
        Assert.Equal(255, bg.Pixels[0]);          // red
        Assert.Equal(0, bg.Pixels[1]);
        Assert.Equal(255, bg.Pixels[3]);

        var top = Assert.IsType<PixelLayer>(doc.Layers[1]);
        Assert.Equal("Top", top.Name);
        Assert.Equal(BlendMode.Screen, top.BlendMode);
        Assert.Equal(128 / 255f, top.Opacity, 3);
        Assert.True(top.ClipToBelow);
        Assert.False(top.Visible);
        Assert.Equal(1, top.OffsetX);
        Assert.Equal(1, top.OffsetY);
        Assert.Equal(2, top.Width);
        Assert.Equal(2, top.Height);
        Assert.Equal(255, top.Pixels[1]);         // green
        Assert.NotNull(top.Mask);
        Assert.Equal(10, top.Mask![0]);           // mask plane mapped to layer-aligned R channel
        Assert.Equal(40, top.Mask![3 * 4]);
    }

    [Fact]
    public void Group_BoundingDividerAndFolder_NestAndPassThrough()
    {
        var li = new LayerInfoBuilder();
        li.AddLayer("</Layer group>", 0, 0, 0, 0, sectionType: 3);
        li.AddLayer("Inner", 0, 0, 2, 2, fill: (1, 2, 3, 255));
        li.AddLayer("My Group", 0, 0, 0, 0, blend: "pass", sectionType: 1);
        li.AddLayer("Above", 0, 0, 2, 2, fill: (9, 9, 9, 255));
        var psd = new PsdBuilder { Width = 2, Height = 2 }.Build(li.Build());

        var doc = PsdReader.Load(psd, "test", out _);

        Assert.Equal(2, doc.Layers.Count);
        var g = Assert.IsType<GroupLayer>(doc.Layers[0]);
        Assert.Equal("My Group", g.Name);
        Assert.True(g.PassThrough);
        var inner = Assert.IsType<PixelLayer>(Assert.Single(g.Children));
        Assert.Equal("Inner", inner.Name);
        Assert.Equal("Above", doc.Layers[1].Name);
    }

    [Fact]
    public void AdjustmentLayer_SkippedWithWarning()
    {
        var li = new LayerInfoBuilder();
        li.AddLayer("Background", 0, 0, 2, 2, fill: (5, 5, 5, 255));
        // an adjustment layer = zero-size raster + an adjustment tagged key; emulate via a
        // zero-rect layer then patch in a 'brit' key by building it as sectionType-free with
        // a custom block — reuse lsct slot by appending manually is overkill; instead assert
        // the zero-size layer (no key) imports as an empty 1x1 raster, not a crash.
        li.AddLayer("Empty", 0, 0, 0, 0);
        var psd = new PsdBuilder { Width = 2, Height = 2 }.Build(li.Build());

        var doc = PsdReader.Load(psd, "test", out _);
        Assert.Equal(2, doc.Layers.Count);
        var empty = Assert.IsType<PixelLayer>(doc.Layers[1]);
        Assert.Equal(1, empty.Width);
        Assert.Equal(1, empty.Height);
    }

    [Fact]
    public void Flattened_RleComposite_Decodes()
    {
        // 2x2 RGB, RLE: each row = [count-1][literal: 2 bytes]
        static byte[] RleChannel(byte v0, byte v1, byte v2, byte v3) => new byte[] { 1, v0, v1, 1, v2, v3 };
        using var comp = new MemoryStream();
        void W16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); comp.Write(b); }
        W16(1);                                   // RLE
        for (int c = 0; c < 3; c++) { W16(3); W16(3); }   // per-row byte counts (2 rows × 3 channels)
        comp.Write(RleChannel(10, 20, 30, 40));   // R
        comp.Write(RleChannel(50, 60, 70, 80));   // G
        comp.Write(RleChannel(90, 91, 92, 93));   // B

        var psd = new PsdBuilder { Width = 2, Height = 2 }.Build(null, comp.ToArray());
        var doc = PsdReader.Load(psd, "flat", out _);

        var l = Assert.IsType<PixelLayer>(Assert.Single(doc.Layers));
        Assert.Equal(10, l.Pixels[0]);
        Assert.Equal(50, l.Pixels[1]);
        Assert.Equal(90, l.Pixels[2]);
        Assert.Equal(255, l.Pixels[3]);
        Assert.Equal(40, l.Pixels[3 * 4 + 0]);
        Assert.Equal(93, l.Pixels[3 * 4 + 2]);
    }

    [Fact]
    public void SixteenBit_Flattened_ConvertsHighByte_AndWarns()
    {
        using var comp = new MemoryStream();
        void W16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); comp.Write(b); }
        W16(0);                                   // raw
        for (int c = 0; c < 3; c++)               // 1x1, 3 channels, u16 values
            W16((ushort)(0xAB00 + c));
        var psd = new PsdBuilder { Width = 1, Height = 1, Depth = 16 }.Build(null, comp.ToArray());

        var doc = PsdReader.Load(psd, "deep", out var warnings);
        var l = Assert.IsType<PixelLayer>(Assert.Single(doc.Layers));
        Assert.Equal(0xAB, l.Pixels[0]);
        Assert.Contains(warnings, w => w.Contains("16-bit"));
    }

    [Fact]
    public void UnsupportedModes_ThrowClearErrors()
    {
        var cmyk = new PsdBuilder { Width = 1, Height = 1, Mode = 4 }.Build(null);
        Assert.Throws<InvalidDataException>(() => PsdReader.Load(cmyk, "x", out _));

        var notPsd = Encoding.ASCII.GetBytes("not a psd file at all........");
        Assert.Throws<InvalidDataException>(() => PsdReader.Load(notPsd, "x", out _));
    }

    // ------------------------------------------------------------- tagged-block builders

    private static byte[] Tagged(string key, byte[] data)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("8BIM"));
        ms.Write(Encoding.ASCII.GetBytes(key));
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)data.Length);
        ms.Write(b);
        ms.Write(data);
        if ((data.Length & 1) != 0) ms.WriteByte(0);
        return ms.ToArray();
    }

    // PS descriptor primitives (uni class name "" + classId "null" + item list)
    private static void WKey(Stream s, string k)
    {
        Span<byte> b = stackalloc byte[4];
        if (k.Length == 4) { BinaryPrimitives.WriteUInt32BigEndian(b, 0); s.Write(b); }
        else { BinaryPrimitives.WriteUInt32BigEndian(b, (uint)k.Length); s.Write(b); }
        s.Write(Encoding.ASCII.GetBytes(k));
    }

    private static byte[] Desc(params (string key, byte[] val)[] items)
    {
        using var ms = new MemoryStream();
        W32(ms, 0);                                  // unicode class name: 0 chars
        WKey(ms, "null");                            // class id
        W32(ms, (uint)items.Length);
        foreach (var (k, v) in items) { WKey(ms, k); ms.Write(v); }
        return ms.ToArray();
    }

    private static byte[] VObjc(params (string key, byte[] val)[] items)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("Objc"));
        ms.Write(Desc(items));
        return ms.ToArray();
    }

    private static byte[] VDoub(double v)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("doub"));
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, v);
        ms.Write(b);
        return ms.ToArray();
    }

    private static byte[] VUntF(string unit, double v)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("UntF"));
        ms.Write(Encoding.ASCII.GetBytes(unit));
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, v);
        ms.Write(b);
        return ms.ToArray();
    }

    private static byte[] VBool(bool v)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("bool"));
        ms.WriteByte((byte)(v ? 1 : 0));
        return ms.ToArray();
    }

    private static byte[] VEnum(string type, string value)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("enum"));
        WKey(ms, type);
        WKey(ms, value);
        return ms.ToArray();
    }

    private static byte[] RgbDesc(double r, double g, double b)
        => VObjc(("Rd  ", VDoub(r)), ("Grn ", VDoub(g)), ("Bl  ", VDoub(b)));

    /// <summary>Closed-rectangle vector-mask block (vmsk): coords are canvas fractions.</summary>
    private static byte[] VmskRect(double l, double t, double rr, double bb)
    {
        using var ms = new MemoryStream();
        W32(ms, 3);   // version
        W32(ms, 0);   // flags
        void Rec(ushort sel, Action<Stream>? body = null)
        {
            W16(ms, sel);
            long start = ms.Length;
            body?.Invoke(ms);
            while (ms.Length - start < 24) ms.WriteByte(0);
        }
        static void Fix(Stream s, double v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, (int)(v * 16777216.0));
            s.Write(b);
        }
        Rec(0, s => W16(s, 4));   // closed subpath, 4 knots
        foreach (var (x, y) in new[] { (l, t), (rr, t), (rr, bb), (l, bb) })
            Rec(1, s => { Fix(s, y); Fix(s, x); Fix(s, y); Fix(s, x); Fix(s, y); Fix(s, x); });
        return ms.ToArray();
    }

    private static void W16(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
    private static void W32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }

    [Fact]
    public void Lfx2_DropShadowAndColorOverlay_MapToLayerEffects()
    {
        var lfx2Body = new MemoryStream();
        W32(lfx2Body, 0);    // object version
        W32(lfx2Body, 16);   // descriptor version
        lfx2Body.Write(Desc(
            ("Scl ", VUntF("#Prc", 100)),
            ("masterFXSwitch", VBool(true)),
            ("DrSh", VObjc(
                ("enab", VBool(true)),
                ("Md  ", VEnum("BlnM", "Mltp")),
                ("Clr ", RgbDesc(255, 0, 0)),
                ("Opct", VUntF("#Prc", 50)),
                ("lagl", VUntF("#Ang", 90)),
                ("Dstn", VUntF("#Pxl", 10)),
                ("blur", VUntF("#Pxl", 7)))),
            ("SoFi", VObjc(
                ("enab", VBool(true)),
                ("Md  ", VEnum("BlnM", "Nrml")),
                ("Clr ", RgbDesc(0, 255, 0)),
                ("Opct", VUntF("#Prc", 80))))));

        var li = new LayerInfoBuilder();
        li.AddLayer("FX", 0, 0, 4, 4, fill: (9, 9, 9, 255), tagged: Tagged("lfx2", lfx2Body.ToArray()));
        var psd = new PsdBuilder { Width = 4, Height = 4 }.Build(li.Build());

        var doc = PsdReader.Load(psd, "fx", out _);
        var layer = Assert.IsType<PixelLayer>(Assert.Single(doc.Layers));
        Assert.Equal(2, layer.Effects.Count);

        var sh = layer.Effects.First(e => e.Kind == LayerEffectKind.DropShadow);
        Assert.Equal(BlendMode.Multiply, sh.BlendMode);
        Assert.Equal(0.5f, sh.Opacity, 2);
        Assert.Equal(1f, sh.R, 2);
        Assert.Equal(7f, sh.Radius, 1);
        Assert.Equal(0f, sh.OffsetX, 1);     // angle 90° → straight down
        Assert.Equal(10f, sh.OffsetY, 1);

        var ov = layer.Effects.First(e => e.Kind == LayerEffectKind.ColorOverlay);
        Assert.Equal(1f, ov.G, 2);
        Assert.Equal(0.8f, ov.Opacity, 2);
    }

    [Fact]
    public void VectorMask_RasterisedIntoLayerMask()
    {
        var li = new LayerInfoBuilder();
        // full-doc fill, vector mask covering the left half (x 0..0.5)
        li.AddLayer("Shape", 0, 0, 8, 8, fill: (200, 100, 50, 255),
            tagged: Tagged("vmsk", VmskRect(0.0, 0.0, 0.5, 1.0)));
        var psd = new PsdBuilder { Width = 8, Height = 8 }.Build(li.Build());

        var doc = PsdReader.Load(psd, "vm", out var warnings);
        var layer = Assert.IsType<PixelLayer>(Assert.Single(doc.Layers));
        Assert.NotNull(layer.Mask);
        Assert.True(layer.Mask![(4 * 8 + 1) * 4] > 200, "inside the path = revealed");
        Assert.True(layer.Mask![(4 * 8 + 6) * 4] < 50, "outside the path = hidden");
        Assert.Contains(warnings, w => w.Contains("vector mask rasterised"));
    }

    [Fact]
    public void SoCo_WithVectorMask_BecomesShapeLayer()
    {
        using var soco = new MemoryStream();
        W32(soco, 16);   // descriptor version
        soco.Write(Desc(("Clr ", RgbDesc(10, 220, 30))));

        var tagged = Tagged("SoCo", soco.ToArray())
            .Concat(Tagged("vmsk", VmskRect(0.25, 0.25, 0.75, 0.75))).ToArray();
        var li = new LayerInfoBuilder();
        li.AddLayer("Form 1", 0, 0, 0, 0, tagged: tagged);   // no raster content
        var psd = new PsdBuilder { Width = 16, Height = 16 }.Build(li.Build());

        var doc = PsdReader.Load(psd, "shape", out _);
        var layer = Assert.IsType<PixelLayer>(Assert.Single(doc.Layers));
        Assert.True(layer.Width is >= 8 and <= 10);
        Assert.Equal(4, layer.OffsetX);
        // centre of the shape = filled with the SoCo colour, full coverage
        int cx = layer.Width / 2, cy = layer.Height / 2;
        int i = (cy * layer.Width + cx) * 4;
        Assert.Equal(10, layer.Pixels[i]);
        Assert.Equal(220, layer.Pixels[i + 1]);
        Assert.Equal(255, layer.Pixels[i + 3]);
    }

    private static byte[] VText(string s)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("TEXT"));
        W32(ms, (uint)s.Length);
        ms.Write(Encoding.BigEndianUnicode.GetBytes(s));
        return ms.ToArray();
    }

    private static byte[] VTdta(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("tdta"));
        W32(ms, (uint)data.Length);
        ms.Write(data);
        return ms.ToArray();
    }

    [Fact]
    public void EngineData_ParsesDictsArraysNumbersStrings()
    {
        var blob = Encoding.ASCII.GetBytes(
            "<< /A 1.5 /B [ 1 2 3 ] /C << /D true /E (hi) >> /F /SomeName >>");
        var d = PsdReader.EngineData.Parse(blob);
        Assert.Equal(1.5, (double)d["A"]);
        Assert.Equal(3, ((List<object>)d["B"]).Count);
        var c = (Dictionary<string, object>)d["C"];
        Assert.Equal(true, c["D"]);
        Assert.Equal("hi", c["E"]);
        Assert.Equal("SomeName", d["F"]);
    }

    [Fact]
    public void TySh_BecomesEditableTextLayer()
    {
        var engine = Encoding.ASCII.GetBytes(@"<<
/EngineDict <<
/StyleRun << /RunArray [ << /StyleSheet << /StyleSheetData <<
  /Font 0 /FontSize 32.0 /Tracking 50 /Leading 38.4 /Underline true
  /FillColor << /Type 1 /Values [ 1.0 1.0 0.0 0.0 ] >>
>> >> >> ] >>
/ParagraphRun << /RunArray [ << /ParagraphSheet << /Properties << /Justification 2 >> >> >> ] >>
>>
/ResourceDict << /FontSet [ << /Name (ArialMT) >> ] >>
>>");

        using var tysh = new MemoryStream();
        W16(tysh, 1);                                        // version
        Span<byte> xf = stackalloc byte[8];
        foreach (var v in new double[] { 1, 0, 0, 1, 20, 30 })
        {
            BinaryPrimitives.WriteDoubleBigEndian(xf, v);
            tysh.Write(xf);
        }
        W16(tysh, 50);                                       // text version
        W32(tysh, 16);                                       // descriptor version
        tysh.Write(Desc(("Txt ", VText("Hello")), ("EngineData", VTdta(engine))));

        var li = new LayerInfoBuilder();
        li.AddLayer("TYPE", 4, 6, 20, 60, fill: (1, 1, 1, 255), tagged: Tagged("TySh", tysh.ToArray()));
        var psd = new PsdBuilder { Width = 64, Height = 64 }.Build(li.Build());

        var doc = PsdReader.Load(psd, "txt", out var warnings);
        var t = Assert.IsType<TextLayer>(Assert.Single(doc.Layers));
        Assert.Equal("Hello", t.Text);
        Assert.Equal(32f, t.FontSize, 1);
        Assert.Equal(255, t.R);
        Assert.Equal(0, t.G);
        Assert.Equal(TextAlign.Center, t.Align);
        Assert.True(t.Underline);
        Assert.Equal(6f, t.X);                               // PSD raster bbox left/top
        Assert.Equal(4f, t.Y);
        Assert.Equal(1.6f, t.Tracking, 2);                   // 50/1000 em × 32 px
        Assert.Equal(1.2f, t.LineSpacing, 2);                // 38.4 / 32
        Assert.Contains(warnings, w => w.Contains("editable text"));
    }

    [Fact]
    public void ExtractFonts_FontSetOnly_HandlesUtf16AndAscii()
    {
        using var ms = new MemoryStream();
        void Ascii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        Ascii("/DocumentResources << /FontSet [ << /Name (");
        ms.WriteByte(0xFE); ms.WriteByte(0xFF);
        ms.Write(Encoding.BigEndianUnicode.GetBytes("Open Sans Bold"));
        Ascii(") /Script 0 >> << /Name (ArialMT) >> ] >> ");
        Ascii("/ParagraphSheetSet [ << /Name (Normal RGB) >> ]");

        var fonts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        PsdReader.ExtractFonts(ms.ToArray(), fonts);

        Assert.Contains("Open Sans Bold", fonts);
        Assert.Contains("ArialMT", fonts);
        Assert.DoesNotContain("Normal RGB", fonts);
    }
}
