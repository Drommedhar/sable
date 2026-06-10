using Sable.Tools;
using Xunit;

namespace Sable.Tests;

public class BrushDynamicsTests
{
    private static byte[] Buf(int w, int h) => new byte[w * h * 4];

    private static int PaintedPixels(byte[] px)
    {
        int n = 0;
        for (int i = 3; i < px.Length; i += 4) if (px[i] > 0) n++;
        return n;
    }

    [Fact]
    public void PressureSize_LowPressure_PaintsSmallerDab()
    {
        var full = Buf(64, 64);
        var light = Buf(64, 64);
        var b = new BrushTool { Radius = 16, Hardness = 1f, PressureSize = true };
        b.Stroke(full, 64, 64, 32, 32, 32, 32, 1f, 1f);
        b.Stroke(light, 64, 64, 32, 32, 32, 32, 0.2f, 0.2f);
        Assert.True(PaintedPixels(light) < PaintedPixels(full) / 2);
    }

    [Fact]
    public void PressureFlow_LowPressure_PaintsFainter()
    {
        var full = Buf(32, 32);
        var light = Buf(32, 32);
        var b = new BrushTool { Radius = 8, Hardness = 1f, PressureSize = false, PressureFlow = true };
        b.Stroke(full, 32, 32, 16, 16, 16, 16, 1f, 1f);
        b.Stroke(light, 32, 32, 16, 16, 16, 16, 0.3f, 0.3f);
        int ci = (16 * 32 + 16) * 4 + 3;
        Assert.True(light[ci] < full[ci]);
        Assert.True(light[ci] > 0);
    }

    [Fact]
    public void Spacing_SparseStroke_LeavesGaps()
    {
        // hard small dabs with 200% spacing along a long line → unpainted pixels between dabs
        var dense = Buf(256, 16);
        var sparse = Buf(256, 16);
        var b = new BrushTool { Radius = 3, Hardness = 1f, PressureSize = false };
        b.Spacing = 0f;
        b.Stroke(dense, 256, 16, 4, 8, 250, 8);
        b.Spacing = 2f;
        b.Stroke(sparse, 256, 16, 4, 8, 250, 8);
        Assert.True(PaintedPixels(sparse) < PaintedPixels(dense));
    }

    [Fact]
    public void GradientShapes_RadialIsRotationSymmetric_ConicalWraps()
    {
        // radial: equal distance from the start → equal colour, regardless of direction
        var px = Buf(64, 64);
        var def = GradientDef.ForegroundToTransparent(255, 0, 0);
        GradientTool.Apply(px, 64, 64, 32, 32, 32, 12, def, shape: GradientShape.Radial);
        int A(int x, int y) => px[(y * 64 + x) * 4 + 3];
        Assert.Equal(A(32 + 10, 32), A(32, 32 + 10));
        Assert.True(A(32 + 5, 32) > A(32 + 15, 32));   // closer = more opaque

        // reflected: symmetric about the start point along the drag axis
        // (pixel CENTRES sample the ramp: x=40 → +8.5, x=23 → -8.5 from the start)
        var rx = Buf(64, 64);
        GradientTool.Apply(rx, 64, 64, 32, 32, 52, 32, def, shape: GradientShape.Reflected);
        int RA(int x, int y) => rx[(y * 64 + x) * 4 + 3];
        Assert.Equal(RA(40, 32), RA(23, 32));
    }

    [Fact]
    public void DefaultPressure_FullEverywhere_MatchesLegacyStroke()
    {
        var a = Buf(64, 64);
        var c = Buf(64, 64);
        var b = new BrushTool { Radius = 10, Hardness = 0.5f };
        b.Stroke(a, 64, 64, 10, 10, 50, 50);                 // legacy overload
        b.Stroke(c, 64, 64, 10, 10, 50, 50, 1f, 1f);         // explicit full pressure
        Assert.Equal(a, c);
    }
}
