namespace JBTheatreTools;

/// <summary>
/// Helpers for installing a heavy "Full" app that ships as a one-dir PyInstaller build inside a .zip
/// (a single-file .exe would be huge and slow to start). Contract (controller-owned Full CI): the zip has
/// one top-level "&lt;App&gt; &lt;Variant&gt;/" folder with "&lt;App&gt; &lt;Variant&gt;.exe" at its root and
/// PyInstaller's <c>_internal/</c> alongside (which holds DLLs/pyd, never an .exe).
///
/// Pure so the risky picking heuristic is unit-testable off Windows (the extraction itself lives in the
/// WinForms InstallManager).
/// </summary>
public static class FullApp
{
    /// <summary>Picks the launcher .exe from the paths found under an extracted Full app. Prefers an exact
    /// stem match on the expected "&lt;App&gt; &lt;Variant&gt;" name; otherwise the shallowest .exe (the
    /// app-root launcher — <c>_internal/</c> contains no .exe, so depth disambiguates). Null when none.</summary>
    public static string? PickMainExe(IReadOnlyList<string> exePaths, string preferredStem)
    {
        if (exePaths.Count == 0) return null;

        foreach (var p in exePaths)
            if (string.Equals(StemOf(p), preferredStem, StringComparison.OrdinalIgnoreCase))
                return p;

        string? best = null;
        int bestDepth = int.MaxValue;
        foreach (var p in exePaths)
        {
            int d = Depth(p);
            if (d < bestDepth) { best = p; bestDepth = d; }
        }
        return best;
    }

    /// <summary>The expected launcher stem for a Full app: "&lt;App&gt; &lt;Variant&gt;" (e.g. "Image Tools Full"),
    /// or just the app name when there's no variant label.</summary>
    public static string ExpectedStem(string appName, string? variantLabel)
        => string.IsNullOrEmpty(variantLabel) ? appName : $"{appName} {variantLabel}";

    // Own path helpers (not System.IO.Path) so the same code counts '/' and '\' regardless of the OS it
    // runs on — lets the mac unit tests use Windows-style paths.
    private static int Depth(string p)
    {
        int n = 0;
        foreach (var c in p) if (c == '/' || c == '\\') n++;
        return n;
    }

    private static string StemOf(string p)
    {
        int slash = Math.Max(p.LastIndexOf('/'), p.LastIndexOf('\\'));
        var name = slash >= 0 ? p[(slash + 1)..] : p;
        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name[..dot] : name;
    }
}
