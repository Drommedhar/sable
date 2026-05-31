using Sable.Engine.Layers;
using Xunit;

namespace Sable.Tests;

public class ShapeLayerTests
{
    private static byte AlphaAt(byte[] buf, int w, int x, int y) => buf[(y * w + x) * 4 + 3];

    [Fact]
    public void Polygon_HasExpectedVertexCount()
    {
        var s = new ShapeLayer(ShapeKind.Polygon, 0, 0, 100, 100, 200, 100, 50) { Sides = 6 };
        var (pts, closed) = s.BuildOutline();
        Assert.True(closed);
        Assert.Equal(6, pts.Count);
    }

    [Fact]
    public void Star_HasDoubleVertexCount_AlternatingRadius()
    {
        var s = new ShapeLayer(ShapeKind.Star, 0, 0, 100, 100, 200, 100, 50) { Sides = 5, InnerRatio = 0.4f };
        var (pts, _) = s.BuildOutline();
        Assert.Equal(10, pts.Count);   // 5 outer + 5 inner
    }

    [Fact]
    public void Rectangle_FillsInterior()
    {
        int dw = 40, dh = 40;
        var s = new ShapeLayer(ShapeKind.Rectangle, 10, 10, 20, 20, 200, 50, 50) { Filled = true, A = 255 };
        var buf = new byte[dw * dh * 4];
        s.Rasterize(buf, dw, dh);
        Assert.True(AlphaAt(buf, dw, 20, 20) > 200);
        Assert.Equal(0, AlphaAt(buf, dw, 2, 2));
        Assert.Equal(200, buf[(20 * dw + 20) * 4]);   // fill R
    }

    [Fact]
    public void RoundedRect_CornerIsTransparent_CenterFilled()
    {
        int dw = 60, dh = 60;
        var s = new ShapeLayer(ShapeKind.RoundedRect, 5, 5, 50, 50, 100, 100, 100) { Filled = true, CornerRadius = 18 };
        var buf = new byte[dw * dh * 4];
        s.Rasterize(buf, dw, dh);
        Assert.True(AlphaAt(buf, dw, 30, 30) > 200);     // centre filled
        Assert.Equal(0, AlphaAt(buf, dw, 6, 6));         // rounded corner clipped → transparent
    }

    [Fact]
    public void Line_Strokes_AlongPath_NoFill()
    {
        int dw = 40, dh = 40;
        var s = new ShapeLayer(ShapeKind.Line, 5, 20, 30, 0, 0, 0, 0) { StrokeWidth = 4 };
        var buf = new byte[dw * dh * 4];
        s.Rasterize(buf, dw, dh);
        Assert.True(AlphaAt(buf, dw, 20, 20) > 200);   // on the line
        Assert.Equal(0, AlphaAt(buf, dw, 20, 5));      // far away
    }

    [Fact]
    public void Stroke_Dashed_HasGaps()
    {
        int dw = 80, dh = 20;
        var s = new ShapeLayer(ShapeKind.Line, 2, 10, 76, 0, 0, 0, 0)
        { StrokeWidth = 3, DashOn = true, DashLen = 6, GapLen = 6 };
        var buf = new byte[dw * dh * 4];
        s.Rasterize(buf, dw, dh);
        int on = 0, off = 0;
        for (int x = 4; x < 76; x++) { if (AlphaAt(buf, dw, x, 10) > 128) on++; else off++; }
        Assert.True(on > 0, "dash should draw some pixels");
        Assert.True(off > 0, "dash should leave gaps");
    }

    [Fact]
    public void Caps_SquareExtendsPastEnd_ButtDoesNot()
    {
        int dw = 40, dh = 20;
        // horizontal line ending at x=20; check 2px past the end (x=22), on-axis
        var buf = new byte[dw * dh * 4];
        new ShapeLayer(ShapeKind.Line, 5, 10, 15, 0, 0, 0, 0) { StrokeWidth = 8, Cap = Sable.Engine.Layers.LineCap.Butt }
            .Rasterize(buf, dw, dh);
        int buttPast = AlphaAt(buf, dw, 22, 10);

        var buf2 = new byte[dw * dh * 4];
        new ShapeLayer(ShapeKind.Line, 5, 10, 15, 0, 0, 0, 0) { StrokeWidth = 8, Cap = Sable.Engine.Layers.LineCap.Square }
            .Rasterize(buf2, dw, dh);
        int squarePast = AlphaAt(buf2, dw, 22, 10);

        Assert.Equal(0, buttPast);           // butt cap stops at the endpoint
        Assert.True(squarePast > 200);       // square cap extends ~halfWidth past it
    }

    [Fact]
    public void Arrow_DrawsHeadNearTip()
    {
        int dw = 60, dh = 20;
        var s = new ShapeLayer(ShapeKind.Arrow, 2, 10, 50, 0, 0, 0, 0) { StrokeWidth = 3 };
        var buf = new byte[dw * dh * 4];
        s.Rasterize(buf, dw, dh);
        // the head triangle is wider than the 3px shaft: a point 3px off the axis near the base
        // is covered by the head but would not be by a bare line
        Assert.True(AlphaAt(buf, dw, 43, 13) > 128 || AlphaAt(buf, dw, 43, 7) > 128);
    }
}
