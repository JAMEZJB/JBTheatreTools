using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>When the launcher offers to install itself. The Swift launcher's Applications rule carries the same idea.</summary>
public class LauncherHomeTests
{
    private static readonly string Local = Path.Combine(Path.GetTempPath(), "u", "AppData", "Local");
    private static readonly string Default = LauncherHome.DefaultExe(Local);
    private static readonly string Downloads = Path.Combine(Path.GetTempPath(), "u", "Downloads");

    [Fact]
    public void DefaultIsThePerUserProgramsFolder() =>
        Assert.Equal(Path.Combine(Local, "Programs", "JB Theatre Tools", "JBTheatreTools.exe"), Default);

    [Fact]
    public void OffersFromDownloadsButNotFromTheDefaultPlace()
    {
        Assert.True(LauncherHome.ShouldOffer(Path.Combine(Downloads, "JBTheatreTools-Windows-x64.exe"), Default, null));
        Assert.False(LauncherHome.ShouldOffer(Default, Default, null));
        Assert.False(LauncherHome.ShouldOffer(Default.ToUpperInvariant(), Default, null));
    }

    [Fact]
    public void AKeptOrChosenCopyIsNeverAskedAgain()
    {
        var mine = Path.Combine(Path.GetTempPath(), "Tools", "JBTheatreTools.exe");
        Assert.False(LauncherHome.ShouldOffer(mine, Default, new[] { mine.ToLowerInvariant() }));
        Assert.True(LauncherHome.ShouldOffer(mine, Default, new[] { Path.Combine(Downloads, "JBTheatreTools.exe") }));
    }

    [Fact]
    public void InstallsUseTheStandardFileName() =>
        Assert.Equal(Path.Combine("X", "JBTheatreTools.exe"), LauncherHome.ExeIn("X"));

    [Fact]
    public void InsideComparesWholeFolders()
    {
        Assert.True(LauncherHome.IsInside(Path.Combine(Downloads, "a.exe"), Downloads));
        Assert.True(LauncherHome.IsInside(Path.Combine(Downloads, "sub", "a.exe"), Downloads + Path.DirectorySeparatorChar));
        Assert.False(LauncherHome.IsInside(Path.Combine(Downloads + "2", "a.exe"), Downloads));
        Assert.False(LauncherHome.IsInside(Path.Combine(Downloads, "a.exe"), ""));
    }
}
