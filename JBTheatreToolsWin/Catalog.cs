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

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 6 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "catalog.json");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "catalog.json not found (not embedded and not in any parent of the working directory).");
    }
}

public sealed class CatalogApp
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("blurb")] public string Blurb { get; set; } = "";
    [JsonPropertyName("owner")] public string Owner { get; set; } = "";
    [JsonPropertyName("repo")] public string Repo { get; set; } = "";

    /// <summary>Platform key → exact release-asset name. Keys: macos, windows-x64, windows-arm64.</summary>
    [JsonPropertyName("assets")] public Dictionary<string, string> Assets { get; set; } = new();

    /// <summary>The Windows asset name for this machine's architecture.</summary>
    public string? WindowsAssetName => Assets.TryGetValue(Platform.AssetKey, out var n) ? n : null;
}
