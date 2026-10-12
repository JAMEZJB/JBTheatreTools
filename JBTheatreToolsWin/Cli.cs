using System.Runtime.InteropServices;

namespace JBTheatreTools;

/// <summary>
/// Headless command-line interface (parity with the macOS CLI) — used for scripting and for
/// verifying the catalog, GitHub API access, asset resolution, and the download/install path.
///
/// Token order: <c>--token &lt;pat&gt;</c>, then <c>$GITHUB_TOKEN</c>, then Credential Manager.
///
///   JBTheatreTools.exe --list                 [--json] [--token X] [--catalog path]
///   JBTheatreTools.exe --installed            [--json]
///   JBTheatreTools.exe --releases &lt;id&gt;        [--token X]
///   JBTheatreTools.exe --install  &lt;id&gt;        [--tag vX.Y.Z] [--token X]
///   JBTheatreTools.exe --install-file &lt;id&gt; &lt;file&gt; --tag vX.Y.Z [--edition E] [--sums SHA256SUMS]
///   JBTheatreTools.exe --download &lt;id&gt; --tag vX.Y.Z --asset &lt;name&gt; --to &lt;folder&gt; [--edition E] [--json]
///   JBTheatreTools.exe --uninstall &lt;id&gt;
///   JBTheatreTools.exe --launch   &lt;id&gt;
///   JBTheatreTools.exe --self-check           [--token X]
///   JBTheatreTools.exe --backup-all  &lt;file&gt;   [--include-passwords] [--passphrase-stdin] [--apps a,b] [--no-launcher]
///   JBTheatreTools.exe --restore-all &lt;file&gt;   [--no-passwords] [--passphrase-stdin] [--apps a,b] [--no-launcher]
///   JBTheatreTools.exe --help
/// </summary>
public static partial class Cli
{
    // Single source of truth for the recognised verbs — Program.cs reads this to decide CLI-vs-GUI, so
    // the dispatch allowlist can't drift from what Run actually handles.
    public static readonly HashSet<string> Commands = new()
    {
        "--list", "--installed", "--releases", "--install", "--install-file", "--download", "--uninstall",
        "--launch", "--self-check", "--self-update", "--help", "-h",
        "--backup-all", "--restore-all",
    };

    /// <summary>Download-server override (`--server` / `--server-pass`): when set, every client the
    /// CLI builds goes through the relay with Basic auth instead of GitHub with a Bearer token.</summary>
    private static string? _serverBase;
    private static string? _serverPass;

    private static bool ServerConfigured => _serverBase != null && _serverPass != null;

    /// <summary>ARM64 PCs: the install slots set (in the app) to use the x64 build — the command line installs the same
    /// build the app would. The CLI works on each app's default slot.</summary>
    private static List<string> _x64Slots = new();

    private static string? AssetName(CatalogApp app) => app.WindowsAsset(null, _x64Slots);

    /// <summary>The client for this invocation: the server relay when configured, else direct GitHub.</summary>
    private static GitHubClient MakeClient(string? token) =>
        ServerConfigured ? new GitHubClient(_serverBase!, _serverPass!) : new GitHubClient(token);

    /// <summary>Whether this invocation has ANY usable download auth (token or server pair).</summary>
    private static bool HasAuth(string? token) => token != null || ServerConfigured;

    public static async Task<int> Run(string[] args)
    {
        // History.Add queues its write on the thread pool; the process exits as soon as this returns, so wait for
        // every queued line (bounded) before any command hands back its exit code.
        try { return await RunCommandAsync(args); }
        finally { History.Flush(); }
    }

    private static async Task<int> RunCommandAsync(string[] args)
    {
        // The verb may appear anywhere (e.g. `--token X --install helo`); find the first recognised one
        // rather than assuming args[0]. Args present but no verb → usage error (don't open the GUI).
        var cmd = args.FirstOrDefault(a => Commands.Contains(a));
        if (cmd == null) { PrintHelp(); return args.Length > 0 ? 2 : 0; }
        if (cmd is "--help" or "-h") { PrintHelp(); return 0; }

        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        string? catalogPath = null;
        string? tag = null;
        bool toApplications = false, json = false;
        string? sums = null, assetName = null, toFolder = null, edition = null;
        var positional = new List<string>();
        bool cmdSkipped = false;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!cmdSkipped && a == cmd) { cmdSkipped = true; continue; }
            switch (a)
            {
                case "--token": if (++i < args.Length) token = args[i]; break;
                case "--catalog": if (++i < args.Length) catalogPath = args[i]; break;
                case "--tag": if (++i < args.Length) tag = args[i]; break;
                case "--to-applications": toApplications = true; break;
                case "--json": json = true; break;
                case "--sums": if (++i < args.Length) sums = args[i]; break;
                case "--asset": if (++i < args.Length) assetName = args[i]; break;
                case "--to": if (++i < args.Length) toFolder = args[i]; break;
                case "--edition": if (++i < args.Length) edition = args[i]; break;
                case "--server": if (++i < args.Length) _serverBase = args[i]; break;
                case "--server-pass": if (++i < args.Length) _serverPass = args[i]; break;
                default: positional.Add(a); break;
            }
        }
        // Only verbs that talk to GitHub need credentials. Local verbs (--installed / --uninstall /
        // --launch) must never touch the Credential Manager / Keychain-equivalent (audit F14).
        var needsAuth = cmd is not ("--installed" or "--uninstall" or "--launch" or "--self-update" or "--install-file");
        if (needsAuth) token ??= SafeLoadToken();

        Catalog catalog;
        try { catalog = Catalog.Load(catalogPath); }
        catch (Exception ex) { return Fail(ExitCode.Failed, $"could not load catalog: {ex.Message}"); }
        try
        {
            // Normalise the shared install manifest the same way the GUI does (v1.15.0 → per-variant slots),
            // so the CLI sees the same slots as the app.
            InstallManager.Shared.MigrateVariantSlots(catalog.Apps);
        }
        catch (Exception ex) { return Fail(CodeFor(ex), ex.Message); }
        // Every run brings connectors.json and the entry points in step (and repairs them): Claude starts apps through
        // them, and another launcher process may have changed what's installed since they were last written.
        if (OperatingSystem.IsWindows()) ConnectorShims.Start(catalog.Apps);
        if (cmd is "--backup-all" or "--restore-all") return await BackupCommandAsync(cmd, catalog, args);

        // Resolve download auth: explicit server flags win (either flag implies server mode, the URL
        // defaulting to the built-in relay); then a usable token; then the GUI-configured server mode
        // (built-in URL + saved passphrase) so a machine set up in the app works with no flags at all.
        var settings = AppSettings.Load();
        _x64Slots = settings.X64Slots;
        var builtInServer = AuthClient.ResolveServerUrl(settings, catalog.DownloadServer);
        if (!needsAuth)
        {
            // Local verb — dispatch with no credentials resolved. --self-update takes only what's on the command line
            // (--server/--server-pass, --token/GITHUB_TOKEN): the launcher's repo is public, so it never reads the saved
            // passphrase by itself.
            if (cmd == "--self-update" && (_serverBase != null || _serverPass != null)) _serverBase ??= builtInServer;
        }
        else if (_serverBase != null || _serverPass != null)
        {
            _serverBase ??= builtInServer;
            _serverPass ??= SafeLoadServerPass();
        }
        else if (token == null && settings.AuthMode == "server" && builtInServer != null)
        {
            var pass = SafeLoadServerPass();
            if (pass != null) { _serverBase = builtInServer; _serverPass = pass; }
        }

        // Show lock (set in the app) pauses installs, updates and removals — the command line honours it too.
        if ((settings.ShowLock || ClaudeSettingsFile.SuiteShowLock() == true) && cmd is "--install" or "--install-file" or "--uninstall")
            return Fail(ExitCode.ShowLock, "show lock is on — installs, updates and uninstalls are paused. Turn it off in JB Theatre Tools (More ▾ → Show lock) first.");

        return cmd switch
        {
            "--list"       => json ? await ListJsonAsync(catalog, token) : await ListAsync(catalog, token),
            "--installed"  => json ? InstalledJson(catalog) : Installed(catalog),
            "--releases"   => await ReleasesAsync(catalog, token, positional.FirstOrDefault()),
            "--install"    => await InstallAsync(catalog, token, positional.FirstOrDefault(), tag, edition, toApplications),
            "--install-file" => InstallFile(catalog, positional, tag, sums, edition, toApplications),
            "--download"   => await DownloadAsync(catalog, token, positional.FirstOrDefault(), tag, assetName, toFolder, edition, json),
            "--uninstall"  => Uninstall(catalog, positional.FirstOrDefault(), edition),
            "--launch"     => Launch(catalog, positional.FirstOrDefault()),
            "--self-check" => await SelfCheckAsync(catalog, token),
            "--self-update" => await SelfUpdateAsync(catalog, settings, token),
            _              => PrintHelpReturn(),
        };
    }

    private static async Task<int> ListAsync(Catalog catalog, string? token)
    {
        Console.WriteLine($"JBTheatreTools — {catalog.Apps.Count} apps (platform: {Platform.NativeKey})\n");
        using var client = HasAuth(token) ? MakeClient(token) : null;
        foreach (var app in catalog.Apps)
        {
            var installed = InstallManager.Shared.InstalledVersion(app.Id) ?? "—";
            string latest = "?", note = "";
            if (client != null)
            {
                try
                {
                    // Use the releases LIST endpoint (like the GUI and the macOS CLI) so we can tell
                    // "no access" (404 → hidden) apart from "accessible but no release yet" (200 []).
                    var all = await client.ReleasesAsync(app.Owner, app.Repo);
                    var info = Versions.Latest(all);
                    if (info == null) { latest = "none"; note = "accessible, no release yet"; }
                    else
                    {
                        latest = info.TagName;
                        var name = AssetName(app);
                        var asset = info.Assets.FirstOrDefault(a => a.Name == name);
                        note = asset != null
                            ? $"{asset.Name} ({ByteString(asset.Size)})"
                            : $"⚠ no Windows asset ({name})";
                    }
                }
                catch (GitHubException ge) when (ge.Kind == GitHubErrorKind.NotAccessible) { latest = "—"; note = "HIDDEN — token has no access to this repo"; }
                catch (GitHubException ge) when (ge.Kind == GitHubErrorKind.Unauthorized) { latest = "—"; note = "token invalid or expired"; }
                catch (Exception ex) { latest = "error"; note = ex.Message; }
            }
            else note = "no auth — set $GITHUB_TOKEN, pass --token, or --server + --server-pass";

            Console.WriteLine($"  {Pad(app.Name, 20)}  installed={Pad(installed, 8)}  latest={Pad(latest, 10)}  {note}");
        }
        return 0;
    }

    private static int Installed(Catalog catalog)
    {
        var m = InstallManager.Shared.Manifest();
        if (m.Count == 0) { Console.WriteLine("No apps installed."); return 0; }
        Console.WriteLine($"Installed apps ({InstallManager.Shared.ManifestPath}):\n");
        foreach (var app in catalog.Apps)
        {
            // Every install slot: the app itself, or one per variant for variant apps (Light + Full).
            var slots = app.HasVariants && app.Variants != null
                ? app.Variants.Select(v => (key: app.InstallKey(v.Id), label: (string?)v.Label)).ToList()
                : new List<(string key, string? label)> { (app.Id, null) };
            foreach (var s in slots)
                if (m.TryGetValue(s.key, out var r))
                {
                    var name = s.label != null ? $"{app.Name} ({s.label})" : app.Name;
                    Console.WriteLine($"  {Pad(name, 20)}  {Pad(r.Version, 10)}  {r.Path}");
                }
        }
        return 0;
    }

    private static async Task<int> ReleasesAsync(Catalog catalog, string? token, string? id)
    {
        var app = id != null ? catalog.Apps.FirstOrDefault(a => a.Id == id) : null;
        if (app == null) return Fail(ExitCode.Usage, $"pass an app id. Known ids: {string.Join(", ", catalog.Apps.Select(a => a.Id))}");
        if (!HasAuth(token)) return Fail(ExitCode.NoAccess, "no auth (token or --server + --server-pass).");
        using var client = MakeClient(token);
        try
        {
            var all = await client.ReleasesAsync(app.Owner, app.Repo);
            Console.WriteLine($"{app.Name} — {all.Count} release(s):\n");
            foreach (var rel in all)
            {
                bool hasWin = rel.Assets.Any(a => a.Name == AssetName(app));
                var flags = (rel.Prerelease ? " [pre-release]" : "") + (hasWin ? "" : " [no Windows asset]");
                Console.WriteLine($"  {Pad(rel.TagName, 12)}{flags}");
            }
            return 0;
        }
        catch (Exception ex) { return Fail(CodeFor(ex), ex.Message); }
    }

    private static async Task<int> InstallAsync(Catalog catalog, string? token, string? id, string? tag, string? edition, bool toApplications)
    {
        var app = id != null ? catalog.Apps.FirstOrDefault(a => a.Id == id) : null;
        if (app == null) return Fail(ExitCode.Usage, $"pass an app id. Known ids: {string.Join(", ", catalog.Apps.Select(a => a.Id))}");
        if (!app.TryResolveEdition(edition, out var variant))
            return Fail(ExitCode.Usage, $"{app.Name} has no edition \"{edition}\". Editions: {app.EditionChoices}.");
        var slot = app.InstallKey(variant);
        var appName = app.Name + app.VariantSuffix(variant);
        if (!HasAuth(token)) return Fail(ExitCode.NoAccess, "no auth (set $GITHUB_TOKEN, pass --token, save one in the app, or use --server + --server-pass).");

        using var client = MakeClient(token);
        bool downloading = false;
        try
        {
            var all = await client.ReleasesAsync(app.Owner, app.Repo);
            var rel = tag != null
                ? all.FirstOrDefault(r => VersionCompare.Equal(r.TagName, tag))   // "1.2.0" finds "v1.2.0"
                : Versions.Latest(all);
            if (rel == null) return Fail(ExitCode.NotFound, $"version {tag ?? "latest"} not found.");
            var name = app.WindowsAsset(variant, _x64Slots);
            var asset = rel.Assets.FirstOrDefault(a => a.Name == name);
            if (asset == null) return Fail(ExitCode.NotFound, $"release {rel.TagName} has no Windows asset{(name == null ? "" : $" ({name})")}.");

            Console.WriteLine($"Downloading {asset.Name} ({ByteString(asset.Size)}) @ {rel.TagName}…");
            var cache = Path.Combine(InstallManager.Shared.CacheDir, $"{slot.Replace('@', '-')}-{rel.TagName}-{asset.Name}");
            int last = -1;
            var progress = new Progress<double>(p =>
            {
                int pct = (int)(p * 100);
                if (pct != last && pct % 10 == 0) { last = pct; Console.WriteLine($"  {pct}%"); }
            });
            downloading = true;
            await client.DownloadAssetAsync(app.Owner, app.Repo, asset.Id, cache, progress);
            downloading = false;
            var verification = await InstallManager.VerifyDownloadAsync(cache, asset, rel, app.Owner, app.Repo, client);
            // Strict for current releases (no --tag): must checksum-verify. An explicit --tag install
            // (older build) stays verify-if-present. A hash mismatch always aborts (throws above).
            if (tag == null && verification != VerifyResult.Verified)
            {
                InstallManager.TryDelete(cache);
                var reason = InstallManager.StrictFailureReason(verification, asset.Name);
                Log.Write($"cli: install {slot} {rel.TagName} BLOCKED (strict) — {reason}");
                return Fail(ExitCode.Rejected, $"refusing to install {appName} {rel.TagName} — {reason}. (Re-run with --tag {rel.TagName} to install it anyway, unverified.)");
            }
            Console.WriteLine(verification switch
            {
                VerifyResult.Verified => $"Verified {asset.Name} (signed sha256).",
                VerifyResult.Unsigned => $"⚠ {asset.Name} installed: sha256 ok but the manifest is UNSIGNED (older tag).",
                VerifyResult.NoManifest => $"⚠ {asset.Name} installed unverified (older tag; no SHA256SUMS).",
                _ => $"⚠ {asset.Name} installed unverified (older tag; not in SHA256SUMS).",
            });
            var before = InstallManager.Shared.InstalledVersion(slot);
            var dest = InstallManager.Shared.Install(app, rel.TagName, cache, asset.Name, toApplications, variant);
            InstallManager.TryDelete(cache);
            History.Add(app.Id, appName, ActivityHistory.ActionFor(before, rel.TagName), before, rel.TagName);
            Log.Write($"cli: installed {slot} {rel.TagName}{(toApplications ? " (+shortcuts)" : "")}{(verification == VerifyResult.Verified ? " (sha256 ok)" : " (unverified)")}");
            Console.WriteLine($"Installed {appName} {rel.TagName} → {dest}");
            return 0;
        }
        catch (Exception ex) { return Fail(CodeFor(ex, downloading), ex.Message); }
    }

    private static int Uninstall(Catalog catalog, string? id, string? edition)
    {
        var app = id != null ? catalog.Apps.FirstOrDefault(a => a.Id == id) : null;
        if (app == null) return Fail(ExitCode.Usage, "pass an app id.");
        if (!app.TryResolveEdition(edition, out var variant))
            return Fail(ExitCode.Usage, $"{app.Name} has no edition \"{edition}\". Editions: {app.EditionChoices}.");
        var slot = app.InstallKey(variant);
        var appName = app.Name + app.VariantSuffix(variant);
        try
        {
            var before = InstallManager.Shared.InstalledVersion(slot);
            InstallManager.Shared.Uninstall(slot);
            if (before != null) History.Add(app.Id, appName, "uninstall", before);
            Log.Write($"cli: uninstalled {slot}"); Console.WriteLine($"Uninstalled {appName}."); return 0;
        }
        catch (Exception ex) { return Fail(CodeFor(ex), ex.Message); }
    }

    private static int Launch(Catalog catalog, string? id)
    {
        var app = id != null ? catalog.Apps.FirstOrDefault(a => a.Id == id) : null;
        if (app == null) return Fail(ExitCode.Usage, "pass an app id.");
        if (InstallManager.Shared.InstalledPath(app.Id) == null) return Fail(ExitCode.NotFound, $"{app.Name} isn't installed.");
        try { InstallManager.Shared.Launch(app); Log.Write($"cli: launched {app.Id}"); Console.WriteLine($"Launched {app.Name}."); return 0; }
        catch (Exception ex) { return Fail(CodeFor(ex), ex.Message); }
    }

    /// <summary>--self-update: the launcher's in-place update, headless — this exe is replaced where it is (same name),
    /// verified like every download; no restart (the next start runs the new version). The launcher's repo is public:
    /// credentials are optional and only taken from the command line.</summary>
    private static async Task<int> SelfUpdateAsync(Catalog catalog, AppSettings settings, string? token)
    {
        var s = catalog.Self;
        if (s == null) { Console.Error.WriteLine("error: no `self` entry in catalog."); return 1; }
        // The launcher's own show lock, or the suite-wide one set from Stagehand or an app (claude.json).
        if (settings.ShowLock || ClaudeSettingsFile.SuiteShowLock() == true)
            return Fail(ExitCode.ShowLock, "show lock is on — turn it off in JB Theatre Tools first.");
        Versions.DevChannel = settings.DevChannel;   // the same pick the window makes on this machine
        var current = MainForm.CurrentVersion();
        try
        {
            // Credentials given on the command line (a relay-only network, or GitHub's anonymous rate limit) are used.
            var staged = await LauncherUpdate.DownloadAsync(s, HasAuth(token) ? MakeClient(token) : new GitHubClient((string?)null), current);
            LauncherUpdate.Install(staged);
            Console.WriteLine($"Updated JB Theatre Tools v{current} -> {staged.Tag} in place: {Environment.ProcessPath}");
            Console.WriteLine("The next start runs it. The previous version is kept beside it as a .old file (delete it any time).");
            return 0;
        }
        catch (Exception ex) when (ex.Message == "You're up to date.")
        {
            Console.WriteLine($"JB Theatre Tools v{current} is up to date.");
            return 0;
        }
        catch (Exception ex) { return Fail(CodeFor(ex), ex.Message); }
    }

    private static async Task<int> SelfCheckAsync(Catalog catalog, string? token)
    {
        var s = catalog.Self;
        if (s == null) { Console.Error.WriteLine("error: no `self` entry in catalog."); return 1; }
        var current = "1.0.0";
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v != null) current = $"{v.Major}.{v.Minor}.{v.Build}";
        using var client = MakeClient(token);
        try
        {
            var info = await client.LatestReleaseAsync(s.Owner, s.Repo);
            var newer = Versions.IsNewer(info.TagName, current);
            Console.WriteLine($"JB Theatre Tools: current v{current}, latest {info.TagName} → {(newer ? "UPDATE AVAILABLE" : "up to date")}");
            return 0;
        }
        catch (GitHubException ge) when (ge.Kind == GitHubErrorKind.NoRelease)
        {
            Console.WriteLine($"JB Theatre Tools: current v{current}, no launcher release published yet");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
    }

    // Credential Manager is Windows-only; guard so the CLI still runs cross-platform (env/--token).
    private static string? SafeLoadToken()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        try { return TokenStore.Load(); } catch { return null; }
    }

    private static string? SafeLoadServerPass()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        try { return TokenStore.LoadServerPass(); } catch { return null; }
    }

    private static void PrintHelp() => Console.WriteLine(
        """
        JBTheatreTools — theatre/AV app launcher (headless CLI)

          --list                 List the catalog with installed & latest versions (--json: by catalog id,
                                 with every platform's file name per edition)
          --installed            Show locally installed apps (--json: by catalog id)
          --releases  <id>       List all available versions of an app
          --install   <id>       Download & install (latest, or --tag vX.Y.Z for an older one)
          --install-file <id> <file> --tag vX.Y.Z
                                 Install a build already on this PC. The release's SHA256SUMS and
                                 SHA256SUMS.minisig must sit next to it (or pass --sums <path>); they're
                                 checked against the suite key, so an altered or wrong file is refused.
          --download  <id> --tag vX.Y.Z --asset <file name> --to <folder>
                                 Fetch one build of a release (any platform's) with its SHA256SUMS and
                                 SHA256SUMS.minisig into <folder>, verified, for a PC without internet
                                 (it installs them with --install-file). --json: path, sums, sig, size, sha256
          --uninstall <id>       Remove an installed app
          --launch    <id>       Launch an installed app
          --self-check           Check whether a newer launcher release exists
          --self-update          Update this launcher in place (verified; the next start runs it)
          --backup-all  <file>   Back up every installed app's settings (+ this launcher's) into one file
          --restore-all <file>   Restore them from that file (installed apps only; close each app first)
          --help                 This help

        Options: --token <pat>       GitHub PAT (else $GITHUB_TOKEN, else Credential Manager)
                 --server <url>      Download-server (relay) base URL — use instead of a token
                 --server-pass <p>   Suite passphrase for --server (else the saved one)
                 --tag <vX.Y.Z>      Install a specific release (with --install)
                 --edition <e>       An app's Light or Full edition (light / full; default: its first) — with
                                     --install, --install-file, --uninstall and --download
                 --to-applications   Also create Start Menu + Desktop shortcuts (with --install)
                 --catalog <path>    Use a specific catalog.json
                 --json              Machine-readable answer (with --list, --installed, --download)
                 --include-passwords Also back up saved passwords (with --backup-all)
                 --no-passwords      Leave this PC's passwords as they are (with --restore-all)
                 --passphrase-stdin  Read the backup passphrase from one line of stdin
                                     (else %JBTT_SETTINGS_PASSPHRASE%; never on the command line)
                 --apps <a,b>        Only these app ids;  --no-launcher: leave the launcher's own settings out

        Exit codes: 0 ok · 1 failed · 2 usage · 3 no access · 4 not found · 5 download failed (retry)
                    6 verification failed / wrong file · 7 show lock on · 8 busy (another install) — retry
                    Errors are one line on stderr, starting "error: ".

        Note: a secret passed via --token / --server-pass is visible to other local users (process
              list / shell history). Prefer $GITHUB_TOKEN or the saved Credential Manager values.
        """);

    private static int PrintHelpReturn() { PrintHelp(); return 0; }

    private static string Pad(string s, int width) => s.Length >= width ? s : s + new string(' ', width - s.Length);

    private static string ByteString(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB" };
        double b = bytes; int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.#} {u[i]}";
    }
}
