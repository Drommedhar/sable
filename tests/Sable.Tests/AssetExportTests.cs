using Sable.Engine.IO;

namespace Sable.Tests;

/// <summary>Pure batch-asset-export helpers (ROADMAP P3).</summary>
public sealed class AssetExportTests
{
    [Theory]
    [InlineData("Layer 1", "Layer 1")]
    [InlineData("a/b:c*", "a_b_c_")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("trailing.", "trailing")]
    [InlineData("", "asset")]
    [InlineData("   ", "asset")]
    public void SanitizeName_strips_invalid(string input, string expected)
        => Assert.Equal(expected, AssetExport.SanitizeName(input));

    [Fact]
    public void BuildFileName_combines_base_suffix_ext()
        => Assert.Equal("Icon@2x.png", AssetExport.BuildFileName("Icon", "@2x", "png"));

    [Fact]
    public void UniqueNames_disambiguates_collisions_case_insensitively()
    {
        var outp = AssetExport.UniqueNames(new[] { "a.png", "a.png", "A.png", "b.png" });
        Assert.Equal(new[] { "a.png", "a-2.png", "A-3.png", "b.png" }, outp);
    }

    [Fact]
    public void AlphaBounds_finds_tight_box()
    {
        // 4×4, opaque only at (1,2)
        var rgba = new byte[4 * 4 * 4];
        rgba[(2 * 4 + 1) * 4 + 3] = 255;
        Assert.True(AssetExport.AlphaBounds(rgba, 4, 4, out int x, out int y, out int w, out int h));
        Assert.Equal((1, 2, 1, 1), (x, y, w, h));
    }

    [Fact]
    public void AlphaBounds_false_when_empty()
        => Assert.False(AssetExport.AlphaBounds(new byte[4 * 4 * 4], 4, 4, out _, out _, out _, out _));

    [Fact]
    public void Crop_extracts_subrect()
    {
        // 3×3, mark the centre pixel red+opaque
        var rgba = new byte[3 * 3 * 4];
        int c = (1 * 3 + 1) * 4;
        rgba[c] = 200; rgba[c + 3] = 255;
        var cropped = AssetExport.Crop(rgba, 3, 3, 1, 1, 1, 1);
        Assert.Equal(4, cropped.Length);
        Assert.Equal(200, cropped[0]);
        Assert.Equal(255, cropped[3]);
    }
}
