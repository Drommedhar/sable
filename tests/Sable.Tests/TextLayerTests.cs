using Sable.Imaging;
using Xunit;

namespace Sable.Tests;

public class TextLayerTests
{
    [Fact]
    public void AreaText_Wraps_ToTallerBitmap()
    {
        const string s = "wrap wrap wrap wrap wrap wrap";
        var (_, hPoint, _) = TextRaster.Render(s, 24, 0, 0, 0);                 // point text: one line
        var (_, hBox, _) = TextRaster.Render(s, 24, 0, 0, 0, maxWidth: 80);     // area text: wraps
        Assert.True(hBox > hPoint, $"wrapped height {hBox} should exceed single-line {hPoint}");
    }

    [Fact]
    public void Tracking_WidensPointText()
    {
        const string s = "AAAAA";
        var (w0, _, _) = TextRaster.Render(s, 24, 0, 0, 0);
        var (w1, _, _) = TextRaster.Render(s, 24, 0, 0, 0, tracking: 10);
        Assert.True(w1 > w0, $"tracked width {w1} should exceed {w0}");
    }

    [Fact]
    public void ToPath_ProducesVectorContours()
    {
        var txt = new Sable.Engine.Layers.TextLayer("O", 5, 5, 48, 0, 0, 0);
        var path = txt.ToPath();
        Assert.True(path.Nodes.Count >= 3);             // exterior contour
        Assert.NotEmpty(path.ExtraContours);            // "O" has a counter (hole)
        Assert.True(path.Filled);
    }

    [Fact]
    public void OnPath_RendersIntoPathBbox()
    {
        var path = new (float, float)[] { (10, 50), (60, 50), (110, 50) };
        var (w, h, rgba, ox, oy) = TextRaster.RenderOnPath("hi there", 20, 0, 0, 0, "", false, false, 0, path);
        Assert.True(w > 1 && h > 1);
        Assert.True(ox <= 10 && oy <= 50);   // bitmap origin covers the path start (with padding)
        Assert.Equal(w * h * 4, rgba.Length);
    }
}
