using Sable.Engine;
using Sable.Tools;
using Xunit;

namespace Sable.Tests;

public class SelectionTests
{
    [Fact]
    public void Invert_FlipsCoverage()
    {
        var m = Selections.Rect(10, 10, new SelRect(0, 0, 5, 10));   // left half selected
        var inv = Selections.Invert(m);
        Assert.Equal(0, inv[2]);                 // was selected → now 0
        Assert.Equal(255, inv[7]);               // was 0 → now 255
    }

    [Fact]
    public void GrowShrink_ChangeSelectedArea()
    {
        var m = Selections.Rect(40, 40, new SelRect(15, 15, 10, 10));   // 10x10 block
        int Count(byte[] a) { int n = 0; foreach (var v in a) if (v > 127) n++; return n; }
        int baseN = Count(m);
        Assert.True(Count(Selections.Grow(m, 40, 40, 3)) > baseN);
        Assert.True(Count(Selections.Shrink(m, 40, 40, 3)) < baseN);
    }

    [Fact]
    public void Border_IsBandAroundEdge_EmptyInterior()
    {
        var m = Selections.Rect(60, 60, new SelRect(20, 20, 20, 20));
        var b = Selections.Border(m, 60, 60, 3);
        Assert.True(b[30 * 60 + 30] < 128);      // deep interior not in the border band
        Assert.True(b[20 * 60 + 30] > 0);        // near the top edge is in the band
    }

    [Fact]
    public void ColorRange_SelectsAllMatching_NonContiguous()
    {
        // 4x1 strip: red, blue, red, blue — color-range on red picks both reds (non-contiguous)
        var px = new byte[4 * 1 * 4];
        void Set(int i, byte r, byte g, byte b) { px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = 255; }
        Set(0, 255, 0, 0); Set(1, 0, 0, 255); Set(2, 255, 0, 0); Set(3, 0, 0, 255);
        var m = Selections.ColorRange(px, 4, 1, 255, 0, 0, 10);
        Assert.Equal(255, m[0]); Assert.Equal(0, m[1]); Assert.Equal(255, m[2]); Assert.Equal(0, m[3]);
    }

    [Fact]
    public void Shift_TranslatesMask()
    {
        var m = new byte[5 * 5];
        m[2 * 5 + 2] = 255;                       // centre set
        var s = Selections.Shift(m, 5, 5, 1, -1); // right 1, up 1 → (3,1)
        Assert.Equal(0, s[2 * 5 + 2]);
        Assert.Equal(255, s[1 * 5 + 3]);
    }

    [Fact]
    public void Full_SelectsEverything()
    {
        var m = Selections.Full(8, 8);
        Assert.All(m, v => Assert.Equal(255, v));
    }

    [Fact]
    public void Ellipse_CentreSelected_CornersNot()
    {
        var m = Selections.Ellipse(100, 100, new SelRect(10, 10, 80, 80));
        Assert.Equal(255, m[50 * 100 + 50]);   // centre
        Assert.Equal(0, m[10 * 100 + 10]);      // bbox corner outside ellipse
        Assert.Equal(0, m[89 * 100 + 89]);
    }

    [Fact]
    public void Ellipse_BoundsRoughlyMatchRect()
    {
        var rect = new SelRect(20, 30, 60, 40);
        var m = Selections.Ellipse(120, 120, rect);
        var b = Selections.Bounds(m, 120, 120);
        // ellipse touches each edge midpoint, so bounds ≈ rect (±1 px rounding)
        Assert.InRange(b.X, rect.X - 1, rect.X + 1);
        Assert.InRange(b.Right, rect.Right - 1, rect.Right + 1);
        Assert.InRange(b.Bottom, rect.Bottom - 1, rect.Bottom + 1);
    }

    [Fact]
    public void Polygon_Triangle_FillsInterior()
    {
        var pts = new (double, double)[] { (10, 10), (90, 10), (50, 90) };
        var m = Selections.Polygon(100, 100, pts);
        Assert.Equal(255, m[20 * 100 + 50]);   // near top-centre, inside
        Assert.Equal(0, m[80 * 100 + 15]);      // bottom-left, outside the triangle
    }

    [Fact]
    public void Polygon_DegenerateReturnsEmpty()
    {
        var m = Selections.Polygon(50, 50, new (double, double)[] { (1, 1), (2, 2) });
        Assert.All(m, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Wand_SelectsContiguousColorRegion()
    {
        int w = 20, h = 20;
        var px = new byte[w * h * 4];
        // left half red, right half blue
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            if (x < 10) { px[i] = 255; px[i + 3] = 255; }
            else { px[i + 2] = 255; px[i + 3] = 255; }
        }
        var m = Selections.Wand(px, w, h, 2, 2, 16);
        Assert.Equal(255, m[2 * w + 2]);     // in red region
        Assert.Equal(0, m[2 * w + 15]);      // blue region not selected
        var b = Selections.Bounds(m, w, h);
        Assert.Equal(0, b.X);
        Assert.Equal(10, b.W);               // exactly the left half
    }

    [Fact]
    public void SetMaskSelection_SetsBoundsAndMask_EmptyClears()
    {
        var doc = new Document(64, 64);
        var m = Selections.Ellipse(64, 64, new SelRect(8, 8, 32, 32));
        doc.SetMaskSelection(m);
        Assert.NotNull(doc.SelectionMask);
        Assert.NotNull(doc.Selection);

        doc.SetMaskSelection(new byte[64 * 64]);   // all-zero → clears
        Assert.Null(doc.SelectionMask);
        Assert.Null(doc.Selection);
    }

    [Fact]
    public void Rect_FillsInsideOnly()
    {
        var m = Selections.Rect(50, 50, new SelRect(10, 10, 20, 20));
        Assert.Equal(255, m[15 * 50 + 15]);   // inside
        Assert.Equal(0, m[5 * 50 + 5]);        // outside
        Assert.Equal(0, m[35 * 50 + 35]);      // past the rect
    }

    [Fact]
    public void Combine_Add_IsUnion()
    {
        var a = new byte[] { 255, 0, 0, 100 };
        var b = new byte[] { 0, 255, 0, 200 };
        var r = Selections.Combine(a, b, SelMode.Add);
        Assert.Equal(new byte[] { 255, 255, 0, 200 }, r);   // max per element
    }

    [Fact]
    public void Combine_Subtract_RemovesB()
    {
        var a = new byte[] { 255, 255, 0, 255 };
        var b = new byte[] { 0, 255, 0, 0 };
        var r = Selections.Combine(a, b, SelMode.Subtract);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, r);     // cleared where b>0
    }

    [Fact]
    public void Combine_Intersect_IsMin()
    {
        var a = new byte[] { 255, 255, 0, 100 };
        var b = new byte[] { 255, 0, 255, 200 };
        var r = Selections.Combine(a, b, SelMode.Intersect);
        Assert.Equal(new byte[] { 255, 0, 0, 100 }, r);     // min per element
    }

    [Fact]
    public void Gradient_FadesAlongLine()
    {
        int w = 20, h = 1;
        var px = new byte[w * h * 4];   // transparent
        int changed = GradientTool.Apply(px, w, h, 0, 0, w, 0, 200, 100, 50);
        Assert.True(changed > 0);
        int aStart = px[(0) * 4 + 3];
        int aEnd = px[(w - 1) * 4 + 3];
        Assert.True(aStart > aEnd);        // opaque at start, fading to end
        Assert.Equal(200, px[0]);          // foreground color at start
        Assert.InRange(aStart, 200, 255);  // near-full alpha at start
    }

    [Fact]
    public void GradientDef_Sample_InterpolatesBetweenStops()
    {
        var d = new GradientDef(new GradientStop(0f, 0, 0, 0, 255), new GradientStop(1f, 255, 255, 255, 255));
        Assert.Equal((byte)0, d.Sample(0f).r);
        Assert.Equal((byte)255, d.Sample(1f).r);
        var mid = d.Sample(0.5f);
        Assert.InRange(mid.r, 126, 129);          // ~halfway grey
        // clamps outside range
        Assert.Equal((byte)0, d.Sample(-1f).r);
        Assert.Equal((byte)255, d.Sample(2f).r);
    }

    [Fact]
    public void Gradient_ZeroLength_NoOp()
    {
        var px = new byte[10 * 10 * 4];
        Assert.Equal(0, GradientTool.Apply(px, 10, 10, 5, 5, 5, 5, 255, 0, 0));
    }

    [Fact]
    public void Gradient_HonorsMaskCoverage()
    {
        int w = 10, h = 1;
        var px = new byte[w * h * 4];
        var mask = new byte[w * h];        // only x=0 selected
        mask[0] = 255;
        GradientTool.Apply(px, w, h, 0, 0, w, 0, 255, 255, 255, null, mask, w);
        Assert.True(px[0 * 4 + 3] > 0);    // masked pixel painted
        Assert.Equal(0, px[5 * 4 + 3]);    // outside mask untouched
    }

    [Fact]
    public void Feather_SoftensEdges_KeepsCore()
    {
        int w = 40, h = 40;
        var m = Selections.Rect(w, h, new SelRect(10, 10, 20, 20));
        var f = Selections.Feather(m, w, h, 4);
        Assert.Equal(255, f[20 * w + 20]);                 // deep interior stays fully selected
        int edge = f[10 * w + 20];                          // on the original hard boundary
        Assert.InRange(edge, 1, 254);                       // now a partial (soft) value
        Assert.True(f[6 * w + 20] > 0);                     // coverage bled outside the old edge
        Assert.Equal(0, f[0]);                              // far corner still unselected
    }

    [Fact]
    public void Feather_ZeroRadius_Unchanged()
    {
        var m = Selections.Rect(20, 20, new SelRect(5, 5, 10, 10));
        Assert.Same(m, Selections.Feather(m, 20, 20, 0));
    }

    [Fact]
    public void SnapshotSelectionMask_RasterizesRect_NullWhenEmpty()
    {
        var doc = new Document(32, 32);
        Assert.Null(doc.SnapshotSelectionMask());            // no selection

        doc.Selection = new SelRect(4, 4, 8, 8);
        var snap = doc.SnapshotSelectionMask();
        Assert.NotNull(snap);
        Assert.Equal(255, snap![6 * 32 + 6]);                // inside rect
        Assert.Equal(0, snap[0]);                            // outside
    }

    [Fact]
    public void Dodge_Lightens_Burn_Darkens()
    {
        int w = 6, h = 6;
        byte[] Mid() { var p = new byte[w * h * 4]; for (int i = 0; i < p.Length; i += 4) { p[i] = p[i + 1] = p[i + 2] = 120; p[i + 3] = 255; } return p; }
        int c = (3 * w + 3) * 4;

        var d = Mid();
        new BrushTool { Radius = 3, Hardness = 1f, Mode = BrushMode.Dodge, Strength = 0.5f }.Stamp(d, w, h, 3, 3);
        Assert.True(d[c] > 120);   // lightened

        var b = Mid();
        new BrushTool { Radius = 3, Hardness = 1f, Mode = BrushMode.Burn, Strength = 0.5f }.Stamp(b, w, h, 3, 3);
        Assert.True(b[c] < 120);   // darkened
    }

    [Fact]
    public void Sponge_Desaturates_TowardLuminance()
    {
        int w = 6, h = 6;
        var p = new byte[w * h * 4];
        for (int i = 0; i < p.Length; i += 4) { p[i] = 200; p[i + 1] = 50; p[i + 2] = 50; p[i + 3] = 255; }
        int c = (3 * w + 3) * 4;
        int beforeSpread = p[c] - p[c + 1];
        new BrushTool { Radius = 3, Hardness = 1f, Mode = BrushMode.Sponge, Strength = 0.8f }.Stamp(p, w, h, 3, 3);
        Assert.True(p[c] - p[c + 1] < beforeSpread);   // channels pulled together (less saturated)
    }

    [Fact]
    public void Clone_CopiesFromSourceAtOffset()
    {
        int w = 12, h = 12;
        var src = new byte[w * h * 4];
        int s = (3 * w + 3) * 4;
        src[s] = 200; src[s + 1] = 50; src[s + 2] = 0; src[s + 3] = 255;   // orange at (3,3)
        var dst = new byte[w * h * 4];

        var b = new BrushTool
        {
            Radius = 1, Hardness = 1f, Flow = 1f,
            Clone = true, CloneSrc = src, CloneSrcW = w, CloneSrcH = h,
            CloneOffX = 5, CloneOffY = 5   // dest - 5 = source
        };
        b.Stamp(dst, w, h, 8, 8);          // (8,8) → samples src (3,3) = orange

        int d = (8 * w + 8) * 4;
        Assert.True(dst[d + 3] > 0);       // painted
        Assert.Equal(200, dst[d]);         // copied source colour
        Assert.Equal(50, dst[d + 1]);
    }

    [Fact]
    public void BrushClipMask_RestrictsPaintToMaskedPixels()
    {
        int w = 16, h = 16;
        var px = new byte[w * h * 4];
        var mask = new byte[w * h];
        mask[5 * w + 5] = 255;   // only this pixel paintable

        var brush = new BrushTool { Radius = 8, Hardness = 1f, R = 255, G = 0, B = 0, Flow = 1f, Erase = false,
            ClipMask = mask, ClipMaskW = w };
        brush.Stamp(px, w, h, 5, 5);

        Assert.True(px[(5 * w + 5) * 4 + 3] > 0);   // masked pixel painted
        Assert.Equal(0, px[(5 * w + 6) * 4 + 3]);   // neighbour outside mask untouched
    }

    [Fact]
    public void FillFlood_WithMask_FillsOnlyInsideMask()
    {
        int w = 10, h = 10;
        var px = new byte[w * h * 4];   // uniform transparent black → one flood region
        var mask = new byte[w * h];
        // mask = left 5 columns
        for (int y = 0; y < h; y++)
        for (int x = 0; x < 5; x++) mask[y * w + x] = 255;

        int changed = FillTool.Flood(px, w, h, 0, 0, 9, 9, 9, 255, 0, null, mask, w);
        Assert.Equal(50, changed);                       // only the 5×10 masked half
        Assert.Equal(255, px[(0 * w + 0) * 4 + 3]);
        Assert.Equal(0, px[(0 * w + 7) * 4 + 3]);        // outside mask untouched
    }
}
