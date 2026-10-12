using System.Text.Json;
using System.Text.Json.Nodes;

namespace JBTheatreTools;

/// <summary>What another program needs to drive the command line (Stagehand sets a show PC up from a laptop over SSH):
/// fixed exit codes with a one-line error, JSON listings keyed by catalog id, and installing a build that was copied
/// onto the PC because it has no internet of its own.</summary>
public static partial class Cli
{
    /// <summary>Exit codes, the same for every verb (listed in --help). Errors go to stderr as one "error: …" line.</summary>
    public static class ExitCode
    {
        public const int Ok = 0;
        public const int Failed = 1;        // anything not covered below
        public const int Usage = 2;         // unknown verb, app id or option; a missing argument
        public const int NoAccess = 3;      // no credentials, or they're refused
        public const int NotFound = 4;      // no such version, or it has no build for this PC; not installed
        public const int Download = 5;      // the download (or the server) failed — worth retrying
        public const int Rejected = 6;      // the build failed verification (signature, checksum, size) or is the wrong file
        public const int ShowLock = 7;      // show lock is on
        public const int Busy = 8;          // another launcher window or command kept the install lock — retry later
    }

    private static int Fail(int code, string message)
    {
        var line = string.Join(' ', message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
        Console.Error.WriteLine($"error: {line}");
        return code;
    }

    /// <summary>The exit code for a failure; <paramref name="downloading"/> files an I/O error under Download.</summary>
    private static int CodeFor(Exception ex, bool downloading = false) => ex switch
    {
        SupportLock.BusyException => ExitCode.Busy,
        InstallVerificationException or InvalidDataException => ExitCode.Rejected,
        GitHubException { Kind: GitHubErrorKind.Unauthorized or GitHubErrorKind.NotAccessible } => ExitCode.NoAccess,
        GitHubException { Kind: GitHubErrorKind.NoRelease or GitHubErrorKind.AssetNotFound } => ExitCode.NotFound,
        GitHubException or HttpRequestException or TaskCanceledException => ExitCode.Download,
        IOException when downloading => ExitCode.Download,
        _ => ExitCode.Failed,
    };

    private static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = true };

    private static void PrintJson(JsonNode node) => Console.WriteLine(node.ToJsonString(JsonOut));

    /// <summary>--installed --json: every install slot, keyed by catalog id (Light/Full apps list each edition).</summary>
    private static int InstalledJson(Catalog catalog)
    {
        var m = InstallManager.Shared.Manifest();
        var apps = new JsonArray();
        foreach (var app in catalog.Apps)
            foreach (var (key, variant, label) in Slots(app))
                if (m.TryGetValue(key, out var r))
                    apps.Add(new JsonObject
                    {
                        ["id"] = app.Id, ["slot"] = key, ["edition"] = variant, ["editionName"] = label, ["name"] = app.Name,
                        ["version"] = r.Version, ["path"] = r.Path, ["present"] = File.Exists(r.Path),
                        ["installedAt"] = r.InstalledAt,
                    });
        PrintJson(new JsonObject { ["schema"] = 1, ["manifest"] = InstallManager.Shared.ManifestPath, ["apps"] = apps });
        return ExitCode.Ok;
    }

    private static IEnumerable<(string Key, string? Variant, string? Label)> Slots(CatalogApp app) =>
        app.HasVariants && app.Variants != null
            ? app.Variants.Select(v => (app.InstallKey(v.Id), (string?)v.Id, (string?)v.Label))
            : new[] { (app.Id, (string?)null, (string?)null) };

    /// <summary>--list --json: the catalog with what's installed and the build this PC would get.</summary>
    private static async Task<int> ListJsonAsync(Catalog catalog, string? token)
    {
        using var client = HasAuth(token) ? MakeClient(token) : null;
        var apps = new JsonArray();
        foreach (var app in catalog.Apps)
        {
            var o = new JsonObject
            {
                ["id"] = app.Id, ["name"] = app.Name, ["installed"] = InstallManager.Shared.InstalledVersion(app.Id),
                ["latest"] = null, ["asset"] = AssetName(app), ["size"] = null, ["status"] = "no-auth",
                // Every platform's file name, per edition: what another computer fetches for this one (--download).
                ["assets"] = AssetsJson(app.Assets), ["editions"] = EditionsJson(app),
            };
            if (client != null)
            {
                try
                {
                    var info = Versions.Latest(await client.ReleasesAsync(app.Owner, app.Repo));
                    if (info == null) o["status"] = "no-release";
                    else
                    {
                        o["latest"] = info.TagName;
                        var asset = info.Assets.FirstOrDefault(a => a.Name == AssetName(app));
                        o["size"] = asset?.Size;
                        o["status"] = asset != null ? "ok" : "no-build";
                    }
                }
                catch (GitHubException ge) when (ge.Kind == GitHubErrorKind.NotAccessible) { o["status"] = "no-access"; }
                catch (GitHubException ge) when (ge.Kind == GitHubErrorKind.Unauthorized) { o["status"] = "unauthorized"; }
                catch (Exception ex) { o["status"] = "error"; o["error"] = ex.Message; }
            }
            apps.Add(o);
        }
        PrintJson(new JsonObject { ["schema"] = 1, ["platform"] = Platform.NativeKey, ["apps"] = apps });
        return ExitCode.Ok;
    }

    private static JsonObject AssetsJson(Dictionary<string, string> assets)
    {
        var o = new JsonObject();
        foreach (var (platform, name) in assets.OrderBy(kv => kv.Key, StringComparer.Ordinal)) o[platform] = name;
        return o;
    }

    /// <summary>A Light/Full app's editions (the first is the default, installed as the plain id); null for one edition.</summary>
    private static JsonArray? EditionsJson(CatalogApp app)
    {
        if (!app.HasVariants) return null;
        var list = new JsonArray();
        foreach (var v in app.Variants!)
            list.Add(new JsonObject
            {
                ["id"] = v.Id, ["label"] = v.Label, ["default"] = app.IsDefaultVariant(v.Id), ["slot"] = app.InstallKey(v.Id),
                ["assets"] = AssetsJson(v.Assets),
            });
        return list;
    }

    /// <summary>--install-file &lt;id&gt; &lt;file&gt; --tag vX.Y.Z [--sums SHA256SUMS]: installs a build that is already on this
    /// PC, exactly as --install would have (manifest, connector entry point, shortcuts with --to-applications). It is
    /// checked here, not trusted: the release's SHA256SUMS + SHA256SUMS.minisig (next to the file unless --sums says
    /// where) must verify with the suite key for that app and tag, and the file must be the build this PC uses for that
    /// edition (<c>--edition full</c> installs the Full slot beside Light; omitted = the default edition).</summary>
    private static int InstallFile(Catalog catalog, List<string> positional, string? tag, string? sums, string? edition, bool toApplications)
    {
        if (positional.Count < 2) return Fail(ExitCode.Usage, "usage: --install-file <id> <file> --tag vX.Y.Z [--edition <edition>] [--sums <SHA256SUMS>]");
        var app = catalog.Apps.FirstOrDefault(a => a.Id == positional[0]);
        if (app == null) return Fail(ExitCode.Usage, $"unknown app id \"{positional[0]}\". Known ids: {string.Join(", ", catalog.Apps.Select(a => a.Id))}");
        if (!app.TryResolveEdition(edition, out var variant))
            return Fail(ExitCode.Usage, $"{app.Name} has no edition \"{edition}\". Editions: {app.EditionChoices}.");
        var slot = app.InstallKey(variant);
        var appName = app.Name + app.VariantSuffix(variant);
        if (string.IsNullOrWhiteSpace(tag)) return Fail(ExitCode.Usage, "--install-file needs --tag (the release the file came from, e.g. v1.16.2).");
        var tagName = tag.StartsWith('v') ? tag : "v" + tag;
        var file = Path.GetFullPath(positional[1]);
        if (!File.Exists(file)) return Fail(ExitCode.NotFound, $"no file at {file}.");
        var assetName = app.WindowsAsset(variant, _x64Slots);
        if (assetName == null) return Fail(ExitCode.NotFound, $"{appName} has no Windows build.");
        if (!string.Equals(Path.GetFileName(file), assetName, StringComparison.OrdinalIgnoreCase))
        {
            // The other edition's build: say how to install it instead.
            var other = app.HasVariants ? app.Variants!.FirstOrDefault(v => v.Id != variant &&
                string.Equals(app.WindowsAsset(v.Id, _x64Slots), Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)) : null;
            return Fail(ExitCode.Rejected, $"this PC installs {assetName} for {appName}, not {Path.GetFileName(file)}." +
                (other != null ? $" That's the {other.Label} build: add --edition {other.Id}." : ""));
        }
        var sumsPath = Path.GetFullPath(sums ?? Path.Combine(Path.GetDirectoryName(file)!, "SHA256SUMS"));

        // Verify and install the same bytes: a private copy in the download cache, never the file a caller can change —
        // its own copy, so two of these running at once (Stagehand may start several) never share one.
        var cache = Path.Combine(InstallManager.Shared.CacheDir, $"{app.Id}-{tagName}-{Guid.NewGuid():N}-{assetName}");
        try
        {
            File.Copy(file, cache, overwrite: true);
            InstallManager.VerifyLocalFile(cache, assetName, sumsPath, app.Repo, tagName);
            Console.WriteLine($"Verified {assetName} (signed sha256, {app.Repo} {tagName}).");
            var before = InstallManager.Shared.InstalledVersion(slot);
            var dest = InstallManager.Shared.Install(app, tagName, cache, assetName, toApplications, variant);
            History.Add(app.Id, appName, ActivityHistory.ActionFor(before, tagName), before, tagName);
            Log.Write($"cli: installed {slot} {tagName} from a file{(toApplications ? " (+shortcuts)" : "")} (sha256 ok)");
            Console.WriteLine($"Installed {appName} {tagName} → {dest}");
            return ExitCode.Ok;
        }
        catch (Exception ex)
        {
            Log.Write($"cli: install-file {slot} {tagName} failed: {ex.Message}");
            return Fail(CodeFor(ex), ex.Message);
        }
        finally { InstallManager.TryDelete(cache); }
    }

    /// <summary>--download &lt;id&gt; --tag vX.Y.Z --asset &lt;name&gt; --to &lt;folder&gt; [--json]: fetches one build of a
    /// release (any platform's) with SHA256SUMS + SHA256SUMS.minisig into the folder, verified as --install-file will
    /// check them, for a PC without internet. Uses this launcher's saved credentials or the server flags (never printed).</summary>
    private static async Task<int> DownloadAsync(Catalog catalog, string? token, string? id, string? tag, string? assetName,
                                                 string? folder, string? edition, bool json)
    {
        const string usage = "usage: --download <id> --tag vX.Y.Z --asset <file name> --to <folder> [--edition <edition>] [--json]";
        if (id == null) return Fail(ExitCode.Usage, usage);
        var app = catalog.Apps.FirstOrDefault(a => a.Id == id);
        if (app == null) return Fail(ExitCode.Usage, $"unknown app id \"{id}\". Known ids: {string.Join(", ", catalog.Apps.Select(a => a.Id))}");
        if (string.IsNullOrWhiteSpace(tag)) return Fail(ExitCode.Usage, "--download needs --tag (the release, e.g. v1.16.2).");
        if (string.IsNullOrWhiteSpace(assetName)) return Fail(ExitCode.Usage, "--download needs --asset (the build's file name, e.g. ShowDashboard-Windows-x64.exe).");
        if (!ReleaseDownload.IsPlainAssetName(assetName)) return Fail(ExitCode.Usage, $"\"{assetName}\" isn't a release file name.");
        if (string.IsNullOrWhiteSpace(folder)) return Fail(ExitCode.Usage, "--download needs --to <folder>.");
        // --edition: the file must be one of that edition's builds (any platform's), so a Light name can't stand in for Full.
        if (edition != null)
        {
            if (!app.TryResolveEdition(edition, out var variant))
                return Fail(ExitCode.Usage, $"{app.Name} has no edition \"{edition}\". Editions: {app.EditionChoices}.");
            var names = app.AssetsFor(variant).Values;
            if (!names.Contains(assetName))
                return Fail(ExitCode.Usage, $"{assetName} isn't a build of {app.Name}{app.VariantSuffix(variant)}. Its builds: {string.Join(", ", names.OrderBy(n => n, StringComparer.Ordinal))}.");
        }
        if (!HasAuth(token)) return Fail(ExitCode.NoAccess, "no auth (save a token or the suite passphrase in the app, set %GITHUB_TOKEN%, or pass --server + --server-pass).");

        using var client = MakeClient(token);
        try
        {
            var all = await client.ReleasesAsync(app.Owner, app.Repo);
            var rel = all.FirstOrDefault(r => VersionCompare.Equal(r.TagName, tag));
            if (rel == null) return Fail(ExitCode.NotFound, $"{app.Name} has no release {tag}.");
            var asset = rel.Assets.FirstOrDefault(a => a.Name == assetName);
            if (asset == null)
                return Fail(ExitCode.NotFound, $"{app.Name} {rel.TagName} has no file named {assetName}. It has: {string.Join(", ", rel.Assets.Select(a => a.Name).Where(ReleaseDownload.IsPlainAssetName))}.");
            var files = new[] { asset }.Concat(rel.Assets.Where(a => a.Name is ReleaseDownload.SumsName or ReleaseDownload.SigName)).ToList();
            if (files.Count != 3) return Fail(ExitCode.Rejected, $"{app.Name} {rel.TagName} isn't signed (no SHA256SUMS + SHA256SUMS.minisig), so a PC can't check it.");

            if (!json) Console.WriteLine($"Downloading {asset.Name} ({ByteString(asset.Size)}) @ {rel.TagName}…");
            var result = await ReleaseDownload.RunAsync(folder, asset.Name, asset.Size, app.Repo, rel.TagName, async (name, dest) =>
            {
                var a = files.First(f => f.Name == name);
                try { await client.DownloadAssetAsync(app.Owner, app.Repo, a.Id, dest, null); }
                catch (IOException ex) { throw new HttpRequestException(ex.Message, ex); }   // a failed write mid-download is a download failure
            });
            Log.Write($"cli: downloaded {app.Id} {rel.TagName} {asset.Name} (sha256 ok)");
            if (json)
                PrintJson(new JsonObject
                {
                    ["path"] = result.Path, ["sums"] = result.Sums, ["sig"] = result.Sig, ["size"] = result.Size, ["sha256"] = result.Sha256,
                });
            else
            {
                Console.WriteLine($"Verified {asset.Name} (signed sha256, {app.Repo} {rel.TagName}).");
                Console.WriteLine($"Saved → {result.Path}");
                Console.WriteLine($"      + {ReleaseDownload.SumsName}, {ReleaseDownload.SigName}");
            }
            return ExitCode.Ok;
        }
        catch (ArgumentException ex) { return Fail(ExitCode.Usage, ex.Message); }   // e.g. a --to path Windows can't use
        catch (Exception ex)
        {
            Log.Write($"cli: download {app.Id} {tag} {assetName} failed: {ex.Message}");
            return Fail(CodeFor(ex), ex.Message);
        }
    }
}
