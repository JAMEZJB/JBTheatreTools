using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>
/// Verifies the passphrase reduction rule (case-insensitive; spaces &amp; punctuation ignored). Uses only
/// SYNTHETIC inputs — the real suite phrases are secrets and must never appear in this public repo. The
/// server applies the same rule to its private allow-list.
/// </summary>
public class PassphraseTests
{
    [Theory]
    [InlineData("HELLO", "hello")]
    [InlineData("HeLLo", "hello")]
    [InlineData("hello world", "helloworld")]
    [InlineData("  hello,  world!  ", "helloworld")]
    [InlineData("don't panic", "dontpanic")]
    [InlineData("a-b_c.d", "abcd")]
    [InlineData("Studio 54", "studio54")]
    [InlineData("!!! ,.- ", "")]
    public void NormalizesConsistently(string input, string expected)
        => Assert.Equal(expected, Passphrase.Normalize(input));

    [Fact]
    public void CaseAndSpacingVariantsCollapseToOneToken()
    {
        string[] variants = { "Foo Bar Baz", "foo bar baz", "FOOBARBAZ", "  foo,bar.baz  ", "Foo-Bar-Baz" };
        var normalized = new HashSet<string>();
        foreach (var v in variants) normalized.Add(Passphrase.Normalize(v));
        Assert.Single(normalized);
        Assert.Contains("foobarbaz", normalized);
    }
}
