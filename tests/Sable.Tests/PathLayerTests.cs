using Sable.Engine.Layers;
using Xunit;

namespace Sable.Tests;

public class PathLayerTests
{
    private static byte AlphaAt(byte[] buf, int w, int x, int y) => buf[(y * w + x) * 4 + 3];

    [Fact]
    public void Flatten_CornerSquare_HasExpectedVertexCount()
    {
        var p = new PathLayer
        {
            Closed = true,
            Nodes =
            {
                new PathNode(0, 0), new PathNode(10, 0), new PathNode(10, 10), new PathNode(0, 10),
            }
        };
        var poly = p.Flatten(8);
        // 4 nodes, closed → 4 segments × 8 steps + the initial moveto point
        Assert.Equal(4 * 8 + 1, poly.Count);
        Assert.Equal((0.0, 0.0), poly[0]);
    }

    [Fact]
    public void Rasterize_FilledClosedRect_FillsInterior_NotOutside()
    {
        int dw = 40, dh = 40;
        var p = new PathLayer
        {
            Closed = true, Filled = true, FillA = 255, FillR = 200,
            Nodes =
            {
                new PathNode(10, 10), new PathNode(30, 10), new PathNode(30, 30), new PathNode(10, 30),
            }
        };
        var buf = new byte[dw * dh * 4];
        p.Rasterize(buf, dw, dh);

        Assert.True(AlphaAt(buf, dw, 20, 20) > 200);   // centre opaque
        Assert.Equal(0, AlphaAt(buf, dw, 2, 2));       // outside transparent
        Assert.Equal(200, buf[(20 * dw + 20) * 4]);    // fill colour R
    }

    [Fact]
    public void Rasterize_OpenPath_NoFill_EvenIfFilledTrue()
    {
        int dw = 40, dh = 40;
        var p = new PathLayer
        {
            Closed = false, Filled = true, FillA = 255,
            Nodes = { new PathNode(10, 10), new PathNode(30, 10), new PathNode(30, 30) }
        };
        var buf = new byte[dw * dh * 4];
        p.Rasterize(buf, dw, dh);
        // open path with no stroke → nothing drawn
        Assert.Equal(0, AlphaAt(buf, dw, 20, 20));
    }

    [Fact]
    public void Rasterize_StrokedLine_DrawsAlongPath()
    {
        int dw = 40, dh = 40;
        var p = new PathLayer
        {
            Closed = false, Filled = false, Stroked = true, StrokeWidth = 4, StrokeA = 255,
            Nodes = { new PathNode(5, 20), new PathNode(35, 20) }
        };
        var buf = new byte[dw * dh * 4];
        p.Rasterize(buf, dw, dh);
        Assert.True(AlphaAt(buf, dw, 20, 20) > 200);   // on the line
        Assert.Equal(0, AlphaAt(buf, dw, 20, 5));      // far from the line
    }

    [Fact]
    public void ContentBounds_TightAroundNodes()
    {
        var p = new PathLayer
        {
            Nodes = { new PathNode(10, 12), new PathNode(30, 40) }
        };
        var (x, y, w, h) = p.ContentBounds(100, 100);
        Assert.True(x <= 10 && y <= 12);
        Assert.True(x + w >= 30 && y + h >= 40);
    }

    [Fact]
    public void EditPathCommand_AppliesAndUndoes()
    {
        var doc = new Sable.Engine.Document(32, 32);
        var p = new PathLayer { Closed = true, Nodes = { new PathNode(0, 0), new PathNode(10, 0), new PathNode(10, 10) } };
        doc.Layers.Add(p);
        var before = new System.Collections.Generic.List<PathNode>(p.Nodes);
        var after = new System.Collections.Generic.List<PathNode>(p.Nodes) { new PathNode(0, 10) };
        var cmd = new Sable.Engine.Commands.EditPathCommand(doc, p, before, true, after, true);

        cmd.Do();
        Assert.Equal(4, p.Nodes.Count);
        cmd.Undo();
        Assert.Equal(3, p.Nodes.Count);
        cmd.Do();
        Assert.Equal(4, p.Nodes.Count);
        Assert.Equal(0, p.Nodes[3].Ax);
        Assert.Equal(10, p.Nodes[3].Ay);
    }

    [Fact]
    public void MultiContour_EvenOdd_LeavesHole()
    {
        int dw = 60, dh = 60;
        // outer square 10..50 with an inner square 22..38 as an extra contour → donut (hole in centre)
        var inner = new System.Collections.Generic.List<PathNode>
        { new(22, 22), new(38, 22), new(38, 38), new(22, 38) };
        var p = new PathLayer
        {
            Closed = true, Filled = true, FillA = 255, FillR = 180,
            Nodes = { new PathNode(10, 10), new PathNode(50, 10), new PathNode(50, 50), new PathNode(10, 50) },
            ExtraContours = { (inner, true) },
        };
        var buf = new byte[dw * dh * 4];
        p.Rasterize(buf, dw, dh);
        Assert.True(AlphaAt(buf, dw, 14, 30) > 200);   // ring (between squares) filled
        Assert.Equal(0, AlphaAt(buf, dw, 30, 30));     // centre = hole (even-odd)
    }

    [Fact]
    public void MiterJoin_FillsOuterCorner_BevelDoesNot()
    {
        int dw = 40, dh = 40;
        // an L: down then right, sharp right-angle at (10,10); thick stroke
        var nodes = new System.Collections.Generic.List<PathNode>
        { new(10, 30), new(10, 10), new(30, 10) };
        PathLayer Make(Sable.Engine.Layers.LineJoin j) => new()
        {
            Closed = false, Filled = false, Stroked = true, StrokeWidth = 8, StrokeA = 255,
            Join = j, Nodes = new System.Collections.Generic.List<PathNode>(nodes),
        };
        var miter = new byte[dw * dh * 4]; Make(Sable.Engine.Layers.LineJoin.Miter).Rasterize(miter, dw, dh);
        var bevel = new byte[dw * dh * 4]; Make(Sable.Engine.Layers.LineJoin.Bevel).Rasterize(bevel, dw, dh);
        // outer corner pixel (~7,7) is inside the miter spike but cut off by a bevel
        Assert.True(AlphaAt(miter, dw, 7, 7) > 128);
        Assert.Equal(0, AlphaAt(bevel, dw, 7, 7));
    }

    [Fact]
    public void Clone_CopiesExtraContours()
    {
        var p = new PathLayer { Nodes = { new PathNode(0, 0), new PathNode(5, 0) } };
        p.ExtraContours.Add((new System.Collections.Generic.List<PathNode> { new(1, 1), new(2, 2) }, true));
        var c = (PathLayer)p.Clone();
        Assert.Single(c.ExtraContours);
        c.ExtraContours[0].Nodes.Add(new PathNode(9, 9));
        Assert.Equal(2, p.ExtraContours[0].Nodes.Count);   // independent
    }

    [Fact]
    public void Clone_DeepCopiesNodesAndStyle()
    {
        var p = new PathLayer
        {
            Closed = true, Stroked = true, StrokeWidth = 7, FillR = 5,
            Nodes = { new PathNode(1, 2), new PathNode(3, 4) }
        };
        var c = (PathLayer)p.Clone();
        Assert.Equal(2, c.Nodes.Count);
        Assert.True(c.Closed);
        Assert.Equal(7, c.StrokeWidth);
        Assert.Equal(5, c.FillR);
        c.Nodes.Add(new PathNode(9, 9));
        Assert.Equal(2, p.Nodes.Count);   // independent list
    }
}
