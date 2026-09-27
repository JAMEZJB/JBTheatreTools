namespace JBTheatreTools;

/// <summary>Which editions of an app (Light / Full) the header's Download All menu works on — kept pure so it's
/// unit-tested and identical on every launcher.</summary>
public static class BatchEditions
{
    /// <summary>The editions "Install every app" considers, in catalog order: the edition the row shows (what the
    /// person picked — it used to be the first edition whatever the row showed, so an app switched to Full was never
    /// fetched and the button hid as soon as every Light edition was current), plus every edition already installed
    /// (so none is left out of date). <paramref name="includeFull"/> ("plus the Full editions") considers them all.
    /// A single-edition app is one slot, null.</summary>
    public static IReadOnlyList<string?> ForDownloadAll(IReadOnlyList<string>? variants, string? shown, bool includeFull,
                                                        Func<string?, bool> isInstalled)
    {
        if (variants == null || variants.Count <= 1) return new string?[] { null };
        var picked = shown != null && variants.Contains(shown) ? shown : variants[0];
        return variants.Where(v => includeFull || v == picked || isInstalled(v)).Select(v => (string?)v).ToList();
    }
}
