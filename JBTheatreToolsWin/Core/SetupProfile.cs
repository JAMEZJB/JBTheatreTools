using System.Globalization;
using System.Text;
using System.Text.Json;

namespace JBTheatreTools;

/// <summary>A machine's setup — which apps (and editions) are installed at which versions, which are held, and
/// optionally the list layout — saved to a file and replayed on another machine (any launcher: the ids are the
/// catalog's). Format (schema 1):
/// <code>{"kind":"jbtheatretools-setup","schemaVersion":1,"createdAt":"…","createdBy":"…",
///  "apps":[{"id":"nditools","variant":"full","version":"v2.0.0","held":true}],
///  "layout":{"pinned":[],"hidden":[],"order":[],"categoryOrder":[],"collapsed":[]}}</code>
/// "variant" is omitted for an app's default edition. Parsing is strict about shape and size so a stray file
/// can't do anything surprising.</summary>
public sealed class SetupProfile
{
    public const string Kind = "jbtheatretools-setup";
    public const int SchemaVersion = 1;
    public const int MaxBytes = 1_000_000;
    public const int MaxEntries = 500;

    public sealed record Entry(string Id, string? Variant, string? Version, bool Held);

    public sealed record Layout(List<string> Pinned, List<string> Hidden, List<string> Order,
                                List<string> CategoryOrder, List<string> Collapsed);

    public string CreatedAt { get; init; } = "";
    public string CreatedBy { get; init; } = "";
    public List<Entry> Apps { get; init; } = new();
    public Layout? AppLayout { get; init; }

    public static SetupProfile Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new FormatException("This file is too large to be a setup file.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new FormatException("This file isn't a JB Theatre Tools setup file."); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "kind") != Kind)
                throw new FormatException("This file isn't a JB Theatre Tools setup file.");
            if (!root.TryGetProperty("schemaVersion", out var sv) || sv.ValueKind != JsonValueKind.Number ||
                !sv.TryGetInt32(out var schema) || schema < 1)
                throw new FormatException("This setup file has no valid schemaVersion.");
            if (schema > SchemaVersion)
                throw new FormatException("This setup file was made by a newer JB Theatre Tools — update the launcher first.");
            if (!root.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array)
                throw new FormatException("This setup file lists no apps.");
            var entries = new List<Entry>();
            foreach (var a in apps.EnumerateArray())
            {
                if (entries.Count >= MaxEntries) break;
                if (a.ValueKind != JsonValueKind.Object) continue;
                var id = Str(a, "id")?.Trim();
                if (string.IsNullOrEmpty(id)) continue;
                var variant = Str(a, "variant")?.Trim();
                var version = Str(a, "version")?.Trim();
                bool held = a.TryGetProperty("held", out var h) && h.ValueKind == JsonValueKind.True;
                entries.Add(new Entry(id, string.IsNullOrEmpty(variant) ? null : variant,
                                      string.IsNullOrEmpty(version) ? null : version, held));
            }
            Layout? layout = null;
            if (root.TryGetProperty("layout", out var l) && l.ValueKind == JsonValueKind.Object)
                layout = new Layout(Ids(l, "pinned"), Ids(l, "hidden"), Ids(l, "order"), Ids(l, "categoryOrder"), Ids(l, "collapsed"));
            return new SetupProfile
            {
                CreatedAt = Str(root, "createdAt") ?? "",
                CreatedBy = Str(root, "createdBy") ?? "",
                Apps = entries,
                AppLayout = layout,
            };
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> Ids(JsonElement e, string name)
    {
        var list = new List<string>();
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var x in arr.EnumerateArray())
            if (x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()) && list.Count < MaxEntries)
                list.Add(x.GetString()!);
        return list;
    }

    public string Serialize()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("kind", Kind);
            w.WriteNumber("schemaVersion", SchemaVersion);
            w.WriteString("createdAt", CreatedAt);
            w.WriteString("createdBy", CreatedBy);
            w.WriteStartArray("apps");
            foreach (var e in Apps)
            {
                w.WriteStartObject();
                w.WriteString("id", e.Id);
                if (e.Variant != null) w.WriteString("variant", e.Variant);
                if (e.Version != null) w.WriteString("version", e.Version);
                if (e.Held) w.WriteBoolean("held", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (AppLayout != null)
            {
                w.WriteStartObject("layout");
                void Arr(string n, List<string> v) { w.WriteStartArray(n); foreach (var s in v) w.WriteStringValue(s); w.WriteEndArray(); }
                Arr("pinned", AppLayout.Pinned);
                Arr("hidden", AppLayout.Hidden);
                Arr("order", AppLayout.Order);
                Arr("categoryOrder", AppLayout.CategoryOrder);
                Arr("collapsed", AppLayout.Collapsed);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string Timestamp(DateTimeOffset now) =>
        now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>A suggested file name: "JB Theatre Tools setup 2026-09-25.json".</summary>
    public static string SuggestedFileName(DateTime nowLocal) =>
        $"JB Theatre Tools setup {nowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json";
}

/// <summary>What importing a setup file would do on this machine — computed before anything installs so the user
/// sees a preview.</summary>
public static class SetupPlanner
{
    /// <summary>One catalog app as the planner needs it: its editions in order (empty = no editions).</summary>
    public sealed record CatalogEntry(string Id, string Name, IReadOnlyList<(string Id, string Label)> Variants);

    /// <summary>One slot to install. <paramref name="VariantId"/> null = the default edition;
    /// <paramref name="Tag"/> null = the latest release.</summary>
    public sealed record Install(string AppId, string? VariantId, string? Tag, string Label);

    public sealed record Plan(List<Install> ToInstall, List<string> AlreadyInstalled, List<string> Skipped, List<string> HoldIds,
        List<string>? HoldNames = null);

    /// <param name="installedKeys">Install keys present on this machine ("id" or "id@variant").</param>
    /// <param name="supportsVariants">False on Android: editions other than the default are skipped.</param>
    /// <param name="allowDevTags">False when Development builds are off here: an entry held at a development build
    /// is skipped — listed under Skipped, not installed and not held — so the preview never promises what the
    /// import won't do.</param>
    public static Plan Build(SetupProfile profile, IReadOnlyList<CatalogEntry> catalog, ISet<string> installedKeys,
                             bool supportsVariants = true, bool allowDevTags = true)
    {
        var byId = catalog.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var toInstall = new List<Install>();
        var already = new List<string>();
        var skipped = new List<string>();
        var hold = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in profile.Apps)
        {
            if (!byId.TryGetValue(e.Id, out var app)) { skipped.Add($"{e.Id} — not in this launcher's catalog"); continue; }
            string? variant = e.Variant;
            string? variantLabel = null;
            if (variant != null)
            {
                int idx = -1;
                for (int i = 0; i < app.Variants.Count; i++) if (app.Variants[i].Id == variant) { idx = i; break; }
                if (idx < 0) { skipped.Add($"{app.Name} ({variant}) — no such edition"); continue; }
                if (idx == 0) variant = null;                          // the default edition's own id
                else if (!supportsVariants) { skipped.Add($"{app.Name} ({app.Variants[idx].Label}) — not available here"); continue; }
                else variantLabel = app.Variants[idx].Label;
            }
            var key = variant == null ? app.Id : $"{app.Id}@{variant}";
            if (!seenKeys.Add(key)) continue;                           // listed twice
            var label = variantLabel == null ? app.Name : $"{app.Name} ({variantLabel})";
            if (e.Held && e.Version != null && !allowDevTags && VersionCompare.IsDev(e.Version))
            {
                skipped.Add($"{label} {VersionCompare.Display(e.Version)} — a development build (not switched on here)");
                continue;
            }
            if (e.Held && !hold.Contains(app.Id)) hold.Add(app.Id);
            if (installedKeys.Contains(key)) { already.Add(label); continue; }
            toInstall.Add(new Install(app.Id, variant, e.Held ? e.Version : null, label));
        }
        return new Plan(toInstall, already, skipped, hold, hold.Select(id => byId.TryGetValue(id, out var c) ? c.Name : id).ToList());
    }

    /// <summary>The preview text shown before an import runs.</summary>
    public static string Summary(Plan plan)
    {
        var sb = new StringBuilder();
        if (plan.ToInstall.Count == 0) sb.Append("Nothing to install — this machine already has every app in the file.");
        else
        {
            sb.Append($"Install {plan.ToInstall.Count} app{(plan.ToInstall.Count == 1 ? "" : "s")}:");
            foreach (var i in plan.ToInstall)
                sb.Append("\n  • ").Append(i.Label).Append(i.Tag != null ? $" {VersionCompare.Display(i.Tag)} (held)" : "");
        }
        if (plan.AlreadyInstalled.Count > 0)
            sb.Append($"\n\nAlready installed (left as they are): {string.Join(", ", plan.AlreadyInstalled)}");
        if (plan.HoldIds.Count > 0)
            sb.Append($"\n\nHeld at their versions: {plan.HoldIds.Count} app{(plan.HoldIds.Count == 1 ? "" : "s")}"
                + (plan.HoldNames is { Count: > 0 } n ? $" ({string.Join(", ", n)})" : "")
                + " — Update All and automatic updates leave them at the version this machine has");
        if (plan.Skipped.Count > 0)
            sb.Append("\n\nSkipped:\n  • ").Append(string.Join("\n  • ", plan.Skipped));
        return sb.ToString();
    }
}
