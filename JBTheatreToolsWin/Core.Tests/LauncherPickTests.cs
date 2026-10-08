using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>Which launcher release is offered: only one that carries this platform's build. The Swift and Kotlin
/// launchers carry the same cases.</summary>
public class LauncherPickTests
{
    private sealed record R(string Tag, bool Pre, params string[] Assets);
    private const string Win = "JBTheatreTools-Windows-x64.exe";

    private static R? Pick(string current, bool dev, params R[] rs) =>
        VersionCompare.PickLauncher(rs, r => r.Tag, r => r.Pre, r => r.Assets.Contains(Win), current, dev);

    [Fact]
    public void ADevBuildWithoutThisPlatformsBuildIsSkipped()
    {
        // v1.32.1-dev.1 was published with the macOS zip only: it must not be offered to Windows.
        var macOnly = new R("v1.32.1-dev.1", true, "JBTheatreTools-macOS.zip");
        var release = new R("v1.32.0", false, Win, "JBTheatreTools-macOS.zip");
        Assert.Null(Pick("1.32.0", dev: true, macOnly, release));
        Assert.Equal("v1.32.0", Pick("1.31.0", dev: true, macOnly, release)?.Tag);
    }

    [Fact]
    public void NewestBuildCarryingReleaseWins()
    {
        var a = new R("v1.32.1-dev.2", true, Win);
        var b = new R("v1.32.1-dev.1", true, "JBTheatreTools-macOS.zip");
        Assert.Equal("v1.32.1-dev.2", Pick("1.32.0", dev: true, b, a)?.Tag);
        Assert.Null(Pick("1.32.0", dev: false, b, a));   // dev builds only with the Dev channel on
    }

    [Fact]
    public void BackToTheReleaseFromADevBuild()
    {
        var release = new R("v1.32.0", false, Win);
        Assert.Equal("v1.32.0", Pick("1.32.1-dev.3", dev: false, release)?.Tag);   // Dev channel off: offered
        Assert.Null(Pick("1.32.1-dev.3", dev: true, release));                      // on: it's older, not offered
        Assert.Null(Pick("1.32.0", dev: false, new R("v1.32.0", false, "JBTheatreTools-macOS.zip")));
    }
}
