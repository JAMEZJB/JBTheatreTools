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
    /// <summary>The payload generation folder to remove on uninstall (EXE or ZIP). Null for a
    /// legacy single-file install (older manifests have no field → treated as single-file).</summary>
    public string? InstallDir { get; set; }
}

/// <summary>
/// Installs / tracks / launches the downloaded Windows apps.
///
/// Most Windows assets are self-contained single <c>.exe</c> files (install = place the exe under the apps
/// dir). Heavy "Full" editions instead ship as a one-dir PyInstaller build inside a <c>.zip</c> (a single
/// huge onefile exe would start slowly) — those are extracted into a per-slot folder and the launcher .exe
/// inside is recorded. Either way the version is tracked in a JSON manifest.
/// </summary>
public sealed class InstallManager
{
    private static readonly Lazy<InstallManager> SharedInstance = new(() => new InstallManager());
    public static InstallManager Shared => SharedInstance.Value;

    public string SupportDir { get; }
    public string AppsDir { get; }
    public string CacheDir { get; }
    public string ManifestPath { get; }

    // Resolution caches for the read hot path. InstalledVersion/InstalledPath/InstalledDisplayName are called
    // per app on every refresh; each used to deserialize installed.json from disk (and DisplayName re-read the
    // exe's version info) every call. Cache a manifest snapshot + the resolved path/name per id, invalidated
    // on any manifest write (the only thing that changes what's on disk). `lock` is re-entrant, so the
    // resolution methods can call one another under one lock.
    private readonly object _readLock = new();
    private readonly object _mutationLock = new();
    private Dictionary<string, InstalledRecord>? _snapshot;
    private readonly Dictionary<string, string?> _pathCache = new();
    private readonly Dictionary<string, string?> _nameCache = new();

    public InstallManager(string? supportDirectory = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        SupportDir = supportDirectory ?? Path.Combine(local, "JBTheatreTools");
        AppsDir = Path.Combine(SupportDir, "apps");
        CacheDir = Path.Combine(SupportDir, "cache");
        ManifestPath = Path.Combine(SupportDir, "installed.json");
        Directory.CreateDirectory(AppsDir);
        Directory.CreateDirectory(CacheDir);
    }

    public Dictionary<string, InstalledRecord> Manifest(bool strict = false)
    {
        Dictionary<string, InstalledRecord> m = new();
        try
        {
            m = JsonSerializer.Deserialize<Dictionary<string, InstalledRecord>>(
                File.ReadAllText(ManifestPath)) ?? throw new JsonException("The install manifest is empty.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch when (!strict) { /* read-only display may fall through to empty */ }
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

    private void WriteManifest(Dictionary<string, InstalledRecord> m, bool strict = false)
    {
        try
        {
            Directory.CreateDirectory(SupportDir);
            InstallTransaction.WriteManifest(ManifestPath,
                JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (!strict) { Log.Write($"manifest write failed: {ex.Message}"); }
        // Installs/removals change what's on disk → drop the read caches so the next resolve re-reads.
        lock (_readLock) { _snapshot = null; _pathCache.Clear(); _nameCache.Clear(); }
    }

    /// <summary>A cached manifest snapshot for the read hot path (this app is the only writer, so a snapshot
    /// is authoritative between writes). Caller holds <c>_readLock</c>.</summary>
    private Dictionary<string, InstalledRecord> Snapshot() => _snapshot ??= Manifest();

    public string? InstalledVersion(string id)
    {
        lock (_readLock)
        {
            if (!Snapshot().TryGetValue(id, out var r)) return null;
            return ResolvedPath(id) != null ? r.Version : null;
        }
    }

    public string? InstalledPath(string id)
    {
        lock (_readLock) { return ResolvedPath(id); }
    }

    /// <summary>The installed path if the recorded exe still exists — existence checked once, then cached.
    /// Caller holds <c>_readLock</c>.</summary>
    private string? ResolvedPath(string id)
    {
        if (_pathCache.TryGetValue(id, out var cached)) return cached;
        var resolved = Snapshot().TryGetValue(id, out var r) && File.Exists(r.Path) ? r.Path : null;
        _pathCache[id] = resolved;
        return resolved;
    }

    /// <summary>The installed app's own display name, read from the exe's version info (ProductName) — the
    /// authoritative "what this app calls itself", so an installed row is never wrong. Parsed once, cached.</summary>
    public string? InstalledDisplayName(string id)
    {
        lock (_readLock)
        {
            if (_nameCache.TryGetValue(id, out var cached)) return cached;
            string? resolved = null;
            var path = ResolvedPath(id);
            if (path != null)
            {
                try
                {
                    var name = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductName;
                    resolved = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
                }
                catch { resolved = null; }
            }
            _nameCache[id] = resolved;
            return resolved;
        }
    }

    /// <summary>Installs a downloaded self-contained .exe and records its version. When
    /// <paramref name="toApplications"/> is true, also creates Start Menu + Desktop shortcuts so the
    /// app is launchable without this launcher (the Windows equivalent of macOS's Applications folder).</summary>
    /// <summary>One-time migration from v1.15.0, where a non-default variant (e.g. NDI "Full") was recorded
    /// under the plain app id. Re-keys such a record to its own variant slot. (The exe already has a
    /// distinct name on Windows, so no rename is needed.) No-op when nothing matches.</summary>
    public void MigrateVariantSlots(IEnumerable<CatalogApp> apps)
    {
        lock (_mutationLock)
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
    }

    public string Install(CatalogApp app, string version, string downloadedExe, string assetName,
        bool toApplications, string? variant = null, CancellationToken cancellationToken = default)
    {
        InstallTransaction.ValidateComponent(app.Id);
        var dir = Path.Combine(AppsDir, app.Id);
        var key = app.InstallKey(variant);
        InstalledRecord? previous = null;
        bool shortcutsUpdated = true;
        var payload = InstallTransaction.Install(dir, key, downloadedExe, assetName,
            FullApp.ExpectedStem(app.Name, app.VariantLabel(variant)), staged =>
            {
                lock (_mutationLock)
                {
                    // Never overwrite unreadable metadata with an apparently empty inventory.
                    var m = Manifest(strict: true);
                    m.TryGetValue(key, out previous);
                    m[key] = new InstalledRecord
                    {
                        Version = version, Path = staged.Executable,
                        InstalledAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        StartMenuShortcut = previous?.StartMenuShortcut,
                        DesktopShortcut = previous?.DesktopShortcut,
                        Variant = variant, InstallDir = staged.Directory
                    };
                    WriteManifest(m, strict: true); // the commit point; failure leaves old install intact
                }
            }, cancellationToken);

        try
        {
            // Shortcuts change only AFTER payload + manifest commit. Preserve an old shortcut and its
            // payload if recreating it fails, rather than removing the user's working link first.
            lock (_mutationLock)
            {
                var m = Manifest(strict: true);
                if (m.TryGetValue(key, out var current) && current.Path == payload.Executable)
                {
                    var name = Shortcuts.SafeName(app.Name + app.VariantSuffix(variant));
                    if (toApplications || previous?.StartMenuShortcut != null)
                    {
                        if (Shortcuts.CreateStartMenu(name, payload.Executable))
                        {
                            current.StartMenuShortcut = name;
                        }
                        else shortcutsUpdated = false;
                    }
                    if (toApplications || previous?.DesktopShortcut != null)
                    {
                        if (Shortcuts.CreateDesktop(name, payload.Executable))
                        {
                            current.DesktopShortcut = name;
                        }
                        else shortcutsUpdated = false;
                    }
                    WriteManifest(m, strict: true);
                    if (current.StartMenuShortcut == name && previous?.StartMenuShortcut is string oldStart && oldStart != name)
                        Shortcuts.RemoveStartMenu(oldStart);
                    if (current.DesktopShortcut == name && previous?.DesktopShortcut is string oldDesktop && oldDesktop != name)
                        Shortcuts.RemoveDesktop(oldDesktop);
                    if (shortcutsUpdated && previous != null)
                        RemovePreviousPayload(previous, payload, dir, m.Values);
                }
            }
        }
        catch (Exception ex)
        {
            // The install is already committed. A shortcut/cleanup failure must not turn it into
            // an apparent failed install or remove a payload still referenced by metadata.
            Log.Write($"install {app.Id}: post-install cleanup deferred: {ex.Message}");
        }
        return payload.Executable;
    }

    private static void RemovePreviousPayload(InstalledRecord previous, InstalledPayload current,
        string appDirectory, IEnumerable<InstalledRecord> records)
    {
        var old = Path.GetFullPath(previous.InstallDir ?? previous.Path);
        var root = Path.GetFullPath(appDirectory) + Path.DirectorySeparatorChar;
        if (!old.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(old, current.Directory, StringComparison.OrdinalIgnoreCase)) return;
        // A legacy slot can share a directory with another variant. Never remove a sibling's payload.
        if (records.Any(r => Path.GetFullPath(r.Path).Equals(old, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(r.Path).StartsWith(old + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            if (previous.InstallDir != null) Directory.Delete(old, recursive: true);
            else File.Delete(old);
        }
        catch (Exception ex) { Log.Write($"previous install retained: {ex.Message}"); }
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
    /// sibling variant (Light and Full share apps/&lt;id&gt;/) survives.</summary>
    public void Uninstall(string installKey)
    {
        lock (_mutationLock)
        {
            var m = Manifest();
            if (m.TryGetValue(installKey, out var rec))
            {
                if (rec.StartMenuShortcut != null) Shortcuts.RemoveStartMenu(rec.StartMenuShortcut);
                if (rec.DesktopShortcut != null) Shortcuts.RemoveDesktop(rec.DesktopShortcut);
                // Remove the payload: a whole extracted one-dir (.zip Full) install, or a single-file .exe.
                try
                {
                    if (rec.InstallDir != null && Directory.Exists(rec.InstallDir))
                        Directory.Delete(rec.InstallDir, recursive: true);
                    else if (File.Exists(rec.Path))
                        File.Delete(rec.Path);
                }
                catch (Exception ex) { Log.Write($"uninstall {installKey}: could not delete payload: {ex.Message}"); }
                // Clean the app's base dir once no sibling slot's files remain (Light/Full share apps/<id>/).
                try
                {
                    var appDir = Path.Combine(AppsDir, installKey.Split('@')[0]);
                    if (Directory.Exists(appDir) && !Directory.EnumerateFileSystemEntries(appDir).Any())
                        Directory.Delete(appDir);
                }
                catch (Exception ex) { Log.Write($"uninstall {installKey}: could not remove empty app dir: {ex.Message}"); }
            }
            if (m.Remove(installKey)) WriteManifest(m);
        }
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
        lock (_mutationLock)
        {
            var m = Manifest();
            if (!m.TryGetValue(installKey, out var r) || !File.Exists(r.Path)) return;
            if (toApplications)
            {
                var name = Shortcuts.SafeName(shortcutName);
                if (r.StartMenuShortcut == null && Shortcuts.CreateStartMenu(name, r.Path)) r.StartMenuShortcut = name;
                if (r.DesktopShortcut == null && Shortcuts.CreateDesktop(name, r.Path)) r.DesktopShortcut = name;
            }
            else
            {
                if (r.StartMenuShortcut != null) { Shortcuts.RemoveStartMenu(r.StartMenuShortcut); r.StartMenuShortcut = null; }
                if (r.DesktopShortcut != null) { Shortcuts.RemoveDesktop(r.DesktopShortcut); r.DesktopShortcut = null; }
            }
            WriteManifest(m);
        }
    }

    // --- Per-app shortcut toggles (from the row's ⋯ menu) ---

    public bool HasDesktopShortcut(string appId) => Manifest().TryGetValue(appId, out var r) && r.DesktopShortcut != null;
    public bool HasStartMenuShortcut(string appId) => Manifest().TryGetValue(appId, out var r) && r.StartMenuShortcut != null;

    public void SetDesktopShortcut(string installKey, string shortcutName, bool on)
    {
        lock (_mutationLock)
        {
            var m = Manifest();
            if (!m.TryGetValue(installKey, out var r) || !File.Exists(r.Path)) return;
            if (on && r.DesktopShortcut == null)
            {
                var name = Shortcuts.SafeName(shortcutName);
                if (Shortcuts.CreateDesktop(name, r.Path)) r.DesktopShortcut = name;
            }
            else if (!on && r.DesktopShortcut != null)
            {
                Shortcuts.RemoveDesktop(r.DesktopShortcut);
                r.DesktopShortcut = null;
            }
            WriteManifest(m);
        }
    }

    public void SetStartMenuShortcut(string installKey, string shortcutName, bool on)
    {
        lock (_mutationLock)
        {
            var m = Manifest();
            if (!m.TryGetValue(installKey, out var r) || !File.Exists(r.Path)) return;
            if (on && r.StartMenuShortcut == null)
            {
                var name = Shortcuts.SafeName(shortcutName);
                if (Shortcuts.CreateStartMenu(name, r.Path)) r.StartMenuShortcut = name;
            }
            else if (!on && r.StartMenuShortcut != null)
            {
                Shortcuts.RemoveStartMenu(r.StartMenuShortcut);
                r.StartMenuShortcut = null;
            }
            WriteManifest(m);
        }
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
        var actual = await Task.Run(() => Sha256Hex(file));   // 450 MB hash — never on the UI thread
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
