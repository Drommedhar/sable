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
    public void Resize_ScalesDocument_UndoRestores()
    {
        var doc = new Document(4, 4);
        var px = new PixelLayer(4, 4, "L");
        for (int k = 0; k < px.Pixels.Length; k += 4) { px.Pixels[k] = 255; px.Pixels[k + 3] = 255; }  // solid red
        doc.Layers.Add(px);

        var cmd = new ResizeCommand(doc, 8, 8, 144, bilinear: true);
        cmd.Do();
        Assert.Equal(8, doc.Width);
        Assert.Equal(8, px.Width);
        Assert.Equal(8 * 8 * 4, px.Pixels.Length);
        Assert.Equal(144, doc.Dpi);
        Assert.Equal(255, px.Pixels[(4 * 8 + 4) * 4]);     // still red after upscale
        Assert.Equal(255, px.Pixels[(4 * 8 + 4) * 4 + 3]);

        cmd.Undo();
        Assert.Equal(4, doc.Width);
        Assert.Equal(96, doc.Dpi);
        Assert.Equal(4 * 4 * 4, px.Pixels.Length);
    }

    [Fact]
    public void TextLayer_Rasterizes_NonEmpty()
    {
        var t = new TextLayer("Hi", 5, 5, 32, 255, 255, 255);
        var buf = new byte[200 * 60 * 4];
        t.Rasterize(buf, 200, 60);
        int painted = 0;
        for (int i = 3; i < buf.Length; i += 4) if (buf[i] > 0) painted++;
        Assert.True(painted > 0);                              // glyphs rendered something
        var (bx, by, _, _) = t.ContentBounds(200, 60);
        Assert.Equal((5, 5), (bx, by));                        // bounds at the text position
    }

    [Fact]
    public void ShapeLayer_Rasterizes_AndHasTightBounds()
    {
        var sh = new ShapeLayer(ShapeKind.Rectangle, 10, 10, 20, 30, 255, 0, 0);
        var (bx, by, bw, bh) = sh.ContentBounds(100, 100);
        Assert.Equal((10, 10, 20, 30), (bx, by, bw, bh));   // tight bounds = the rect, not the doc

        var buf = new byte[100 * 100 * 4];
        sh.Rasterize(buf, 100, 100);
        Assert.Equal(255, buf[(20 * 100 + 20) * 4]);        // inside rect → red
        Assert.Equal(255, buf[(20 * 100 + 20) * 4 + 3]);    // opaque
        Assert.Equal(0, buf[(0 * 100 + 0) * 4 + 3]);        // outside rect → transparent

        // recolour after the fact (editable fill)
        sh.R = 0; sh.B = 255; sh.Rasterize(buf, 100, 100);
        Assert.Equal(255, buf[(20 * 100 + 20) * 4 + 2]);    // now blue
    }

    [Fact]
    public void Crop_NegativeOrigin_GrowsCanvasWithTransparentPad()
    {
        // canvas resize (grow) is CropCommand with a negative origin — pads transparent, no scaling
        var doc = new Document(4, 4);
        var px = new PixelLayer(4, 4, "L");
        int i0 = (0 * 4 + 0) * 4;
        px.Pixels[i0] = 255; px.Pixels[i0 + 3] = 255;   // red at (0,0)
        doc.Layers.Add(px);

        new CropCommand(doc, -2, -2, 8, 8).Do();         // centre 4×4 inside 8×8
        Assert.Equal(8, doc.Width);
        Assert.Equal(8, px.Width);
        Assert.Equal(255, px.Pixels[(2 * 8 + 2) * 4]);   // old (0,0) → new (2,2), still red, NOT scaled
        Assert.Equal(0, px.Pixels[(0 * 8 + 0) * 4 + 3]); // padded corner is transparent
    }

    [Fact]
    public void Crop_ResizesAndMovesContent_UndoRestores()
    {
        var doc = new Document(8, 8);
        var px = new PixelLayer(8, 8, "L");
        // mark pixel (5,5) red so we can track where it lands after crop
        int i = (5 * 8 + 5) * 4;
        px.Pixels[i] = 255; px.Pixels[i + 3] = 255;
        doc.Layers.Add(px);

        var cmd = new CropCommand(doc, 4, 4, 4, 4);   // crop to bottom-right quadrant
        cmd.Do();
        Assert.Equal(4, doc.Width);
        Assert.Equal(4, doc.Height);
        Assert.Equal(4, px.Width);
        // old (5,5) → new (1,1)
        int ni = (1 * 4 + 1) * 4;
        Assert.Equal(255, px.Pixels[ni]);
        Assert.Equal(255, px.Pixels[ni + 3]);

        cmd.Undo();
        Assert.Equal(8, doc.Width);
        Assert.Equal(8, px.Width);
        Assert.Equal(255, px.Pixels[(5 * 8 + 5) * 4]);   // original restored
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
