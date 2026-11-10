using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>How far the releases list is read: one page unless a long run of development builds has pushed the last
/// full release off it. No network: the pages are handed in.</summary>
public class ReleasePagingTests
{
    private sealed record Rel(string Tag, bool Pre = false, bool Draft = false);

    private static List<Rel> DevBuilds(int from, int to) =>
        Enumerable.Range(from, to - from + 1).Reverse().Select(n => new Rel($"v2.0.0-dev.{n}", Pre: true)).ToList();

    private sealed class Pages(params List<Rel>[] pages)
    {
        public List<int> Asked { get; } = new();
        public int FailFrom { get; set; } = int.MaxValue;
        public Task<List<Rel>> Fetch(int page)
        {
            Asked.Add(page);
            if (page >= FailFrom) throw new HttpRequestException("down");
            return Task.FromResult(page <= pages.Length ? pages[page - 1] : new List<Rel>());
        }
    }

    private static Task<List<Rel>> Collect(Pages pages, int maxPages = ReleasePaging.MaxPages) =>
        ReleasePaging.CollectAsync(pages.Fetch, r => r.Tag, r => r.Draft, r => r.Pre, maxPages: maxPages);

    private static Rel? Latest(IEnumerable<Rel> releases) =>
        VersionCompare.PickLatest(releases, r => r.Tag, r => r.Pre, devChannel: false);

    [Fact]
    public async Task OnePageIsEnoughWhenItHoldsAFullRelease()
    {
        var first = DevBuilds(2, 50).Append(new Rel("v1.9.0")).ToList();   // a full page of 50
        var pages = new Pages(first, new() { new Rel("v1.8.0") });
        var all = await Collect(pages);
        Assert.Equal(new[] { 1 }, pages.Asked);
        Assert.Equal(50, all.Count);
    }

    [Fact]
    public async Task AShortFirstPageIsNeverFollowed()
    {
        var pages = new Pages(DevBuilds(1, 12));
        var all = await Collect(pages);
        Assert.Equal(new[] { 1 }, pages.Asked);
        Assert.Equal(12, all.Count);
    }

    [Fact]
    public async Task MoreThanAPageOfDevelopmentBuildsStillFindsTheLastFullRelease()
    {
        // 60 development builds since v1.9.0: the first page is all development builds.
        var second = DevBuilds(1, 10).Concat(new[] { new Rel("v1.9.0"), new Rel("v1.8.0") }).ToList();
        var pages = new Pages(DevBuilds(11, 60), second);
        var all = await Collect(pages);
        Assert.Equal(new[] { 1, 2 }, pages.Asked);
        Assert.Equal(62, all.Count);
        Assert.Equal("v1.9.0", Latest(all)?.Tag);
        Assert.Null(Latest(all.Take(50)));   // the first page alone has no release for a launcher not on dev builds
    }

    [Fact]
    public async Task APreReleaseOrADraftDoesNotCountAsAFullRelease()
    {
        var first = DevBuilds(3, 50).Concat(new[] { new Rel("v1.9.0-rc.1", Pre: true), new Rel("v1.9.0", Draft: true) }).ToList();
        var pages = new Pages(first, new() { new Rel("v1.8.0") });
        var all = await Collect(pages);
        Assert.Equal(new[] { 1, 2 }, pages.Asked);
        Assert.DoesNotContain(all, r => r.Draft);
        Assert.Equal("v1.8.0", all[^1].Tag);
    }

    [Fact]
    public async Task ReadingStopsAtThePageLimit()
    {
        var pages = new Pages(Enumerable.Range(0, 5).Select(p => DevBuilds(p * 50 + 1, p * 50 + 50)).ToArray());
        var all = await Collect(pages, maxPages: 3);
        Assert.Equal(new[] { 1, 2, 3 }, pages.Asked);
        Assert.Equal(150, all.Count);
    }

    [Fact]
    public async Task AFailedLaterPageKeepsWhatTheFirstGave()
    {
        var pages = new Pages(DevBuilds(1, 50), new() { new Rel("v1.9.0") }) { FailFrom = 2 };
        var all = await Collect(pages);
        Assert.Equal(new[] { 1, 2 }, pages.Asked);
        Assert.Equal(50, all.Count);
    }

    [Fact]
    public async Task AFailedFirstPageIsAnError()
    {
        var pages = new Pages() { FailFrom = 1 };
        await Assert.ThrowsAsync<HttpRequestException>(() => Collect(pages));
    }

    [Fact]
    public async Task ATagThatComesBackOnTheNextPageIsListedOnce()
    {
        // A release published between the two reads moves the list down one.
        var second = DevBuilds(10, 11).Append(new Rel("v1.9.0")).ToList();
        var pages = new Pages(DevBuilds(11, 60), second);
        var all = await Collect(pages);
        Assert.Single(all, r => r.Tag == "v2.0.0-dev.11");
        Assert.Equal(52, all.Count);
    }
}
