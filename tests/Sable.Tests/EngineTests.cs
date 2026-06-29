using Sable.Core;
using Sable.Core.Undo;
using Sable.Engine;
using Sable.Engine.Commands;
using Sable.Engine.Layers;
using Sable.Tools;
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
    public void Crop_OffsetSubDocLayer_LandsContentCorrectly()   // audit: crop honors layer offset/dims
    {
        var doc = new Document(8, 8);
        var px = new PixelLayer(4, 4, "L") { OffsetX = 4, OffsetY = 4 };   // covers doc [4,8)x[4,8)
        px.Pixels[0] = 255; px.Pixels[3] = 255;                            // layer-local (0,0) = doc (4,4) red
        doc.Layers.Add(px);

        new CropCommand(doc, 4, 4, 4, 4).Do();   // crop to the bottom-right quadrant
        Assert.Equal(4, doc.Width);
        Assert.Equal(4, px.Width);
        Assert.Equal(0, px.OffsetX);             // re-origined to the new doc
        Assert.Equal(255, px.Pixels[0]);         // doc (4,4) → new (0,0) still red
        Assert.Equal(255, px.Pixels[3]);
        Assert.Equal(0, px.Pixels[(3 * 4 + 3) * 4 + 3]);   // area the layer never covered → transparent
    }

    [Fact]
    public void Resize_OffsetSubDocLayer_ScalesLayerNotDoc()   // audit: resize scales each layer to its own size
    {
        var doc = new Document(8, 8);
        doc.Layers.Add(new PixelLayer(4, 4, "L") { OffsetX = 2, OffsetY = 2 });
        var px = (PixelLayer)doc.Layers[0];

        new ResizeCommand(doc, 16, 16, 96, bilinear: false).Do();   // 2x document
        Assert.Equal(16, doc.Width);
        Assert.Equal(8, px.Width);     // 4*2 — scaled to the layer's own new size, NOT the doc's 16
        Assert.Equal(8, px.Height);
        Assert.Equal(4, px.OffsetX);   // offset scaled proportionally (2*2)
        Assert.Equal(4, px.OffsetY);
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
        stack.Execute(new TransformLayerCommand(doc, a, old, new LayerXform(5, 6, 1.5f, 1.5f, 30f, 0, 0)));
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

public class ReplaceLayersCommandTests
{
    [Fact]
    public void Replace_CollapsesAndUndoRestores()
    {
        var doc = new Document(8, 8);
        var a = new PixelLayer(8, 8, "a");
        var b = new PixelLayer(8, 8, "b");
        var c = new PixelLayer(8, 8, "c");
        doc.Layers.Add(a); doc.Layers.Add(b); doc.Layers.Add(c);   // [a,b,c]
        var merged = new PixelLayer(8, 8, "merged");
        var stack = new UndoStack();

        stack.Execute(new ReplaceLayersCommand(doc, doc.Layers, new[] { a, b }, 0, merged, "Merge Down"));
        Assert.Equal(2, doc.Layers.Count);
        Assert.Same(merged, doc.Layers[0]);
        Assert.Same(c, doc.Layers[1]);

        stack.Undo();
        Assert.Equal(3, doc.Layers.Count);
        Assert.Same(a, doc.Layers[0]);
        Assert.Same(b, doc.Layers[1]);
        Assert.Same(c, doc.Layers[2]);

        stack.Redo();
        Assert.Equal(2, doc.Layers.Count);
        Assert.Same(merged, doc.Layers[0]);
    }
}

public class LayerCloneTests
{
    [Fact]
    public void Clone_PixelLayer_DeepCopiesPixelsMaskEffects()
    {
        var p = new PixelLayer(8, 8, "A") { Opacity = 0.5f, BlendMode = BlendMode.Multiply, OffsetX = 3 };
        p.Pixels[0] = 200;
        p.AddWhiteMask(8, 8);
        p.Effects.Add(new LayerEffect { Kind = LayerEffectKind.DropShadow, Radius = 9 });

        var c = (PixelLayer)p.Clone();
        Assert.NotSame(p.Pixels, c.Pixels);
        Assert.Equal(200, c.Pixels[0]);
        Assert.Equal(0.5f, c.Opacity, 4);
        Assert.Equal(BlendMode.Multiply, c.BlendMode);
        Assert.Equal(3, c.OffsetX);
        Assert.NotSame(p.Mask, c.Mask);
        Assert.Single(c.Effects);
        Assert.NotSame(p.Effects[0], c.Effects[0]);
        Assert.Equal(9f, c.Effects[0].Radius, 4);

        c.Pixels[0] = 1;                 // mutating the clone must not touch the original
        Assert.Equal(200, p.Pixels[0]);
    }

    [Fact]
    public void Clone_Adjustment_CopiesParamsAndCurves()
    {
        var a = new AdjustmentLayer(AdjustmentKind.Curves) { Brightness = 0.3f };
        a.Curves[0].Insert(1, (0.4f, 0.7f));
        var c = (AdjustmentLayer)a.Clone();
        Assert.Equal(AdjustmentKind.Curves, c.Kind);
        Assert.Equal(0.3f, c.Brightness, 4);
        Assert.Equal(3, c.Curves[0].Count);
        Assert.NotSame(a.Curves[0], c.Curves[0]);
    }

    [Fact]
    public void Clone_Group_DeepCopiesChildren()
    {
        var g = new GroupLayer("G");
        g.Children.Add(new PixelLayer(4, 4, "child"));
        var c = (GroupLayer)g.Clone();
        Assert.Single(c.Children);
        Assert.NotSame(g.Children[0], c.Children[0]);
        Assert.IsType<PixelLayer>(c.Children[0]);
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
        var a = new AdjustmentLayer(AdjustmentKind.Levels)
        { InBlack = 0.1f, InWhite = 0.9f, Gamma = 2f, OutBlack = 0.05f, OutWhite = 0.8f };
        Span<float> p = stackalloc float[6];
        a.PackParams(p);
        Assert.Equal(0.1f, p[0], 4);
        Assert.Equal(0.9f, p[1], 4);
        Assert.Equal(2f, p[2], 4);
        Assert.Equal(0.05f, p[3], 4);
        Assert.Equal(0.8f, p[4], 4);
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

    [Theory]
    [InlineData(AdjustmentKind.Exposure)]
    [InlineData(AdjustmentKind.Vibrance)]
    [InlineData(AdjustmentKind.Threshold)]
    [InlineData(AdjustmentKind.Posterize)]
    public void PackParams_SingleParam(AdjustmentKind kind)
    {
        var a = new AdjustmentLayer(kind);
        a.Exposure = 1.5f; a.Vibrance = 0.4f; a.Threshold = 0.6f; a.Posterize = 5f;
        Span<float> p = stackalloc float[6];
        a.PackParams(p);
        float expected = kind switch
        {
            AdjustmentKind.Exposure => 1.5f,
            AdjustmentKind.Vibrance => 0.4f,
            AdjustmentKind.Threshold => 0.6f,
            _ => 5f,
        };
        Assert.Equal(expected, p[0], 4);
    }

    [Fact]
    public void PackParams_ColorBalance_NineValues()
    {
        var a = new AdjustmentLayer(AdjustmentKind.ColorBalance);
        for (int i = 0; i < 9; i++) a.ColorBalance[i] = (i + 1) * 0.1f;
        Span<float> p = stackalloc float[12];
        a.PackParams(p);
        for (int i = 0; i < 9; i++) Assert.Equal((i + 1) * 0.1f, p[i], 4);
    }

    [Fact]
    public void PackParams_ChannelMixer_DefaultIdentity()
    {
        var a = new AdjustmentLayer(AdjustmentKind.ChannelMixer);
        Span<float> p = stackalloc float[12];
        a.PackParams(p);
        // identity 3x3 row-major
        Assert.Equal(1f, p[0], 4); Assert.Equal(1f, p[4], 4); Assert.Equal(1f, p[8], 4);
        Assert.Equal(0f, p[1], 4); Assert.Equal(0f, p[5], 4);
    }

    [Fact]
    public void Curves_DefaultLutIsIdentity()
    {
        var a = new AdjustmentLayer(AdjustmentKind.Curves);
        Assert.True(a.CurvesAreIdentity());
        Span<float> lut = stackalloc float[AdjustmentLayer.CurveChannels * AdjustmentLayer.LutSize];
        a.BuildLut(lut);
        for (int ch = 0; ch < AdjustmentLayer.CurveChannels; ch++)
        {
            int b = ch * AdjustmentLayer.LutSize;
            Assert.Equal(0f, lut[b], 3);
            Assert.Equal(1f, lut[b + AdjustmentLayer.LutSize - 1], 3);
            Assert.Equal(0.5f, lut[b + 127], 2);   // midpoint passes through
        }
    }

    [Fact]
    public void Curves_RaisedMidpointLiftsLut()
    {
        var a = new AdjustmentLayer(AdjustmentKind.Curves);
        a.Curves[0].Insert(1, (0.5f, 0.75f));      // lift the composite midtone
        Assert.False(a.CurvesAreIdentity());
        Assert.True(a.EvalChannel(0, 0.5f) > 0.7f);
        Assert.Equal(0f, a.EvalChannel(0, 0f), 3);
        Assert.Equal(1f, a.EvalChannel(0, 1f), 3);
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

    [Fact]
    public void Shear_RoundTripsThroughInverse()
    {
        // forward then inverse should return the original layer point (shear included)
        int w = 100, h = 80;
        float shx = 0.4f, shy = 0.2f;
        var (dx, dy) = AffineMath.LayerToDoc(w, h, 7, -3, 1.2f, 0.9f, 15f, 30f, 25f, shx, shy);
        var m = AffineMath.DocToLayer(w, h, 7, -3, 1.2f, 0.9f, 15f, shx, shy);
        float lx = m[0] * dx + m[1] * dy + m[4];
        float ly = m[2] * dx + m[3] * dy + m[5];
        Assert.Equal(30f, lx, 2);
        Assert.Equal(25f, ly, 2);
    }

    [Fact]
    public void Homography_MapsDraggedCornersToLayerRect()
    {
        int w = 100, h = 80;
        // a non-affine quad (true perspective): corners pulled in on one side
        var corners = new float[] { 10, 10, 200, 30, 180, 150, 30, 120 };   // TL,TR,BR,BL doc
        var (inv6, persp) = Homography.DocToLayerQuad(w, h, corners);

        (float lx, float ly) Map(float dx, float dy)
        {
            float wgt = persp[0] * dx + persp[1] * dy + persp[2];
            float lx = (inv6[0] * dx + inv6[1] * dy + inv6[4]) / wgt;   // [m00,m01,_,_,b0,_]
            float ly = (inv6[2] * dx + inv6[3] * dy + inv6[5]) / wgt;
            return (lx, ly);
        }
        // each doc corner must map back to the corresponding layer-rect corner
        var tl = Map(10, 10); Assert.Equal(0f, tl.lx, 1); Assert.Equal(0f, tl.ly, 1);
        var tr = Map(200, 30); Assert.Equal(w, tr.lx, 1); Assert.Equal(0f, tr.ly, 1);
        var br = Map(180, 150); Assert.Equal(w, br.lx, 1); Assert.Equal(h, br.ly, 1);
        var bl = Map(30, 120); Assert.Equal(0f, bl.lx, 1); Assert.Equal(h, bl.ly, 1);
    }

    [Fact]
    public void ShearX_SlantsHorizontally()
    {
        // pure +X shear: a point below centre shifts right in doc space
        int w = 100, h = 80;
        var (cxDoc, _) = AffineMath.LayerToDoc(w, h, 0, 0, 1, 1, 0, 50, 40, 0, 0);        // centre stays
        var (belowX, _) = AffineMath.LayerToDoc(w, h, 0, 0, 1, 1, 0, 50, 80, 0.5f, 0);    // below centre, sheared
        Assert.Equal(50f, cxDoc, 3);
        Assert.True(belowX > 50f);   // sheared right
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

public class PixelLayerBoundsTests
{
    [Fact]
    public void ExpandToCover_GrowsAndPreservesContentAndOffset()
    {
        // a 20x10 layer placed partly off-canvas (left & below the top edge) in a 100x80 doc
        var px = new PixelLayer(20, 10, "sub") { OffsetX = -5, OffsetY = 70 };
        px.Pixels[0] = 11; px.Pixels[1] = 22; px.Pixels[2] = 33; px.Pixels[3] = 255;   // top-left texel

        bool changed = px.ExpandToCover(100, 80);

        Assert.True(changed);
        // union of [-5,15)x[70,80) and [0,100)x[0,80) → [-5,100)x[0,80) = 105 x 80
        Assert.Equal(105, px.Width);
        Assert.Equal(80, px.Height);
        Assert.Equal(-5, px.OffsetX);
        Assert.Equal(0, px.OffsetY);
        // the old top-left texel moved to new-buffer (dx=0, dy=70)
        int i = (70 * px.Width + 0) * 4;
        Assert.Equal(11, px.Pixels[i]);
        Assert.Equal(22, px.Pixels[i + 1]);
        Assert.Equal(33, px.Pixels[i + 2]);
        Assert.Equal(255, px.Pixels[i + 3]);
    }

    [Fact]
    public void ExpandToCover_NoChange_WhenAlreadyCoversDoc()
    {
        var px = new PixelLayer(100, 80, "bg");   // doc-sized at origin
        Assert.False(px.ExpandToCover(100, 80));
        Assert.Equal(100, px.Width);
        Assert.Equal(0, px.OffsetX);
    }

    [Fact]
    public void TrimToContent_CropsToBoundingBox_AndShiftsOffset()
    {
        // doc-sized 100x80 layer, content is a single texel at (40,30)
        var px = new PixelLayer(100, 80, "paint");
        int i = (30 * 100 + 40) * 4;
        px.Pixels[i] = 9; px.Pixels[i + 1] = 8; px.Pixels[i + 2] = 7; px.Pixels[i + 3] = 255;

        Assert.True(px.TrimToContent());
        Assert.Equal(1, px.Width);
        Assert.Equal(1, px.Height);
        Assert.Equal(40, px.OffsetX);    // content's doc position preserved
        Assert.Equal(30, px.OffsetY);
        Assert.Equal(9, px.Pixels[0]);
        Assert.Equal(255, px.Pixels[3]);
    }

    [Fact]
    public void TrimToContent_FullyTransparent_CollapsesTo1x1()
    {
        var px = new PixelLayer(50, 50, "empty");
        Assert.True(px.TrimToContent());
        Assert.Equal(1, px.Width);
        Assert.Equal(1, px.Height);
    }

    [Fact]
    public void RasterStateCommand_RoundTripsBufferSizeAndOffset()
    {
        // simulate a paint gesture that grows + crops: before = empty 1x1, after = a painted 8x8 at (20,20)
        var layer = new PixelLayer(1, 1, "L");
        var before = RasterState.Capture(layer);

        layer.SetBuffer(8, 8, new float[8 * 8 * 4]);
        layer.OffsetX = 20; layer.OffsetY = 20;
        for (int k = 3; k < layer.Pixels.Length; k += 4) layer.Pixels[k] = 1f;
        var after = RasterState.Capture(layer);

        var cmd = new RasterStateCommand(layer, before, after, () => { });
        cmd.Undo();
        Assert.Equal(1, layer.Width);
        Assert.Equal(0, layer.OffsetX);
        cmd.Do();
        Assert.Equal(8, layer.Width);
        Assert.Equal(20, layer.OffsetX);
        Assert.Equal(1f, layer.Pixels[3]);
    }

    [Fact]
    public void RasterStateCommand_RoundTripsMask()   // audit C5: mask is realloc'd by ExpandToCover/Trim → must snapshot it too
    {
        var layer = new PixelLayer(4, 4, "L");
        layer.AddWhiteMask(4, 4);
        layer.Mask![0] = 10;
        var before = RasterState.Capture(layer);

        // a paint gesture grows the layer (and its mask) to 8x8
        layer.SetBuffer(8, 8, new float[8 * 8 * 4]);
        layer.Mask = new byte[8 * 8 * 4];
        layer.Mask![0] = 99;
        var after = RasterState.Capture(layer);

        var cmd = new RasterStateCommand(layer, before, after, () => { });
        cmd.Undo();
        Assert.Equal(4 * 4 * 4, layer.Mask!.Length);   // mask restored to the BEFORE size (was 8x8)
        Assert.Equal(10, layer.Mask![0]);
        cmd.Do();
        Assert.Equal(8 * 8 * 4, layer.Mask!.Length);
        Assert.Equal(99, layer.Mask![0]);
    }
}
