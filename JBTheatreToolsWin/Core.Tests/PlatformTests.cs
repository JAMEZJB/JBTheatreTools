using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>Which Windows build a slot installs on ARM64 / x64 PCs (parity with the macOS MacArch cases), and the PE
/// header reader behind "is the installed build already the chosen one?".</summary>
public class PlatformPickTests
{
    private static readonly Dictionary<string, string> Both = new() { ["windows-arm64"] = "A-arm64.exe", ["windows-x64"] = "A-x64.exe" };
    private static readonly Dictionary<string, string> X64Only = new() { ["windows-x64"] = "A-Full-x64.zip", ["macos"] = "A.zip" };
    private static readonly Dictionary<string, string> ArmOnly = new() { ["windows-arm64"] = "A-Full-arm64.zip" };
    private static readonly Dictionary<string, string> MacOnly = new() { ["macos"] = "A.zip" };

    [Fact]
    public void X64PcTakesTheX64BuildWhateverTheChoice()
    {
        Assert.Equal(("A-x64.exe", false), Platform.Pick(Both, osIsArm64: false, preferX64: false));
        Assert.Equal(("A-x64.exe", false), Platform.Pick(Both, osIsArm64: false, preferX64: true));
        Assert.Equal(("A-Full-x64.zip", false), Platform.Pick(X64Only, osIsArm64: false, preferX64: false));
        Assert.Null(Platform.Pick(ArmOnly, osIsArm64: false, preferX64: false));
        Assert.Null(Platform.Pick(ArmOnly, osIsArm64: false, preferX64: true));
        Assert.Null(Platform.Pick(MacOnly, osIsArm64: false, preferX64: false));
    }

    [Fact]
    public void Arm64PcPrefersNativeAndFallsBackToEmulatedX64()
    {
        Assert.Equal(("A-arm64.exe", false), Platform.Pick(Both, osIsArm64: true, preferX64: false));
        Assert.Equal(("A-Full-arm64.zip", false), Platform.Pick(ArmOnly, osIsArm64: true, preferX64: false));
        // No ARM64 build (PDF Tools Full, NDI Tools Full): the x64 build, emulated — not "No Windows build".
        Assert.Equal(("A-Full-x64.zip", true), Platform.Pick(X64Only, osIsArm64: true, preferX64: false));
        Assert.Null(Platform.Pick(MacOnly, osIsArm64: true, preferX64: false));
    }

    [Fact]
    public void Arm64PcSetToX64TakesTheX64BuildWhenThereIsOne()
    {
        Assert.Equal(("A-x64.exe", true), Platform.Pick(Both, osIsArm64: true, preferX64: true));
        Assert.Equal(("A-Full-x64.zip", true), Platform.Pick(X64Only, osIsArm64: true, preferX64: true));
        // No x64 build: the ARM64 one, native.
        Assert.Equal(("A-Full-arm64.zip", false), Platform.Pick(ArmOnly, osIsArm64: true, preferX64: true));
        Assert.Null(Platform.Pick(MacOnly, osIsArm64: true, preferX64: true));
    }

    [Fact]
    public void ChoiceOnlyWhenBothBuildsExistOnArm64()
    {
        Assert.True(Platform.CanChoose(Both, osIsArm64: true));
        Assert.False(Platform.CanChoose(Both, osIsArm64: false));
        Assert.False(Platform.CanChoose(X64Only, osIsArm64: true));
        Assert.False(Platform.CanChoose(ArmOnly, osIsArm64: true));
        Assert.False(Platform.CanChoose(MacOnly, osIsArm64: true));
    }

    [Fact]
    public void CatalogAppAppliesTheSlotsChoicePerEdition()
    {
        var app = new CatalogApp
        {
            Id = "ndi",
            Variants = new()
            {
                new AppVariant { Id = "standard", Label = "Light", Assets = new(Both) },
                new AppVariant { Id = "full", Label = "Full", Assets = new(X64Only) },
            },
        };
        // The pick itself is covered above; here, that the slot key ("<id>" / "<id>@<variant>") selects the choice.
        var slots = new List<string> { "ndi@full" };
        Assert.Equal(app.WindowsPick("full", slots), Platform.Pick(X64Only, Platform.OsIsArm64, preferX64: true));
        Assert.Equal(app.WindowsPick("standard", slots), Platform.Pick(Both, Platform.OsIsArm64, preferX64: false));
        Assert.Equal(app.WindowsPick("standard", new[] { "ndi" }), Platform.Pick(Both, Platform.OsIsArm64, preferX64: true));
        Assert.Equal(app.CanChooseX64("standard"), Platform.CanChoose(Both, Platform.OsIsArm64));
        Assert.False(app.CanChooseX64("full"));
    }
}

public class PeArchTests
{
    /// <summary>A minimal PE header: "MZ", e_lfanew at 0x3C, "PE\0\0" at that offset, then the Machine field.</summary>
    private static byte[] Pe(ushort machine, int peOffset = 0x80, int length = 0x200)
    {
        var b = new byte[length];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(peOffset).CopyTo(b, 0x3C);
        b[peOffset] = (byte)'P'; b[peOffset + 1] = (byte)'E';
        b[peOffset + 4] = (byte)(machine & 0xFF); b[peOffset + 5] = (byte)(machine >> 8);
        return b;
    }

    [Fact]
    public void ReadsTheMachine()
    {
        Assert.Equal(PeArch.X64, PeArch.Machine(Pe(0x8664)));
        Assert.Equal(PeArch.Arm64, PeArch.Machine(Pe(0xAA64)));
        Assert.Equal((ushort)0x014C, PeArch.Machine(Pe(0x014C)));   // i386: not either build
        Assert.Equal(PeArch.Arm64, PeArch.Machine(Pe(0xAA64, peOffset: 0x118)));
    }

    [Fact]
    public void RejectsNonPeInput()
    {
        Assert.Null(PeArch.Machine(Array.Empty<byte>()));
        Assert.Null(PeArch.Machine(System.Text.Encoding.ASCII.GetBytes("#!/bin/sh\necho this is not an exe at all, just text padding it out\n")));
        var noSignature = Pe(0x8664);
        noSignature[0x80] = (byte)'X';
        Assert.Null(PeArch.Machine(noSignature));
        var wildOffset = Pe(0x8664);
        BitConverter.GetBytes(-4).CopyTo(wildOffset, 0x3C);
        Assert.Null(PeArch.Machine(wildOffset));
        BitConverter.GetBytes(int.MaxValue).CopyTo(wildOffset, 0x3C);
        Assert.Null(PeArch.Machine(wildOffset));
    }

    [Fact]
    public void RejectsShortInput()
    {
        Assert.Null(PeArch.Machine(Pe(0x8664)[..0x20]));                    // cut inside the DOS header
        Assert.Null(PeArch.Machine(Pe(0x8664)[..0x84]));                    // cut before the Machine field
        Assert.Equal(PeArch.X64, PeArch.Machine(Pe(0x8664)[..0x86]));       // exactly long enough
    }

    [Fact]
    public void ReadsFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jbtt-pearch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string Write(string name, byte[] bytes) { var p = Path.Combine(dir, name); File.WriteAllBytes(p, bytes); return p; }
            Assert.Equal(PeArch.X64, PeArch.OfFile(Write("x64.exe", Pe(0x8664))));
            Assert.Equal(PeArch.Arm64, PeArch.OfFile(Write("arm64.exe", Pe(0xAA64, peOffset: 0x1000, length: 0x1100))));
            Assert.Null(PeArch.OfFile(Write("short.exe", new byte[] { (byte)'M', (byte)'Z', 0, 0 })));
            Assert.Null(PeArch.OfFile(Write("cut.exe", Pe(0x8664)[..0x82])));
            Assert.Null(PeArch.OfFile(Write("text.exe", System.Text.Encoding.ASCII.GetBytes(new string('x', 300)))));
            Assert.Null(PeArch.OfFile(Path.Combine(dir, "missing.exe")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
