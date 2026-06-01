using System.Linq;
using Sable.Core.Ai;
using Xunit;

namespace Sable.Tests;

/// <summary>Pure-logic tests for the Linux CUDA GPU-runtime catalog (PHASE8_AI Linux).</summary>
public class GpuRuntimeCatalogTests
{
    [Fact]
    public void ResolveFor_Blackwell_FindsCoveringArtifact()
    {
        var art = GpuRuntimeCatalog.ResolveFor("120");   // sm_120 / RTX 5090
        Assert.NotNull(art);
        Assert.Contains("120", art!.Archs);
        Assert.Equal(GpuRuntimeCatalog.OrtVersion, art.OrtVersion);
    }

    [Fact]
    public void ResolveFor_UnsupportedArch_ReturnsNull()
    {
        Assert.Null(GpuRuntimeCatalog.ResolveFor("75"));   // Turing not in any published build
        Assert.Null(GpuRuntimeCatalog.ResolveFor(null));
    }

    [Fact]
    public void Covers_MatchesOnlyListedArchs()
    {
        var a = new GpuRuntimeArtifact("1.24.4", new[] { "89", "90", "120" }, "13", Url: "", SizeBytes: 1);
        Assert.True(a.Covers("120"));
        Assert.True(a.Covers("89"));
        Assert.False(a.Covers("86"));
    }

    [Fact]
    public void HasUrl_FalseUntilPublished()
    {
        // shipped entries have no URL yet (maintainer sets it after building + uploading)
        Assert.All(GpuRuntimeCatalog.All, a => Assert.False(a.HasUrl));
    }
}
