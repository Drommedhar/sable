using System.Buffers.Binary;
using System.Text;
using Sable.Core;
using Sable.Tools;
using Xunit;

namespace Sable.Tests;

/// <summary>Brush depth (plan §2): elliptical dabs, sampled tips, jitter, paint blend modes, .abr import.</summary>
public class BrushShapeTests
{
    private static byte[] NewBuf(int w, int h) => new byte[w * h * 4];

    private static (int minX, int minY, int maxX, int maxY, int count) PaintedBounds(byte[] px, int w, int h)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1, n = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (px[(y * w + x) * 4 + 3] > 0)
            {
                n++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        return (minX, minY, maxX, maxY, n);
    }

    [Fact]
    public void Roundness_SquashesDabVertically()
    {
        var px = NewBuf(64, 64);
        var b = new BrushTool { Radius = 10, Roundness = 0.5f, Pencil = true, PressureSize = false };
        b.Stamp(px, 64, 64, 32, 32);
        var (minX, minY, maxX, maxY, _) = PaintedBounds(px, 64, 64);
        int w = maxX - minX + 1, h = maxY - minY + 1;
        Assert.InRange(w, 18, 21);
        Assert.InRange(h, 8, 11);
    }

    [Fact]
    public void Angle_RotatesEllipse()
    {
        var px = NewBuf(64, 64);
        var b = new BrushTool { Radius = 10, Roundness = 0.5f, Angle = 90, Pencil = true, PressureSize = false };
        b.Stamp(px, 64, 64, 32, 32);
        var (minX, minY, maxX, maxY, _) = PaintedBounds(px, 64, 64);
        int w = maxX - minX + 1, h = maxY - minY + 1;
        Assert.InRange(h, 18, 21);   // long axis now vertical
        Assert.InRange(w, 8, 11);
    }

    [Fact]
    public void SampledTip_LeftHalfPaints()
    {
        var px = NewBuf(64, 64);
        var b = new BrushTool { Radius = 8, Pencil = true, PressureSize = false };
        b.Tip = new byte[] { 255, 0 };   // 2x1: left full, right empty
        b.TipW = 2; b.TipH = 1;
        b.Stamp(px, 64, 64, 32, 32);
        Assert.True(px[(32 * 64 + 28) * 4 + 3] > 0);    // left of centre painted
        Assert.Equal(0, px[(32 * 64 + 37) * 4 + 3]);    // right of centre empty
    }

    [Fact]
    public void Jitter_DeterministicWithSeed()
    {
        var a = NewBuf(64, 64);
        var c = NewBuf(64, 64);
        var b = new BrushTool
        {
            Radius = 6, PressureSize = false,
            SizeJitter = 0.6f, ScatterJitter = 0.8f, AngleJitter = 0.5f, FlowJitter = 0.4f,
            JitterSeed = 7,
        };
        b.BeginStroke();
        b.Stroke(a, 64, 64, 10, 32, 54, 32);
        b.BeginStroke();
        b.Stroke(c, 64, 64, 10, 32, 54, 32);
        Assert.Equal(a, c);
    }

    [Fact]
    public void Scatter_MovesDabsOffTheStrokeLine()
    {
        var px = NewBuf(64, 64);
        var b = new BrushTool { Radius = 3, PressureSize = false, ScatterJitter = 1f, JitterSeed = 3 };
        b.BeginStroke();
        b.Stroke(px, 64, 64, 8, 32, 56, 32);
        var (_, minY, _, maxY, n) = PaintedBounds(px, 64, 64);
        Assert.True(n > 0);
        Assert.True(maxY - minY > 8, $"scatter should spread dabs vertically (got {maxY - minY})");
        Assert.True(b.MaxReach > b.Radius + 1);
    }

    [Fact]
    public void PaintBlend_MultiplyDarkens()
    {
        var px = NewBuf(8, 8);
        for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 200; px[i + 2] = 200; px[i + 3] = 255; }
        var b = new BrushTool
        {
            Radius = 3, Hardness = 1f, PressureSize = false, Pencil = true,
            R = 128, G = 128, B = 128,
            PaintBlend = BlendMode.Multiply,
        };
        b.Stamp(px, 8, 8, 4, 4);
        int i4 = (4 * 8 + 4) * 4;
        Assert.InRange(px[i4], 98, 103);   // 200/255 * 128/255 ≈ 0.394 → ~100
    }

    [Fact]
    public void BlendOps_SpotValues_AndAllModesStayInRange()
    {
        Assert.Equal(0.25f, BlendOps.Blend(BlendMode.Multiply, (0.5f, 0.5f, 0.5f), (0.5f, 0.5f, 0.5f)).r, 3);
        Assert.Equal(0.75f, BlendOps.Blend(BlendMode.Screen, (0.5f, 0.5f, 0.5f), (0.5f, 0.5f, 0.5f)).r, 3);
        Assert.Equal(0.3f, BlendOps.Blend(BlendMode.Difference, (0.8f, 0.8f, 0.8f), (0.5f, 0.5f, 0.5f)).r, 3);

        foreach (BlendMode m in System.Enum.GetValues<BlendMode>())
        {
            var v = BlendOps.Blend(m, (0.7f, 0.2f, 0.9f), (0.3f, 0.8f, 0.1f));
            Assert.True(v.r is >= -0.001f and <= 1.001f, $"{m} r={v.r}");
            Assert.True(v.g is >= -0.001f and <= 1.001f, $"{m} g={v.g}");
            Assert.True(v.b is >= -0.001f and <= 1.001f, $"{m} b={v.b}");
        }
    }

    // ------------------------------------------------------------- .abr

    private static void W16(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
    private static void W32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }

    [Fact]
    public void Abr_V1_ComputedBrush()
    {
        using var ms = new MemoryStream();
        W16(ms, 1);   // version
        W16(ms, 1);   // count
        W16(ms, 1);   // type computed
        W32(ms, 15);  // size
        W32(ms, 0);   // misc
        W16(ms, 30);  // spacing %
        ms.WriteByte(0);   // antialiasing
        W16(ms, 40);  // diameter
        W16(ms, 80);  // hardness %
        W16(ms, unchecked((ushort)(short)-45));   // angle
        W16(ms, 60);  // roundness %

        var presets = AbrReader.Load(ms.ToArray(), "pack", out var notes);
        var p = Assert.Single(presets);
        Assert.Equal(20f, p.Radius);
        Assert.Equal(0.8f, p.Hardness, 2);
        Assert.Equal(0.3f, p.Spacing, 2);
        Assert.Equal(-45f, p.Angle);
        Assert.Equal(0.6f, p.Roundness, 2);
        Assert.Null(p.Tip);
    }

    [Fact]
    public void Abr_V6_SampledTip()
    {
        // payload: idLen + id + 10 unknown + rect(16) + depth(2) + comp(1) + 4x4 raw rows
        const string id = "abc";
        using var payload = new MemoryStream();
        payload.WriteByte((byte)id.Length);
        payload.Write(Encoding.ASCII.GetBytes(id));
        payload.Write(new byte[10]);
        W32(payload, 0); W32(payload, 0); W32(payload, 4); W32(payload, 4);   // top/left/bottom/right
        W16(payload, 8);      // depth
        payload.WriteByte(0); // raw
        var rows = new byte[16];
        Array.Fill(rows, (byte)200);
        payload.Write(rows);

        using var samp = new MemoryStream();
        W32(samp, (uint)payload.Length);
        payload.WriteTo(samp);
        while (samp.Length % 4 != 0) samp.WriteByte(0);

        using var ms = new MemoryStream();
        W16(ms, 6);   // version
        W16(ms, 1);   // subversion
        ms.Write(Encoding.ASCII.GetBytes("8BIM"));
        ms.Write(Encoding.ASCII.GetBytes("samp"));
        W32(ms, (uint)samp.Length);
        samp.WriteTo(ms);

        var presets = AbrReader.Load(ms.ToArray(), "pack", out _);
        var p = Assert.Single(presets);
        Assert.NotNull(p.Tip);
        Assert.Equal(4, p.TipW);
        Assert.Equal(4, p.TipH);
        Assert.Equal(200, p.Tip![0]);
        Assert.Equal(2f, p.Radius);
    }

    [Fact]
    public void Abr_PresetRoundTripsThroughBrush()
    {
        var b = new BrushTool
        {
            Radius = 12, Angle = 30, Roundness = 0.4f,
            SizeJitter = 0.2f, ScatterJitter = 0.3f,
            PaintBlend = BlendMode.Screen,
            Tip = new byte[] { 1, 2, 3, 4 }, TipW = 2, TipH = 2,
        };
        var p = BrushPreset.From("x", b);
        var b2 = new BrushTool();
        p.ApplyTo(b2);
        Assert.Equal(30f, b2.Angle);
        Assert.Equal(0.4f, b2.Roundness);
        Assert.Equal(0.2f, b2.SizeJitter);
        Assert.Equal(BlendMode.Screen, b2.PaintBlend);
        Assert.Equal(2, b2.TipW);
        Assert.Equal(b.Tip, b2.Tip);
    }
}
