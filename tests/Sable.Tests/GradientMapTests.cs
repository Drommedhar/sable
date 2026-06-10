using Sable.Engine.Layers;
using Xunit;

namespace Sable.Tests;

public class GradientMapTests
{
    [Fact]
    public void BuildGradientLut_DefaultBlackWhite_IsIdentityRamp()
    {
        var a = new AdjustmentLayer(AdjustmentKind.GradientMap);
        var lut = new float[4 * AdjustmentLayer.LutSize];
        a.BuildGradientLut(lut);

        // channels 1..3 ramp 0 → 1
        for (int ch = 1; ch <= 3; ch++)
        {
            Assert.Equal(0f, lut[ch * 256 + 0], 3);
            Assert.Equal(1f, lut[ch * 256 + 255], 3);
            Assert.Equal(0.5f, lut[ch * 256 + 128], 1);
        }
    }

    [Fact]
    public void BuildGradientLut_TwoColourStops_Interpolates()
    {
        var a = new AdjustmentLayer(AdjustmentKind.GradientMap);
        a.GradientStops.Clear();
        a.GradientStops.Add((0f, 255, 0, 0));     // red
        a.GradientStops.Add((1f, 0, 0, 255));     // blue
        var lut = new float[4 * AdjustmentLayer.LutSize];
        a.BuildGradientLut(lut);

        Assert.Equal(1f, lut[1 * 256 + 0], 3);    // R at luma 0
        Assert.Equal(0f, lut[3 * 256 + 0], 3);    // B at luma 0
        Assert.Equal(0f, lut[1 * 256 + 255], 3);  // R at luma 1
        Assert.Equal(1f, lut[3 * 256 + 255], 3);  // B at luma 1
        // midpoint is an even mix
        Assert.Equal(0.5f, lut[1 * 256 + 128], 1);
        Assert.Equal(0.5f, lut[3 * 256 + 128], 1);
    }

    [Fact]
    public void SampleGradient_OutsideStops_ClampsToEnds()
    {
        var stops = new System.Collections.Generic.List<(float, byte, byte, byte)>
        {
            (0.25f, 10, 20, 30), (0.75f, 200, 210, 220),
        };
        var lo = AdjustmentLayer.SampleGradient(stops, 0f);
        var hi = AdjustmentLayer.SampleGradient(stops, 1f);
        Assert.Equal(10 / 255f, lo.r, 3);
        Assert.Equal(220 / 255f, hi.b, 3);
    }

    [Fact]
    public void GroupPassThrough_ClonesAndDefaultsOff()
    {
        var g = new GroupLayer("g");
        Assert.False(g.PassThrough);
        g.PassThrough = true;
        var c = (GroupLayer)g.Clone();
        Assert.True(c.PassThrough);
    }

    [Fact]
    public void Clone_CopiesGradientStops()
    {
        var a = new AdjustmentLayer(AdjustmentKind.GradientMap);
        a.GradientStops.Add((0.5f, 1, 2, 3));
        var c = (AdjustmentLayer)a.Clone();
        Assert.Equal(a.GradientStops.Count, c.GradientStops.Count);
        Assert.Equal((0.5f, (byte)1, (byte)2, (byte)3), c.GradientStops[^1]);
        c.GradientStops.RemoveAt(c.GradientStops.Count - 1);
        Assert.NotEqual(a.GradientStops.Count, c.GradientStops.Count);   // deep copy
    }
}
