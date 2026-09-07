namespace JBTheatreTools;

/// <summary>SHA256SUMS manifest parsing (the shapes <c>sha256sum</c> / <c>shasum</c> actually produce).</summary>
public static class Sha256Sums
{
    /// <summary>The hash listed for <paramref name="assetName"/> in a SHA256SUMS text, or null when it isn't
    /// listed. Handles "hash  name", "hash *name" (binary-mode marker), tab separators and CRLF line endings.
    /// The name must match exactly (case-sensitive, whole name).</summary>
    public static string? Expected(string assetName, string sumsText)
    {
        foreach (var raw in sumsText.Split('\n'))
        {
            var line = raw.Trim();
            int sep = line.IndexOfAny(new[] { ' ', '\t' });
            if (sep <= 0) continue;
            var hash = line[..sep];
            var name = line[(sep + 1)..].Trim();
            if (name.StartsWith('*')) name = name[1..];   // sha256sum "binary mode" marker
            if (name == assetName) return hash;
        }
        return null;
    }
}
