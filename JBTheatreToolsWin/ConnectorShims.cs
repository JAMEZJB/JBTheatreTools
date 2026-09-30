using System.Diagnostics;

namespace JBTheatreTools;

/// <summary>Keeps connectors.json (see Core/ClaudeLink.cs) and a stable entry point per connector app in step with the
/// install manifest. Windows installs live in per-version folders, so each app gets a directory junction
/// <c>%LOCALAPPDATA%\JBTheatreTools\bin\&lt;id&gt;</c> pointing at its current version's folder; the command Stagehand
/// registers is the exe inside the junction, which stays the same across updates. The junction is re-pointed on every
/// update and removed with the app.</summary>
internal static class ConnectorShims
{
    private static readonly object Gate = new();
    private static IReadOnlyList<CatalogApp> _apps = Array.Empty<CatalogApp>();

    private static string BinDir => Path.Combine(InstallManager.Shared.SupportDir, "bin");
    private static string FilePath => Path.Combine(InstallManager.Shared.SupportDir, "connectors.json");

    /// <summary>Starts following the manifest (once, at start-up) and brings everything in step now.</summary>
    public static void Start(IReadOnlyList<CatalogApp> apps)
    {
        _apps = apps;
        InstallManager.Shared.ManifestChanged += Sync;
        Sync();
    }

    public static void Sync()
    {
        lock (Gate)
        {
            try
            {
                var im = InstallManager.Shared;
                var entries = Connectors.Entries(_apps.Select(a => a.ToConnectorApp()), im.InstalledVersion, im.InstalledPath);
                var commands = new Dictionary<string, string>();
                foreach (var e in entries) commands[e.Id] = StableCommand(e);
                RemoveStale(entries.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
                Connectors.Write(FilePath, Connectors.Document(entries, e => commands[e.Id]));
            }
            catch (Exception ex) { Log.Write($"connectors.json: {ex.Message}"); }
        }
    }

    /// <summary>bin\&lt;id&gt;\&lt;exe&gt; through a junction to the installed version's folder; the exe's own path when a
    /// junction can't be made (the command then changes on the next update, and Stagehand re-reads it).</summary>
    private static string StableCommand(Connectors.Entry e)
    {
        var target = Path.GetDirectoryName(e.Executable)!;
        var link = Path.Combine(BinDir, e.Id);
        try
        {
            Directory.CreateDirectory(BinDir);
            var info = new DirectoryInfo(link);
            if (info.Exists)
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) == 0) return e.Executable;   // not ours: leave it
                if (string.Equals(Path.GetFullPath(info.LinkTarget ?? ""), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(link, Path.GetFileName(e.Executable));
                Directory.Delete(link);   // a junction: removes the link, never what it points at
            }
            if (!MakeJunction(link, target)) return e.Executable;
            return Path.Combine(link, Path.GetFileName(e.Executable));
        }
        catch (Exception ex)
        {
            Log.Write($"connector entry point for {e.Id}: {ex.Message}");
            return e.Executable;
        }
    }

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
