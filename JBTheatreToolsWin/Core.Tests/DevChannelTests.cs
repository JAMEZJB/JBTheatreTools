using JBTheatreTools;
using Xunit;

/// <summary>Semver pre-release ordering and the Dev-channel release pick.</summary>
public class DevChannelTests
{
    private sealed record R(string Tag, bool Pre = false);
    private static R? Pick(bool dev, params R[] rs) => VersionCompare.PickLatest(rs, r => r.Tag, r => r.Pre, dev);

    [Fact]
    public void PreReleasesSortBeforeTheirRelease()
    {
        Assert.True(VersionCompare.IsNewer("0.1.0-dev.2", "0.1.0-dev.1"));
        Assert.True(VersionCompare.IsNewer("v0.1.0", "v0.1.0-dev.2"));
        Assert.True(VersionCompare.IsNewer("0.1.0-dev.1", "0.0.9"));
        Assert.True(VersionCompare.IsNewer("0.1.0-dev.10", "0.1.0-dev.9"));
        Assert.False(VersionCompare.IsNewer("1.2", "1.2.0"));
        Assert.True(VersionCompare.IsNewer("build-20260921", "build-20260915"));
        Assert.True(VersionCompare.IsDev("v0.1.0-dev.1"));
        Assert.False(VersionCompare.IsDev("v0.1.0"));
    }

    [Fact]
    public void DevChannelOffNeverPicksADevBuild()
    {
        Assert.Equal("v0.1.0", Pick(false, new("v0.2.0-dev.1", true), new("v0.1.0"))!.Tag);
        Assert.Null(Pick(false, new R("v0.1.0-dev.1", true)));                  // dev-only app → nothing
        Assert.Equal("v0.1.0-rc1", Pick(false, new R("v0.1.0-rc1", true))!.Tag); // non-dev pre-release fallback kept
    }

    [Fact]
    public void DevChannelOnPicksTheHighestIncludingDevBuilds()
    {
        Assert.Equal("v0.2.0-dev.2", Pick(true, new("v0.1.0"), new("v0.2.0-dev.1", true), new("v0.2.0-dev.2", true))!.Tag);
        Assert.Equal("v0.2.0", Pick(true, new("v0.2.0-dev.2", true), new("v0.2.0"))!.Tag);
    }
}
