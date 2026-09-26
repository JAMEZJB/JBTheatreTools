namespace JBTheatreTools;

/// <summary>Lenient, overflow-safe version-string comparison (audit F13). Tolerates a leading "v" and
/// differing component counts; parses each numeric segment into a <c>long</c> with saturation, so a long
/// numeric tag (e.g. "v20260906123456") can't wrap a 32-bit int and compare as OLDER than the installed
/// build — which would silently hide an update. Shared by the WinForms <c>Versions</c> facade so there's
/// one comparator, unit-tested here.</summary>
public static class VersionCompare
{
    public static string Norm(string s)
    {
        s = s.Trim();
        return s.StartsWith('v') || s.StartsWith('V') ? s[1..] : s;
    }

    public static bool Equal(string a, string b) => Norm(a) == Norm(b);

    /// <summary>True if `a` is a strictly newer version than `b`.</summary>
    public static bool IsNewer(string a, string b) => Compare(a, b) > 0;

    /// <summary>Semver-aware ordering: the numeric core compares component-wise (missing parts = 0); a
    /// pre-release (<c>1.2.0-dev.3</c>, <c>1.2.0-rc1</c>) sorts BEFORE its release, and pre-release identifiers
    /// compare semver-style (numbers numerically, numbers before words, a longer list after its prefix) — so
    /// <c>0.1.0-dev.1 &lt; 0.1.0-dev.2 &lt; 0.1.0</c>. A tag that doesn't start with a digit (Convert's
    /// <c>build-20260912</c>) keeps the lenient digit-run parse and never counts as a pre-release.</summary>
    public static int Compare(string a, string b)
    {
        var (ca, pa) = SplitPre(a);
        var (cb, pb) = SplitPre(b);
        long[] x = Parts(ca), y = Parts(cb);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            long u = i < x.Length ? x[i] : 0;
            long v = i < y.Length ? y[i] : 0;
            if (u != v) return u.CompareTo(v);
        }
        if (pa == null && pb == null) return 0;
        if (pa == null) return 1;
        if (pb == null) return -1;
        for (int i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            int c = CompareIdentifier(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }

    /// <summary>A tag as the launchers print it: "v" + the normalised version for a numeric tag ("1.2.0" and
    /// "v1.2.0" both → "v1.2.0"), the tag itself otherwise (Convert's "build-20260912").</summary>
    public static string Display(string tag)
    {
        var n = Norm(tag);
        return n.Length > 0 && char.IsDigit(n[0]) ? "v" + n : tag.Trim();
    }

    /// <summary>True for development pre-releases (<c>vX.Y.Z-dev.N</c>).</summary>
    public static bool IsDev(string tag) => Norm(tag).Contains("-dev.", StringComparison.OrdinalIgnoreCase);

    /// <summary>The release to treat as "latest". Dev pre-releases are candidates ONLY with the Dev channel on
    /// (then: highest semver among stable + dev); off, they're invisible — even for an app whose only releases
    /// are dev builds. Otherwise the highest stable, falling back to the highest non-dev pre-release.</summary>
    public static T? PickLatest<T>(IEnumerable<T> releases, Func<T, string> tag, Func<T, bool> isPre, bool devChannel)
        where T : class
    {
        var list = releases.ToList();
        var nonDev = list.Where(r => !IsDev(tag(r))).ToList();
        var stable = nonDev.Where(r => !isPre(r)).ToList();
        var pool = devChannel ? stable.Concat(list.Where(r => IsDev(tag(r)))).ToList() : stable;
        if (pool.Count == 0) pool = nonDev;
        T? best = null;
        foreach (var r in pool)
            if (best == null || Compare(tag(r), tag(best)) > 0) best = r;
        return best;
    }

    private static (string Core, string[]? Pre) SplitPre(string s)
    {
        var n = Norm(s);
        int dash = n.IndexOf('-');
        if (n.Length == 0 || !char.IsDigit(n[0]) || dash < 0) return (n, null);
        return (n[..dash], n[(dash + 1)..].Split('.'));
    }

    private static int CompareIdentifier(string a, string b)
    {
        bool na = long.TryParse(a, out var x), nb = long.TryParse(b, out var y);
        if (na && nb) return x.CompareTo(y);
        if (na) return -1;
        if (nb) return 1;
        return string.CompareOrdinal(a, b);
    }

    private static long[] Parts(string s) => Norm(s).Split('.').Select(p =>
    {
        // First contiguous digit run in the segment: skip any leading non-digits, take the digits, stop at
        // the next non-digit. This handles date-style tags like "build-20260912" (→ 20260912) that a rolling
        // app such as Convert uses, while staying identical for ordinary semver segments ("2", "0-rc1" → 0).
        long n = 0;
        bool started = false;
        foreach (var c in p)
        {
            if (char.IsDigit(c))
            {
                started = true;
                n = n > (long.MaxValue - 9) / 10 ? long.MaxValue : n * 10 + (c - '0');   // saturate, never wrap
            }
            else if (started) break;
        }
        return n;
    }).ToArray();
}
