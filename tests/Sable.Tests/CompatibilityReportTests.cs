using Sable.Format;
using Xunit;

namespace Sable.Tests;

/// <summary>
/// Categorisation logic for the import compatibility report (roadmap §15). Pure logic —
/// the <see cref="CompatibilityReportWindow"/> binds to <see cref="CompatibilityReport"/>,
/// so these tests lock the severity bucketing the UI presents.
/// </summary>
public class CompatibilityReportTests
{
    [Fact]
    public void Build_CategorisesEachWarningKind()
    {
        var warnings = new List<string>
        {
            "\"Layer1\": smart object rasterised.",          // Rasterised
            "\"Layer2\": vector mask rasterised.",           // Rasterised
            "16-bit document converted to 8-bit.",           // Partial
            "\"L3\": Dissolve blend mapped to Normal.",      // Partial
            "\"L4\": adjustment layer skipped (no raster content).", // Skipped
            "\"L5\": disabled layer mask dropped.",          // Skipped
            "Unbalanced group markers — group structure flattened.", // Structural
            "\"L6\": layer effects unreadable.",             // Structural
        };
        var rep = CompatibilityReport.Build("test.psd", warnings, new List<string> { "ArialMT" });

        Assert.Equal("test.psd", rep.DocumentName);
        Assert.Single(rep.Fonts);
        Assert.Equal(8, rep.Entries.Count);
        Assert.Equal(2, rep.Count(CompatibilityReport.Severity.Rasterised));
        Assert.Equal(2, rep.Count(CompatibilityReport.Severity.Partial));
        Assert.Equal(2, rep.Count(CompatibilityReport.Severity.Skipped));
        Assert.Equal(2, rep.Count(CompatibilityReport.Severity.Structural));
        Assert.True(rep.HasIssues);
    }

    [Fact]
    public void Build_NoWarningsNoFonts_Clean()
    {
        var rep = CompatibilityReport.Build("clean.psd", new List<string>(), new List<string>());
        Assert.Empty(rep.Entries);
        Assert.Empty(rep.MissingFonts);
        Assert.False(rep.HasIssues);
    }

    [Fact]
    public void Build_LayerNameSplitFromMessage()
    {
        var rep = CompatibilityReport.Build("x", new List<string> { "\"My Layer\": smart object rasterised." }, new());
        var e = Assert.Single(rep.Entries);
        Assert.Equal("My Layer", e.Layer);
        Assert.Equal("smart object rasterised.", e.Message);
    }

    [Fact]
    public void Build_BareWarningHasEmptyLayer()
    {
        var rep = CompatibilityReport.Build("x", new List<string> { "16-bit document converted to 8-bit." }, new());
        var e = Assert.Single(rep.Entries);
        Assert.Equal("", e.Layer);
        Assert.Equal("16-bit document converted to 8-bit.", e.Message);
    }

    [Fact]
    public void Build_MissingFontsTrackedSeparately()
    {
        var rep = CompatibilityReport.Build("x", new List<string>(), new List<string> { "ArialMT", "OpenSans-Bold" });
        rep.MissingFonts.AddRange(rep.Fonts);
        Assert.Equal(2, rep.MissingFonts.Count);
        Assert.True(rep.HasIssues);   // missing fonts count as issues even with no warnings
    }
}
