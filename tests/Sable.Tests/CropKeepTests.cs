using Sable.Engine;
using Sable.Engine.Commands;
using Sable.Engine.Layers;
using Xunit;

namespace Sable.Tests;

public class CropKeepTests
{
    [Fact]
    public void CropKeep_ShiftsOffsets_KeepsPixels_AndUndoes()
    {
        var doc = new Document(100, 80);
        var layer = new PixelLayer(100, 80, "bg");
        layer.Pixels[(10 * 100 + 10) * 4 + 3] = 255;   // a pixel at doc (10,10)
        doc.Layers.Add(layer);

        var cmd = new CropKeepCommand(doc, 20, 10, 50, 40);
        cmd.Do();

        Assert.Equal(50, doc.Width);
        Assert.Equal(40, doc.Height);
        // buffer untouched — same array, same size; only the offset moved
        Assert.Equal(100, layer.Width);
        Assert.Equal(-20, layer.OffsetX);
        Assert.Equal(-10, layer.OffsetY);
        Assert.Equal(255, layer.Pixels[(10 * 100 + 10) * 4 + 3]);   // pixel preserved (now off-canvas)

        cmd.Undo();
        Assert.Equal(100, doc.Width);
        Assert.Equal(0, layer.OffsetX);
        Assert.Equal(0, layer.OffsetY);
    }

    [Fact]
    public void CropKeep_GroupNotShifted_ChildrenAre()
    {
        var doc = new Document(100, 80);
        var g = new GroupLayer("g");
        var child = new PixelLayer(100, 80, "child");
        g.Children.Add(child);
        doc.Layers.Add(g);

        new CropKeepCommand(doc, 5, 5, 60, 60).Do();
        Assert.Equal(0, g.OffsetX);        // group placement untouched
        Assert.Equal(-5, child.OffsetX);   // doc-space child shifted
        Assert.Equal(-5, child.OffsetY);
    }
}
