namespace JBTheatreTools;

/// <summary>The pre-download disk-space check: refuse a download that can't possibly be installed rather than
/// failing half-way through an extract. An archive needs room for the download AND its extracted copy (and the
/// old install stays until the new one is committed), so it's budgeted at 3× its size; a single file at 2×; plus
/// a fixed 50 MB margin.</summary>
public static class DiskSpace
{
    public const long Margin = 50_000_000;

    public static bool IsArchive(string assetName) =>
        assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    public static long Required(long assetSize, string assetName)
    {
        if (assetSize <= 0) return Margin;
        long factor = IsArchive(assetName) ? 3 : 2;
        return assetSize > (long.MaxValue - Margin) / factor ? long.MaxValue : assetSize * factor + Margin;
    }

    /// <summary>The refusal message, or null when there's room. A negative <paramref name="free"/> means "couldn't
    /// tell" — never block on an unknown.</summary>
    public static string? Shortfall(long required, long free) =>
        free < 0 || free >= required
            ? null
            : $"Not enough disk space — needs about {ByteSize.Format(required)}, {ByteSize.Format(free)} free.";
}
