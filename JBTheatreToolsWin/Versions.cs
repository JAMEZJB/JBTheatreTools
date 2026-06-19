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
