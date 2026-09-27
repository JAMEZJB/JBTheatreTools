using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>Which editions Download All works on. The Swift launcher carries the same cases.</summary>
public class BatchEditionsTests
{
    private static readonly string[] LightFull = { "light", "full" };
    private static Func<string?, bool> Installed(params string?[] slots) => v => slots.Contains(v);

    [Fact]
    public void SingleEditionAppIsOneSlot()
    {
        Assert.Equal(new string?[] { null }, BatchEditions.ForDownloadAll(null, null, false, Installed()));
        Assert.Equal(new string?[] { null }, BatchEditions.ForDownloadAll(new[] { "only" }, "only", true, Installed()));
    }

    [Fact]
    public void RowShowingLightFetchesLight() =>
        Assert.Equal(new string?[] { "light" }, BatchEditions.ForDownloadAll(LightFull, "light", false, Installed()));

    [Fact]
    public void RowSwitchedToFullFetchesFullNotLight() =>
        Assert.Equal(new string?[] { "full" }, BatchEditions.ForDownloadAll(LightFull, "full", false, Installed()));

    [Fact]
    public void InstalledEditionsStayIncludedWhateverTheRowShows()
    {
        Assert.Equal(new string?[] { "light", "full" }, BatchEditions.ForDownloadAll(LightFull, "full", false, Installed("light")));
        Assert.Equal(new string?[] { "light", "full" }, BatchEditions.ForDownloadAll(LightFull, "light", false, Installed("full")));
    }

    [Fact]
    public void PlusFullEditionsTakesEveryEdition() =>
        Assert.Equal(new string?[] { "light", "full" }, BatchEditions.ForDownloadAll(LightFull, "light", true, Installed()));

    [Fact]
    public void UnknownOrMissingChoiceFallsBackToTheFirstEdition()
    {
        Assert.Equal(new string?[] { "light" }, BatchEditions.ForDownloadAll(LightFull, null, false, Installed()));
        Assert.Equal(new string?[] { "light" }, BatchEditions.ForDownloadAll(LightFull, "gone", false, Installed()));
    }
}
