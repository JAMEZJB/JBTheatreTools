namespace JBTheatreTools;

/// <summary>Release-list "latest" selection. The version-STRING comparison lives in the platform-neutral,
/// unit-tested <see cref="VersionCompare"/> (audit F13 — one comparator, overflow-safe); this facade keeps
/// the existing call sites and adds the ReleaseInfo-aware <see cref="Latest"/>.</summary>
public static class Versions
{
    public static string Norm(string s) => VersionCompare.Norm(s);

    public static bool Equal(string a, string b) => VersionCompare.Equal(a, b);

    /// <summary>
    /// Picks the release to treat as "latest": the highest <b>semver</b> among non-prereleases (falling
    /// back to the highest among all releases if every one is a prerelease). GitHub's list endpoint is
    /// ordered by creation date, so a backport/hotfix published after a newer release would otherwise be
    /// mis-selected as "latest" — we sort by version instead, matching GitHub's <c>releases/latest</c>.
    /// </summary>
    public static ReleaseInfo? Latest(IEnumerable<ReleaseInfo> releases)
    {
        var list = releases as IList<ReleaseInfo> ?? releases.ToList();
        var pick = VersionCompare.PickLatest(list, r => r.TagName, r => r.Prerelease, DevChannel);
        // Dev builds are made on one Mac (mac + Android only). When the newest dev build has nothing for
        // Windows, stay on the newest stable release rather than stranding the row; an app with ONLY dev
        // releases keeps the dev pick, and the row says "No Windows build in this development release".
        if (pick != null && DevChannel && VersionCompare.IsDev(pick.TagName) && !HasWindowsAsset(pick))
            return VersionCompare.PickLatest(list, r => r.TagName, r => r.Prerelease, devChannel: false) ?? pick;
        return pick;
    }

    /// <summary>The release for ONE edition (asset name). A development build may not carry every edition, so
    /// when the dev pick lacks this asset, take the newest release that has it (e.g. Light gets the dev build
    /// while Full stays on its release) instead of reading "No Windows build".</summary>
    public static ReleaseInfo? LatestFor(IEnumerable<ReleaseInfo> releases, string? assetName)
    {
        var list = releases as IList<ReleaseInfo> ?? releases.ToList();
        var pick = Latest(list);
        if (pick == null || assetName == null || !VersionCompare.IsDev(pick.TagName) ||
            pick.Assets.Any(a => a.Name == assetName)) return pick;
        return VersionCompare.PickLatest(list.Where(r => r.Assets.Any(a => a.Name == assetName)),
                   r => r.TagName, r => r.Prerelease, DevChannel) ?? pick;
    }

    private static bool HasWindowsAsset(ReleaseInfo r) => r.Assets.Any(a =>
        a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        a.Name.Contains("Windows", StringComparison.OrdinalIgnoreCase));

    /// <summary>The launcher release to offer, or null when there's nothing to do — among the releases that carry this
    /// PC's build: Dev channel ON, the highest including the launcher's own dev builds (a dev build reports its full tag
    /// via InformationalVersion, stamped by build-win.sh from JBTT_VERSION); OFF, the latest release — and when THIS copy
    /// is a dev build, that release even if it's older ("back to release"). See VersionCompare.PickLauncher.</summary>
    public static async Task<ReleaseInfo?> LauncherTargetAsync(GitHubClient client, SelfInfo self, string current)
    {
        // Only a release that has THIS PC's build: a development build can be published for one platform only (a
        // Mac-only dev build was offered here and failed with "no asset named JBTheatreTools-Windows-x64.exe").
        self.Assets.TryGetValue(Platform.AssetKey, out var assetName);
        var releases = (await client.ReleasesAsync(self.Owner, self.Repo)).Where(r => !r.Draft);
        return VersionCompare.PickLauncher(releases, r => r.TagName, r => r.Prerelease,
            r => assetName != null && r.Assets.Any(a => a.Name == assetName), current, DevChannel);
    }

    /// <summary>The releases a person is shown or offered (release notes, the ⋯ version list, Roll Back): development
    /// builds only with the Development builds switch on.</summary>
    public static List<ReleaseInfo> Offered(IEnumerable<ReleaseInfo> releases) =>
        releases.Where(r => DevChannel || !VersionCompare.IsDev(r.TagName)).ToList();

    /// <summary>True when the release carries the signed checksum manifest a strict install needs.</summary>
    public static bool HasSignedManifest(ReleaseInfo r) =>
        r.Assets.Any(a => a.Name == "SHA256SUMS") && r.Assets.Any(a => a.Name == "SHA256SUMS.minisig");

    /// <summary>This PC's Dev channel (settings.json <c>DevChannel</c>; MainForm keeps it in step). Off by
    /// default and for the CLI: development pre-releases are never picked.</summary>
    public static bool DevChannel { get; set; }

    /// <summary>True if `a` is a strictly newer version than `b`. Delegates to the overflow-safe comparator.</summary>
    public static bool IsNewer(string a, string b) => VersionCompare.IsNewer(a, b);
}
