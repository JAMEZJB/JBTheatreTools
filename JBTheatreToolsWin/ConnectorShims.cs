using System.Diagnostics;

namespace JBTheatreTools;

/// <summary>Keeps connectors.json (see Core/ClaudeLink.cs) and a stable entry point per connector app in step with the
/// install manifest. Windows installs live in per-version folders, so each app gets a directory junction
/// <c>%LOCALAPPDATA%\JBTheatreTools\bin\&lt;id&gt;</c> pointing at its current version's folder; the command Stagehand
/// registers is the exe inside the junction, which stays the same across updates. The junction is re-pointed on every
/// update and removed with the app.</summary>
internal static class ConnectorShims
{
    private static IReadOnlyList<CatalogApp> _apps = Array.Empty<CatalogApp>();

    private static string BinDir => Path.Combine(InstallManager.Shared.SupportDir, "bin");
    private static string FilePath => Path.Combine(InstallManager.Shared.SupportDir, "connectors.json");

    private static int _following;

    /// <summary>Starts following the manifest (subscribed once, however often the catalog reloads) and brings everything
    /// in step now — which also repairs what another launcher process left behind.</summary>
    public static void Start(IReadOnlyList<CatalogApp> apps)
    {
        _apps = apps;
        if (Interlocked.Exchange(ref _following, 1) == 0) InstallManager.Shared.ManifestChanged += () => Sync();
        // Briefly: a process holding the lock is changing what's installed, and brings these in step itself after.
        Sync(TimeSpan.FromSeconds(10));
    }

    public static void Sync(TimeSpan? wait = null)
    {
        try
        {
            var im = InstallManager.Shared;
            // Every launcher process on the PC writes these: hold the shared lock and read what is on disk now, never
            // this process's cached copy (an install another process just made, or the version folder it just removed).
            using (im.Lock.Hold(wait))
            {
                im.Refresh();
                var entries = Connectors.Entries(_apps.Select(a => a.ToConnectorApp()), im.InstalledVersion, im.InstalledPath);
                var commands = new Dictionary<string, string>();
                foreach (var e in entries) commands[e.Id] = StableCommand(e);
                RemoveStale(entries.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
                Connectors.Write(FilePath, Connectors.Document(entries, e => commands[e.Id]));
            }
        }
        catch (Exception ex) { Log.Write($"connectors.json: {ex.Message}"); }
    }

    /// <summary>bin\&lt;id&gt;\&lt;exe&gt; through a junction to the installed version's folder; the exe's own path when a
    /// junction can't be made (the command then changes on the next update, and Stagehand re-reads it). A command is
    /// only ever one that opens the program now: a junction that points elsewhere, at a folder that has gone, or an
    /// empty folder in its place is replaced.</summary>
    private static string StableCommand(Connectors.Entry e)
    {
        var target = Path.GetDirectoryName(e.Executable)!;
        var link = Path.Combine(BinDir, e.Id);
        var command = Path.Combine(link, Path.GetFileName(e.Executable));
        try
        {
            Directory.CreateDirectory(BinDir);
            var info = new DirectoryInfo(link);
            if (Attributes(link) is { } attributes)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (SamePath(info.LinkTarget, target) && File.Exists(command)) return command;
                    Directory.Delete(link);   // a junction: removes the link, never what it points at
                }
                else if ((attributes & FileAttributes.Directory) != 0 && !Directory.EnumerateFileSystemEntries(link).Any())
                    Directory.Delete(link);   // an empty folder where the junction belongs
                else return e.Executable;     // not ours: leave it
            }
            if (!MakeJunction(link, target) || !File.Exists(command))
            {
                Log.Write($"connector entry point for {e.Id}: could not link {link}; using the program's own path");
                return e.Executable;
            }
            return command;
        }
        catch (Exception ex)
        {
            Log.Write($"connector entry point for {e.Id}: {ex.Message}");
            return e.Executable;
        }
    }

    /// <summary>The entry's own attributes (a junction's, not its target's), or null when there is nothing there.</summary>
    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static bool SamePath(string? a, string b) =>
        !string.IsNullOrEmpty(a) && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                                                  StringComparison.OrdinalIgnoreCase);

    private static void RemoveStale(HashSet<string> keep)
    {
        if (!Directory.Exists(BinDir)) return;
        foreach (var d in Directory.EnumerateDirectories(BinDir))
        {
            if (keep.Contains(Path.GetFileName(d))) continue;
            try
            {
                if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(d);
            }
            catch (Exception ex) { Log.Write($"connector entry point {Path.GetFileName(d)}: {ex.Message}"); }
        }
    }

    /// <summary>A directory junction needs no administrator (unlike a symbolic link).</summary>
    private static bool MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
                                                    RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var p = Process.Start(psi);
        if (p == null) return false;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch (Exception) { } return false; }
        return p.ExitCode == 0 && Directory.Exists(link);
    }
}
