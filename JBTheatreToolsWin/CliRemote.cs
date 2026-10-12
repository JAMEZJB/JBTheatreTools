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

    /// <summary>--install-file &lt;id&gt; &lt;file&gt; --tag vX.Y.Z [--sums SHA256SUMS]: installs a build that is already on this
    /// PC, exactly as --install would have (manifest, connector entry point, shortcuts with --to-applications). It is
    /// checked here, not trusted: the release's SHA256SUMS + SHA256SUMS.minisig (next to the file unless --sums says
    /// where) must verify with the suite key for that app and tag, and the file must be the build this PC uses.</summary>
    private static int InstallFile(Catalog catalog, List<string> positional, string? tag, string? sums, bool toApplications)
    {
        if (positional.Count < 2) return Fail(ExitCode.Usage, "usage: --install-file <id> <file> --tag vX.Y.Z [--sums <SHA256SUMS>]");
        var app = catalog.Apps.FirstOrDefault(a => a.Id == positional[0]);
        if (app == null) return Fail(ExitCode.Usage, $"unknown app id \"{positional[0]}\". Known ids: {string.Join(", ", catalog.Apps.Select(a => a.Id))}");
        if (string.IsNullOrWhiteSpace(tag)) return Fail(ExitCode.Usage, "--install-file needs --tag (the release the file came from, e.g. v1.16.2).");
        var tagName = tag.StartsWith('v') ? tag : "v" + tag;
        var file = Path.GetFullPath(positional[1]);
        if (!File.Exists(file)) return Fail(ExitCode.NotFound, $"no file at {file}.");
        var assetName = AssetName(app);
        if (assetName == null) return Fail(ExitCode.NotFound, $"{app.Name} has no Windows build.");
        if (!string.Equals(Path.GetFileName(file), assetName, StringComparison.OrdinalIgnoreCase))
            return Fail(ExitCode.Rejected, $"this PC installs {assetName} for {app.Name}, not {Path.GetFileName(file)}.");
        var sumsPath = Path.GetFullPath(sums ?? Path.Combine(Path.GetDirectoryName(file)!, "SHA256SUMS"));

        // Verify and install the same bytes: a private copy in the download cache, never the file a caller can change —
        // its own copy, so two of these running at once (Stagehand may start several) never share one.
        var cache = Path.Combine(InstallManager.Shared.CacheDir, $"{app.Id}-{tagName}-{Guid.NewGuid():N}-{assetName}");
        try
        {
            File.Copy(file, cache, overwrite: true);
            InstallManager.VerifyLocalFile(cache, assetName, sumsPath, app.Repo, tagName);
            Console.WriteLine($"Verified {assetName} (signed sha256, {app.Repo} {tagName}).");
            var before = InstallManager.Shared.InstalledVersion(app.Id);
            var dest = InstallManager.Shared.Install(app, tagName, cache, assetName, toApplications);
            History.Add(app.Id, app.Name, ActivityHistory.ActionFor(before, tagName), before, tagName);
            Log.Write($"cli: installed {app.Id} {tagName} from a file{(toApplications ? " (+shortcuts)" : "")} (sha256 ok)");
            Console.WriteLine($"Installed {app.Name} {tagName} → {dest}");
            return ExitCode.Ok;
        }
        catch (Exception ex)
        {
            Log.Write($"cli: install-file {app.Id} {tagName} failed: {ex.Message}");
            return Fail(CodeFor(ex), ex.Message);
        }
        finally { InstallManager.TryDelete(cache); }
    }
}
