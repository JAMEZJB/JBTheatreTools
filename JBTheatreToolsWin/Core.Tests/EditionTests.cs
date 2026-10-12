using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>--edition on the command line (--install, --install-file, --uninstall, --download): naming an app's Light or
/// Full edition picks that edition's install slot and builds.</summary>
public class EditionTests
{
    private static CatalogApp Pdf() => new()
    {
        Id = "pdftools", Name = "PDF Tools",
        Assets = new() { ["macos"] = "PDFTools-macOS.zip", ["windows-x64"] = "PDFTools-Windows-x64.exe" },
        Variants = new()
        {
            new() { Id = "light", Label = "Light", Assets = new() { ["macos"] = "PDFTools-macOS.zip", ["windows-x64"] = "PDFTools-Windows-x64.exe" } },
            new() { Id = "full", Label = "Full", Assets = new() { ["macos-arm64"] = "PDFTools-Full-macOS-arm64.zip", ["windows-x64"] = "PDFTools-Full-Windows-x64.zip",
                                                                  ["windows-arm64"] = "PDFTools-Full-Windows-arm64.zip" } },
        },
    };

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("full", "full")]
    [InlineData("Full", "full")]
    [InlineData("LIGHT", "light")]
    public void AnEditionIsNamedByIdOrLabel(string? edition, string? expected)
    {
        Assert.True(Pdf().TryResolveEdition(edition, out var variant));
        Assert.Equal(expected, variant);
    }

    [Fact]
    public void ALegacyIdWithAnotherLabelMatchesEither()
    {
        // NDI Tools' Light edition still has the id "standard".
        var ndi = new CatalogApp { Id = "nditools", Name = "NDI Tools", Variants = new()
            { new() { Id = "standard", Label = "Light" }, new() { Id = "full", Label = "Full" } } };
        Assert.True(ndi.TryResolveEdition("light", out var a)); Assert.Equal("standard", a);
        Assert.True(ndi.TryResolveEdition("standard", out var b)); Assert.Equal("standard", b);
        Assert.Equal("light (standard), full", ndi.EditionChoices);
        Assert.Equal("nditools", ndi.InstallKey(a));   // the default edition keeps the plain id
    }

    [Fact]
    public void AnUnknownEditionOrOneOnASingleEditionAppIsRefused()
    {
        Assert.False(Pdf().TryResolveEdition("pro", out _));
        var one = new CatalogApp { Id = "dmxtools", Name = "DMX Tools" };
        Assert.False(one.TryResolveEdition("full", out _));
        Assert.True(one.TryResolveEdition(null, out var v));
        Assert.Null(v);
        Assert.Equal("none — it has one edition", one.EditionChoices);
    }

    [Fact]
    public void TheFullEditionHasItsOwnSlotAndBuildPerChip()
    {
        var app = Pdf();
        Assert.True(app.TryResolveEdition("full", out var full));
        Assert.Equal("pdftools@full", app.InstallKey(full));
        Assert.Equal(" (Full)", app.VariantSuffix(full));
        Assert.Equal("PDFTools-Full-Windows-x64.zip", Platform.Pick(app.AssetsFor(full), osIsArm64: false, preferX64: false)?.Name);
        Assert.Equal("PDFTools-Full-Windows-arm64.zip", Platform.Pick(app.AssetsFor(full), osIsArm64: true, preferX64: false)?.Name);
        Assert.Equal("PDFTools-Full-Windows-x64.zip", Platform.Pick(app.AssetsFor(full), osIsArm64: true, preferX64: true)?.Name);
        // Light has no ARM64 build: an ARM64 PC gets the x64 one, run emulated.
        Assert.Equal(("PDFTools-Windows-x64.exe", true), Platform.Pick(app.AssetsFor("light"), osIsArm64: true, preferX64: false));
    }
}
