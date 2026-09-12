namespace JBTheatreTools;

/// <summary>Guards the download-relay URL (audit F2). The built-in relay URL lives in the catalog and is
/// trusted; a user-invisible emergency override (<c>settings.json</c> ServerUrl) is honoured ONLY when it
/// is https on a jamesbreedon.com host — otherwise anything that can write settings could redirect every
/// API call and steal the suite passphrase over the wire. A rejected/absent override falls back to the
/// catalog URL.</summary>
public static class RelayUrl
{
    /// <summary>Resolves the effective relay base: the override when it's allowed, else the catalog URL.</summary>
    public static string? Resolve(string? overrideUrl, string? catalogUrl)
    {
        var over = overrideUrl?.Trim();
        if (!string.IsNullOrEmpty(over) && IsAllowed(over)) return over;
        return catalogUrl?.Trim();
    }

    /// <summary>True only for an absolute https URL whose host is jamesbreedon.com or a subdomain of it.</summary>
    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        var host = u.Host.ToLowerInvariant();
        return host == "jamesbreedon.com" || host.EndsWith(".jamesbreedon.com", StringComparison.Ordinal);
    }
}
