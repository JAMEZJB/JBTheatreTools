using System.Text.Json;

namespace JBTheatreTools;

/// <summary>
/// The suite's "New in vX.Y.Z:" lines, served by the download relay so they can be edited on the server
/// without shipping a launcher release. The bundled catalog's lines are the FALLBACK: the launcher fetches
/// this document alongside every update check (server/passphrase mode only), overlays it on the catalog,
/// and keeps the last good copy on disk so an offline start still shows the latest lines it saw.
///
/// Document (<c>&lt;relay origin&gt;/notes/whats-new.json</c>, same passphrase auth as the release endpoints):
/// <code>{ "schemaVersion": 1, "apps": { "&lt;app id&gt;": { "whatsNew": "one line", "whatsNewVersion": "v1.2.3" } } }</code>
///
/// Rules: unknown ids are ignored; a missing key means "keep the bundled line"; an EMPTY <c>whatsNew</c> hides
/// the row's line; text is trimmed, whitespace-collapsed, control characters stripped and length-capped.
/// Pure (no IO) so it's unit-tested off Windows; the fetch + cache live in the WinForms project.
/// </summary>
public sealed class WhatsNewNotes
{
    public const int SchemaVersion = 1;
    public const int MaxLineLength = 160;
    public const int MaxVersionLength = 24;
    public const string RelativePath = "/notes/whats-new.json";

    public sealed record Note(string WhatsNew, string? WhatsNewVersion);   // WhatsNew "" = hide the line

    private readonly Dictionary<string, Note> _notes;
    private WhatsNewNotes(Dictionary<string, Note> notes) { _notes = notes; }

    public int Count => _notes.Count;
    public Note? this[string id] => _notes.TryGetValue(id, out var n) ? n : null;

    /// <summary>Parses the relay document; throws on anything that isn't the documented shape (a stray HTML
    /// error page or redirect body can never clobber the bundled lines).</summary>
    public static WhatsNewNotes Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Object)
            throw new FormatException("whats-new.json: expected {\"apps\": {...}}");
        if (root.TryGetProperty("schemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number && sv.GetInt32() > SchemaVersion)
            throw new FormatException($"whats-new.json: schemaVersion {sv.GetInt32()} is newer than this launcher understands");
        var notes = new Dictionary<string, Note>(StringComparer.Ordinal);
        foreach (var app in apps.EnumerateObject())
        {
            if (app.Value.ValueKind != JsonValueKind.Object) continue;
            if (!app.Value.TryGetProperty("whatsNew", out var line) || line.ValueKind != JsonValueKind.String) continue;
            string? version = null;
            if (app.Value.TryGetProperty("whatsNewVersion", out var v) && v.ValueKind == JsonValueKind.String)
                version = Clean(v.GetString()!, MaxVersionLength);
            notes[app.Name] = new Note(Clean(line.GetString()!, MaxLineLength) ?? "", version);
        }
        return new WhatsNewNotes(notes);
    }

    /// <summary>Trim, strip control characters, collapse runs of whitespace, cap the length. Null when empty.</summary>
    public static string? Clean(string s, int max)
    {
        var chars = s.Where(c => !char.IsControl(c)).ToArray();
        var collapsed = string.Join(' ', new string(chars).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0) return null;
        return collapsed.Length > max ? collapsed[..max].TrimEnd() + "…" : collapsed;
    }

    /// <summary>The line to show for an app: the relay's if it has one (empty = hide), else the catalog's.</summary>
    public (string? WhatsNew, string? WhatsNewVersion) Resolve(string appId, string? bundledLine, string? bundledVersion)
    {
        if (!_notes.TryGetValue(appId, out var n)) return (bundledLine, bundledVersion);
        return (n.WhatsNew.Length == 0 ? null : n.WhatsNew, n.WhatsNewVersion);
    }

    /// <summary><c>&lt;origin of the relay base&gt;/notes/whats-new.json</c> — the base is the <c>/ghapi</c>
    /// pass-through root; the notes live beside it, not under it. Null when the base doesn't parse.</summary>
    public static Uri? UrlFor(string relayBase)
    {
        if (!Uri.TryCreate(relayBase.Trim(), UriKind.Absolute, out var b) || string.IsNullOrEmpty(b.Host)) return null;
        var origin = b.IsDefaultPort ? $"{b.Scheme}://{b.Host}" : $"{b.Scheme}://{b.Host}:{b.Port}";
        return new Uri(origin + RelativePath);
    }
}
