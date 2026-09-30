using System.Text.Json.Nodes;

namespace JBTheatreTools;

/// <summary><c>claude.json</c> — Claude's switch, show lock and permissions, shared by Stagehand (the suite app that sets
/// what Claude may do in each app), every app and this launcher, which writes only the suite-wide <c>showLock</c> so
/// the three show locks stay in step. %APPDATA%\JBTheatreTools\claude.json, written atomically.</summary>
public static class ClaudeSettingsFile
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JBTheatreTools", "claude.json");

    /// <summary>The suite-wide show lock, or null when there's no file or it can't be read.</summary>
    public static bool? SuiteShowLock(string? path = null)
    {
        try
        {
            var p = path ?? DefaultPath;
            if (!File.Exists(p) || SuiteBackup.ParseObject(File.ReadAllBytes(p)) is not { } d || SuiteBackup.Int(d["schema"]) != 1)
                return null;
            return SuiteBackup.Bool(d["showLock"]) ?? false;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Sets the suite-wide show lock and keeps the rest of the file. An unreadable file becomes a fresh,
    /// disconnected one rather than refusing the switch: the operator is never blocked. Never throws (false = not saved;
    /// the launcher's own show lock still holds).</summary>
    public static bool SetSuiteShowLock(bool on, string? path = null)
    {
        try { Write(on, path ?? DefaultPath); return true; }
        catch (Exception) { return false; }
    }

    private static void Write(bool on, string p)
    {
        JsonObject d = new() { ["schema"] = 1, ["enabled"] = false, ["showLock"] = false, ["apps"] = new JsonObject() };
        try
        {
            if (File.Exists(p) && SuiteBackup.ParseObject(File.ReadAllBytes(p)) is { } old && SuiteBackup.Int(old["schema"]) == 1) d = old;
        }
        catch (IOException) { }
        if (File.Exists(p) && SuiteBackup.Bool(d["showLock"]) == on) return;
        d["showLock"] = on;
        // Readers (every connected app) hold it for milliseconds; a replace can briefly be refused — try again.
        for (int attempt = 0; ; attempt++)
        {
            try { SuiteBackup.AtomicWrite(p, SuiteBackup.Json(d)); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 40) { Thread.Sleep(50); }
        }
    }
}

/// <summary><c>connectors.json</c> — for every installed app whose build is a Claude connector (<c>&lt;app&gt; --mcp</c>),
/// a command that keeps working across updates. Stagehand starts only these (an app without a connector would open its
/// window instead). Next to installed.json (%LOCALAPPDATA%\JBTheatreTools\), rewritten after every install, update and
/// removal.</summary>
public static class Connectors
{
    /// <summary>One app as the connector list sees it: the first release with a connector, and its install slots
    /// (Full first) with the installed version and exe of each.</summary>
    public sealed record App(string Id, string Name, string? ClaudeSince, IReadOnlyList<(string Key, string? Edition)> Slots);

    public static bool HasConnector(string? since, string installed) =>
        !string.IsNullOrEmpty(since) && !VersionCompare.IsNewer(since, installed);

    public sealed record Entry(string Id, string Name, string Version, string? Edition, string Executable);

    /// <summary>One entry per app id, the Full edition when both are installed, only for connector builds.</summary>
    public static List<Entry> Entries(IEnumerable<App> apps, Func<string, string?> installedVersion, Func<string, string?> installedPath)
    {
        var list = new List<Entry>();
        foreach (var app in apps)
        {
            if (string.IsNullOrEmpty(app.ClaudeSince)) continue;
            foreach (var (key, edition) in app.Slots)
            {
                if (installedVersion(key) is not { } v || installedPath(key) is not { } exe || !HasConnector(app.ClaudeSince, v)) continue;
                list.Add(new Entry(app.Id, app.Name, v, edition, exe));
                break;
            }
        }
        return list;
    }

    /// <summary>The document; <paramref name="stableCommand"/> maps an entry to the command that survives updates.</summary>
    public static JsonObject Document(IEnumerable<Entry> entries, Func<Entry, string> stableCommand)
    {
        var apps = new JsonObject();
        foreach (var e in entries)
        {
            var o = new JsonObject { ["name"] = e.Name, ["command"] = stableCommand(e), ["args"] = new JsonArray("--mcp"), ["version"] = e.Version };
            if (e.Edition != null) o["edition"] = e.Edition;
            apps[e.Id] = o;
        }
        return new JsonObject { ["schema"] = 1, ["apps"] = apps };
    }

    /// <summary>Writes the file only when what it says has changed.</summary>
    public static void Write(string path, JsonObject doc)
    {
        var data = SuiteBackup.Json(doc);
        try { if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) return; } catch (IOException) { }
        SuiteBackup.AtomicWrite(path, data);
    }
}
