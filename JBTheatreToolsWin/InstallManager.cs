using System.Diagnostics;
using System.Text.Json;

namespace JBTheatreTools;

public sealed class InstalledRecord
{
    public string Version { get; set; } = "";
    public string Path { get; set; } = "";
    public string InstalledAt { get; set; } = "";
}

/// <summary>
/// Installs / tracks / launches the downloaded Windows apps.
///
/// Windows assets are self-contained single <c>.exe</c> files, so there is no archive to extract:
/// install = place the exe under the apps dir and record its version in a JSON manifest.
/// </summary>
public sealed class InstallManager
{
    public static readonly InstallManager Shared = new();

    public string SupportDir { get; }
    public string AppsDir { get; }
    public string CacheDir { get; }
    public string ManifestPath { get; }

    public InstallManager()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        SupportDir = Path.Combine(local, "JBTheatreTools");
        AppsDir = Path.Combine(SupportDir, "apps");
        CacheDir = Path.Combine(SupportDir, "cache");
        ManifestPath = Path.Combine(SupportDir, "installed.json");
        Directory.CreateDirectory(AppsDir);
        Directory.CreateDirectory(CacheDir);
    }

    public Dictionary<string, InstalledRecord> Manifest()
    {
        try
        {
            if (File.Exists(ManifestPath))
                return JsonSerializer.Deserialize<Dictionary<string, InstalledRecord>>(
                    File.ReadAllText(ManifestPath)) ?? new();
        }
        catch { /* fall through to empty */ }
        return new();
    }

    private void WriteManifest(Dictionary<string, InstalledRecord> m)
    {
        try
        {
            Directory.CreateDirectory(SupportDir);
            File.WriteAllText(ManifestPath,
                JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }

    public string? InstalledVersion(string id)
    {
        var m = Manifest();
        return m.TryGetValue(id, out var r) && File.Exists(r.Path) ? r.Version : null;
    }

    public string? InstalledPath(string id)
    {
        var m = Manifest();
        return m.TryGetValue(id, out var r) && File.Exists(r.Path) ? r.Path : null;
    }

    /// <summary>Installs a downloaded self-contained .exe and records its version.</summary>
    public string Install(CatalogApp app, string version, string downloadedExe, string assetName)
    {
        var dir = Path.Combine(AppsDir, app.Id);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, assetName);
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(downloadedExe, dest);

        var m = Manifest();
        m[app.Id] = new InstalledRecord
        {
            Version = version,
            Path = dest,
            InstalledAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        };
        WriteManifest(m);
        return dest;
    }

    public void Launch(CatalogApp app)
    {
        var path = InstalledPath(app.Id) ?? throw new InvalidOperationException("App is not installed.");
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }
}
