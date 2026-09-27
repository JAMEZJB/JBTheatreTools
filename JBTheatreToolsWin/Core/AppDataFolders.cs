namespace JBTheatreTools;

/// <summary>Where an app keeps its own settings, logs and caches (outside its install folder), from the catalog's
/// <c>dataFolders</c> (folder names) and <c>bundleIds</c> (macOS) — so "Uninstall" can offer to remove them too.
/// Kept pure (roots passed in) so it's unit-tested; the macOS launcher carries the same rules.</summary>
public static class AppDataFolders
{
    /// <summary>Shared folders no app entry may ever name, whatever the catalog says (compared case-insensitively).</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "JBTheatreTools", "JB Theatre Tools", "Microsoft", "Packages", "Programs", "Temp", "Google", "Apple", "Caches",
        "Logs", "Application Support", "Preferences", "WebKit", "HTTPStorages", "Saved Application State", "Documents",
        "Desktop", "Downloads", "Library", "AppData", "Local", "Roaming", "LocalLow", "Application Data", "Mozilla",
    };

    /// <summary>A single, ordinary folder name: 1–64 characters, no path separators or wildcards, not "." / "..", no
    /// leading or trailing space or dot, not one of the shared folders above, and never Apple's own (com.apple.*).</summary>
    public static bool IsSafeName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64
        && !name.StartsWith("com.apple.", StringComparison.OrdinalIgnoreCase)
        && name.Trim() == name && !name.StartsWith('.') && !name.EndsWith('.')
        && name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0' }) < 0
        && !Reserved.Contains(name);

    /// <summary>The Windows locations for an app's folder names: each name under Roaming (%APPDATA% — settings, the
    /// web view's data) and Local (%LOCALAPPDATA% — logs, caches). Unsafe names are skipped; order is stable.</summary>
    public static IReadOnlyList<string> WindowsPaths(IEnumerable<string>? folders, string roaming, string local)
    {
        var list = new List<string>();
        foreach (var name in (folders ?? Enumerable.Empty<string>()).Where(IsSafeName).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var root in new[] { roaming, local })
                if (!string.IsNullOrEmpty(root)) list.Add(Path.Combine(root, name));
        return list;
    }
}
