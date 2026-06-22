using System.Diagnostics;
using System.Text.Json;

namespace JBTheatreTools;

public sealed class InstalledRecord
{
    public string Version { get; set; } = "";
    public string Path { get; set; } = "";
    public string InstalledAt { get; set; } = "";
    /// <summary>Name of the Start Menu/Desktop shortcuts created for this app, if any — so uninstall
    /// (and reinstall) can remove them. Null when the app was installed without shortcuts.</summary>
    public string? ShortcutName { get; set; }
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

    /// <summary>The installed app's own display name, read live from the exe's version info
    /// (ProductName) — the authoritative "what this app calls itself", so an installed row is never wrong.</summary>
    public string? InstalledDisplayName(string id)
    {
        var path = InstalledPath(id);
        if (path == null) return null;
        try
        {
            var name = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductName;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch { return null; }
    }

    /// <summary>Installs a downloaded self-contained .exe and records its version. When
    /// <paramref name="toApplications"/> is true, also creates Start Menu + Desktop shortcuts so the
    /// app is launchable without this launcher (the Windows equivalent of macOS's Applications folder).</summary>
    public string Install(CatalogApp app, string version, string downloadedExe, string assetName, bool toApplications)
    {
        var dir = Path.Combine(AppsDir, app.Id);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, assetName);
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(downloadedExe, dest);

        var m = Manifest();
        // Remove shortcuts from any previous install (the name or the setting may have changed).
        if (m.TryGetValue(app.Id, out var prev) && !string.IsNullOrEmpty(prev.ShortcutName))
            Shortcuts.Remove(prev.ShortcutName!);

        string? shortcutName = null;
        if (toApplications)
        {
            // Name the shortcut after the app's own ProductName, falling back to the catalog name.
            shortcutName = Shortcuts.SafeName(TryProductName(dest) ?? app.Name);
            Shortcuts.Create(shortcutName, dest);
        }

        m[app.Id] = new InstalledRecord
        {
            Version = version,
            Path = dest,
            InstalledAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ShortcutName = shortcutName
        };
        WriteManifest(m);
        return dest;
    }

    private static string? TryProductName(string exe)
    {
        try
        {
            var n = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductName;
            return string.IsNullOrWhiteSpace(n) ? null : n.Trim();
        }
        catch { return null; }
    }

    public void Launch(CatalogApp app)
    {
        var path = InstalledPath(app.Id) ?? throw new InvalidOperationException("App is not installed.");
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    /// <summary>Removes the installed app's folder and its manifest entry.</summary>
    public void Uninstall(string appId)
    {
        var m = Manifest();
        if (m.TryGetValue(appId, out var rec) && !string.IsNullOrEmpty(rec.ShortcutName))
            Shortcuts.Remove(rec.ShortcutName!);
        var dir = Path.Combine(AppsDir, appId);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        if (m.Remove(appId)) WriteManifest(m);
    }

    // MARK: - Reconcile install location (Windows: the exe never moves, only its shortcuts)

    /// <summary>True if the app's shortcuts don't match the setting (need creating, or removing).</summary>
    public bool NeedsShortcutSync(string appId, bool toApplications)
    {
        var m = Manifest();
        if (!m.TryGetValue(appId, out var r) || !File.Exists(r.Path)) return false;
        bool has = !string.IsNullOrEmpty(r.ShortcutName);
        return toApplications ? !has : has;
    }

    /// <summary>Adds or removes an installed app's Start Menu/Desktop shortcuts to match the setting.</summary>
    public void SyncShortcuts(CatalogApp app, bool toApplications)
    {
        var m = Manifest();
        if (!m.TryGetValue(app.Id, out var r) || !File.Exists(r.Path)) return;
        if (toApplications)
        {
            if (string.IsNullOrEmpty(r.ShortcutName))
            {
                var name = Shortcuts.SafeName(TryProductName(r.Path) ?? app.Name);
                Shortcuts.Create(name, r.Path);
                r.ShortcutName = name;
                WriteManifest(m);
            }
        }
        else if (!string.IsNullOrEmpty(r.ShortcutName))
        {
            Shortcuts.Remove(r.ShortcutName!);
            r.ShortcutName = null;
            WriteManifest(m);
        }
    }
}
