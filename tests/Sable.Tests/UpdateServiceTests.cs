using Sable.Core.Services;
using Xunit;

namespace Sable.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.2", true)]    // patch newer
    [InlineData("1.3.0", "1.2.9", true)]    // minor newer
    [InlineData("2.0.0", "1.9.9", true)]    // major newer
    [InlineData("1.2.3", "1.2.3", false)]   // equal
    [InlineData("1.2.3", "1.2.4", false)]   // older
    [InlineData("1.2", "1.2.0", false)]     // missing parts treated as 0
    [InlineData("1.14.2", "0.1.0", true)]   // the live testing case (novalist latest vs Sable)
    public void IsNewer_ComparesSemverParts(string remote, string current, bool expected)
        => Assert.Equal(expected, UpdateService.IsNewer(remote, current));

    [Fact]
    public void Version_IsReadable()
        => Assert.False(string.IsNullOrWhiteSpace(Sable.Core.VersionInfo.Version));

    [Theory]
    [InlineData("1.2.3", "1.2.2", 1)]
    [InlineData("1.2.2", "1.2.3", -1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("1.2", "1.2.0", 0)]      // missing parts = 0
    public void CompareVersions_OrdersSemver(string a, string b, int expectedSign)
        => Assert.Equal(expectedSign, Math.Sign(UpdateService.CompareVersions(a, b)));

    [Fact]
    public void BuildChangelog_HeadingsAndRulesAcrossVersions()
    {
        var md = UpdateService.BuildChangelog(new[]
        {
            ("v1.2.0", "- New thing\n- Fix"),
            ("v1.1.0", "Older notes"),
        });

        Assert.Contains("## v1.2.0", md);
        Assert.Contains("## v1.1.0", md);
        Assert.Contains("- New thing", md);
        Assert.Contains("\n\n---\n\n", md);                 // separator between versions
        Assert.True(md.IndexOf("v1.2.0") < md.IndexOf("v1.1.0"));   // newest first
    }

    [Fact]
    public void BuildChangelog_EmptyBodyGetsPlaceholder()
    {
        var md = UpdateService.BuildChangelog(new[] { ("v1.0.0", "   ") });
        Assert.Contains("_No release notes._", md);
    }

    [Fact]
    public void BuildChangelog_SingleVersionHasNoRule()
    {
        var md = UpdateService.BuildChangelog(new[] { ("v1.0.0", "notes") });
        Assert.DoesNotContain("---", md);
    }
}
