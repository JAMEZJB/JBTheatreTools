using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>v1.30 feature logic: sizes, ages, find &amp; filter, release notes, scheduled checks / notifications,
/// activity history, setup profiles, the disk-space check, diagnostics and the launcher's what's-new. The Swift and
/// Kotlin launchers carry the same cases, so the three stay word-for-word identical.</summary>
public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(999, "999 bytes")]
    [InlineData(1000, "1.0 KB")]
    [InlineData(1049, "1.0 KB")]
    [InlineData(1050, "1.1 KB")]
    [InlineData(9_949, "9.9 KB")]
    [InlineData(9_950, "10 KB")]
    [InlineData(12_400_000, "12 MB")]
    [InlineData(4_200_000, "4.2 MB")]
    [InlineData(450_000_000, "450 MB")]
    [InlineData(999_499, "999 KB")]
    [InlineData(999_500, "1.0 MB")]
    [InlineData(1_234_000_000, "1.2 GB")]
    [InlineData(5_000_000_000_000_000, "5000 TB")]
    [InlineData(-5, "0 bytes")]
    public void Formats(long bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void SumIgnoresUnknownAndSaturates()
    {
        Assert.Equal(30, ByteSize.Sum(new long[] { 10, -1, 0, 20 }));
        Assert.Equal(long.MaxValue, ByteSize.Sum(new[] { long.MaxValue - 1, 5L }));
    }
}

public class RelativeAgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-3, "today")]
    [InlineData(0, "today")]
    [InlineData(0.9, "today")]
    [InlineData(1, "yesterday")]
    [InlineData(1.99, "yesterday")]
    [InlineData(2, "2 days ago")]
    [InlineData(6.5, "6 days ago")]
    [InlineData(7, "1 week ago")]
    [InlineData(13, "1 week ago")]
    [InlineData(14, "2 weeks ago")]
    [InlineData(29, "4 weeks ago")]
    [InlineData(30, "1 month ago")]
    [InlineData(59, "1 month ago")]
    [InlineData(60, "2 months ago")]
    [InlineData(364, "12 months ago")]
    [InlineData(365, "1 year ago")]
    [InlineData(730, "2 years ago")]
    public void Describes(double daysAgo, string expected) =>
        Assert.Equal(expected, RelativeAge.Describe(Now.AddDays(-daysAgo), Now));

    [Fact]
    public void ParsesGitHubTimestamps()
    {
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 8, 30, 0, TimeSpan.Zero), RelativeAge.ParseIso("2026-09-12T08:30:00Z"));
        Assert.Null(RelativeAge.ParseIso(null));
        Assert.Null(RelativeAge.ParseIso(""));
        Assert.Null(RelativeAge.ParseIso("not a date"));
    }
}

public class AppFilterTests
{
    [Fact]
    public void EveryTokenMustMatchSomewhere()
    {
        Assert.True(AppFilter.MatchesQuery("dmx", "DMX Tools", "Art-Net monitor", "Show control", "dmxtools"));
        Assert.True(AppFilter.MatchesQuery("  tools   DMX ", "DMX Tools", "", null, "dmxtools"));
        Assert.True(AppFilter.MatchesQuery("show art-net", "DMX Tools", "Art-Net monitor", "Show control", "dmxtools"));
        Assert.False(AppFilter.MatchesQuery("dmx cisco", "DMX Tools", "Art-Net monitor", "Show control", "dmxtools"));
        Assert.True(AppFilter.MatchesQuery("", "anything"));
        Assert.True(AppFilter.MatchesQuery(null, "anything"));
        Assert.False(AppFilter.MatchesQuery("x", null, ""));
    }

    [Fact]
    public void StatusFilters()
    {
        Assert.True(AppFilter.MatchesStatus(StatusFilter.All, false, false, false));
        Assert.True(AppFilter.MatchesStatus(StatusFilter.Installed, true, false, false));
        Assert.False(AppFilter.MatchesStatus(StatusFilter.Installed, false, false, true));
        Assert.True(AppFilter.MatchesStatus(StatusFilter.Updates, true, true, false));
        Assert.False(AppFilter.MatchesStatus(StatusFilter.Updates, true, false, false));
        Assert.True(AppFilter.MatchesStatus(StatusFilter.NotInstalled, false, false, true));
        Assert.False(AppFilter.MatchesStatus(StatusFilter.NotInstalled, false, false, false));
        Assert.False(AppFilter.MatchesStatus(StatusFilter.NotInstalled, true, false, true));
    }

    [Fact]
    public void ActiveWhenNarrowed()
    {
        Assert.False(AppFilter.IsActive("  ", StatusFilter.All));
        Assert.True(AppFilter.IsActive("a", StatusFilter.All));
        Assert.True(AppFilter.IsActive(null, StatusFilter.Updates));
    }
}

public class ReleaseNotesTextTests
{
    [Fact]
    public void EmptyBody() { Assert.Equal(ReleaseNotesText.Empty, ReleaseNotesText.Plain(null)); Assert.Equal(ReleaseNotesText.Empty, ReleaseNotesText.Plain(" \n\n ")); }

    [Fact]
    public void GitHubStyleBody()
    {
        var md = "## What's new\r\n\r\n- **Faster** start-up (`--fast`)\n* Fixed [the crash](https://x.y/z) on _launch_\n  - nested ~~old~~ item\n\n\n\n### Notes\n> Quoted *text*\n\n---\n1. Step one\n![shot](a.png)\n<!-- hidden\ncomment -->Done &amp; dusted <br/>";
        var expected = "What's new\n\n• Faster start-up (--fast)\n• Fixed the crash on launch\n  • nested old item\n\nNotes\nQuoted text\n\n1. Step one\nDone & dusted";
        Assert.Equal(expected, ReleaseNotesText.Plain(md));
    }

    [Fact]
    public void FencedCodeIsVerbatim()
    {
        var md = "Run:\n```bash\n**not bold** `x`\n```\nafter";
        Assert.Equal("Run:\n**not bold** `x`\nafter", ReleaseNotesText.Plain(md));
    }

    [Fact]
    public void SnakeCaseAndMathSurvive()
    {
        Assert.Equal("use install_to_applications and 2 * 3 * 4", ReleaseNotesText.Plain("use install_to_applications and 2 * 3 * 4"));
        Assert.Equal("a_b_c", ReleaseNotesText.Plain("a_b_c"));
        Assert.Equal("Task done", ReleaseNotesText.Plain("- [x] Task done").TrimStart('•', ' '));
        Assert.Equal("see https://x.y", ReleaseNotesText.Plain("see <https://x.y>"));
        Assert.Equal("1*2", ReleaseNotesText.Plain(@"1\*2"));
    }

    [Fact]
    public void CapsLength()
    {
        var s = ReleaseNotesText.Plain(new string('a', 30_000));
        Assert.Equal(ReleaseNotesText.MaxLength + 2, s.Length);
        Assert.EndsWith("\n…", s);
    }
}

public class UpdatePolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Intervals()
    {
        Assert.Null(UpdatePolicy.Interval("off"));
        Assert.Equal(TimeSpan.FromHours(1), UpdatePolicy.Interval("1h"));
        Assert.Equal(TimeSpan.FromHours(4), UpdatePolicy.Interval("4h"));
        Assert.Equal(TimeSpan.FromHours(12), UpdatePolicy.Interval("12h"));
        Assert.Equal(TimeSpan.FromHours(24), UpdatePolicy.Interval("24h"));
        Assert.Equal(TimeSpan.FromHours(4), UpdatePolicy.Interval("garbage"));
        Assert.Equal(TimeSpan.FromHours(4), UpdatePolicy.Interval(null));
        Assert.Contains(UpdatePolicy.Intervals, i => i.Raw == UpdatePolicy.DefaultInterval);
    }

    [Fact]
    public void DueChecks()
    {
        Assert.False(UpdatePolicy.IsDue(null, T0, "off"));
        Assert.True(UpdatePolicy.IsDue(null, T0, "4h"));
        Assert.False(UpdatePolicy.IsDue(T0.AddHours(-3.9), T0, "4h"));
        Assert.True(UpdatePolicy.IsDue(T0.AddHours(-4), T0, "4h"));
        Assert.True(UpdatePolicy.IsDue(T0.AddHours(1), T0, "4h"));   // clock went backwards
        Assert.False(UpdatePolicy.IsDue(T0.AddMinutes(-59), T0, "1h"));
    }

    [Fact]
    public void NotifiesOnlyNewAndPrunes()
    {
        var pending = new[] { new UpdatePolicy.Pending("dmx", "DMX Tools", "v1.2.0"), new UpdatePolicy.Pending("psn", "PSN Tools", "0.4.1") };
        var (toNotify, notified) = UpdatePolicy.Notify(pending, new[] { "dmx 1.2.0", "gone 9.9.9" });
        Assert.Single(toNotify);
        Assert.Equal("psn", toNotify[0].Id);
        Assert.Equal(new[] { "dmx 1.2.0", "psn 0.4.1" }, notified);
        var (again, _) = UpdatePolicy.Notify(pending, notified);
        Assert.Empty(again);
        var (newer, _) = UpdatePolicy.Notify(new[] { new UpdatePolicy.Pending("dmx", "DMX Tools", "v1.3.0") }, notified);
        Assert.Single(newer);
    }

    [Fact]
    public void AFailedCheckDoesNotReannounce()
    {
        var pending = new[] { new UpdatePolicy.Pending("dmx", "DMX Tools", "v1.2.0"), new UpdatePolicy.Pending("psn", "PSN Tools", "0.4.1") };
        var notified = new List<string> { "dmx 1.2.0", "psn 0.4.1" };
        var (_, afterFailure) = UpdatePolicy.Notify(pending.Skip(1), notified);   // dmx's check failed this time
        Assert.Equal(new[] { "psn 0.4.1", "dmx 1.2.0" }, UpdatePolicy.Remembered(afterFailure, notified, new HashSet<string> { "dmx" }));
        Assert.Equal(new[] { "psn 0.4.1" }, UpdatePolicy.Remembered(afterFailure, notified, new HashSet<string>()));
        var kept = UpdatePolicy.Remembered(afterFailure, notified, new HashSet<string> { "dmx" });
        Assert.Empty(UpdatePolicy.Notify(pending, kept).ToNotify);
    }

    [Fact]
    public void Wording()
    {
        var items = new List<UpdatePolicy.Pending>
        {
            new("a", "A", "1.0.0"), new("b", "B", "v2.0"), new("c", "Convert", "build-20260912"), new("d", "D", "1"), new("e", "E", "1"),
        };
        Assert.Equal("A v1.0.0, B v2.0, Convert build-20260912 and 2 more", UpdatePolicy.NotificationBody(items));
        Assert.Equal("A v1.0.0", UpdatePolicy.NotificationBody(items.Take(1).ToList()));
        Assert.Equal("Update available", UpdatePolicy.NotificationTitle(1));
        Assert.Equal("Updates available", UpdatePolicy.NotificationTitle(2));
        Assert.Equal("Updated DMX Tools to v1.2.0", UpdatePolicy.AutoUpdateSummary(new[] { ("DMX Tools", "1.2.0") }));
        Assert.Equal("Updated 2 apps", UpdatePolicy.AutoUpdateSummary(new[] { ("A", "1"), ("B", "2") }));
    }

    [Fact]
    public void DisplayVersions()
    {
        Assert.Equal("v1.2.0", VersionCompare.Display("1.2.0"));
        Assert.Equal("v1.2.0", VersionCompare.Display("V1.2.0"));
        Assert.Equal("build-20260912", VersionCompare.Display(" build-20260912 "));
    }
}

public class ActivityHistoryTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 25, 13, 2, 0, TimeSpan.Zero);

    [Fact]
    public void Classifies()
    {
        Assert.Equal("install", ActivityHistory.ActionFor(null, "v1.0.0"));
        Assert.Equal("update", ActivityHistory.ActionFor("v1.0.0", "v1.1.0"));
        Assert.Equal("downgrade", ActivityHistory.ActionFor("v1.1.0", "v1.0.0"));
        Assert.Equal("reinstall", ActivityHistory.ActionFor("v1.1.0", "1.1.0"));
    }

    [Fact]
    public void RoundTripsAndCaps()
    {
        var list = new List<ActivityEvent>();
        for (int i = 0; i < ActivityHistory.Cap + 5; i++)
            list = ActivityHistory.Append(list, new ActivityEvent(T.AddMinutes(i), "dmx", "DMX Tools", "update", "1.0", $"1.{i}"));
        Assert.Equal(ActivityHistory.Cap, list.Count);
        Assert.Equal("1.5", list[0].To);                       // the five oldest dropped
        var back = ActivityHistory.Parse(ActivityHistory.Serialize(list));
        Assert.Equal(list, back);
    }

    [Fact]
    public void NoteTrimmedAndCapped()
    {
        var e = ActivityHistory.Append(new List<ActivityEvent>(), new ActivityEvent(T, "a", "A", "failed", Note: "  " + new string('x', 300) + " "))[0];
        Assert.Equal(ActivityHistory.MaxNoteLength + 1, e.Note!.Length);
        Assert.EndsWith("…", e.Note);
        Assert.Null(ActivityHistory.Append(new List<ActivityEvent>(), new ActivityEvent(T, "a", "A", "failed", Note: "  "))[0].Note);
    }

    [Fact]
    public void DamagedFilesReadAsEmptyOrSkipBadEntries()
    {
        Assert.Empty(ActivityHistory.Parse("{not json"));
        Assert.Empty(ActivityHistory.Parse("{}"));
        Assert.Empty(ActivityHistory.Parse(null));
        var one = ActivityHistory.Parse("[{\"at\":\"2026-09-25T13:02:00Z\",\"app\":\"a\",\"action\":\"install\",\"to\":\"1.0\"},{\"app\":\"b\"},7]");
        Assert.Single(one);
        Assert.Equal("a", one[0].Name);                          // name falls back to the id
    }

    [Fact]
    public void Describes()
    {
        Assert.Equal("Installed DMX Tools v1.0.0", ActivityHistory.Describe(new(T, "d", "DMX Tools", "install", null, "1.0.0")));
        Assert.Equal("Updated DMX Tools v1.0.0 → v1.1.0", ActivityHistory.Describe(new(T, "d", "DMX Tools", "update", "v1.0.0", "v1.1.0")));
        Assert.Equal("Rolled back DMX Tools v1.1.0 → v1.0.0", ActivityHistory.Describe(new(T, "d", "DMX Tools", "downgrade", "1.1.0", "1.0.0")));
        Assert.Equal("Reinstalled DMX Tools v1.0.0", ActivityHistory.Describe(new(T, "d", "DMX Tools", "reinstall", "1.0.0", "1.0.0")));
        Assert.Equal("Removed DMX Tools v1.0.0", ActivityHistory.Describe(new(T, "d", "DMX Tools", "uninstall", "1.0.0")));
        Assert.Equal("Removed DMX Tools", ActivityHistory.Describe(new(T, "d", "DMX Tools", "uninstall")));
        Assert.Equal("Couldn't install DMX Tools v1.1.0: offline", ActivityHistory.Describe(new(T, "d", "DMX Tools", "failed", null, "1.1.0", "offline")));
    }

    [Fact]
    public void When()
    {
        var now = new DateTime(2026, 9, 25, 18, 0, 0);
        Assert.Equal("Today 14:02", ActivityHistory.When(new DateTime(2026, 9, 25, 14, 2, 0), now));
        Assert.Equal("Yesterday 09:10", ActivityHistory.When(new DateTime(2026, 9, 24, 9, 10, 0), now));
        Assert.Equal("2 Sep 2026 07:05", ActivityHistory.When(new DateTime(2026, 9, 2, 7, 5, 0), now));
    }
}

public class SetupProfileTests
{
    private static readonly List<SetupPlanner.CatalogEntry> Catalog = new()
    {
        new("dmx", "DMX Tools", Array.Empty<(string, string)>()),
        new("ndi", "NDI Tools", new[] { ("standard", "Light"), ("full", "Full") }),
        new("psn", "PSN Tools", Array.Empty<(string, string)>()),
    };

    private static SetupProfile Sample() => new()
    {
        CreatedAt = "2026-09-25T12:00:00Z", CreatedBy = "JB Theatre Tools 1.30.0 (Windows)",
        Apps = new()
        {
            new("dmx", null, "v1.2.0", true),
            new("ndi", "standard", "v2.0.0", false),
            new("ndi", "full", "v2.0.0", false),
            new("psn", null, "0.4.1", false),
            new("gone", null, "1.0", false),
            new("ndi", "ultra", "1.0", false),
        },
        AppLayout = new(new() { "dmx" }, new() { "psn" }, new() { "ndi", "dmx" }, new() { "Networking" }, new()),
    };

    [Fact]
    public void RoundTrips()
    {
        var p = SetupProfile.Parse(Sample().Serialize());
        Assert.Equal(Sample().Apps, p.Apps);
        Assert.Equal("JB Theatre Tools 1.30.0 (Windows)", p.CreatedBy);
        Assert.Equal(new[] { "dmx" }, p.AppLayout!.Pinned);
        Assert.Equal(new[] { "ndi", "dmx" }, p.AppLayout.Order);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"kind\":\"something-else\",\"schemaVersion\":1,\"apps\":[]}")]
    [InlineData("{\"kind\":\"jbtheatretools-setup\",\"apps\":[]}")]
    [InlineData("{\"kind\":\"jbtheatretools-setup\",\"schemaVersion\":2,\"apps\":[]}")]
    [InlineData("{\"kind\":\"jbtheatretools-setup\",\"schemaVersion\":1}")]
    [InlineData("not json")]
    public void RejectsForeignFiles(string json) => Assert.Throws<FormatException>(() => SetupProfile.Parse(json));

    [Fact]
    public void RejectsHugeFiles() =>
        Assert.Throws<FormatException>(() => SetupProfile.Parse(new string(' ', SetupProfile.MaxBytes + 1)));

    [Fact]
    public void ToleratesJunkEntries()
    {
        var p = SetupProfile.Parse("{\"kind\":\"jbtheatretools-setup\",\"schemaVersion\":1,\"apps\":[7,{\"id\":\"\"},{\"id\":\" dmx \",\"variant\":\"\",\"held\":\"yes\"}]}");
        Assert.Equal(new[] { new SetupProfile.Entry("dmx", null, null, false) }, p.Apps);
        Assert.Null(p.AppLayout);
    }

    [Fact]
    public void Plans()
    {
        var plan = SetupPlanner.Build(Sample(), Catalog, new HashSet<string> { "psn" });
        Assert.Equal(new[] { "DMX Tools", "NDI Tools", "NDI Tools (Full)" }, plan.ToInstall.Select(i => i.Label));
        Assert.Equal("v1.2.0", plan.ToInstall[0].Tag);          // held → its recorded version
        Assert.Null(plan.ToInstall[1].Tag);                      // not held → latest
        Assert.Null(plan.ToInstall[1].VariantId);                // the default edition's id normalises to null
        Assert.Equal("full", plan.ToInstall[2].VariantId);
        Assert.Equal(new[] { "PSN Tools" }, plan.AlreadyInstalled);
        Assert.Equal(new[] { "dmx" }, plan.HoldIds);
        Assert.Equal(2, plan.Skipped.Count);
        Assert.Contains("gone — not in this launcher's catalog", plan.Skipped);
        Assert.Contains("NDI Tools (ultra) — no such edition", plan.Skipped);
    }

    [Fact]
    public void AndroidSkipsNonDefaultEditions()
    {
        var plan = SetupPlanner.Build(Sample(), Catalog, new HashSet<string>(), supportsVariants: false);
        Assert.DoesNotContain(plan.ToInstall, i => i.VariantId != null);
        Assert.Contains("NDI Tools (Full) — not available here", plan.Skipped);
    }

    [Fact]
    public void HeldDevBuildsSkipWithoutDevelopmentBuilds()
    {
        var p = new SetupProfile
        {
            Apps = new()
            {
                new("dmx", null, "1.3.0-dev.2", true),        // held at a dev build (an export without the "v")
                new("psn", null, "v0.5.0-dev.1", false),      // not held: installs the latest, dev tag irrelevant
                new("ndi", "full", "v2.1.0-dev.4", true),
            },
        };
        var off = SetupPlanner.Build(p, Catalog, new HashSet<string> { "ndi@full" }, allowDevTags: false);
        Assert.Equal(new[] { "PSN Tools" }, off.ToInstall.Select(i => i.Label));
        Assert.Null(off.ToInstall[0].Tag);
        Assert.Empty(off.HoldIds);                               // skipped entries are never held
        Assert.Empty(off.AlreadyInstalled);                      // …even when that slot is already here
        Assert.Contains("DMX Tools v1.3.0-dev.2 — a development build (not switched on here)", off.Skipped);
        Assert.Contains("NDI Tools (Full) v2.1.0-dev.4 — a development build (not switched on here)", off.Skipped);
        Assert.DoesNotContain("DMX Tools", SetupPlanner.Summary(off).Split("Skipped:")[0]);

        var on = SetupPlanner.Build(p, Catalog, new HashSet<string> { "ndi@full" }, allowDevTags: true);
        Assert.Equal("1.3.0-dev.2", on.ToInstall.Single(i => i.AppId == "dmx").Tag);
        Assert.Equal(new[] { "dmx", "ndi" }, on.HoldIds);
        Assert.Equal(new[] { "NDI Tools (Full)" }, on.AlreadyInstalled);
        Assert.Empty(on.Skipped);
    }

    [Fact]
    public void DuplicateSlotsInstallOnce()
    {
        var p = new SetupProfile { Apps = new() { new("dmx", null, null, false), new("dmx", null, null, false) } };
        Assert.Single(SetupPlanner.Build(p, Catalog, new HashSet<string>()).ToInstall);
    }

    [Fact]
    public void Summary()
    {
        var plan = SetupPlanner.Build(Sample(), Catalog, new HashSet<string> { "psn" });
        var s = SetupPlanner.Summary(plan);
        Assert.StartsWith("Install 3 apps:\n  • DMX Tools v1.2.0 (held)\n  • NDI Tools\n  • NDI Tools (Full)", s);
        Assert.Contains("Already installed (left as they are): PSN Tools", s);
        Assert.Contains("Held at their versions: 1 app", s);
        Assert.Contains("Skipped:\n  • ", s);
        Assert.StartsWith("Nothing to install", SetupPlanner.Summary(SetupPlanner.Build(new SetupProfile(), Catalog, new HashSet<string>())));
    }

    [Fact]
    public void FileName() =>
        Assert.Equal("JB Theatre Tools setup 2026-09-25.json", SetupProfile.SuggestedFileName(new DateTime(2026, 9, 25, 23, 0, 0)));
}

public class DiskSpaceTests
{
    [Fact]
    public void Budgets()
    {
        Assert.Equal(3 * 100_000_000L + DiskSpace.Margin, DiskSpace.Required(100_000_000, "NDITools-Full-macOS-arm64.zip"));
        Assert.Equal(2 * 100_000_000L + DiskSpace.Margin, DiskSpace.Required(100_000_000, "DMXTools-Windows-x64.exe"));
        Assert.Equal(DiskSpace.Margin, DiskSpace.Required(0, "x.zip"));
        Assert.Equal(long.MaxValue, DiskSpace.Required(long.MaxValue / 2, "x.zip"));
    }

    [Fact]
    public void Shortfall()
    {
        Assert.Null(DiskSpace.Shortfall(1000, 1000));
        Assert.Null(DiskSpace.Shortfall(1000, -1));
        Assert.Equal("Not enough disk space — needs about 1.2 GB, 300 MB free.", DiskSpace.Shortfall(1_200_000_000, 300_000_000));
    }
}

public class DiagnosticsTests
{
    [Fact]
    public void BuildsWithoutSecrets()
    {
        var log = Enumerable.Range(1, 50).Select(i => $"line {i}").ToList();
        log.Add("oops Authorization: Bearer ghp_abcdefghijklmnop leaked");
        log.Add("pat github_pat_11ABCDEFG0123456789_abcdef here");
        var report = Diagnostics.Build(new Diagnostics.Info("1.30.0", "Windows 11", "x64", "Download server",
            "jbtheatretools.jamesbreedon.com", false, true, "Launcher only",
            new[] { new Diagnostics.AppLine("DMX Tools", "v1.1.0", "v1.2.0", "Update", true) }, log,
            new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero)));
        Assert.StartsWith("JB Theatre Tools diagnostics — 2026-09-25 12:00:00 UTC\nLauncher: v1.30.0\nSystem: Windows 11 (x64)\n", report);
        Assert.Contains("Downloads via: Download server (jbtheatretools.jamesbreedon.com)\n", report);
        Assert.Contains("Show lock: on · Development builds: off\n", report);
        Assert.Contains("  DMX Tools — installed v1.1.0, latest v1.2.0, Update, held\n", report);
        Assert.Contains("Recent log (40 lines):", report);
        Assert.DoesNotContain("line 12\n", report);
        Assert.Contains("line 13\n", report);
        Assert.DoesNotContain("ghp_abcdefghijklmnop", report);
        Assert.DoesNotContain("github_pat_11ABCDEFG", report);
        Assert.Contains("[redacted]", report);
    }

    [Fact]
    public void RedactsAuthorizationValues()
    {
        Assert.Equal("Basic [redacted]", Diagnostics.Redact("Basic c3VpdGU6cGFzcw=="));
        Assert.Equal("installed dmx v1.2.0", Diagnostics.Redact("installed dmx v1.2.0"));
        Assert.Equal("refresh: token rejected (token mode)", Diagnostics.Redact("refresh: token rejected (token mode)"));
    }
}

public class LauncherWhatsNewTests
{
    [Fact]
    public void OnlyAfterAnUpdate()
    {
        Assert.False(LauncherWhatsNew.ShouldShow(null, "1.30.0"));
        Assert.False(LauncherWhatsNew.ShouldShow("", "1.30.0"));
        Assert.False(LauncherWhatsNew.ShouldShow("1.30.0", "1.30.0"));
        Assert.False(LauncherWhatsNew.ShouldShow("1.31.0", "1.30.0"));
        Assert.True(LauncherWhatsNew.ShouldShow("1.29.1", "1.30.0"));
        Assert.True(LauncherWhatsNew.ShouldShow("1.30.0-dev.2", "1.30.0"));
        // Nothing recorded: an update only when the launcher was already in use (pre-1.30 recorded nothing).
        Assert.True(LauncherWhatsNew.ShouldShow(null, "1.30.0", existingInstall: true));
        Assert.True(LauncherWhatsNew.ShouldShow("", "1.30.0", existingInstall: true));
        Assert.False(LauncherWhatsNew.ShouldShow("1.30.0", "1.30.0", existingInstall: true));
    }
}
