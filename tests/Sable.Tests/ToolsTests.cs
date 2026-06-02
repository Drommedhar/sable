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
    public void MeshWarp_IdentityGrid_PreservesContent_ShiftMovesIt()
    {
        int w = 40, h = 40, gx = 3, gy = 3;
        var src = new byte[w * h * 4];
        // a white block at (10..20, 10..20)
        for (int y = 10; y < 20; y++)
        for (int x = 10; x < 20; x++)
        { int i = (y * w + x) * 4; src[i] = src[i + 1] = src[i + 2] = src[i + 3] = 255; }

        var grid = new (float, float)[gx * gy];
        for (int j = 0; j < gy; j++)
        for (int k = 0; k < gx; k++)
            grid[j * gx + k] = (w * k / (float)(gx - 1), h * j / (float)(gy - 1));

        // identity warp → block stays
        var id = MeshWarpTool.Warp(src, w, h, gx, gy, grid, grid);
        Assert.True(id[(15 * w + 15) * 4 + 3] > 200);

        // shift every dst point +8 in X → block moves right by ~8
        var shifted = new (float X, float Y)[grid.Length];
        for (int i = 0; i < grid.Length; i++) shifted[i] = (grid[i].Item1 + 8, grid[i].Item2);
        var moved = MeshWarpTool.Warp(src, w, h, gx, gy, grid, shifted);
        Assert.True(moved[(15 * w + 23) * 4 + 3] > 200);   // block now around x=18..28
        Assert.True(moved[(15 * w + 12) * 4 + 3] < 80);    // original spot mostly empty
    }

    [Fact]
    public void Liquify_Push_MovesEdge()
    {
        int w = 40, h = 40;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            if (x < 20) { px[i] = 255; px[i + 2] = 0; } else { px[i] = 0; px[i + 2] = 255; }   // red | blue
            px[i + 3] = 255;
        }
        LiquifyTool.Stamp(px, w, h, 20, 20, dragX: 10, dragY: 0, LiquifyMode.Push, strength: 1f, radius: 12, hardness: 0.5f);
        // a pixel just right of the old edge now samples from the red side → red rises, blue falls
        int p = (20 * w + 23) * 4;
        Assert.True(px[p] > 80, $"red should bleed right (got {px[p]})");
        Assert.True(px[p + 2] < 220, $"blue should drop (got {px[p + 2]})");
    }

    [Fact]
    public void Heal_MatchesDestinationTone_KeepsSourceTexture()
    {
        int w = 24, h = 24;
        var dest = new byte[w * h * 4];
        var srcBuf = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            dest[i * 4] = dest[i * 4 + 1] = dest[i * 4 + 2] = 100; dest[i * 4 + 3] = 255;
            srcBuf[i * 4] = srcBuf[i * 4 + 1] = srcBuf[i * 4 + 2] = 200; srcBuf[i * 4 + 3] = 255;
        }
        int spot = (12 * w + 12) * 4; srcBuf[spot] = srcBuf[spot + 1] = srcBuf[spot + 2] = 210;

        var brush = new BrushTool
        {
            Radius = 8, Hardness = 1f, Flow = 1f, Clone = true, Heal = true,
            CloneSrc = srcBuf, CloneSrcW = w, CloneSrcH = h, CloneOffX = 0, CloneOffY = 0,
        };
        brush.BeginStroke();
        brush.Stamp(dest, w, h, 12, 12);

        // healed centre ≈ dest tone (100) + the source's local texture excess (~+10), NOT 200
        int c = (12 * w + 12) * 4;
        Assert.InRange(dest[c], 104, 124);
        Assert.True(dest[c] < 160);
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
    public void Paint_Undo_TargetsLiveBuffer_AfterBufferSwap()   // audit C4: command must re-read the live buffer, not a captured ref
    {
        var layer = new PixelLayer(128, 128);
        var session = new StrokeSession(layer.Pixels, 128, 128, new BrushTool { Radius = 20 },
            _ => { }, liveTarget: () => layer.Pixels);
        session.StrokeTo(20, 20, 100, 100);
        var cmd = session.Finalize();
        Assert.NotNull(cmd);

        // swap the layer's buffer to a fresh same-size array (as a later resize/SetBuffer would)
        var fresh = new byte[128 * 128 * 4];
        System.Array.Fill(fresh, (byte)200);
        layer.SetBuffer(128, 128, fresh);

        cmd!.Undo();   // must write into 'fresh' (the live buffer), not the orphaned original
        Assert.True(ReferenceEquals(layer.Pixels, fresh));
        // the painted tiles were reset to the 'before' (transparent) snapshot in the new buffer
        Assert.Equal(0, fresh[(60 * 128 + 60) * 4 + 3]);
    }

    [Fact]
    public void Paint_Undo_SkipsWhenGeometryChanged()   // audit C4: mismatched dims → safe skip, no corruption
    {
        var layer = new PixelLayer(128, 128);
        var session = new StrokeSession(layer.Pixels, 128, 128, new BrushTool { Radius = 20 },
            _ => { }, liveTarget: () => layer.Pixels);
        session.StrokeTo(20, 20, 100, 100);
        var cmd = session.Finalize()!;

        var smaller = new byte[64 * 64 * 4];
        System.Array.Fill(smaller, (byte)123);
        layer.SetBuffer(64, 64, smaller);
        var copy = (byte[])smaller.Clone();

        cmd.Undo();                     // dims no longer match → must not touch the buffer
        Assert.Equal(copy, layer.Pixels);
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

public class PatchToolTests
{
    // a 64x64 RGBA8 image where each pixel's R/G/B = its row (a vertical ramp), full alpha
    private static byte[] RowRamp(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        { int i = (y * w + x) * 4; px[i] = px[i + 1] = px[i + 2] = (byte)y; px[i + 3] = 255; }
        return px;
    }

    [Fact]
    public void Apply_NoOffset_IsIdentity()
    {
        int w = 64, h = 64; var src = RowRamp(w, h);
        var target = (byte[])src.Clone();
        PatchTool.Apply(target, src, w, h, 0, 0, (16, 16, 16, 16), null, w, 0, 0);
        Assert.Equal(src, target);
    }

    [Fact]
    public void Apply_CopiesSourceRegionWithToneMatch()
    {
        // selection rows 30..40, source 20 rows BELOW (offY=+20) reads rows 50..60.
        int w = 64, h = 64; var src = RowRamp(w, h);
        var target = (byte[])src.Clone();
        PatchTool.Apply(target, src, w, h, 0, 0, (16, 30, 16, 10), null, w, 0, 20);

        // tone shift = mean(dest 30..40) - mean(source 50..60) = 34.5 - 54.5 = -20.
        // healed pixel = source(+20 rows) + tone = (y+20) + (-20) = y → ramp preserved (perfect blend-in).
        for (int y = 30; y < 40; y++)
        for (int x = 16; x < 32; x++)
        { int i = (y * w + x) * 4; Assert.InRange(target[i], (byte)(y - 1), (byte)(y + 1)); }
        // outside the selection: untouched
        int o = (10 * w + 5) * 4; Assert.Equal((byte)10, target[o]);
    }

    [Fact]
    public void Apply_IsPureFunctionOfSrcAndOffset_NoCompounding()
    {
        // Applying twice in a row (live-drag frames) with the same offset must be idempotent,
        // and re-applying with a different offset must equal a fresh single application —
        // i.e. it never reads its own prior output (the cross-gesture smear bug).
        int w = 64, h = 64; var src = RowRamp(w, h);
        (int, int, int, int) rect = (10, 25, 20, 15);

        var a = (byte[])src.Clone();
        PatchTool.Apply(a, src, w, h, 0, 0, rect, null, w, 3, 17);
        var aTwice = (byte[])a.Clone();
        PatchTool.Apply(aTwice, src, w, h, 0, 0, rect, null, w, 3, 17);
        Assert.Equal(a, aTwice);   // same offset twice → no change the 2nd time

        var b = (byte[])a.Clone();                       // b currently holds the offset-(3,17) result
        PatchTool.Apply(b, src, w, h, 0, 0, rect, null, w, -5, -9);
        var fresh = (byte[])src.Clone();
        PatchTool.Apply(fresh, src, w, h, 0, 0, rect, null, w, -5, -9);
        Assert.Equal(fresh, b);    // re-patch from a different offset == fresh apply (no compounding)
    }

    [Fact]
    public void Apply_OffsetLayer_HealsHoleInBufferSpace()
    {
        // layer buffer sits at doc offset (100,100); selection in doc space.
        int w = 64, h = 64; var src = RowRamp(w, h);
        var target = (byte[])src.Clone();
        PatchTool.Apply(target, src, w, h, 100, 100, (108, 130, 16, 10), null, 400, 0, 20);
        // buffer rows 30..40 (doc 130..140) healed; ramp preserved by tone-match
        for (int y = 30; y < 40; y++)
        { int i = (y * w + 12) * 4; Assert.InRange(target[i], (byte)(y - 1), (byte)(y + 1)); }
    }
}
