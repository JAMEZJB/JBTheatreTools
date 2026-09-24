namespace JBTheatreTools;

/// <summary>
/// Launcher self-update: download the newest JBTheatreTools build to the user's Downloads folder
/// and reveal it in Explorer. We don't self-replace a running .exe — the user quits and swaps it in.
/// </summary>
public static class LauncherUpdate
{
    /// <returns>The path the new build was saved to.</returns>
    /// <remarks>`client` comes from AuthClient.SelfUpdate so the download follows the active auth
    /// mode (direct GitHub or the download-server relay); disposed here.</remarks>
    public static async Task<string> DownloadAndRevealAsync(SelfInfo self, GitHubClient client, string currentVersion)
    {
        using var _ = client;
        var info = await Versions.LauncherTargetAsync(client, self.Owner, self.Repo, currentVersion)
            ?? throw new Exception("You're up to date.");
        if (!self.Assets.TryGetValue(Platform.AssetKey, out var assetName))
            throw new Exception("No Windows asset configured for this platform.");
        var asset = info.Assets.FirstOrDefault(a => a.Name == assetName)
            ?? throw new Exception($"Release {info.TagName} has no asset named {assetName}.");

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        var dest = Path.Combine(downloads, asset.Name);
        await client.DownloadAssetAsync(self.Owner, self.Repo, asset.Id, dest, null);
        // Strict verify: the launcher's own release always ships SHA256SUMS, so require a clean match
        // before revealing the new build (a size/hash mismatch throws inside VerifyDownloadAsync; a
        // missing manifest or unlisted asset is treated as a verification failure too — current release).
        var verification = await InstallManager.VerifyDownloadAsync(dest, asset, info, self.Owner, self.Repo, client);
        if (verification != VerifyResult.Verified)
        {
            InstallManager.TryDelete(dest);
            throw new Exception($"Couldn't verify the update — {InstallManager.StrictFailureReason(verification, asset.Name)}. Download discarded.");
        }

        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dest}\""); } catch { /* non-fatal */ }
        return dest;
    }
}
