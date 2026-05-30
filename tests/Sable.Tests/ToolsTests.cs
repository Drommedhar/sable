using Sable.Engine.Layers;
using Sable.Gpu;
using Sable.Tools;
using Xunit;

namespace Sable.Tests;

public class BrushToolTests
{
    private static int AlphaPixels(byte[] px)
    {
        int n = 0;
        for (int i = 3; i < px.Length; i += 4) if (px[i] > 0) n++;
        return n;
    }

    [Fact]
    public void Stroke_PaintsCoverage()
    {
        var layer = new PixelLayer(64, 64);
        new BrushTool { Radius = 12 }.Stroke(layer.Pixels, 64, 64, 8, 32, 56, 32);
        Assert.True(AlphaPixels(layer.Pixels) > 0);
    }

    [Fact]
    public void Pencil_HasHardBinaryEdge()
    {
        // soft brush: edge pixels are partially covered; pencil: every painted pixel is fully opaque
        var soft = new PixelLayer(32, 32);
        new BrushTool { Radius = 8, Hardness = 0.3f, Flow = 1f }.Stamp(soft.Pixels, 32, 32, 16, 16);
        var pencil = new PixelLayer(32, 32);
        new BrushTool { Radius = 8, Hardness = 0.3f, Flow = 1f, Pencil = true }.Stamp(pencil.Pixels, 32, 32, 16, 16);

        bool softHasPartial = false;
        for (int i = 3; i < soft.Pixels.Length; i += 4)
            if (soft.Pixels[i] is > 0 and < 255) { softHasPartial = true; break; }
        Assert.True(softHasPartial);                       // soft brush feathers

        for (int i = 3; i < pencil.Pixels.Length; i += 4)
            Assert.True(pencil.Pixels[i] is 0 or 255);     // pencil is all-or-nothing
    }

    [Fact]
    public void Erase_ReducesAlpha()
    {
        var layer = new PixelLayer(64, 64);
        var brush = new BrushTool { Radius = 16 };
        brush.Stroke(layer.Pixels, 64, 64, 8, 32, 56, 32);
        int painted = AlphaPixels(layer.Pixels);

        brush.Erase = true;
        brush.Stroke(layer.Pixels, 64, 64, 8, 32, 56, 32);
        int afterErase = AlphaPixels(layer.Pixels);

        Assert.True(afterErase < painted);
    }

    [Fact]
    public void Stamp_WritesChosenColor_AtCenter()
    {
        var layer = new PixelLayer(32, 32);
        var brush = new BrushTool { Radius = 8, Hardness = 1f, R = 200, G = 100, B = 50 };
        brush.Stamp(layer.Pixels, 32, 32, 16, 16);
        int i = (16 * 32 + 16) * 4;
        Assert.Equal(200, layer.Pixels[i]);
        Assert.Equal(100, layer.Pixels[i + 1]);
        Assert.Equal(50, layer.Pixels[i + 2]);
        Assert.True(layer.Pixels[i + 3] > 0);
    }

    [Fact]
    public void Origin_AppliesDocSpaceClip_ToOffsetBuffer()
    {
        // a 32x32 buffer whose (0,0) is at doc (100,100); a doc-space selection clip covers only
        // doc x>=116 (buffer x>=16). Painting the whole buffer must only mark the right half.
        var layer = new PixelLayer(32, 32);
        var brush = new BrushTool
        {
            Radius = 64, Hardness = 1f, Flow = 1f,
            OriginX = 100, OriginY = 100,
            Clip = (116, 100, 100, 32),   // doc rect → buffer x in [16,32)
        };
        brush.Stamp(layer.Pixels, 32, 32, 16, 16);

        // left half (buffer x<16 → doc x<116) outside the clip → untouched
        Assert.Equal(0, layer.Pixels[(16 * 32 + 4) * 4 + 3]);
        // right half (buffer x>=16) inside the clip → painted
        Assert.True(layer.Pixels[(16 * 32 + 24) * 4 + 3] > 0);
    }
}

public class FillToolTests
{
    [Fact]
    public void Flood_FillsContiguousRegion()
    {
        int w = 8, h = 8;
        var px = new byte[w * h * 4];   // all (0,0,0,0)
        int changed = FillTool.Flood(px, w, h, 0, 0, 255, 0, 0, 255);
        Assert.Equal(w * h, changed);   // whole uniform buffer filled
        Assert.Equal(255, px[0]);       // R
        Assert.Equal(255, px[3]);       // A
    }

    [Fact]
    public void Flood_StopsAtColorBoundary()
    {
        int w = 8, h = 1;
        var px = new byte[w * 4];
        // right half opaque white = a barrier of different color
        for (int x = 4; x < w; x++) { int i = x * 4; px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 255; }
        int changed = FillTool.Flood(px, w, h, 0, 0, 0, 0, 255, 255, tolerance: 0);
        Assert.Equal(4, changed);       // only left half (the seed's region)
        Assert.Equal(255, px[4 * 4]);   // barrier untouched (still white R)
    }

    [Fact]
    public void Flood_RestrictedToClip()
    {
        int w = 8, h = 8;
        var px = new byte[w * h * 4];   // uniform transparent
        // clip to a 3×3 region at (2,2); seed inside it
        int changed = FillTool.Flood(px, w, h, 3, 3, 255, 0, 0, 255, 0, (2, 2, 3, 3));
        Assert.Equal(9, changed);       // only the clip region filled
        Assert.Equal(0, px[0]);         // outside clip untouched
    }

    [Fact]
    public void Flood_NoOp_WhenAlreadyFillColor()
    {
        var px = new byte[4 * 4];
        for (int i = 0; i < px.Length; i++) px[i] = 255;
        int changed = FillTool.Flood(px, 4, 1, 0, 0, 255, 255, 255, 255);
        Assert.Equal(0, changed);
    }
}

public class StrokeSessionTests
{
    [Fact]
    public void Paint_Undo_Redo_RestoresPixels()
    {
        var layer = new PixelLayer(128, 128);
        var tiles = new HashSet<(int, int)>();
        var session = new StrokeSession(layer.Pixels, 128, 128, new BrushTool { Radius = 20 },
            t => { foreach (var x in t) tiles.Add(x); });
        session.StrokeTo(20, 20, 100, 100);
        var cmd = session.Finalize();

        Assert.NotNull(cmd);
        Assert.NotEmpty(tiles);                                        // touched tiles reported
        var painted = (byte[])layer.Pixels.Clone();

        cmd!.Undo();
        Assert.All(EveryAlpha(layer.Pixels), a => Assert.Equal(0, a)); // fully reverted

        cmd!.Do();                                                     // redo
        Assert.Equal(painted, layer.Pixels);
    }

    [Fact]
    public void Finalize_ReturnsNull_WhenNothingPainted()
    {
        var layer = new PixelLayer(64, 64);
        var session = new StrokeSession(layer.Pixels, 64, 64, new BrushTool(), _ => { });
        Assert.Null(session.Finalize());
    }

    [Fact]
    public void Stroke_ReportsOnlyTouchedTiles()
    {
        // 512² layer = 2x2 tiles; a stroke in the top-left tile should not touch (1,1)
        var layer = new PixelLayer(512, 512);
        var tiles = new HashSet<(int, int)>();
        var session = new StrokeSession(layer.Pixels, 512, 512, new BrushTool { Radius = 8 },
            t => { foreach (var x in t) tiles.Add(x); });
        session.StrokeTo(20, 20, 60, 60);
        session.Finalize();
        Assert.Contains((0, 0), tiles);
        Assert.DoesNotContain((1, 1), tiles);
    }

    private static IEnumerable<byte> EveryAlpha(byte[] px)
    {
        for (int i = 3; i < px.Length; i += 4) yield return px[i];
    }
}

public class ViewportTransformTests
{
    [Fact]
    public void Fit_CentersAndScalesToFit()
    {
        // 512² doc into 1000x800: scale = 800/512 = 1.5625, displayed 800x800, centered
        var vp = ViewportTransform.Fit(1000, 800, 512, 512, 1.0, 0, 0);
        Assert.Equal(1.5625f, vp.Scale, 4);
        Assert.Equal(100f, vp.Ox, 3);   // (1000 - 800) / 2
        Assert.Equal(0f, vp.Oy, 3);
    }

    [Fact]
    public void Fit_AppliesZoomAndPan()
    {
        var vp = ViewportTransform.Fit(1000, 800, 512, 512, 2.0, 10, -5);
        Assert.Equal(3.125f, vp.Scale, 4);          // fit * 2
        // ox = (1000 - 512*3.125)/2 + 10
        Assert.Equal((1000 - 512 * 3.125f) / 2 + 10, vp.Ox, 3);
        Assert.Equal((800 - 512 * 3.125f) / 2 - 5, vp.Oy, 3);
    }
}
