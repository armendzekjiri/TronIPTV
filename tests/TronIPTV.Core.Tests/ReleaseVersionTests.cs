using TronIPTV.Core;

namespace TronIPTV.Core.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.2.0", "0.1.0", true)]
    [InlineData("v1.0.0", "0.9.9", true)]
    [InlineData("v0.1.0", "0.1.0", false)]
    [InlineData("v0.1.0", "0.2.0", false)]
    [InlineData("v0.2.0", "0.2.0-alpha.0.4", true)]
    [InlineData("v0.2.0-beta.1", "0.2.0", false)]
    [InlineData("v0.1.1", "0.1.0+abc123", true)]
    [InlineData("garbage", "0.1.0", false)]
    public void ComparesReleases(string candidate, string current, bool expected) =>
        Assert.Equal(expected, ReleaseVersion.IsNewer(candidate, current));
}
