using Sable.Format;
using Xunit;

namespace Sable.Tests;

/// <summary>
/// PostScript-font-name → installed-family matching (roadmap §8 / §3.4). This rule decides which
/// PSD fonts are reported missing and which family text renders with; it was previously duplicated
/// and untested in the importer + app. Installed families are injected, so no font system is needed.
/// </summary>
public class FontMatcherTests
{
    private static readonly string[] Installed =
        { "Open Sans", "Helvetica Neue", "Arial", "Times New Roman", "DejaVu Sans" };

    [Theory]
    [InlineData("OpenSans-Bold", "Open Sans")]
    [InlineData("OpenSans-Regular", "Open Sans")]
    [InlineData("HelveticaNeue-Italic", "Helvetica Neue")]
    [InlineData("Arial", "Arial")]
    [InlineData("ArialMT", "Arial")]            // PS suffix beyond the family still prefix-matches
    public void MatchInstalled_FindsFamily(string ps, string expected)
        => Assert.Equal(expected, FontMatcher.MatchInstalled(ps, Installed));

    [Theory]
    [InlineData("ProximaNova-Bold")]            // not installed
    [InlineData("FuturaPT-Book")]
    public void MatchInstalled_NullWhenMissing(string ps)
        => Assert.Null(FontMatcher.MatchInstalled(ps, Installed));

    [Fact]
    public void MatchInstalled_PrefersLongestFamily()
    {
        // "Helvetica" must not shadow "Helvetica Neue" for a HelveticaNeue PS name.
        var fams = new[] { "Helvetica", "Helvetica Neue" };
        Assert.Equal("Helvetica Neue", FontMatcher.MatchInstalled("HelveticaNeue-Bold", fams));
    }

    [Fact]
    public void MatchInstalled_IgnoresTooShortFamilies()
        => Assert.Null(FontMatcher.MatchInstalled("AbXYZ", new[] { "Ab" }));   // family < 3 chars

    [Theory]
    [InlineData("OpenSans-Bold", true)]
    [InlineData("ProximaNova-Bold", false)]
    [InlineData("", true)]                       // unparseable → don't cry wolf
    [InlineData("---", true)]                    // punctuation only → unparseable
    public void IsInstalled(string ps, bool expected)
        => Assert.Equal(expected, FontMatcher.IsInstalled(ps, Installed));

    [Theory]
    [InlineData("OpenSans-Bold", true, false)]
    [InlineData("HelveticaNeue-Italic", false, true)]
    [InlineData("Roboto-BoldItalic", true, true)]
    [InlineData("Futura-Black", true, false)]    // Black → bold
    [InlineData("Optima-Oblique", false, true)]  // Oblique → italic
    [InlineData("Arial", false, false)]
    public void StyleFlags(string ps, bool bold, bool italic)
        => Assert.Equal((bold, italic), FontMatcher.StyleFlags(ps));

    [Fact]
    public void Resolve_InstalledFamily()
    {
        var fam = FontMatcher.Resolve("OpenSans-Bold", Installed, out bool installed);
        Assert.True(installed);
        Assert.Equal("Open Sans", fam);
    }

    [Fact]
    public void Resolve_MissingFontHumanizes()
    {
        var fam = FontMatcher.Resolve("ProximaNova-Bold", Installed, out bool installed);
        Assert.False(installed);                 // renderer will substitute a default
        Assert.Equal("Proxima Nova", fam);       // camel-case split of the requested base name
    }

    [Theory]
    [InlineData("OpenSans-Bold", "Open Sans")]
    [InlineData("HelveticaNeue-Italic", "Helvetica Neue")]
    [InlineData("Arial", "Arial")]
    [InlineData("ABCFoo", "ABCFoo")]             // no lower→upper boundary inside an all-caps run
    public void Humanize(string ps, string expected)
        => Assert.Equal(expected, FontMatcher.Humanize(ps));
}
