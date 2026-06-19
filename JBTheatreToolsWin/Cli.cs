using System.Runtime.InteropServices;

namespace JBTheatreTools;

/// <summary>
/// Headless command-line interface (parity with the macOS CLI) — used for scripting and for
/// verifying the catalog, GitHub API access, asset resolution, and the download/install path.
///
/// Token order: <c>--token &lt;pat&gt;</c>, then <c>$GITHUB_TOKEN</c>, then Credential Manager.
///
///   JBTheatreTools.exe --list            [--token X] [--catalog path]
///   JBTheatreTools.exe --installed
///   JBTheatreTools.exe --install &lt;id&gt;    [--token X]
///   JBTheatreTools.exe --launch  &lt;id&gt;
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
        var positional = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--token": if (++i < args.Length) token = args[i]; break;
                case "--catalog": if (++i < args.Length) catalogPath = args[i]; break;
                default: positional.Add(args[i]); break;
            }
        }
        token ??= SafeLoadToken();

        Catalog catalog;
        try { catalog = Catalog.Load(catalogPath); }
        catch (Exception ex) { Console.Error.WriteLine($"error: could not load catalog: {ex.Message}"); return 1; }

        return cmd switch
        {
            "--list"      => await ListAsync(catalog, token),
            "--installed" => Installed(catalog),
            "--install"   => await InstallAsync(catalog, token, positional.FirstOrDefault()),
            "--launch"    => Launch(catalog, positional.FirstOrDefault()),
            _             => PrintHelpReturn(),
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

    private static async Task<int> InstallAsync(Catalog catalog, string? token, string? id)
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
            var info = await client.LatestReleaseAsync(app.Owner, app.Repo);
            var asset = info.Assets.FirstOrDefault(a => a.Name == app.WindowsAssetName);
            if (asset == null) { Console.Error.WriteLine($"error: release {info.TagName} has no {Platform.AssetKey} asset."); return 1; }

            Console.WriteLine($"Downloading {asset.Name} ({ByteString(asset.Size)}) @ {info.TagName}…");
            var cache = Path.Combine(InstallManager.Shared.CacheDir, $"{app.Id}-{info.TagName}-{asset.Name}");
            int last = -1;
            var progress = new Progress<double>(p =>
            {
                int pct = (int)(p * 100);
                if (pct != last && pct % 10 == 0) { last = pct; Console.WriteLine($"  {pct}%"); }
            });
            await client.DownloadAssetAsync(app.Owner, app.Repo, asset.Id, cache, progress);
            var dest = InstallManager.Shared.Install(app, info.TagName, cache, asset.Name);
            Console.WriteLine($"Installed {app.Name} {info.TagName} → {dest}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
    }

    private static int Launch(Catalog catalog, string? id)
    {
        var app = id != null ? catalog.Apps.FirstOrDefault(a => a.Id == id) : null;
        if (app == null) { Console.Error.WriteLine("error: pass an app id."); return 1; }
        try { InstallManager.Shared.Launch(app); Console.WriteLine($"Launched {app.Name}."); return 0; }
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
          --install <id>         Download & install an app's latest Windows build
          --launch  <id>         Launch an installed app
          --help                 This help

        Options: --token <pat>    GitHub PAT (else $GITHUB_TOKEN, else Credential Manager)
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
