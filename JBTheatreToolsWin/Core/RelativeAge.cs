namespace JBTheatreTools;

/// <summary>"3 days ago" for a release date — identical wording on every launcher. Counts whole elapsed days
/// (24-hour periods); a date in the future (clock skew) reads as "today".</summary>
public static class RelativeAge
{
    public static string Describe(DateTimeOffset date, DateTimeOffset now)
    {
        double total = (now - date).TotalDays;
        long days = total <= 0 ? 0 : (long)Math.Floor(total);
        if (days < 1) return "today";
        if (days == 1) return "yesterday";
        if (days < 7) return $"{days} days ago";
        if (days < 30) { long w = days / 7; return w == 1 ? "1 week ago" : $"{w} weeks ago"; }
        if (days < 365) { long m = Math.Max(1, days / 30); return m == 1 ? "1 month ago" : $"{m} months ago"; }
        long y = days / 365;
        return y == 1 ? "1 year ago" : $"{y} years ago";
    }

    /// <summary>Parses GitHub's <c>published_at</c> (ISO 8601, UTC "Z"); null when absent or malformed.</summary>
    public static DateTimeOffset? ParseIso(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        return DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var d) ? d : null;
    }
}
