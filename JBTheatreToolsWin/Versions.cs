namespace JBTheatreTools;

/// <summary>Release-list "latest" selection. The version-STRING comparison lives in the platform-neutral,
/// unit-tested <see cref="VersionCompare"/> (audit F13 — one comparator, overflow-safe); this facade keeps
/// the existing call sites and adds the ReleaseInfo-aware <see cref="Latest"/>.</summary>
public static class Versions
{
    public static string Norm(string s) => VersionCompare.Norm(s);

    public static bool Equal(string a, string b) => VersionCompare.Equal(a, b);

    /// <summary>
    /// Picks the release to treat as "latest": the highest <b>semver</b> among non-prereleases (falling
    /// back to the highest among all releases if every one is a prerelease). GitHub's list endpoint is
    /// ordered by creation date, so a backport/hotfix published after a newer release would otherwise be
    /// mis-selected as "latest" — we sort by version instead, matching GitHub's <c>releases/latest</c>.
    /// </summary>
    public static ReleaseInfo? Latest(IEnumerable<ReleaseInfo> releases)
    {
        var list = releases.ToList();
        var pool = list.Where(r => !r.Prerelease).ToList();
        if (pool.Count == 0) pool = list;
        ReleaseInfo? best = null;
        foreach (var r in pool)
            if (best == null || IsNewer(r.TagName, best.TagName)) best = r;
        return best;
    }

    /// <summary>True if `a` is a strictly newer version than `b`. Delegates to the overflow-safe comparator.</summary>
    public static bool IsNewer(string a, string b) => VersionCompare.IsNewer(a, b);
}
