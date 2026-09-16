using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>The Full-app one-dir (.zip) launcher-exe picker (Windows Full install). Windows-style paths are
/// used deliberately — the picker counts both separators so it runs the same on the mac test host.</summary>
public class FullAppTests
{
    [Fact]
    public void PrefersExactStemMatch()
    {
        var exes = new[]
        {
            @"slot\Image Tools Full\_internal\something.exe",   // deeper decoy (shouldn't win)
            @"slot\Image Tools Full\Image Tools Full.exe",
        };
        Assert.Equal(@"slot\Image Tools Full\Image Tools Full.exe",
                     FullApp.PickMainExe(exes, "Image Tools Full"));
    }

    [Fact]
    public void FallsBackToShallowestWhenNoStemMatch()
    {
        var exes = new[]
        {
            @"slot\App\_internal\helper.exe",
            @"slot\App\launcher.exe",              // shallowest → the app-root launcher
        };
        Assert.Equal(@"slot\App\launcher.exe", FullApp.PickMainExe(exes, "Nope"));
    }

    [Fact]
    public void ForwardSlashesCountToo()
    {
        var exes = new[] { "slot/App/_internal/x.exe", "slot/App/main.exe" };
        Assert.Equal("slot/App/main.exe", FullApp.PickMainExe(exes, "Nope"));
    }

    [Fact]
    public void EmptyYieldsNull() => Assert.Null(FullApp.PickMainExe(System.Array.Empty<string>(), "x"));

    [Theory]
    [InlineData("Image Tools", "Full", "Image Tools Full")]
    [InlineData("PDF Tools", "Full", "PDF Tools Full")]
    [InlineData("Solo", null, "Solo")]
    [InlineData("Solo", "", "Solo")]
    public void ExpectedStemComposition(string app, string? label, string expected)
        => Assert.Equal(expected, FullApp.ExpectedStem(app, label));
}
