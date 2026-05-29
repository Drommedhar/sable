using Sable.Engine;
using Sable.Tools;
using Xunit;

namespace Sable.Tests;

public class SelectionTests
{
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
