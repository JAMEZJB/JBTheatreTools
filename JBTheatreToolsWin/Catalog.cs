using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JBTheatreTools;

/// <summary>
/// The shared app catalog (mirrors the repo-root <c>catalog.json</c>, embedded into the exe).
/// Both the macOS and Windows launchers read the same file so the catalog stays single-sourced.
/// </summary>
public sealed class Catalog
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("apps")] public List<CatalogApp> Apps { get; set; } = new();
    /// <summary>JBTheatreTools' own release info, for the launcher self-update check.</summary>
    [JsonPropertyName("self")] public SelfInfo? Self { get; set; }
    /// <summary>Built-in download-relay base URL for the default (passphrase) auth mode.</summary>
    [JsonPropertyName("downloadServer")] public string? DownloadServer { get; set; }

    /// <summary>Loads from (1) an explicit path, (2) the embedded resource, or (3) a parent dir of CWD.</summary>
    public static Catalog Load(string? explicitPath = null)
    {
        var json = LoadJson(explicitPath);
        return JsonSerializer.Deserialize<Catalog>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Catalog();
    }

    private static string LoadJson(string? explicitPath)
    {
        if (explicitPath != null) return File.ReadAllText(explicitPath);

        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
                      .FirstOrDefault(n => n.EndsWith("catalog.json", StringComparison.OrdinalIgnoreCase));
        if (name != null)
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        // No walking up from the CWD (audit F9): a catalog.json in a parent of an arbitrary working
        // directory must never be trusted to dictate owners/repos/relay. The shipped exe embeds it;
        // bare-binary / dev use must pass --catalog explicitly.
        throw new FileNotFoundException("catalog.json is not embedded; pass --catalog <path>.");
    }
}

public sealed class CatalogApp
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("blurb")] public string Blurb { get; set; } = "";
    /// <summary>Optional one-line "what's new" for this app's current release (shown under the row when present).</summary>
    [JsonPropertyName("whatsNew")] public string? WhatsNew { get; set; }
    /// <summary>Optional version the whatsNew line refers to (e.g. "v1.5.0"), used to label it "New in vX.Y.Z:".</summary>
    [JsonPropertyName("whatsNewVersion")] public string? WhatsNewVersion { get; set; }
    [JsonPropertyName("owner")] public string Owner { get; set; } = "";
    [JsonPropertyName("repo")] public string Repo { get; set; } = "";

    /// <summary>Platform key → exact release-asset name. Keys: macos, windows-x64, windows-arm64
    /// (and optional per-arch macOS keys). When <see cref="Variants"/> is present, this is the default
    /// variant's assets.</summary>
    [JsonPropertyName("assets")] public Dictionary<string, string> Assets { get; set; } = new();

    /// <summary>Optional download variants of the SAME app (e.g. NDI Standard vs Full). When present
    /// with more than one entry, the row shows a variant toggle and install resolves the selected one.</summary>
    [JsonPropertyName("variants")] public List<AppVariant>? Variants { get; set; }

    /// <summary>True when this app ships more than one variant → the row shows a Standard/Full toggle.</summary>
    public bool HasVariants => (Variants?.Count ?? 0) > 1;

    /// <summary>The asset map for a variant id (null/unknown → the default = first variant, or the
    /// top-level <see cref="Assets"/> when there are no variants).</summary>
    public Dictionary<string, string> AssetsFor(string? variantId)
    {
        if (Variants is { Count: > 0 } vs)
            return (vs.FirstOrDefault(v => v.Id == variantId) ?? vs[0]).Assets;
        return Assets;
    }

    /// <summary>The Windows asset name for this machine's architecture (default variant).</summary>
    public string? WindowsAssetName => Assets.TryGetValue(Platform.AssetKey, out var n) ? n : null;

    /// <summary>Variant-aware Windows asset name for this machine's architecture.</summary>
    public string? WindowsAsset(string? variantId)
        => AssetsFor(variantId).TryGetValue(Platform.AssetKey, out var n) ? n : null;

    /// <summary>True when <paramref name="variantId"/> is the default (first) variant, or the app has no variants.</summary>
    public bool IsDefaultVariant(string? variantId)
    {
        if (!HasVariants || Variants == null) return true;
        return variantId == null || variantId == Variants[0].Id;
    }

    /// <summary>The label of a variant id, or null.</summary>
    public string? VariantLabel(string? variantId)
        => variantId == null ? null : Variants?.FirstOrDefault(v => v.Id == variantId)?.Label;

    /// <summary>The install-manifest key for a variant. Each variant is its OWN install slot, so Standard
    /// and Full can be installed side by side. The default variant keeps the plain app id (installs made
    /// before variants existed stay valid); other variants are "&lt;id&gt;@&lt;variant&gt;".</summary>
    public string InstallKey(string? variantId) => IsDefaultVariant(variantId) ? Id : $"{Id}@{variantId}";

    /// <summary>Suffix for shortcut names of a non-default variant (" (Full)") so they don't collide with
    /// the default variant's; empty for the default variant.</summary>
    public string VariantSuffix(string? variantId)
    {
        if (IsDefaultVariant(variantId)) return "";
        var label = VariantLabel(variantId);
        return label == null ? "" : $" ({label})";
    }
}

/// <summary>One downloadable variant of an app (e.g. Standard / Full). <c>Label</c> is the toggle text.</summary>
public sealed class AppVariant
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("assets")] public Dictionary<string, string> Assets { get; set; } = new();
}

/// <summary>JBTheatreTools' own release info (for the self-update check).</summary>
public sealed class SelfInfo
{
    [JsonPropertyName("owner")] public string Owner { get; set; } = "";
    [JsonPropertyName("repo")] public string Repo { get; set; } = "";
    [JsonPropertyName("assets")] public Dictionary<string, string> Assets { get; set; } = new();
}
