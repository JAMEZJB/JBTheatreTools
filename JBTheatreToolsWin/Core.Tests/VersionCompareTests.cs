using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>F13: one overflow-safe version comparator. The key regression is a long numeric tag that a
/// 32-bit int would wrap (and then compare as OLDER, silently hiding an update).</summary>
public class VersionCompareTests
{
    [Fact]
    public void NumericNotStringCompare()
    {
        Assert.True(VersionCompare.IsNewer("v1.10.0", "v1.9.9"));   // string compare would say 1.10 < 1.9
        Assert.False(VersionCompare.IsNewer("1.2", "1.2.0"));       // same version, different segment count
        Assert.False(VersionCompare.IsNewer("1.0.0", "1.0.0"));
        Assert.False(VersionCompare.IsNewer("1.0.0", "1.0.1"));     // never a downgrade
    }

    [Fact]
    public void LongNumericTagDoesNotWrap()
    {
        // 14-digit segment overflows Int32 (~2.1e9); the old int parser wrapped and read this as older.
        Assert.True(VersionCompare.IsNewer("v20260906123456", "v1.0.0"));
        Assert.False(VersionCompare.IsNewer("v1.0.0", "v20260906123456"));
        Assert.True(VersionCompare.IsNewer("v20260906123457", "v20260906123456"));
    }

    [Fact]
    public void LeadingVAndEqual()
    {
        Assert.True(VersionCompare.Equal("v1.2.3", "1.2.3"));
        Assert.Equal("1.2.3", VersionCompare.Norm("V1.2.3"));
    }

    [Fact]
    public void DateTaggedBuildsCompareByDate()
    {
        // Convert ships date-style rolling tags (build-YYYYMMDD), not semver — the digit run inside the
        // segment must order them so a fortnightly build is seen as newer.
        Assert.True(VersionCompare.IsNewer("build-20260926", "build-20260912"));
        Assert.False(VersionCompare.IsNewer("build-20260912", "build-20260926"));
        Assert.False(VersionCompare.IsNewer("build-20260912", "build-20260912"));
    }
}
