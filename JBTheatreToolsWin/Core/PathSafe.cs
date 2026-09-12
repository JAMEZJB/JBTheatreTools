using System.Text;

namespace JBTheatreTools;

/// <summary>Sanitises an API-supplied string (release tag / asset name) before it's used as ONE local
/// filename component (audit F8): keep [A-Za-z0-9._-+], map everything else (incl. / and \) to '_', and
/// never let it start with '.', so no component can traverse (..) or hide. Defence-in-depth against a
/// hostile relay steering a cache write outside the caches dir.</summary>
public static class PathSafe
{
    public static string Component(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or '+' ? ch : '_');
        var cleaned = sb.ToString();
        return cleaned.Length == 0 || cleaned[0] == '.' ? "_" + cleaned : cleaned;
    }
}
