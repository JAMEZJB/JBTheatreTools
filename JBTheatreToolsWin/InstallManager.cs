using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace JBTheatreTools;

/// <summary>Outcome of integrity-checking a download (verify-if-present): the SHA-256 matched, the
/// release published no SHA256SUMS at all, or it published one but this asset isn't listed in it (e.g.
/// a name mismatch). The two "unverified" cases are distinguished so logs/messages can tell them apart.</summary>
public enum VerifyResult
{
    /// <summary>Suite-signed manifest, asset listed, hash matches — the only fully trusted outcome.</summary>
    Verified,
    /// <summary>The release publishes no SHA256SUMS at all.</summary>
    NoManifest,
    /// <summary>SHA256SUMS exists but doesn't list this asset.</summary>
    AssetNotListed,
    /// <summary>Asset listed and hash matches, but the manifest carries no suite signature — so it only proves
    /// transport integrity, not authenticity. Accepted for explicit older-tag installs only (audit F1).</summary>
    Unsigned,
}

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
    /// <summary>Which variant (e.g. "standard"/"full") is installed, for apps that ship variants
    /// (null = single-variant app or a manifest written before variants existed).</summary>
    public string? Variant { get; set; }
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
    /// <summary>One-time migration from v1.15.0, where a non-default variant (e.g. NDI "Full") was recorded
    /// under the plain app id. Re-keys such a record to its own variant slot. (The exe already has a
    /// distinct name on Windows, so no rename is needed.) No-op when nothing matches.</summary>
    public void MigrateVariantSlots(IEnumerable<CatalogApp> apps)
    {
        var m = Manifest();
        bool changed = false;
        foreach (var app in apps.Where(a => a.HasVariants))
        {
            if (!m.TryGetValue(app.Id, out var rec) || rec.Variant == null || app.IsDefaultVariant(rec.Variant)) continue;
            var key = app.InstallKey(rec.Variant);
            if (m.ContainsKey(key)) continue;   // that slot already has its own record — leave both alone
            m[key] = rec;
            m.Remove(app.Id);
            changed = true;
        }
        if (changed) WriteManifest(m);
    }

    public string Install(CatalogApp app, string version, string downloadedExe, string assetName, bool toApplications, string? variant = null)
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
        // Each variant is its own install slot (Standard and Full can coexist — their exes have different
        // names, so they share apps/<id>/). Only the previous install of THIS slot is replaced.
        var key = app.InstallKey(variant);
        if (m.TryGetValue(key, out var prev))
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
            // A non-default variant's shortcuts carry its label (" (Full)") so they sit beside the default's.
            // F10: name the shortcut from the CATALOG (trusted), not the downloaded exe's ProductName — a
            // hostile exe could otherwise declare ProductName "Google Chrome" and clobber the user's real
            // Google Chrome.lnk (which Uninstall would then delete).
            var name = Shortcuts.SafeName(app.Name + app.VariantSuffix(variant));
            if (toApplications || hadStart) { Shortcuts.CreateStartMenu(name, dest); startName = name; }
            if (toApplications || hadDesktop) { Shortcuts.CreateDesktop(name, dest); desktopName = name; }
        }
        catch (Exception ex)
        {
            Log.Write($"install {app.Id}: shortcut creation failed: {ex.Message}");
        }

        m[key] = new InstalledRecord
        {
            Version = version,
            Path = dest,
            InstalledAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            StartMenuShortcut = startName,
            DesktopShortcut = desktopName,
            Variant = variant
        };
        WriteManifest(m);
        return dest;
    }

    /// <summary>Launches the app's default-variant slot (CLI / single-variant apps).</summary>
    public void Launch(CatalogApp app) => Launch(app.Id);

    /// <summary>Launches a specific install slot (the row's selected variant).</summary>
    public void Launch(string installKey)
    {
        var path = InstalledPath(installKey) ?? throw new InvalidOperationException("App is not installed.");
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    /// <summary>Removes the installed app's folder, its shortcuts, and its manifest entry.</summary>
    /// <summary>Uninstalls ONE install slot (an app, or one variant of a variant app): removes that slot's
    /// exe and shortcuts. The containing app dir is deleted only once nothing else is left in it, so a
    /// sibling variant (Standard and Full share apps/&lt;id&gt;/) survives.</summary>
    public void Uninstall(string installKey)
    {
        var m = Manifest();
        if (m.TryGetValue(installKey, out var rec))
        {
            if (rec.StartMenuShortcut != null) Shortcuts.RemoveStartMenu(rec.StartMenuShortcut);
            if (rec.DesktopShortcut != null) Shortcuts.RemoveDesktop(rec.DesktopShortcut);
            try { if (File.Exists(rec.Path)) File.Delete(rec.Path); }
            catch (Exception ex) { Log.Write($"uninstall {installKey}: could not delete {rec.Path}: {ex.Message}"); }
            try
            {
                var dir = Path.GetDirectoryName(rec.Path);
                if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) { Log.Write($"uninstall {installKey}: could not remove empty dir: {ex.Message}"); }
        }
        if (m.Remove(installKey)) WriteManifest(m);
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
    /// <summary>Adds (toApplications) or removes both shortcuts for one install slot; <paramref name="shortcutName"/>
    /// is the display name to use (already carrying any variant suffix).</summary>
    public void SyncShortcuts(string installKey, string shortcutName, bool toApplications)
    {
        var m = Manifest();
        if (!m.TryGetValue(installKey, out var r) || !File.Exists(r.Path)) return;
        if (toApplications)
        {
            var name = Shortcuts.SafeName(shortcutName);
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

    public void SetDesktopShortcut(string installKey, string shortcutName, bool on)
    {
        var m = Manifest();
        if (!m.TryGetValue(installKey, out var r) || !File.Exists(r.Path)) return;
        if (on && r.DesktopShortcut == null)
        {
            var name = Shortcuts.SafeName(shortcutName);
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

    public void SetStartMenuShortcut(string installKey, string shortcutName, bool on)
    {
        var m = Manifest();
        if (!m.TryGetValue(installKey, out var r) || !File.Exists(r.Path)) return;
        if (on && r.StartMenuShortcut == null)
        {
            var name = Shortcuts.SafeName(shortcutName);
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
        byte[] sumsBytes;
        try { sumsBytes = File.ReadAllBytes(sumsPath); }
        finally { TryDelete(sumsPath); }

        // Authenticity (audit F1): the manifest is trusted only if the controller's OFFLINE minisign
        // signature over it verifies with the embedded suite key, and its signed trusted comment names
        // THIS release ("<repo> <tag>") so a manifest from another release can't be replayed. A present
        // but invalid signature is always fatal; an absent one downgrades the outcome to Unsigned.
        bool signed = false;
        var sigAsset = release.Assets.FirstOrDefault(a => a.Name == "SHA256SUMS.minisig");
        if (sigAsset != null)
        {
            var sigPath = file + ".SHA256SUMS.minisig";
            await client.DownloadAssetAsync(owner, repo, sigAsset.Id, sigPath, null);
            string sigText;
            try { sigText = File.ReadAllText(sigPath); }
            finally { TryDelete(sigPath); }
            try { Minisign.Verify(sumsBytes, sigText, $"{repo} {release.TagName}"); }
            catch (Minisign.VerifyException ex)
            {
                TryDelete(file);
                throw new Exception($"Refusing {asset.Name}: {ex.Message} Aborting install.");
            }
            signed = true;
        }

        var text = System.Text.Encoding.UTF8.GetString(sumsBytes);
        var expected = ExpectedSha256(asset.Name, text);
        if (expected == null) return VerifyResult.AssetNotListed;
        var actual = Sha256Hex(file);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(file);
            throw new Exception($"Checksum mismatch for {asset.Name} — the download does not match the release's SHA256SUMS. Aborting install.");
        }
        return signed ? VerifyResult.Verified : VerifyResult.Unsigned;
    }

    /// <summary>Why a non-Verified outcome blocks a strict (current-release) install — shared by the GUI
    /// install, the launcher self-update and the CLI so the wording stays consistent.</summary>
    public static string StrictFailureReason(VerifyResult result, string assetName) => result switch
    {
        VerifyResult.Verified => "verified",
        VerifyResult.NoManifest => "this release publishes no SHA256SUMS checksums",
        VerifyResult.AssetNotListed => $"“{assetName}” isn't listed in this release's SHA256SUMS",
        _ => "this release's SHA256SUMS isn't signed with the suite key",
    };

    /// <summary>Returns the expected hex SHA-256 for <paramref name="assetName"/> from a SHA256SUMS body
    /// (standard <c>&lt;hex&gt;␠␠&lt;filename&gt;</c> lines), or null if the asset isn't listed.</summary>
    /// <summary>The hash listed for an asset in a SHA256SUMS text (parsing lives in Core: <see cref="Sha256Sums"/>).</summary>
    public static string? ExpectedSha256(string assetName, string sumsText) => Sha256Sums.Expected(assetName, sumsText);

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
