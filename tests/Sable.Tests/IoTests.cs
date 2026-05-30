using Sable.Core;
using Sable.Engine;
using Sable.Engine.Layers;
using Sable.Format;
using Sable.Imaging;
using Xunit;

namespace Sable.Tests;

public class SableFileTests
{
    [Fact]
    public void SaveLoad_RoundTrips_Layers_Params_Pixels_Mask()
    {
        var doc = Document.CreateDemo(80, 60);
        var path = Path.Combine(Path.GetTempPath(), $"sable_test_{Guid.NewGuid():N}.sable");
        try
        {
            SableFile.Save(doc, path);
            var loaded = SableFile.Load(path);

            Assert.Equal(doc.Width, loaded.Width);
            Assert.Equal(doc.Height, loaded.Height);
            Assert.Equal(doc.Layers.Count, loaded.Layers.Count);

            // blend + opacity of the Screen highlight layer (index 2)
            Assert.Equal(doc.Layers[2].BlendMode, loaded.Layers[2].BlendMode);
            Assert.Equal(doc.Layers[2].Opacity, loaded.Layers[2].Opacity, 4);

            // pixels of the background layer preserved
            var srcBg = (PixelLayer)doc.Layers[0];
            var dstBg = (PixelLayer)loaded.Layers[0];
            Assert.Equal(srcBg.Pixels, dstBg.Pixels);

            // mask preserved on the disc layer
            Assert.True(loaded.Layers[1].HasMask);
            Assert.Equal(((PixelLayer)doc.Layers[1]).Mask, ((PixelLayer)loaded.Layers[1]).Mask);

            // adjustment params preserved
            var srcAdj = (AdjustmentLayer)doc.Layers[3];
            var dstAdj = (AdjustmentLayer)loaded.Layers[3];
            Assert.Equal(srcAdj.Brightness, dstAdj.Brightness, 4);
            Assert.Equal(srcAdj.Contrast, dstAdj.Contrast, 4);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveLoad_PreservesLevelsAndHslAdjustments()
    {
        var doc = new Document(16, 16);
        doc.Layers.Add(new PixelLayer(16, 16, "bg"));
        doc.Layers.Add(new AdjustmentLayer(AdjustmentKind.Levels) { InBlack = 0.1f, InWhite = 0.85f, Gamma = 1.7f });
        doc.Layers.Add(new AdjustmentLayer(AdjustmentKind.Hsl) { HueShift = 0.2f, Saturation = 1.4f, Lightness = -0.1f });
        var path = Path.Combine(Path.GetTempPath(), $"adj_{Guid.NewGuid():N}.sable");
        try
        {
            SableFile.Save(doc, path);
            var loaded = SableFile.Load(path);
            var lv = Assert.IsType<AdjustmentLayer>(loaded.Layers[1]);
            Assert.Equal(AdjustmentKind.Levels, lv.Kind);
            Assert.Equal(0.85f, lv.InWhite, 4);
            Assert.Equal(1.7f, lv.Gamma, 4);
            var hsl = Assert.IsType<AdjustmentLayer>(loaded.Layers[2]);
            Assert.Equal(AdjustmentKind.Hsl, hsl.Kind);
            Assert.Equal(0.2f, hsl.HueShift, 4);
            Assert.Equal(1.4f, hsl.Saturation, 4);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveLoad_PreservesLayerOffset()
    {
        var doc = new Document(16, 16);
        doc.Layers.Add(new PixelLayer(16, 16, "A") { OffsetX = 7, OffsetY = -4 });
        var path = Path.Combine(Path.GetTempPath(), $"off_{Guid.NewGuid():N}.sable");
        try
        {
            SableFile.Save(doc, path);
            var loaded = SableFile.Load(path);
            Assert.Equal(7, loaded.Layers[0].OffsetX);
            Assert.Equal(-4, loaded.Layers[0].OffsetY);
            Assert.Equal(1f, loaded.Layers[0].ScaleX, 4);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveLoad_PreservesTransform()
    {
        var doc = new Document(16, 16);
        doc.Layers.Add(new PixelLayer(16, 16, "A") { ScaleX = 1.5f, ScaleY = 0.8f, Rotation = 33f });
        var path = Path.Combine(Path.GetTempPath(), $"xf_{Guid.NewGuid():N}.sable");
        try
        {
            SableFile.Save(doc, path);
            var loaded = SableFile.Load(path);
            Assert.Equal(1.5f, loaded.Layers[0].ScaleX, 4);
            Assert.Equal(0.8f, loaded.Layers[0].ScaleY, 4);
            Assert.Equal(33f, loaded.Layers[0].Rotation, 4);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveLoad_PreservesClipFlag()
    {
        var doc = Document.CreateDemo(32, 32);
        doc.Layers[2].ClipToBelow = true;
        var path = Path.Combine(Path.GetTempPath(), $"clip_{Guid.NewGuid():N}.sable");
        try
        {
            SableFile.Save(doc, path);
            var loaded = SableFile.Load(path);
            Assert.True(loaded.Layers[2].ClipToBelow);
            Assert.False(loaded.Layers[0].ClipToBelow);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveLoad_PreservesFillOpacity()
    {
        var doc = new Document(16, 16);
        doc.Layers.Add(new PixelLayer(16, 16, "A") { FillOpacity = 0.42f });
        var path = Path.Combine(Path.GetTempPath(), $"fill_{Guid.NewGuid():N}.sable");
        try
        {
            SableFile.Save(doc, path);
            var loaded = SableFile.Load(path);
            Assert.Equal(0.42f, loaded.Layers[0].FillOpacity, 4);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveLoad_PreservesSingleParamAdjustments()
    {
        var doc = new Document(16, 16);
        doc.Layers.Add(new PixelLayer(16, 16, "bg"));
        doc.Layers.Add(new AdjustmentLayer(AdjustmentKind.Exposure) { Exposure = 1.25f });
        doc.Layers.Add(new AdjustmentLayer(AdjustmentKind.Vibrance) { Vibrance = -0.3f });
        doc.Layers.Add(new AdjustmentLayer(AdjustmentKind.Threshold) { Threshold = 0.4f });
        doc.Layers.Add(new AdjustmentLayer(AdjustmentKind.Posterize) { Posterize = 8f });
        doc.Layers.Add(new AdjustmentLayer(AdjustmentKind.Invert));
        var path = Path.Combine(Path.GetTempPath(), $"sp_{Guid.NewGuid():N}.sable");
        try
        {
            SableFile.Save(doc, path);
            var loaded = SableFile.Load(path);
            Assert.Equal(1.25f, ((AdjustmentLayer)loaded.Layers[1]).Exposure, 4);
            Assert.Equal(-0.3f, ((AdjustmentLayer)loaded.Layers[2]).Vibrance, 4);
            Assert.Equal(0.4f, ((AdjustmentLayer)loaded.Layers[3]).Threshold, 4);
            Assert.Equal(8f, ((AdjustmentLayer)loaded.Layers[4]).Posterize, 4);
            Assert.Equal(AdjustmentKind.Invert, ((AdjustmentLayer)loaded.Layers[5]).Kind);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveLoad_PreservesBwAndWhiteBalance()
    {
        var doc = new Document(16, 16);
        doc.Layers.Add(new PixelLayer(16, 16, "bg"));
        doc.Layers.Add(new AdjustmentLayer(AdjustmentKind.BlackWhite) { BwR = 0.5f, BwG = 0.2f, BwB = 0.7f });
        doc.Layers.Add(new AdjustmentLayer(AdjustmentKind.WhiteBalance) { Temperature = 0.3f, Tint = -0.4f });
        var path = Path.Combine(Path.GetTempPath(), $"bw_{Guid.NewGuid():N}.sable");
        try
        {
            SableFile.Save(doc, path);
            var loaded = SableFile.Load(path);
            var bw = (AdjustmentLayer)loaded.Layers[1];
            Assert.Equal(0.5f, bw.BwR, 4); Assert.Equal(0.2f, bw.BwG, 4); Assert.Equal(0.7f, bw.BwB, 4);
            var wb = (AdjustmentLayer)loaded.Layers[2];
            Assert.Equal(0.3f, wb.Temperature, 4); Assert.Equal(-0.4f, wb.Tint, 4);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveLoad_PreservesOutputLevels()
    {
        var doc = new Document(16, 16);
        doc.Layers.Add(new PixelLayer(16, 16, "bg"));
        doc.Layers.Add(new AdjustmentLayer(AdjustmentKind.Levels) { OutBlack = 0.1f, OutWhite = 0.7f });
        var path = Path.Combine(Path.GetTempPath(), $"olv_{Guid.NewGuid():N}.sable");
        try
        {
            SableFile.Save(doc, path);
            var loaded = SableFile.Load(path);
            var la = Assert.IsType<AdjustmentLayer>(loaded.Layers[1]);
            Assert.Equal(0.1f, la.OutBlack, 4);
            Assert.Equal(0.7f, la.OutWhite, 4);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveLoad_PreservesCurvePoints()
    {
        var doc = new Document(16, 16);
        doc.Layers.Add(new PixelLayer(16, 16, "bg"));
        var adj = new AdjustmentLayer(AdjustmentKind.Curves);
        adj.Curves[0].Insert(1, (0.4f, 0.7f));
        adj.Curves[1].Insert(1, (0.25f, 0.1f));
        doc.Layers.Add(adj);
        var path = Path.Combine(Path.GetTempPath(), $"curve_{Guid.NewGuid():N}.sable");
        try
        {
            SableFile.Save(doc, path);
            var loaded = SableFile.Load(path);
            var la = Assert.IsType<AdjustmentLayer>(loaded.Layers[1]);
            Assert.Equal(AdjustmentKind.Curves, la.Kind);
            Assert.Equal(3, la.Curves[0].Count);
            Assert.Equal(0.4f, la.Curves[0][1].x, 4);
            Assert.Equal(0.7f, la.Curves[0][1].y, 4);
            Assert.Equal(0.25f, la.Curves[1][1].x, 4);
            Assert.Equal(0.1f, la.Curves[1][1].y, 4);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveLoad_PreservesGaussianBlurFilter()
    {
        var doc = new Document(16, 16);
        doc.Layers.Add(new PixelLayer(16, 16, "bg"));
        doc.Layers.Add(new FilterLayer(FilterKind.GaussianBlur) { Radius = 12.5f });
        var path = Path.Combine(Path.GetTempPath(), $"flt_{Guid.NewGuid():N}.sable");
        try
        {
            SableFile.Save(doc, path);
            var loaded = SableFile.Load(path);
            var flt = Assert.IsType<FilterLayer>(loaded.Layers[1]);
            Assert.Equal(FilterKind.GaussianBlur, flt.Kind);
            Assert.Equal(12.5f, flt.Radius, 4);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveLoad_PreservesNestedGroups()
    {
        var doc = new Document(16, 16);
        var g = new GroupLayer("G") { Opacity = 0.5f, BlendMode = BlendMode.Multiply };
        g.Children.Add(new PixelLayer(16, 16, "inner"));
        g.Children.Add(new AdjustmentLayer(AdjustmentKind.Hsl) { HueShift = 0.1f });
        doc.Layers.Add(new PixelLayer(16, 16, "bg"));
        doc.Layers.Add(g);
        var path = Path.Combine(Path.GetTempPath(), $"grp_{Guid.NewGuid():N}.sable");
        try
        {
            SableFile.Save(doc, path);
            var loaded = SableFile.Load(path);
            var lg = Assert.IsType<GroupLayer>(loaded.Layers[1]);
            Assert.Equal("G", lg.Name);
            Assert.Equal(0.5f, lg.Opacity, 4);
            Assert.Equal(BlendMode.Multiply, lg.BlendMode);
            Assert.Equal(2, lg.Children.Count);
            Assert.IsType<PixelLayer>(lg.Children[0]);
            Assert.IsType<AdjustmentLayer>(lg.Children[1]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_NonSableFile_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bad_{Guid.NewGuid():N}.sable");
        File.WriteAllText(path, "not a zip");
        try
        {
            Assert.ThrowsAny<Exception>(() => SableFile.Load(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

public class ImageCodecTests
{
    [Fact]
    public void EncodeDecode_PreservesSizeAndPixels()
    {
        int w = 17, h = 9;   // odd size, non-aligned
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 200; rgba[i + 1] = 120; rgba[i + 2] = 30; rgba[i + 3] = 255;
        }

        var path = Path.Combine(Path.GetTempPath(), $"img_{Guid.NewGuid():N}.png");
        try
        {
            ImageCodec.EncodePng(path, w, h, rgba);
            var (dw, dh, decoded) = ImageCodec.DecodeRgba(path);

            Assert.Equal(w, dw);
            Assert.Equal(h, dh);
            // sample center pixel survives the PNG round-trip (lossless)
            int c = ((h / 2) * w + (w / 2)) * 4;
            Assert.Equal(200, decoded[c]);
            Assert.Equal(120, decoded[c + 1]);
            Assert.Equal(30, decoded[c + 2]);
            Assert.Equal(255, decoded[c + 3]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
