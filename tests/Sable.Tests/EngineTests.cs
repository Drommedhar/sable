using Sable.Core;
using Sable.Core.Undo;
using Sable.Engine;
using Sable.Engine.Commands;
using Sable.Engine.Layers;
using Xunit;

namespace Sable.Tests;

public class DocumentTests
{
    [Fact]
    public void CreateDemo_HasExpectedLayers()
    {
        var doc = Document.CreateDemo(128, 96);
        Assert.Equal(128, doc.Width);
        Assert.Equal(96, doc.Height);
        Assert.Equal(4, doc.Layers.Count);                  // bg, disc, highlight, adjustment
        Assert.IsType<AdjustmentLayer>(doc.Layers[^1]);     // adjustment on top
        Assert.True(doc.Layers[1].HasMask);                 // disc is masked
    }

    [Fact]
    public void NeedsComposite_TrueOnParamChange_ClearedAfter()
    {
        var doc = Document.CreateDemo(32, 32);
        doc.ClearDirty();
        Assert.False(doc.NeedsComposite);
        doc.Layers[0].Dirty = true;
        Assert.True(doc.NeedsComposite);
        doc.ClearDirty();
        Assert.False(doc.NeedsComposite);
    }

    [Fact]
    public void MarkStructureChanged_ForcesRecomposite()
    {
        var doc = Document.CreateDemo(32, 32);
        doc.ClearDirty();
        doc.MarkStructureChanged();
        Assert.True(doc.NeedsComposite);
    }
}

public class LayerCommandTests
{
    private static string Order(Document d) => string.Join(",", d.Layers.ConvertAll(l => l.Name));

    [Fact]
    public void Add_Move_Undo_Redo_PreserveOrder()
    {
        var doc = new Document(16, 16);
        doc.Layers.Add(new PixelLayer(16, 16, "A"));
        doc.Layers.Add(new PixelLayer(16, 16, "B"));
        var stack = new UndoStack();

        var c = new PixelLayer(16, 16, "C");
        stack.Execute(new AddLayerCommand(doc, doc.Layers, c, doc.Layers.Count));
        Assert.Equal("A,B,C", Order(doc));

        stack.Execute(new MoveLayerCommand(doc, doc.Layers[0], +2));   // A up by 2 -> top
        Assert.Equal("B,C,A", Order(doc));

        stack.Undo();
        Assert.Equal("A,B,C", Order(doc));
        stack.Undo();
        Assert.Equal("A,B", Order(doc));
        stack.Redo();
        Assert.Equal("A,B,C", Order(doc));
    }

    [Fact]
    public void Remove_Undo_RestoresAtOriginalIndex()
    {
        var doc = new Document(16, 16);
        doc.Layers.Add(new PixelLayer(16, 16, "A"));
        var mid = new PixelLayer(16, 16, "B");
        doc.Layers.Add(mid);
        doc.Layers.Add(new PixelLayer(16, 16, "C"));
        var stack = new UndoStack();

        stack.Execute(new RemoveLayerCommand(doc, mid));
        Assert.Equal("A,C", Order(doc));
        stack.Undo();
        Assert.Equal("A,B,C", Order(doc));                  // back in the middle
    }

    [Fact]
    public void Group_WrapsLayer_FindParent_Undo()
    {
        var doc = new Document(16, 16);
        var a = new PixelLayer(16, 16, "A");
        doc.Layers.Add(a);
        doc.Layers.Add(new PixelLayer(16, 16, "B"));
        var stack = new UndoStack();

        var cmd = new GroupLayersCommand(doc, new[] { a });
        stack.Execute(cmd);

        Assert.Equal(2, doc.Layers.Count);                  // group + B at top level
        Assert.Contains(cmd.Group, doc.Layers);
        Assert.Contains(a, cmd.Group.Children);
        Assert.Equal(cmd.Group.Children, doc.FindParent(a)); // a now lives in the group

        stack.Undo();
        Assert.Contains(a, doc.Layers);                     // back at top level
        Assert.DoesNotContain(cmd.Group, doc.Layers);
    }

    [Fact]
    public void GroupMultiple_PreservesOrder_Undo()
    {
        var doc = new Document(16, 16);
        var a = new PixelLayer(16, 16, "A");
        var b = new PixelLayer(16, 16, "B");
        var c = new PixelLayer(16, 16, "C");
        doc.Layers.Add(a); doc.Layers.Add(b); doc.Layers.Add(c);
        var stack = new UndoStack();

        stack.Execute(new GroupLayersCommand(doc, new Layer[] { c, a }));   // unordered input
        // group sits where the lowest (A) was; B remains; group holds A,C in tree order
        Assert.Equal(2, doc.Layers.Count);
        var g = Assert.IsType<GroupLayer>(doc.Layers[0]);
        Assert.Equal(new Layer[] { a, c }, g.Children);     // bottom→top order preserved
        Assert.Same(b, doc.Layers[1]);

        stack.Undo();
        Assert.Equal(new Layer[] { a, b, c }, doc.Layers);
    }

    [Fact]
    public void MoveOffset_SetsAndUndoes()
    {
        var doc = new Document(16, 16);
        var a = new PixelLayer(16, 16, "A");
        doc.Layers.Add(a);
        var stack = new UndoStack();

        stack.Execute(new MoveOffsetCommand(doc, a, 0, 0, 5, -3));
        Assert.Equal(5, a.OffsetX);
        Assert.Equal(-3, a.OffsetY);

        stack.Undo();
        Assert.Equal(0, a.OffsetX);
        Assert.Equal(0, a.OffsetY);
    }

    [Fact]
    public void Transform_SetsAndUndoes()
    {
        var doc = new Document(16, 16);
        var a = new PixelLayer(16, 16, "A");
        doc.Layers.Add(a);
        var stack = new UndoStack();

        var old = LayerXform.From(a);
        stack.Execute(new TransformLayerCommand(doc, a, old, new LayerXform(5, 6, 1.5f, 1.5f, 30f)));
        Assert.Equal(5, a.OffsetX);
        Assert.Equal(1.5f, a.ScaleX, 4);
        Assert.Equal(30f, a.Rotation, 4);

        stack.Undo();
        Assert.Equal(0, a.OffsetX);
        Assert.Equal(1f, a.ScaleX, 4);
        Assert.Equal(0f, a.Rotation, 4);
    }

    [Fact]
    public void MoveLayerTo_IntoGroup_Undo()
    {
        var doc = new Document(16, 16);
        var a = new PixelLayer(16, 16, "A");
        var g = new GroupLayer("G");
        doc.Layers.Add(a);
        doc.Layers.Add(g);
        var stack = new UndoStack();

        stack.Execute(new MoveLayerToCommand(doc, a, g.Children, 0));
        Assert.DoesNotContain(a, doc.Layers);
        Assert.Contains(a, g.Children);

        stack.Undo();
        Assert.Contains(a, doc.Layers);                     // back at top level
        Assert.Empty(g.Children);
    }

    [Fact]
    public void Ungroup_SplicesChildren_Undo()
    {
        var doc = new Document(16, 16);
        var g = new GroupLayer("G");
        g.Children.Add(new PixelLayer(16, 16, "X"));
        g.Children.Add(new PixelLayer(16, 16, "Y"));
        doc.Layers.Add(new PixelLayer(16, 16, "A"));
        doc.Layers.Add(g);
        var stack = new UndoStack();

        stack.Execute(new UngroupCommand(doc, g));
        Assert.Equal("A,X,Y", Order(doc));                  // children spliced where the group was
        Assert.DoesNotContain(g, doc.Layers);

        stack.Undo();
        Assert.Contains(g, doc.Layers);
        Assert.Equal(2, g.Children.Count);
    }
}

public class AdjustmentLayerTests
{
    [Fact]
    public void PackParams_BrightnessContrast()
    {
        var a = new AdjustmentLayer(AdjustmentKind.BrightnessContrast) { Brightness = 0.2f, Contrast = 1.5f };
        Span<float> p = stackalloc float[6];
        a.PackParams(p);
        Assert.Equal(0.2f, p[0], 4);
        Assert.Equal(1.5f, p[1], 4);
    }

    [Fact]
    public void PackParams_Levels()
    {
        var a = new AdjustmentLayer(AdjustmentKind.Levels) { InBlack = 0.1f, InWhite = 0.9f, Gamma = 2f };
        Span<float> p = stackalloc float[6];
        a.PackParams(p);
        Assert.Equal(0.1f, p[0], 4);
        Assert.Equal(0.9f, p[1], 4);
        Assert.Equal(2f, p[2], 4);
    }

    [Fact]
    public void PackParams_Hsl()
    {
        var a = new AdjustmentLayer(AdjustmentKind.Hsl) { HueShift = 0.25f, Saturation = 1.5f, Lightness = -0.2f };
        Span<float> p = stackalloc float[6];
        a.PackParams(p);
        Assert.Equal(0.25f, p[0], 4);
        Assert.Equal(1.5f, p[1], 4);
        Assert.Equal(-0.2f, p[2], 4);
    }
}

public class SelRectTests
{
    [Fact]
    public void FromCorners_NormalizesAndClamps()
    {
        var s = SelRect.FromCorners(50, 40, 10, 20, 100, 80);
        Assert.Equal(10, s.X); Assert.Equal(20, s.Y);
        Assert.Equal(40, s.W); Assert.Equal(20, s.H);

        var clamped = SelRect.FromCorners(-5, -5, 200, 200, 100, 80);
        Assert.Equal(0, clamped.X); Assert.Equal(0, clamped.Y);
        Assert.Equal(100, clamped.W); Assert.Equal(80, clamped.H);
    }

    [Fact]
    public void Contains_Works()
    {
        var s = new SelRect(10, 10, 5, 5);
        Assert.True(s.Contains(12, 12));
        Assert.False(s.Contains(15, 15));   // exclusive right/bottom
    }
}

public class AffineMathTests
{
    [Fact]
    public void Identity_IsIdentity()
    {
        var m = AffineMath.DocToLayer(100, 80, 0, 0, 1, 1, 0);
        Assert.Equal(1f, m[0], 4); Assert.Equal(0f, m[1], 4);
        Assert.Equal(0f, m[2], 4); Assert.Equal(1f, m[3], 4);
        Assert.Equal(0f, m[4], 4); Assert.Equal(0f, m[5], 4);
    }

    [Fact]
    public void Translate_InvertsOffset()
    {
        // L = D - T  → doc point (50,40) maps to layer (40,30) when T=(10,10)
        var m = AffineMath.DocToLayer(100, 80, 10, 10, 1, 1, 0);
        float lx = m[0] * 50 + m[1] * 40 + m[4];
        float ly = m[2] * 50 + m[3] * 40 + m[5];
        Assert.Equal(40f, lx, 3);
        Assert.Equal(30f, ly, 3);
    }

    [Fact]
    public void Scale2x_AboutCentre_MapsCornerInward()
    {
        // 2× scale about centre (50,40): doc centre maps to layer centre; doc corner maps closer to centre in layer space
        var m = AffineMath.DocToLayer(100, 80, 0, 0, 2, 2, 0);
        float lx = m[0] * 50 + m[1] * 40 + m[4];   // doc centre
        float ly = m[2] * 50 + m[3] * 40 + m[5];
        Assert.Equal(50f, lx, 3);
        Assert.Equal(40f, ly, 3);
        // doc (100,80) → layer (75,60) (half the displacement from centre)
        float cx = m[0] * 100 + m[1] * 80 + m[4];
        Assert.Equal(75f, cx, 3);
    }
}

public class RasterTilesTests
{
    [Fact]
    public void GetSetTile_RoundTrips_IncludingEdgeTiles()
    {
        // non-multiple-of-256 dimensions exercise partial edge tiles
        int w = 300, h = 200;
        var src = new byte[w * h * 4];
        for (int i = 0; i < src.Length; i++) src[i] = (byte)(i * 7 % 251);

        var dst = new byte[w * h * 4];
        for (int ty = 0; ty < RasterTiles.TilesY(h); ty++)
        for (int tx = 0; tx < RasterTiles.TilesX(w); tx++)
            RasterTiles.SetTile(dst, w, h, tx, ty, RasterTiles.GetTile(src, w, h, tx, ty));

        Assert.Equal(src, dst);
    }

    [Fact]
    public void EdgeTile_HasClippedSize()
    {
        int w = 300; // tiles: 256 + 44
        Assert.Equal(2, RasterTiles.TilesX(w));
        Assert.Equal(256, RasterTiles.TileWidth(w, 0));
        Assert.Equal(44, RasterTiles.TileWidth(w, 1));
    }
}
