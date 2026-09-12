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

    /// <summary>True if `a` is a strictly newer version than `b` (component-wise numeric compare).</summary>
    public static bool IsNewer(string a, string b)
    {
        long[] pa = Parts(a), pb = Parts(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            long x = i < pa.Length ? pa[i] : 0;
            long y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    private static long[] Parts(string s) => Norm(s).Split('.').Select(p =>
    {
        long n = 0;
        foreach (var c in p)
        {
            if (!char.IsDigit(c)) break;
            n = n > (long.MaxValue - 9) / 10 ? long.MaxValue : n * 10 + (c - '0');   // saturate, never wrap
        }
        return n;
    }).ToArray();
}
