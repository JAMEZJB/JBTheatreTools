namespace JBTheatreTools;

/// <summary>The status half of the find bar: which rows to show.</summary>
public enum StatusFilter { All, Installed, Updates, NotInstalled }

/// <summary>Find &amp; filter for the app list — the same rules on every launcher. The query is split on
/// whitespace and EVERY word must appear (case-insensitively) somewhere in the app's name, blurb, category or id,
/// so "dmx tools" and "tools dmx" both find DMX Tools.</summary>
public static class AppFilter
{
    public static string[] Tokens(string? query) =>
        (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                     .Select(t => t.ToLowerInvariant()).ToArray();

    public static bool MatchesQuery(string? query, params string?[] fields)
    {
        var tokens = Tokens(query);
        if (tokens.Length == 0) return true;
        var hay = fields.Where(f => !string.IsNullOrEmpty(f)).Select(f => f!.ToLowerInvariant()).ToList();
        return tokens.All(t => hay.Any(h => h.Contains(t, StringComparison.Ordinal)));
    }

    /// <param name="installed">The row's selected edition is installed.</param>
    /// <param name="updateAvailable">An update is on offer and the app isn't held.</param>
    /// <param name="installable">Not installed and a build exists for this machine.</param>
    public static bool MatchesStatus(StatusFilter filter, bool installed, bool updateAvailable, bool installable) => filter switch
    {
        StatusFilter.Installed => installed,
        StatusFilter.Updates => updateAvailable,
        StatusFilter.NotInstalled => !installed && installable,
        _ => true,
    };

    /// <summary>True when the list is narrowed — reordering is disabled then (a drag over a subset would scramble
    /// the hidden rows' order) and collapsed sections are shown open so matches can't hide.</summary>
    public static bool IsActive(string? query, StatusFilter filter) => Tokens(query).Length > 0 || filter != StatusFilter.All;

    public static string Label(StatusFilter f) => f switch
    {
        StatusFilter.Installed => "Installed",
        StatusFilter.Updates => "Updates",
        StatusFilter.NotInstalled => "Not installed",
        _ => "All",
    };
}
