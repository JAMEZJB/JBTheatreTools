namespace JBTheatreTools;

/// <summary>Lenient version-string comparison (tolerates a leading "v" and differing component counts).</summary>
public static class Versions
{
    public static string Norm(string s)
    {
        s = s.Trim();
        return s.StartsWith('v') || s.StartsWith('V') ? s[1..] : s;
    }

    public static bool Equal(string a, string b) => Norm(a) == Norm(b);

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

    /// <summary>True if `a` is a strictly newer version than `b` (component-wise numeric compare).</summary>
    public static bool IsNewer(string a, string b)
    {
        int[] pa = Parts(a), pb = Parts(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length ? pa[i] : 0;
            int y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    private static int[] Parts(string s) => Norm(s).Split('.').Select(p =>
    {
        int n = 0;
        foreach (var c in p) { if (char.IsDigit(c)) n = n * 10 + (c - '0'); else break; }
        return n;
    }).ToArray();
}
