using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>F8: an API-supplied tag/asset name used as a local filename component must not traverse or hide.</summary>
public class PathSafeTests
{
    [Theory]
    [InlineData("v1.2.3", "v1.2.3")]
    [InlineData("JBTheatreTools-macOS.zip", "JBTheatreTools-macOS.zip")]
    [InlineData("PDFTools-Full-macOS-arm64.zip", "PDFTools-Full-macOS-arm64.zip")]
    public void KeepsLegitimateNames(string input, string expected) => Assert.Equal(expected, PathSafe.Component(input));

    [Theory]
    [InlineData("../../Library/x")]
    [InlineData("..\\..\\x")]
    [InlineData("/etc/passwd")]
    [InlineData(".ssh")]
    [InlineData("a/b/c")]
    public void NeutralisesTraversalHiddenAndSeparators(string hostile)
    {
        var o = PathSafe.Component(hostile);
        Assert.DoesNotContain("/", o);
        Assert.DoesNotContain("\\", o);
        Assert.False(o.StartsWith('.'), $"must not start with a dot: {o}");
    }

    [Fact]
    public void EmptyBecomesUnderscore() => Assert.Equal("_", PathSafe.Component(""));
}
