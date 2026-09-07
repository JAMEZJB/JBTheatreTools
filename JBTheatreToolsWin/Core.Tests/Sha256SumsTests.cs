using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>SHA256SUMS parsing — the shapes sha256sum/shasum actually produce, incl. Windows line endings.</summary>
public class Sha256SumsTests
{
    [Fact]
    public void StandardShasumFormat()
    {
        var sums = "abc123  App-macOS.zip\ndef456  App-Windows-x64.exe\n";
        Assert.Equal("abc123", Sha256Sums.Expected("App-macOS.zip", sums));
        Assert.Equal("def456", Sha256Sums.Expected("App-Windows-x64.exe", sums));
        Assert.Null(Sha256Sums.Expected("Missing.zip", sums));
    }

    [Fact]
    public void WindowsLineEndingsBinaryMarkerAndTabs()
    {
        var sums = "abc123 *App-macOS.zip\r\ndef456\tApp-Windows-x64.exe\r\n";
        Assert.Equal("abc123", Sha256Sums.Expected("App-macOS.zip", sums));        // "*" binary marker
        Assert.Equal("def456", Sha256Sums.Expected("App-Windows-x64.exe", sums));  // tab separator
    }

    [Fact]
    public void NameMustMatchExactly()
    {
        var sums = "abc123  App-macOS.zip\n";
        Assert.Null(Sha256Sums.Expected("App-macOS", sums));
        Assert.Null(Sha256Sums.Expected("app-macos.zip", sums));
        Assert.Null(Sha256Sums.Expected("App-macOS.zip", ""));
    }
}
