using System.Globalization;
using System.Text;
using System.Text.Json;

namespace JBTheatreTools;

/// <summary>One line of the install history: what happened to which app, and when.</summary>
/// <param name="At">When (UTC).</param>
/// <param name="App">Catalog id (install key for a non-default edition, e.g. "nditools@full").</param>
/// <param name="Name">Display name at the time (with any edition suffix).</param>
/// <param name="Action">install · update · downgrade · reinstall · uninstall · failed.</param>
public sealed record ActivityEvent(DateTimeOffset At, string App, string Name, string Action,
                                   string? From = null, string? To = null, string? Note = null);

/// <summary>The launcher's activity history (history.json in the support dir): a JSON array, oldest first,
/// capped at <see cref="Cap"/> entries. Pure — parsing, appending and wording; the file IO lives in the app.
/// A damaged file reads as empty history rather than failing a launch.</summary>
public static class ActivityHistory
{
    public const int Cap = 300;
    public const int MaxNoteLength = 200;

    /// <summary>Classifies a successful install by the version it replaced.</summary>
    public static string ActionFor(string? from, string to)
    {
        if (string.IsNullOrEmpty(from)) return "install";
        if (VersionCompare.IsNewer(to, from)) return "update";
        if (VersionCompare.IsNewer(from, to)) return "downgrade";
        return "reinstall";
    }

    public static List<ActivityEvent> Parse(string? json)
    {
        var list = new List<ActivityEvent>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var at = RelativeAge.ParseIso(Str(e, "at"));
                var app = Str(e, "app");
                var action = Str(e, "action");
                if (at == null || string.IsNullOrEmpty(app) || string.IsNullOrEmpty(action)) continue;
                list.Add(new ActivityEvent(at.Value, app, Str(e, "name") ?? app, action, Str(e, "from"), Str(e, "to"), Str(e, "note")));
            }
        }
        catch (JsonException) { list.Clear(); }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string Serialize(IEnumerable<ActivityEvent> events)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartArray();
            foreach (var e in events)
            {
                w.WriteStartObject();
                w.WriteString("at", e.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                w.WriteString("app", e.App);
                w.WriteString("name", e.Name);
                w.WriteString("action", e.Action);
                if (e.From != null) w.WriteString("from", e.From);
                if (e.To != null) w.WriteString("to", e.To);
                if (e.Note != null) w.WriteString("note", e.Note);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Appends, trimming the note, and drops the oldest entries past the cap.</summary>
    public static List<ActivityEvent> Append(IEnumerable<ActivityEvent> existing, ActivityEvent e)
    {
        var note = e.Note?.Trim();
        if (note != null && note.Length > MaxNoteLength) note = note[..MaxNoteLength].TrimEnd() + "…";
        var list = existing.ToList();
        list.Add(e with { Note = string.IsNullOrEmpty(note) ? null : note });
        if (list.Count > Cap) list.RemoveRange(0, list.Count - Cap);
        return list;
    }

    /// <summary>"Updated DMX Tools v1.1.0 → v1.2.0".</summary>
    public static string Describe(ActivityEvent e)
    {
        string V(string? t) => t == null ? "" : " " + VersionCompare.Display(t);
        return e.Action switch
        {
            "install" => $"Installed {e.Name}{V(e.To)}",
            "update" => $"Updated {e.Name}{V(e.From)} →{V(e.To)}",
            "downgrade" => $"Rolled back {e.Name}{V(e.From)} →{V(e.To)}",
            "reinstall" => $"Reinstalled {e.Name}{V(e.To)}",
            "uninstall" => $"Removed {e.Name}{V(e.From)}",
            "failed" => $"Couldn't install {e.Name}{V(e.To)}" + (e.Note != null ? $": {e.Note}" : ""),
            _ => $"{e.Action} {e.Name}{V(e.To)}",
        };
    }

    /// <summary>"Today 14:02", "Yesterday 09:10" or "12 Sep 2026 14:02" — both times already in local time.</summary>
    public static string When(DateTime atLocal, DateTime nowLocal)
    {
        var time = atLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (atLocal.Date == nowLocal.Date) return $"Today {time}";
        if (atLocal.Date == nowLocal.Date.AddDays(-1)) return $"Yesterday {time}";
        return atLocal.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
    }
}
