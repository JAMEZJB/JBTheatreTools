namespace JBTheatreTools;

/// <summary>
/// Launcher self-update: download the newest JBTheatreTools build to the user's Downloads folder
/// and reveal it in Explorer. We don't self-replace a running .exe — the user quits and swaps it in.
/// </summary>
public static class LauncherUpdate
{
    /// <returns>The path the new build was saved to.</returns>
    public static async Task<string> DownloadAndRevealAsync(SelfInfo self)
    {
        using var client = new GitHubClient(TokenStore.Load());   // launcher repo is public; token optional
        var info = await client.LatestReleaseAsync(self.Owner, self.Repo);
        if (!self.Assets.TryGetValue(Platform.AssetKey, out var assetName))
            throw new Exception("No Windows asset configured for this platform.");
        var asset = info.Assets.FirstOrDefault(a => a.Name == assetName)
            ?? throw new Exception($"Release {info.TagName} has no asset named {assetName}.");

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        var dest = Path.Combine(downloads, asset.Name);
        await client.DownloadAssetAsync(self.Owner, self.Repo, asset.Id, dest, null);
        // Integrity-check the launcher download too (the launcher repo publishes SHA256SUMS); a size or
        // checksum mismatch deletes the file and throws (surfaced to the user as a download failure).
        await InstallManager.VerifyDownloadAsync(dest, asset, info, self.Owner, self.Repo, client);

        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dest}\""); } catch { /* non-fatal */ }
        return dest;
    }
}
