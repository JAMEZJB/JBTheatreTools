using JBTheatreTools;
using Xunit;

/// <summary>The relay-served, editable "New in" lines: parsing, sanitising, overlay semantics and URL derivation.</summary>
public class WhatsNewNotesTests
{
    [Fact]
    public void ParsesDocumentAndOverlays()
    {
        var n = WhatsNewNotes.Parse("""{"schemaVersion":1,"apps":{"a":{"whatsNew":"Fresh line","whatsNewVersion":"v2.0.0"}}}""");
        Assert.Equal(("Fresh line", "v2.0.0"), n.Resolve("a", "bundled", "v1"));
        Assert.Equal(("bundled", "v1"), n.Resolve("b", "bundled", "v1"));   // not in the document → bundled stands
    }

    [Fact]
    public void EmptyLineHidesRowLine()
    {
        var n = WhatsNewNotes.Parse("""{"apps":{"a":{"whatsNew":"   "}}}""");
        Assert.Equal((null, null), n.Resolve("a", "bundled", "v1"));
    }

    [Fact]
    public void MalformedEntriesAreSkippedNotFatal()
    {
        var n = WhatsNewNotes.Parse("""{"apps":{"a":"nope","b":{"whatsNewVersion":"v1"},"c":{"whatsNew":"ok","whatsNewVersion":7}}}""");
        Assert.Null(n["a"]); Assert.Null(n["b"]);
        Assert.Equal("ok", n["c"]!.WhatsNew); Assert.Null(n["c"]!.WhatsNewVersion);
    }

    [Theory]
    [InlineData("<html>redirected</html>")]
    [InlineData("""{"notes":{}}""")]
    [InlineData("[]")]
    [InlineData("""{"schemaVersion":2,"apps":{}}""")]
    public void RejectsWrongShapeAndNewerSchema(string json) =>
        Assert.ThrowsAny<Exception>(() => WhatsNewNotes.Parse(json));

    [Fact]
    public void CleansControlCharsWhitespaceAndLength()
    {
        Assert.Equal("ab c", WhatsNewNotes.Clean("  a\0b \n\t c  ", 100));
        Assert.Null(WhatsNewNotes.Clean(" \a ", 100));
        var capped = WhatsNewNotes.Clean(new string('x', 200), 160)!;
        Assert.Equal(161, capped.Length); Assert.EndsWith("…", capped);
    }

    [Fact]
    public void NotesUrlIsTheRelayOriginNotTheApiRoot()
    {
        Assert.Equal("https://jbtheatretools.jamesbreedon.com/notes/whats-new.json",
            WhatsNewNotes.UrlFor("https://jbtheatretools.jamesbreedon.com/ghapi")!.ToString());
        Assert.Equal("https://x.jamesbreedon.com:8443/notes/whats-new.json",
            WhatsNewNotes.UrlFor("https://x.jamesbreedon.com:8443/ghapi/")!.ToString());
        Assert.Null(WhatsNewNotes.UrlFor("not a url"));
    }
}
