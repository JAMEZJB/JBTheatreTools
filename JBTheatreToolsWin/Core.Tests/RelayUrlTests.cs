using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>F2: the invisible relay override is honoured only for https on a jamesbreedon.com host; a
/// rejected/absent override falls back to the catalog URL.</summary>
public class RelayUrlTests
{
    [Theory]
    [InlineData("https://jbtheatretools.jamesbreedon.com/ghapi")]
    [InlineData("https://jamesbreedon.com/ghapi")]
    [InlineData("https://relay2.jamesbreedon.com/ghapi")]
    public void AllowsHttpsJamesbreedonHosts(string url) => Assert.True(RelayUrl.IsAllowed(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://jbtheatretools.jamesbreedon.com/ghapi")]   // not https → cleartext passphrase
    [InlineData("https://evil.example/ghapi")]                     // wrong host
    [InlineData("https://jamesbreedon.com.evil.com/ghapi")]        // suffix trick
    [InlineData("https://eviljamesbreedon.com/ghapi")]             // no dot before the domain
    [InlineData("not a url")]
    [InlineData("ftp://jamesbreedon.com/x")]
    public void RejectsEverythingElse(string? url) => Assert.False(RelayUrl.IsAllowed(url));

    [Fact]
    public void ResolvePrefersAllowedOverrideElseCatalog()
    {
        Assert.Equal("https://relay2.jamesbreedon.com/ghapi",
            RelayUrl.Resolve("https://relay2.jamesbreedon.com/ghapi", "https://jbtheatretools.jamesbreedon.com/ghapi"));
        // A disallowed override is ignored → the catalog URL stands.
        Assert.Equal("https://jbtheatretools.jamesbreedon.com/ghapi",
            RelayUrl.Resolve("http://evil.example", "https://jbtheatretools.jamesbreedon.com/ghapi"));
        Assert.Equal("https://jbtheatretools.jamesbreedon.com/ghapi",
            RelayUrl.Resolve(null, "https://jbtheatretools.jamesbreedon.com/ghapi"));
    }
}
