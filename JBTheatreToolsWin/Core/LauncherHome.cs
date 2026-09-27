namespace JBTheatreTools;

/// <summary>Where the launcher itself lives. It's a single portable exe, so a first-time user tends to run it straight
/// from Downloads; the launcher offers to install itself in the per-user programs folder (no administrator needed) —
/// the macOS launcher offers the Applications folder the same way. Pure, so it's unit-tested.</summary>
public static class LauncherHome
{
    public const string FolderName = "JB Theatre Tools";
    public const string ExeName = "JBTheatreTools.exe";

    /// <summary>%LOCALAPPDATA%\Programs\JB Theatre Tools\JBTheatreTools.exe.</summary>
    public static string DefaultExe(string localAppData) => Path.Combine(localAppData, "Programs", FolderName, ExeName);

    /// <summary>Offer to install unless this copy is already the default one or one the person installed or chose to
    /// keep (<paramref name="homes"/> — exe paths, compared without case).</summary>
    public static bool ShouldOffer(string exePath, string defaultExe, IEnumerable<string>? homes) =>
        !Same(exePath, defaultExe) && !(homes ?? Enumerable.Empty<string>()).Any(h => Same(h, exePath));

    /// <summary>The exe path for an install into <paramref name="folder"/> (always the standard file name — a download
    /// is called e.g. "JBTheatreTools-Windows-x64.exe").</summary>
    public static string ExeIn(string folder) => Path.Combine(folder, ExeName);

    /// <summary>True when <paramref name="path"/> is inside <paramref name="folder"/> (e.g. the person's Downloads).</summary>
    public static bool IsInside(string path, string folder)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Installing would put an older (or the same) launcher over <paramref name="existingVersion"/> — then the
    /// copy already there is simply opened instead (a stale download must never downgrade a self-updated install).</summary>
    public static bool KeepExisting(string? existingVersion, string myVersion) =>
        !string.IsNullOrWhiteSpace(existingVersion) && !VersionCompare.IsNewer(myVersion, existingVersion);

    /// <summary>Why a folder picked with "Choose Folder…" can't take the launcher, or null when it can: it must be a full
    /// path, and never the launcher's own data or install folders or an app's data folder (a later Uninstall, "remove its
    /// data" or clean-up there would take the launcher with it) — nor inside one of them.</summary>
    public static string? RefuseFolder(string folder, IEnumerable<string> ownFolders)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return "Choose a folder on a drive, not a relative path.";
        foreach (var own in ownFolders.Where(o => !string.IsNullOrEmpty(o)))
            if (Same(folder, own) || IsInside(folder, own))
                return $"{folder} is where JB Theatre Tools keeps its own files or an app's data. Choose another folder.";
        return null;
    }

    public static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
