using System.Text.RegularExpressions;

namespace JBTheatreTools;

/// <summary>
/// The file side of the launcher's in-place update, kept free of network and UI so it's unit-tested: Windows can't
/// overwrite a running exe but can rename it, so the old one steps aside as "&lt;name&gt;.old" and the new one takes its
/// exact path and name. Nothing here knows about releases.
/// </summary>
public static class SelfReplace
{
    /// <summary>Set on the restarted launcher: "&lt;previous pid&gt;|&lt;old exe path&gt;|&lt;ready file&gt;" (an environment
    /// variable, not an argument — any "-" argument starts the command line instead of the window).</summary>
    public const string AfterUpdateVar = "JBTT_AFTER_UPDATE";

    /// <summary>How the new launcher's start went.</summary>
    public enum StartResult { Started, StillStarting, Quit }

    /// <summary>Starts <paramref name="exe"/> telling it (via <see cref="AfterUpdateVar"/>) where to write
    /// <paramref name="readyFile"/> once its window is up, and waits for that — or for it to quit, or for
    /// <paramref name="timeout"/> with it still running (a slow start: left alone).</summary>
    public static async Task<StartResult> StartAndWaitAsync(string exe, string oldPath, string readyFile, TimeSpan timeout)
    {
        TryDelete(readyFile);
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
        psi.Environment[AfterUpdateVar] = $"{Environment.ProcessId}|{oldPath}|{readyFile}";
        using var child = System.Diagnostics.Process.Start(psi) ?? throw new IOException("The new version couldn't be started.");
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(readyFile)) { TryDelete(readyFile); return StartResult.Started; }
            if (child.HasExited)
            {
                bool up = File.Exists(readyFile);
                TryDelete(readyFile);
                return up ? StartResult.Started : StartResult.Quit;
            }
            await Task.Delay(250);
        }
        return StartResult.StillStarting;
    }

    /// <summary>Puts <paramref name="newFile"/> in <paramref name="exe"/>'s place and returns where the old exe went.
    /// The new build is first copied into the exe's own folder under a temporary name and checked against
    /// <paramref name="sha256"/> (the hash of the file that was verified); only then do two renames in that one folder —
    /// instant, so the exe is never missing or half-written, even when the staging folder is on another drive.
    /// On any failure nothing has changed.</summary>
    /// <exception cref="UpdateLocationException">The exe's folder can't be written.</exception>
    public static string Swap(string newFile, string exe, string sha256, string label)
    {
        var dir = Path.GetDirectoryName(exe) ?? throw new IOException($"No folder for {exe}");
        var incoming = exe + ".new";
        try
        {
            File.Copy(newFile, incoming, overwrite: true);
            File.SetAttributes(incoming, FileAttributes.Normal);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(incoming);
            throw new UpdateLocationException(dir, ex);
        }
        catch (IOException)
        {
            TryDelete(incoming);
            throw;
        }
        if (!string.Equals(Sha256(incoming), sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(incoming);
            throw new IOException("The update changed while it was being put in place — nothing was replaced. Try again.");
        }
        var old = FreeOldPath(exe);
        try { File.Move(exe, old); }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(incoming);
            throw new UpdateLocationException(dir, ex);
        }
        catch (IOException ex)
        {
            // Not permissions: something else has the exe open without sharing (antivirus, a backup) — worth a retry.
            TryDelete(incoming);
            throw new IOException("Another program is using JB Theatre Tools' file right now — nothing was replaced. Try again in a moment.", ex);
        }
        try { File.Move(incoming, exe); }
        catch
        {
            try { File.Move(old, exe); } catch (Exception back) { Log.Write($"self-update: couldn't restore {exe}: {back.Message}"); }
            TryDelete(incoming);
            throw;
        }
        TryDelete(newFile);
        Log.Write($"self-update: {label} installed at {exe} (old copy {Path.GetFileName(old)})");
        return old;
    }

    /// <summary>Undoes a swap whose new build didn't start: the new exe steps aside and the old one takes its place back.
    /// True when the old exe is back at <paramref name="exe"/>.</summary>
    public static bool RollBack(string exe, string old)
    {
        var failed = exe + ".failed";
        try
        {
            if (File.Exists(failed)) { File.SetAttributes(failed, FileAttributes.Normal); File.Delete(failed); }
            File.Move(exe, failed);
            File.Move(old, exe);
            TryDelete(failed);
            Log.Write($"self-update: rolled back {exe}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"self-update: roll back of {exe} failed: {ex.Message}");
            if (!File.Exists(exe) && File.Exists(failed)) { try { File.Move(failed, exe); } catch { /* keep what we can */ } }
            return false;
        }
    }

    /// <summary>SHA-256 of a file, lower-case hex.</summary>
    public static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>Deletes a file even when it's marked read-only (File.Delete refuses those on Windows).</summary>
    private static void ForceDelete(string path)
    {
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static void TryDelete(string path) { try { ForceDelete(path); } catch { /* best effort */ } }

    /// <summary>Deletes the old copies beside <paramref name="exeName"/>, retrying while one is still in use (the
    /// previous launcher may still be closing). <paramref name="onlyIncoming"/>: just a leftover "&lt;exe&gt;.new".</summary>
    public static void DeleteOldCopies(string dir, string exeName, int attempts = 10, int delayMs = 500, bool onlyIncoming = false)
    {
        foreach (var old in OldCopies(dir, exeName))
        {
            if (onlyIncoming && !old.EndsWith(".new", StringComparison.OrdinalIgnoreCase)) continue;
            for (int attempt = 1; ; attempt++)
            {
                try { ForceDelete(old); Log.Write($"self-update: removed {Path.GetFileName(old)}"); break; }
                catch (Exception ex)
                {
                    if (attempt >= attempts) { Log.Write($"self-update: couldn't remove {old}: {ex.Message}"); break; }
                    Thread.Sleep(delayMs);
                }
            }
        }
    }

    /// <summary>The old copies an update leaves beside <paramref name="exeName"/>: "&lt;exe&gt;.old" or "&lt;exe&gt;.&lt;n&gt;.old".</summary>
    public static IReadOnlyList<string> OldCopies(string dir, string exeName)
    {
        // "<exe>.old", "<exe>.<n>.old" — and "<exe>.new", a copy an interrupted update left before its swap.
        var re = new Regex("^" + Regex.Escape(exeName) + @"((\.\d+)?\.old|\.new)$", RegexOptions.IgnoreCase);
        try { return Directory.EnumerateFiles(dir, exeName + ".*").Where(f => re.IsMatch(Path.GetFileName(f))).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>"&lt;exe&gt;.old", or "&lt;exe&gt;.&lt;n&gt;.old" when an earlier old copy is still in use (a launcher window from
    /// before the last update is still open).</summary>
    public static string FreeOldPath(string exe)
    {
        var old = exe + ".old";
        if (!File.Exists(old)) return old;
        try { ForceDelete(old); return old; } catch { /* still running — pick another name */ }
        for (int n = 2; ; n++)
        {
            var candidate = $"{exe}.{n}.old";
            if (!File.Exists(candidate)) return candidate;
            try { ForceDelete(candidate); return candidate; } catch { /* in use too */ }
        }
    }
}

/// <summary>The launcher's folder can't be written, so it can't replace itself there.</summary>
public sealed class UpdateLocationException(string folder, Exception inner)
    : Exception($"JB Theatre Tools can't replace itself in {folder} — Windows doesn't allow changes there without an administrator.", inner)
{
    public string Folder { get; } = folder;
}
