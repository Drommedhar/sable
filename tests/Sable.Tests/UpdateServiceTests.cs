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

    // --- ChangelogParser (drives the tabbed/collapsible update window) ---

    private const string V105 =
        "### Added\n- Document bit depth\n- Grid settings\n\n" +
        "### Changed\n- Update notice shows markdown\n\n" +
        "### Fixed\n- Menus greyed out with no document\n\n" +
        "**Full Changelog**: https://github.com/Drommedhar/sable/compare/v1.0.4...v1.0.5";

    private const string V104 =
        "### Added\n- Nested effects\n\n" +
        "### Fixed\n- Clicks no longer swallowed";

    [Fact]
    public void Parse_SplitsVersionsAndSections()
    {
        var md = UpdateService.BuildChangelog(new[] { ("1.0.5", V105), ("1.0.4", V104) });
        var versions = ChangelogParser.Parse(md);

        Assert.Equal(2, versions.Count);
        Assert.Equal("1.0.5", versions[0].Heading);                 // newest first, order preserved
        Assert.Equal("1.0.4", versions[1].Heading);
        Assert.Equal(new[] { "Added", "Changed", "Fixed" }, versions[0].Sections.Select(s => s.Name));
        Assert.Equal(new[] { "Added", "Fixed" }, versions[1].Sections.Select(s => s.Name));   // no Changed
        Assert.Contains("Document bit depth", versions[0].Sections.First(s => s.Name == "Added").Markdown);
    }

    [Fact]
    public void Parse_StripsFullChangelogTrailer()
    {
        var md = UpdateService.BuildChangelog(new[] { ("1.0.5", V105) });
        var fixedSection = ChangelogParser.Parse(md)[0].Sections.First(s => s.Name == "Fixed");
        Assert.DoesNotContain("Full Changelog", fixedSection.Markdown);
        Assert.Contains("Menus greyed out", fixedSection.Markdown);
    }

    [Fact]
    public void Parse_UnsectionedContentBucketsAsNotes()
    {
        var md = UpdateService.BuildChangelog(new[] { ("1.0.1", "- Initial tagged release line") });
        var versions = ChangelogParser.Parse(md);
        Assert.Single(versions[0].Sections);
        Assert.Equal(ChangelogParser.GeneralSection, versions[0].Sections[0].Name);
        Assert.Contains("Initial tagged release", versions[0].Sections[0].Markdown);
    }

    [Fact]
    public void SectionOrder_AddedChangedFixedFirstNotesLast()
    {
        var md = UpdateService.BuildChangelog(new[]
        {
            ("1.0.5", V105),
            ("1.0.1", "- Initial tagged release line"),
        });
        var order = ChangelogParser.SectionOrder(ChangelogParser.Parse(md));
        Assert.Equal(new[] { "Added", "Changed", "Fixed", ChangelogParser.GeneralSection }, order);
    }

    [Fact]
    public void Parse_EmptyReturnsNoVersions()
        => Assert.Empty(ChangelogParser.Parse(""));

    [Fact]
    public void Bullets_JoinsWrappedLinesAndKeepsNesting()
    {
        var bullets = ChangelogParser.Bullets(
            "- First bullet that\n  wraps onto a second line\n- Second\n  - Nested one");

        Assert.Equal(3, bullets.Count);
        Assert.Equal("First bullet that wraps onto a second line", bullets[0].Text);
        Assert.True(bullets[0].IsBullet);
        Assert.Equal(0, bullets[0].Indent);
        Assert.Equal(1, bullets[2].Indent);                 // "  - " → nesting level 1
        Assert.Equal("Nested one", bullets[2].Text);
    }

    [Fact]
    public void Spans_SplitsBoldRuns()
    {
        var spans = ChangelogParser.Spans("A **bold** word");
        Assert.Equal(3, spans.Count);
        Assert.Equal(("A ", false), (spans[0].Text, spans[0].Bold));
        Assert.Equal(("bold", true), (spans[1].Text, spans[1].Bold));
        Assert.Equal((" word", false), (spans[2].Text, spans[2].Bold));
    }

    [Fact]
    public void Spans_FlattensLinksAndDropsBackticks()
    {
        var spans = ChangelogParser.Spans("see [Semantic Versioning](https://semver.org/) and `code`");
        var joined = string.Concat(spans.Select(s => s.Text));
        Assert.Equal("see Semantic Versioning and code", joined);
    }
}
