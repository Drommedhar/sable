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
}
