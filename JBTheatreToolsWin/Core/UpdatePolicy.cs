namespace JBTheatreTools;

/// <summary>The rules behind the launcher's unattended behaviour — scheduled checks, update notifications and
/// automatic updates — kept pure so they're unit-tested and identical on every launcher.</summary>
public static class UpdatePolicy
{
    /// <summary>The "While open, check every" choices (settings value → label). The first is the default.</summary>
    public static readonly (string Raw, string Label)[] Intervals =
    {
        ("4h", "4 hours"), ("1h", "Hour"), ("12h", "12 hours"), ("24h", "Day"), ("off", "Off"),
    };
    public const string DefaultInterval = "4h";

    /// <summary>The check interval for a settings value; null = scheduled checks off. Unknown → the default.</summary>
    public static TimeSpan? Interval(string? raw) => (raw ?? DefaultInterval) switch
    {
        "off" => null,
        "1h" => TimeSpan.FromHours(1),
        "12h" => TimeSpan.FromHours(12),
        "24h" => TimeSpan.FromHours(24),
        _ => TimeSpan.FromHours(4),
    };

    /// <summary>True when a scheduled check should run now: scheduled checks are on and either nothing has been
    /// checked yet this session or the interval has passed since the last check (a clock that jumped backwards
    /// counts as due, so a wrong clock can't stall checks for good).</summary>
    public static bool IsDue(DateTimeOffset? lastCheck, DateTimeOffset now, string? raw)
    {
        var interval = Interval(raw);
        if (interval == null) return false;
        if (lastCheck == null) return true;
        var elapsed = now - lastCheck.Value;
        return elapsed < TimeSpan.Zero || elapsed >= interval.Value;
    }

    /// <summary>One app with an update on offer (not held).</summary>
    public sealed record Pending(string Id, string Name, string Version)
    {
        public string Key => $"{Id} {VersionCompare.Norm(Version)}";
    }

    /// <summary>Which pending updates are NEW (not notified before), and the notified set to store: the keys of
    /// everything currently pending — so the set never grows beyond what's on offer, and an update that goes
    /// away and comes back (a new version) is announced again.</summary>
    public static (List<Pending> ToNotify, List<string> Notified) Notify(IEnumerable<Pending> pending, IEnumerable<string> alreadyNotified)
    {
        var seen = new HashSet<string>(alreadyNotified, StringComparer.Ordinal);
        var list = pending.ToList();
        return (list.Where(p => !seen.Contains(p.Key)).ToList(), list.Select(p => p.Key).Distinct().ToList());
    }

    /// <summary>The keys to remember after a check: what's pending now, plus the earlier keys of apps whose check
    /// didn't complete this time — so one check that couldn't reach the feed doesn't make the next announce it again.</summary>
    public static List<string> Remembered(IEnumerable<string> notified, IEnumerable<string> alreadyNotified, ISet<string> uncheckedIds)
    {
        var outList = notified.ToList();
        foreach (var key in alreadyNotified)
        {
            if (outList.Contains(key)) continue;
            var id = key.Split(' ', 2)[0];
            if (uncheckedIds.Contains(id)) outList.Add(key);
        }
        return outList;
    }

    public static string NotificationTitle(int count) => count == 1 ? "Update available" : "Updates available";

    /// <summary>"DMX Tools v1.2.0, PSN Tools v0.4.1 and 2 more".</summary>
    public static string NotificationBody(IReadOnlyList<Pending> items)
    {
        var shown = items.Take(3).Select(p => $"{p.Name} {VersionCompare.Display(p.Version)}").ToList();
        var body = string.Join(", ", shown);
        return items.Count > 3 ? $"{body} and {items.Count - 3} more" : body;
    }

    /// <summary>The summary after an automatic update run: "Updated DMX Tools to v1.2.0" / "Updated 3 apps".</summary>
    public static string AutoUpdateSummary(IReadOnlyList<(string Name, string Version)> updated) =>
        updated.Count == 1 ? $"Updated {updated[0].Name} to {VersionCompare.Display(updated[0].Version)}" : $"Updated {updated.Count} apps";
}
