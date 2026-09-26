using System.Diagnostics;

namespace JBTheatreTools;

/// <summary>
/// Launcher self-update, in place. The new build is downloaded and verified (size + suite-signed SHA256SUMS + hash)
/// in the launcher's own data folder, then swapped in for the RUNNING exe — same folder, same file name, whatever the
/// user called it and wherever they keep it, so shortcuts and taskbar pins keep working — and the launcher restarts
/// into it. The file side (copy beside the exe, re-check the hash, two renames) is <see cref="SelfReplace"/>.
/// The old launcher waits until the new one's window is up; if the new build quits instead, the old exe is put back.
/// Where the folder can't be written (e.g. Program Files), the verified build is saved to Downloads instead.
/// </summary>
public static class LauncherUpdate
{
    /// <summary>A verified build waiting in the staging folder; <see cref="Sha256"/> is the hash the SIGNED manifest
    /// lists for it (the swap re-checks its copy against that).</summary>
    public sealed record Staged(string Path, string Tag, string AssetName, string Sha256);

    private const string AfterUpdateVar = SelfReplace.AfterUpdateVar;

    /// <summary>What the previous launcher told this one (null unless this start is the restart after an update).</summary>
    private sealed record AfterUpdate(int Pid, string Old, string Ready);
    private static AfterUpdate? _afterUpdate;

    /// <summary>%LOCALAPPDATA%\JBTheatreTools\update — never beside the exe until the swap itself.</summary>
    public static string StagingDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JBTheatreTools", "update");

    /// <summary>Downloads the newest launcher build for this machine and verifies it strictly.</summary>
    /// <remarks>`client` is disposed here. Throws "You're up to date." when there's nothing newer.</remarks>
    public static async Task<Staged> DownloadAsync(SelfInfo self, GitHubClient client, string currentVersion,
                                                   IProgress<double>? progress = null)
    {
        using var _ = client;
        var info = await Versions.LauncherTargetAsync(client, self.Owner, self.Repo, currentVersion)
            ?? throw new Exception("You're up to date.");
        if (!self.Assets.TryGetValue(Platform.AssetKey, out var assetName))
            throw new Exception("No Windows asset configured for this platform.");
        var asset = info.Assets.FirstOrDefault(a => a.Name == assetName)
            ?? throw new Exception($"Release {info.TagName} has no asset named {assetName}.");

        Directory.CreateDirectory(StagingDir);
        ClearStaleStaging();
        // Per process: another launcher on this machine may be downloading into the staging folder too.
        var dest = Path.Combine(StagingDir, $"{Environment.ProcessId}-{asset.Name}");
        await client.DownloadAssetAsync(self.Owner, self.Repo, asset.Id, dest, progress);
        // Strict: the launcher's own release always ships a signed SHA256SUMS. A size/hash mismatch throws inside
        // VerifyDownloadAsync; a missing manifest or an unlisted asset is refused too.
        string? signedHash = null;
        var verification = await InstallManager.VerifyDownloadAsync(dest, asset, info, self.Owner, self.Repo, client,
                                                                    onVerifiedHash: h => signedHash = h);
        if (verification != VerifyResult.Verified || signedHash == null)
        {
            InstallManager.TryDelete(dest);
            throw new Exception($"Couldn't verify the update — {InstallManager.StrictFailureReason(verification, asset.Name)}. Download discarded.");
        }
        return new Staged(dest, info.TagName, asset.Name, signedHash);
    }

    /// <summary>Swaps the staged build in for the exe at <paramref name="exePath"/> (default: this running exe) and
    /// returns where the old one went. On any failure nothing has changed.</summary>
    /// <exception cref="UpdateLocationException">The exe's folder can't be written.</exception>
    public static string Install(Staged staged, string? exePath = null)
        => SelfReplace.Swap(staged.Path,
                            exePath ?? Environment.ProcessPath ?? throw new Exception("Can't tell where JB Theatre Tools is running from."),
                            staged.Sha256, staged.Tag);

    /// <summary>Folder not writable: the verified build goes to Downloads and Explorer shows it (the old manual way).
    /// It's checked against the signed hash once more on the way.</summary>
    public static string SaveToDownloads(Staged staged)
    {
        if (!string.Equals(SelfReplace.Sha256(staged.Path), staged.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The update changed after it was verified — it wasn't saved. Try again.");
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        var dest = Path.Combine(downloads, staged.AssetName);
        File.Move(staged.Path, dest, overwrite: true);
        try { Process.Start("explorer.exe", $"/select,\"{dest}\""); } catch { /* non-fatal */ }
        return dest;
    }

    /// <summary>Starts the just-installed launcher at <paramref name="exe"/> and waits (without blocking the UI) until
    /// its window is up — or it quits, or <paramref name="timeout"/> passes with it still starting.</summary>
    public static Task<SelfReplace.StartResult> StartNewAsync(string exe, string oldPath, TimeSpan timeout)
    {
        Directory.CreateDirectory(StagingDir);
        return SelfReplace.StartAndWaitAsync(exe, oldPath, Path.Combine(StagingDir, $"{Environment.ProcessId}-started"), timeout);
    }

    /// <summary>Call first thing at start-up: remembers (and clears, so apps launched from here don't inherit it) what
    /// the previous launcher passed on after an update.</summary>
    public static void ReadAfterUpdate()
    {
        var v = Environment.GetEnvironmentVariable(AfterUpdateVar);
        Environment.SetEnvironmentVariable(AfterUpdateVar, null);
        if (v?.Split('|', 3) is [var pid, var old, var ready] && int.TryParse(pid, out var p))
            _afterUpdate = new AfterUpdate(p, old, ready);
    }

    /// <summary>Call once the window is showing: tells the previous launcher this one started (it then closes), and —
    /// only after an update — removes the old exe once that process has gone. A plain start only drops a
    /// "&lt;name&gt;.new" an interrupted update left; an "&lt;name&gt;.old" the user keeps as a backup is left alone.</summary>
    public static void Started()
    {
        var after = _afterUpdate;
        if (after != null) { try { File.WriteAllText(after.Ready, "started"); } catch (Exception ex) { Log.Write($"self-update: couldn't signal start: {ex.Message}"); } }
        _ = Task.Run(() => CleanUp(after));
    }

    private static void CleanUp(AfterUpdate? after)
    {
        var exe = Environment.ProcessPath;
        var dir = exe == null ? null : Path.GetDirectoryName(exe);
        if (exe == null || dir == null) return;
        if (after != null)
        {
            try { using var p = Process.GetProcessById(after.Pid); p.WaitForExit(15_000); }
            catch (ArgumentException) { /* already gone */ }
            catch (Exception ex) { Log.Write($"self-update: waiting for the previous launcher: {ex.Message}"); }
            SelfReplace.DeleteOldCopies(dir, Path.GetFileName(exe));
        }
        else
        {
            var incoming = exe + ".new";
            if (File.Exists(incoming)) SelfReplace.DeleteOldCopies(dir, Path.GetFileName(exe), onlyIncoming: true);
        }
        ClearStaleStaging();
    }

    /// <summary>Drops staging leftovers more than an hour old (an update that was interrupted) — never a download
    /// another launcher on this machine is making right now.</summary>
    private static void ClearStaleStaging()
    {
        try
        {
            if (!Directory.Exists(StagingDir)) return;
            foreach (var f in Directory.EnumerateFiles(StagingDir))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1)) InstallManager.TryDelete(f);
        }
        catch { /* best effort */ }
    }
}
