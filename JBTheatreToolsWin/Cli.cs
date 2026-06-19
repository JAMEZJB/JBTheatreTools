using System.Runtime.InteropServices;

namespace JBTheatreTools;

/// <summary>
/// Headless command-line interface (parity with the macOS CLI) — used for scripting and for
/// verifying the catalog, GitHub API access, asset resolution, and the download/install path.
///
/// Token order: <c>--token &lt;pat&gt;</c>, then <c>$GITHUB_TOKEN</c>, then Credential Manager.
///
///   JBTheatreTools.exe --list                 [--token X] [--catalog path]
///   JBTheatreTools.exe --installed
///   JBTheatreTools.exe --releases &lt;id&gt;        [--token X]
///   JBTheatreTools.exe --install  &lt;id&gt;        [--tag vX.Y.Z] [--token X]
///   JBTheatreTools.exe --uninstall &lt;id&gt;
///   JBTheatreTools.exe --launch   &lt;id&gt;
///   JBTheatreTools.exe --self-check           [--token X]
///   JBTheatreTools.exe --help
/// </summary>
public static class Cli
{
    public static async Task<int> Run(string[] args)
    {
        var cmd = args[0];
        if (cmd is "--help" or "-h") { PrintHelp(); return 0; }

        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        string? catalogPath = null;
        string? tag = null;
        var positional = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--token": if (++i < args.Length) token = args[i]; break;
                case "--catalog": if (++i < args.Length) catalogPath = args[i]; break;
                case "--tag": if (++i < args.Length) tag = args[i]; break;
                default: positional.Add(args[i]); break;
            }
        }
        token ??= SafeLoadToken();

        Catalog catalog;
        try { catalog = Catalog.Load(catalogPath); }
        catch (Exception ex) { Console.Error.WriteLine($"error: could not load catalog: {ex.Message}"); return 1; }

        return cmd switch
        {
            "--list"       => await ListAsync(catalog, token),
            "--installed"  => Installed(catalog),
            "--releases"   => await ReleasesAsync(catalog, token, positional.FirstOrDefault()),
            "--install"    => await InstallAsync(catalog, token, positional.FirstOrDefault(), tag),
            "--uninstall"  => Uninstall(catalog, positional.FirstOrDefault()),
            "--launch"     => Launch(catalog, positional.FirstOrDefault()),
            "--self-check" => await SelfCheckAsync(catalog, token),
            _              => PrintHelpReturn(),
        };
    }

    private static async Task<int> ListAsync(Catalog catalog, string? token)
    {
        Console.WriteLine($"JBTheatreTools — {catalog.Apps.Count} apps (platform: {Platform.AssetKey})\n");
        using var client = token != null ? new GitHubClient(token) : null;
        foreach (var app in catalog.Apps)
        {
            var installed = InstallManager.Shared.InstalledVersion(app.Id) ?? "—";
            string latest = "?", note = "";
            if (client != null)
            {
                try
                {
                    var info = await client.LatestReleaseAsync(app.Owner, app.Repo);
                    latest = info.TagName;
                    var asset = info.Assets.FirstOrDefault(a => a.Name == app.WindowsAssetName);
                    note = asset != null
                        ? $"{asset.Name} ({ByteString(asset.Size)})"
                        : $"⚠ no {Platform.AssetKey} asset ({app.WindowsAssetName})";
                }
                catch (GitHubException ge) when (ge.Kind == GitHubErrorKind.NoRelease) { latest = "none"; note = "no published release"; }
                catch (Exception ex) { latest = "error"; note = ex.Message; }
            }
            else note = "no token — set $GITHUB_TOKEN or pass --token";

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
            if (m.TryGetValue(app.Id, out var r))
                Console.WriteLine($"  {Pad(app.Name, 20)}  {Pad(r.Version, 10)}  {r.Path}");
        return 0;
    }

    private static async Task<int> ReleasesAsync(Catalog catalog, string? token, string? id)
    {
        var app = id != null ? catalog.Apps.FirstOrDefault(a => a.Id == id) : null;
        if (app == null) { Console.Error.WriteLine($"error: pass an app id. Known ids: {string.Join(", ", catalog.Apps.Select(a => a.Id))}"); return 1; }
        if (token == null) { Console.Error.WriteLine("error: no token."); return 1; }
        using var client = new GitHubClient(token);
        try
        {
            var all = await client.ReleasesAsync(app.Owner, app.Repo);
            Console.WriteLine($"{app.Name} — {all.Count} release(s):\n");
            foreach (var rel in all)
            {
                bool hasWin = rel.Assets.Any(a => a.Name == app.WindowsAssetName);
                var flags = (rel.Prerelease ? " [pre-release]" : "") + (hasWin ? "" : $" [no {Platform.AssetKey} asset]");
                Console.WriteLine($"  {Pad(rel.TagName, 12)}{flags}");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
    }

    private static async Task<int> InstallAsync(Catalog catalog, string? token, string? id, string? tag)
    {
        var app = id != null ? catalog.Apps.FirstOrDefault(a => a.Id == id) : null;
        if (app == null)
        {
            Console.Error.WriteLine($"error: pass an app id. Known ids: {string.Join(", ", catalog.Apps.Select(a => a.Id))}");
            return 1;
        }
        if (token == null) { Console.Error.WriteLine("error: no token (set $GITHUB_TOKEN, pass --token, or save one in the app)."); return 1; }

        using var client = new GitHubClient(token);
        try
        {
            var all = await client.ReleasesAsync(app.Owner, app.Repo);
            var rel = tag != null
                ? all.FirstOrDefault(r => r.TagName == tag)
                : (all.FirstOrDefault(r => !r.Prerelease) ?? all.FirstOrDefault());
            if (rel == null) { Console.Error.WriteLine($"error: version {tag ?? "latest"} not found."); return 1; }
            var asset = rel.Assets.FirstOrDefault(a => a.Name == app.WindowsAssetName);
            if (asset == null) { Console.Error.WriteLine($"error: release {rel.TagName} has no {Platform.AssetKey} asset."); return 1; }

            Console.WriteLine($"Downloading {asset.Name} ({ByteString(asset.Size)}) @ {rel.TagName}…");
            var cache = Path.Combine(InstallManager.Shared.CacheDir, $"{app.Id}-{rel.TagName}-{asset.Name}");
            int last = -1;
            var progress = new Progress<double>(p =>
            {
                int pct = (int)(p * 100);
                if (pct != last && pct % 10 == 0) { last = pct; Console.WriteLine($"  {pct}%"); }
            });
            await client.DownloadAssetAsync(app.Owner, app.Repo, asset.Id, cache, progress);
            var dest = InstallManager.Shared.Install(app, rel.TagName, cache, asset.Name);
            Console.WriteLine($"Installed {app.Name} {rel.TagName} → {dest}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
    }

    private static int Uninstall(Catalog catalog, string? id)
    {
        var app = id != null ? catalog.Apps.FirstOrDefault(a => a.Id == id) : null;
        if (app == null) { Console.Error.WriteLine("error: pass an app id."); return 1; }
        try { InstallManager.Shared.Uninstall(app.Id); Console.WriteLine($"Uninstalled {app.Name}."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
    }

    private static int Launch(Catalog catalog, string? id)
    {
        var app = id != null ? catalog.Apps.FirstOrDefault(a => a.Id == id) : null;
        if (app == null) { Console.Error.WriteLine("error: pass an app id."); return 1; }
        try { InstallManager.Shared.Launch(app); Console.WriteLine($"Launched {app.Name}."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
    }

    private static async Task<int> SelfCheckAsync(Catalog catalog, string? token)
    {
        var s = catalog.Self;
        if (s == null) { Console.Error.WriteLine("error: no `self` entry in catalog."); return 1; }
        var current = "1.0.0";
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v != null) current = $"{v.Major}.{v.Minor}.{v.Build}";
        using var client = new GitHubClient(token);
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

    private static void PrintHelp() => Console.WriteLine(
        """
        JBTheatreTools — theatre/AV app launcher (headless CLI)

          --list                 List the catalog with installed & latest versions
          --installed            Show locally installed apps
          --releases  <id>       List all available versions of an app
          --install   <id>       Download & install (latest, or --tag vX.Y.Z for an older one)
          --uninstall <id>       Remove an installed app
          --launch    <id>       Launch an installed app
          --self-check           Check whether a newer launcher release exists
          --help                 This help

        Options: --token <pat>    GitHub PAT (else $GITHUB_TOKEN, else Credential Manager)
                 --tag <vX.Y.Z>   Install a specific release (with --install)
                 --catalog <path> Use a specific catalog.json
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
