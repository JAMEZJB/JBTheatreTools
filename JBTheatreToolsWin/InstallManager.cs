using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace JBTheatreTools;

/// <summary>Outcome of integrity-checking a download (verify-if-present): the SHA-256 matched, the
/// release published no SHA256SUMS at all, or it published one but this asset isn't listed in it (e.g.
/// a name mismatch). The two "unverified" cases are distinguished so logs/messages can tell them apart.</summary>
public enum VerifyResult { Verified, NoManifest, AssetNotListed }

public sealed class InstalledRecord
{
    public string Version { get; set; } = "";
    public string Path { get; set; } = "";
    public string InstalledAt { get; set; } = "";
    /// <summary>LEGACY (pre-v1.11): one name covering BOTH the Start Menu and Desktop .lnk. Migrated
    /// into the two per-location fields on manifest read; kept only so old manifests deserialize.</summary>
    public string? ShortcutName { get; set; }
    /// <summary>.lnk base name in the Start Menu (null = no Start Menu shortcut).</summary>
    public string? StartMenuShortcut { get; set; }
    /// <summary>.lnk base name on the Desktop (null = no Desktop shortcut).</summary>
    public string? DesktopShortcut { get; set; }
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
        Dictionary<string, InstalledRecord> m = new();
        try
        {
            if (File.Exists(ManifestPath))
                m = JsonSerializer.Deserialize<Dictionary<string, InstalledRecord>>(
                    File.ReadAllText(ManifestPath)) ?? new();
        }
        catch { /* fall through to empty */ }
        // Migrate the legacy single ShortcutName (which meant BOTH locations) to per-location fields.
        foreach (var rec in m.Values)
        {
            if (string.IsNullOrEmpty(rec.ShortcutName)) continue;
            rec.StartMenuShortcut ??= rec.ShortcutName;
            rec.DesktopShortcut ??= rec.ShortcutName;
            rec.ShortcutName = null;   // persisted on the next WriteManifest
        }
        return m;
    }

    private void WriteManifest(Dictionary<string, InstalledRecord> m)
    {
        try
        {
            Directory.CreateDirectory(SupportDir);
            File.WriteAllText(ManifestPath,
                JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write($"manifest write failed: {ex.Message}"); }
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
        // Copy (not move) so the verified download stays in the cache; the caller removes it after a
        // successful install (mirroring the macOS zip cleanup). Moving would destroy the only copy, so a
        // mid-install failure would leave nothing to recover from.
        File.Copy(downloadedExe, dest, overwrite: true);

        var m = Manifest();
        // Remove shortcuts from any previous install (the name may have changed), remembering which
        // locations the user had so an update re-creates them.
        bool hadStart = false, hadDesktop = false;
        if (m.TryGetValue(app.Id, out var prev))
        {
            hadStart = prev.StartMenuShortcut != null;
            hadDesktop = prev.DesktopShortcut != null;
            if (prev.StartMenuShortcut != null) Shortcuts.RemoveStartMenu(prev.StartMenuShortcut);
            if (prev.DesktopShortcut != null) Shortcuts.RemoveDesktop(prev.DesktopShortcut);
        }

        // The global install-to-Applications setting creates both; otherwise keep whatever the user
        // had added per-app. Best-effort: a shortcut failure must not abort the install.
        string? startName = null, desktopName = null;
        try
        {
            var name = Shortcuts.SafeName(TryProductName(dest) ?? app.Name);
            if (toApplications || hadStart) { Shortcuts.CreateStartMenu(name, dest); startName = name; }
            if (toApplications || hadDesktop) { Shortcuts.CreateDesktop(name, dest); desktopName = name; }
        }
        catch (Exception ex)
        {
            Log.Write($"install {app.Id}: shortcut creation failed: {ex.Message}");
        }

        m[app.Id] = new InstalledRecord
        {
            Version = version,
            Path = dest,
            InstalledAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            StartMenuShortcut = startName,
            DesktopShortcut = desktopName
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

    /// <summary>Removes the installed app's folder, its shortcuts, and its manifest entry.</summary>
    public void Uninstall(string appId)
    {
        var m = Manifest();
        if (m.TryGetValue(appId, out var rec))
        {
            if (rec.StartMenuShortcut != null) Shortcuts.RemoveStartMenu(rec.StartMenuShortcut);
            if (rec.DesktopShortcut != null) Shortcuts.RemoveDesktop(rec.DesktopShortcut);
        }
        var dir = Path.Combine(AppsDir, appId);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { Log.Write($"uninstall {appId}: could not delete {dir}: {ex.Message}"); }
        if (m.Remove(appId)) WriteManifest(m);
    }

    // --- Reconcile install location (Windows: the exe never moves, only its shortcuts) ---

    /// <summary>True if the app's shortcuts don't match the setting (need creating, or removing).</summary>
    public bool NeedsShortcutSync(string appId, bool toApplications)
    {
        var m = Manifest();
        if (!m.TryGetValue(appId, out var r) || !File.Exists(r.Path)) return false;
        bool hasBoth = r.StartMenuShortcut != null && r.DesktopShortcut != null;
        bool hasAny = r.StartMenuShortcut != null || r.DesktopShortcut != null;
        return toApplications ? !hasBoth : hasAny;
    }

    /// <summary>Adds or removes an installed app's Start Menu/Desktop shortcuts to match the setting
    /// (the global reconcile acts on BOTH locations, including per-app-added ones).</summary>
    public void SyncShortcuts(CatalogApp app, bool toApplications)
    {
        var m = Manifest();
        if (!m.TryGetValue(app.Id, out var r) || !File.Exists(r.Path)) return;
        if (toApplications)
        {
            var name = Shortcuts.SafeName(TryProductName(r.Path) ?? app.Name);
            if (r.StartMenuShortcut == null) { Shortcuts.CreateStartMenu(name, r.Path); r.StartMenuShortcut = name; }
            if (r.DesktopShortcut == null) { Shortcuts.CreateDesktop(name, r.Path); r.DesktopShortcut = name; }
        }
        else
        {
            if (r.StartMenuShortcut != null) { Shortcuts.RemoveStartMenu(r.StartMenuShortcut); r.StartMenuShortcut = null; }
            if (r.DesktopShortcut != null) { Shortcuts.RemoveDesktop(r.DesktopShortcut); r.DesktopShortcut = null; }
        }
        WriteManifest(m);
    }

    // --- Per-app shortcut toggles (from the row's ⋯ menu) ---

    public bool HasDesktopShortcut(string appId) => Manifest().TryGetValue(appId, out var r) && r.DesktopShortcut != null;
    public bool HasStartMenuShortcut(string appId) => Manifest().TryGetValue(appId, out var r) && r.StartMenuShortcut != null;

    public void SetDesktopShortcut(CatalogApp app, bool on)
    {
        var m = Manifest();
        if (!m.TryGetValue(app.Id, out var r) || !File.Exists(r.Path)) return;
        if (on && r.DesktopShortcut == null)
        {
            var name = Shortcuts.SafeName(TryProductName(r.Path) ?? app.Name);
            Shortcuts.CreateDesktop(name, r.Path);
            r.DesktopShortcut = name;
        }
        else if (!on && r.DesktopShortcut != null)
        {
            Shortcuts.RemoveDesktop(r.DesktopShortcut);
            r.DesktopShortcut = null;
        }
        WriteManifest(m);
    }

    public void SetStartMenuShortcut(CatalogApp app, bool on)
    {
        var m = Manifest();
        if (!m.TryGetValue(app.Id, out var r) || !File.Exists(r.Path)) return;
        if (on && r.StartMenuShortcut == null)
        {
            var name = Shortcuts.SafeName(TryProductName(r.Path) ?? app.Name);
            Shortcuts.CreateStartMenu(name, r.Path);
            r.StartMenuShortcut = name;
        }
        else if (!on && r.StartMenuShortcut != null)
        {
            Shortcuts.RemoveStartMenu(r.StartMenuShortcut);
            r.StartMenuShortcut = null;
        }
        WriteManifest(m);
    }

    // --- Download integrity (SHA-256) ---

    /// <summary>
    /// Integrity-checks a freshly downloaded asset before install. The file size must match the
    /// release's declared size, and — when the release publishes a <c>SHA256SUMS</c> manifest — its
    /// SHA-256 must match. A mismatch deletes the file and throws. Returns <c>Verified</c> on a match,
    /// or (verify-if-present) <c>NoManifest</c> / <c>AssetNotListed</c> when there's nothing to check
    /// against: the caller proceeds but should report it as unverified.
    /// </summary>
    public static async Task<VerifyResult> VerifyDownloadAsync(string file, ReleaseAsset asset, ReleaseInfo release,
                                                               string owner, string repo, GitHubClient client)
    {
        var fi = new FileInfo(file);
        if (asset.Size > 0 && fi.Exists && fi.Length != asset.Size)
        {
            TryDelete(file);
            throw new Exception($"Download is {fi.Length} bytes but the release lists {asset.Size}. Aborting install.");
        }
        var sumsAsset = release.Assets.FirstOrDefault(a => a.Name == "SHA256SUMS");
        if (sumsAsset == null) return VerifyResult.NoManifest;

        var sumsPath = file + ".SHA256SUMS";
        await client.DownloadAssetAsync(owner, repo, sumsAsset.Id, sumsPath, null);
        string text;
        try { text = File.ReadAllText(sumsPath); }
        finally { TryDelete(sumsPath); }

        var expected = ExpectedSha256(asset.Name, text);
        if (expected == null) return VerifyResult.AssetNotListed;
        var actual = Sha256Hex(file);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(file);
            throw new Exception($"Checksum mismatch for {asset.Name} — the download does not match the release's SHA256SUMS. Aborting install.");
        }
        return VerifyResult.Verified;
    }

    /// <summary>Returns the expected hex SHA-256 for <paramref name="assetName"/> from a SHA256SUMS body
    /// (standard <c>&lt;hex&gt;␠␠&lt;filename&gt;</c> lines), or null if the asset isn't listed.</summary>
    public static string? ExpectedSha256(string assetName, string sumsText)
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

    /// <summary>Streams <paramref name="path"/> through SHA-256 and returns the lowercase hex digest.</summary>
    public static string Sha256Hex(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
