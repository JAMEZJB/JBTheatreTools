using System.Runtime.CompilerServices;
using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>Which folders "also remove its settings" may touch. The Swift launcher carries the same name rules.</summary>
public class AppDataFoldersTests
{
    [Theory]
    [InlineData("PSN Tools")]
    [InlineData("Convert to it!")]
    [InlineData("p2r3-convert-updater")]
    [InlineData("ShowControlTools")]
    public void OrdinaryAppFolderNamesAreAccepted(string name) => Assert.True(AppDataFolders.IsSafeName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".config")]
    [InlineData("PSN Tools.")]
    [InlineData(" PSN Tools")]
    [InlineData("..\\Windows")]
    [InlineData("PSN/Tools")]
    [InlineData("C:")]
    [InlineData("*")]
    [InlineData("JBTheatreTools")]
    [InlineData("microsoft")]
    [InlineData("Application Support")]
    [InlineData("Documents")]
    [InlineData("Logs")]
    public void PathsSharedFoldersAndOddNamesAreRefused(string? name) => Assert.False(AppDataFolders.IsSafeName(name));

    [Fact]
    public void TooLongNameIsRefused() => Assert.False(AppDataFolders.IsSafeName(new string('a', 65)));

    [Fact]
    public void EachNameUnderRoamingThenLocal()
    {
        var paths = AppDataFolders.WindowsPaths(new[] { "ProjectorControl", "Projector Control" }, @"C:\U\AppData\Roaming", @"C:\U\AppData\Local");
        Assert.Equal(new[]
        {
            Path.Combine(@"C:\U\AppData\Roaming", "ProjectorControl"), Path.Combine(@"C:\U\AppData\Local", "ProjectorControl"),
            Path.Combine(@"C:\U\AppData\Roaming", "Projector Control"), Path.Combine(@"C:\U\AppData\Local", "Projector Control"),
        }, paths);
    }

    [Fact]
    public void UnsafeAndDuplicateNamesAreSkippedAndNoneGivesNothing()
    {
        Assert.Equal(2, AppDataFolders.WindowsPaths(new[] { "DMX Tools", "dmx tools", "..", "Microsoft" }, "R", "L").Count);
        Assert.Empty(AppDataFolders.WindowsPaths(null, "R", "L"));
    }

    /// Every catalog app names at least one data folder, all of them pass the rules, and no two apps share one.
    [Fact]
    public void CatalogDataEntries()
    {
        var catalog = Catalog.Load(Path.Combine(RepoRoot(), "catalog.json"));
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in catalog.Apps)
        {
            Assert.NotEmpty(app.DataFolders ?? new());
            foreach (var n in app.DataFolders!)
            {
                Assert.True(AppDataFolders.IsSafeName(n), $"{app.Id}: {n}");
                Assert.True(owner.TryAdd(n, app.Id), $"{n} listed twice");
            }
        }
    }

    /// The repo root (catalog.json), from this source file's path.
    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
