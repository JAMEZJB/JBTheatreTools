using System.Text.Json.Nodes;

namespace JBTheatreTools;

/// <summary>What the launcher's actions need from the window (the WinForms main window, or a test stand-in). Every call
/// is made on the window's own thread (see <see cref="LauncherTrigger"/>), as if the person had pressed the button.</summary>
public interface ILauncherActionHost
{
    bool ShowLock { get; }
    /// <summary>Turns show lock on exactly as Ctrl+L does; returns how many downloads it stopped.</summary>
    int TurnShowLockOn(string source);
    /// <summary>The catalog's apps, in catalog order, each with its install slots (the Full edition first).</summary>
    IReadOnlyList<LauncherActions.App> Apps();
    /// <summary>The app ids ticked in Settings → Dashboard web trigger (only these can be opened).</summary>
    IReadOnlyCollection<string> TickedApps();
    string? InstalledVersion(string slotKey);
    /// <summary>The slot is being downloaded, verified, installed or removed right now.</summary>
    bool SlotBusy(string slotKey);
    /// <summary>A person has the slot's app open (a headless helper — a Claude connector — doesn't count).</summary>
    bool IsOpen(string slotKey);
    void Launch(string slotKey);
    LauncherActions.Facts Status();
    void Log(string line);
}

/// <summary>The launcher's three web-trigger actions — none takes a value from the request:
/// <list type="bullet">
/// <item><c>open-&lt;app id&gt;</c> (saved: only apps ticked in Settings) — opens an installed app like its Launch button;
/// not installed → 422, being installed or updated → 409, already open → 200 and nothing starts.</item>
/// <item><c>show-lock-on</c> (fixed) — show lock ON, as Ctrl+L. There is deliberately no "off": a person at the computer
/// releases it.</item>
/// <item><c>status</c> (fixed, read-only, never goes online) — show lock, apps installed, updates waiting from the last
/// check, the launcher's version.</item>
/// </list></summary>
public static class LauncherActions
{
    public const string StatusId = "status";
    public const string ShowLockOnId = "show-lock-on";
    public const string OpenPrefix = "open-";

    /// <summary>One install slot: its manifest key and the edition suffix it is shown with (" (Full)", or "").</summary>
    public sealed record Slot(string Key, string Suffix);
    public sealed record App(string Id, string Name, IReadOnlyList<Slot> Slots);
    public sealed record Facts(bool ShowLock, int Installed, int Updates, DateTimeOffset? LastCheck, string Version);

    /// <summary>GET /actions: the two fixed actions, then one "open-…" per ticked app (catalog order).</summary>
    public static List<(string Id, string Name)> Listing(ILauncherActionHost host)
    {
        var list = new List<(string, string)> { (StatusId, "Launcher status"), (ShowLockOnId, "Show lock on") };
        var ticked = host.TickedApps();
        foreach (var app in host.Apps())
            if (ticked.Contains(app.Id) && WebTriggerContract.ValidActionId(OpenPrefix + app.Id))
                list.Add((OpenPrefix + app.Id, "Open " + app.Name));
        return list;
    }

    public static (int Status, JsonObject Reply) Run(ILauncherActionHost host, string id, string source, DateTimeOffset now)
    {
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        double Secs() => t0.Elapsed.TotalSeconds;
        if (id == StatusId)
        {
            var f = host.Status();
            return (200, WebTriggerContract.Reply(true, id, "Launcher status", StatusLine(f, now), Secs(),
                ("show_lock", f.ShowLock), ("apps_installed", f.Installed), ("updates_waiting", f.LastCheck == null ? null : f.Updates),
                ("last_check", f.LastCheck?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)),
                ("launcher_version", VersionCompare.Norm(f.Version))));
        }
        if (id == ShowLockOnId)
        {
            if (host.ShowLock)
                return (200, WebTriggerContract.Reply(true, id, "Show lock on", "Show lock is already on", Secs(), ("show_lock", true), ("changed", false)));
            int stopped = host.TurnShowLockOn(source);
            var summary = "Show lock on" + (stopped > 0 ? $" ({stopped} download{(stopped == 1 ? "" : "s")} stopped)" : "");
            return (200, WebTriggerContract.Reply(true, id, "Show lock on", summary, Secs(),
                ("show_lock", true), ("changed", true), ("downloads_stopped", stopped)));
        }
        if (id.StartsWith(OpenPrefix, StringComparison.Ordinal))
        {
            var appId = id[OpenPrefix.Length..];
            var app = host.Apps().FirstOrDefault(a => a.Id == appId);
            if (app == null || !host.TickedApps().Contains(appId)) return WebTriggerContract.NotFound(id);
            var name = "Open " + app.Name;
            var installed = app.Slots.Select(s => (Slot: s, Version: host.InstalledVersion(s.Key))).Where(x => x.Version != null).ToList();
            if (installed.Count == 0)
                return Fail(422, id, name, $"{app.Name} isn't installed on this computer", Secs());
            if (app.Slots.Any(s => host.SlotBusy(s.Key)))
                return Fail(409, id, name, $"{app.Name} is being installed or updated — try again when it has finished", Secs());
            if (installed.Any(x => host.IsOpen(x.Slot.Key)))
                return (200, WebTriggerContract.Reply(true, id, name, $"{app.Name} is already open", Secs(), ("started", false)));
            var (slot, version) = installed[0];
            try { host.Launch(slot.Key); }
            catch (Exception ex)
            {
                host.Log($"launch {slot.Key} ({source}) failed: {ex.Message}");
                return Fail(502, id, name, $"Couldn't open {app.Name}: {ex.Message}", Secs());
            }
            host.Log($"launched {slot.Key} ({source})");
            var v = VersionCompare.Norm(version!);
            return (200, WebTriggerContract.Reply(true, id, name, $"Opened {app.Name}{slot.Suffix} {v}", Secs(),
                ("started", true), ("version", v)));
        }
        return WebTriggerContract.NotFound(id);
    }

    private static (int, JsonObject) Fail(int status, string id, string name, string summary, double seconds)
    {
        var r = WebTriggerContract.Reply(false, id, name, summary, seconds);
        r["error"] = summary + ".";
        return (status, r);
    }

    /// <summary>"Show lock off · 11 apps installed · 2 updates waiting (last check 14 min ago) · launcher 1.36.0".</summary>
    public static string StatusLine(Facts f, DateTimeOffset now)
    {
        var parts = new List<string>
        {
            f.ShowLock ? "Show lock on" : "Show lock off",
            $"{f.Installed} app{(f.Installed == 1 ? "" : "s")} installed",
            f.LastCheck is { } last
                ? $"{(f.Updates == 0 ? "no updates" : f.Updates == 1 ? "1 update" : $"{f.Updates} updates")} waiting (last check {CheckAge(last, now)})"
                : "no update check yet",
            "launcher " + VersionCompare.Norm(f.Version),
        };
        return string.Join(" · ", parts);
    }

    /// <summary>"just now", "14 min ago", "3 hours ago", "2 days ago".</summary>
    public static string CheckAge(DateTimeOffset last, DateTimeOffset now)
    {
        var span = now - last;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 48) { int h = (int)span.TotalHours; return h == 1 ? "1 hour ago" : $"{h} hours ago"; }
        return $"{(int)span.TotalDays} days ago";
    }
}

/// <summary>The launcher's web trigger: the listener, one run at a time, and every action run on the window's thread
/// through <paramref name="onWindow"/> (the window marshals it; a test passes <c>f =&gt; f()</c>).</summary>
public sealed class LauncherTrigger
{
    private readonly ILauncherActionHost _host;
    private readonly Func<Func<(int, JsonObject)>, (int, JsonObject)> _onWindow;
    private readonly OneAtATime _gate = new();

    public WebTriggerServer Server { get; }

    public LauncherTrigger(ILauncherActionHost host, Func<Func<(int, JsonObject)>, (int, JsonObject)> onWindow,
                           Func<string> token, Action<string>? log = null)
    {
        _host = host;
        _onWindow = onWindow;
        Server = new WebTriggerServer(Run, Listing, token, log);
    }

    private (int, JsonObject) Run(string id, string source)
    {
        if (!_gate.TryEnter()) return WebTriggerContract.Busy();
        try { return _onWindow(() => LauncherActions.Run(_host, id, source, DateTimeOffset.UtcNow)); }
        finally { _gate.Exit(); }
    }

    private IReadOnlyList<(string Id, string Name)> Listing()
    {
        List<(string, string)>? list = null;
        _onWindow(() => { list = LauncherActions.Listing(_host); return (200, new JsonObject()); });
        return list ?? new List<(string, string)>();
    }

    /// <summary>The Settings status line: "Off", where it listens and an example address, waiting for a taken port, or why
    /// it isn't listening.</summary>
    public static (string Text, bool Problem) Describe(bool enabled, string bind, WebTriggerServer server, bool waiting = false,
                                                       int port = WebTriggerContract.DefaultPort, bool background = false)
    {
        if (!enabled) return ("Off. Nothing listens until you switch it on.", false);
        if (!server.Running && background)
            return ($"The background copy is listening on port {port}. This window takes over if it stops.", false);
        if (!server.Running && waiting) return (WebTriggerContract.WaitingText(port), true);
        if (!server.Running) return (server.Error.Length > 0 ? server.Error : "Not listening.", true);
        var addr = WebTriggerContract.ParseIp(bind);
        bool every = addr != null && (addr.Equals(System.Net.IPAddress.Any) || addr.Equals(System.Net.IPAddress.IPv6Any));
        var host = every ? "<this computer's address>" : addr?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{bind}]" : bind;
        return ($"Listening on {server.Address}. Try: POST http://{host}:{server.Port}/actions/status?reply=text", false);
    }

    /// <summary>Settings: who answers a press right now. '' while the trigger is off.</summary>
    public static string Answering(bool enabled, bool windowOwns, bool background, bool stagehandInstalled)
    {
        if (!enabled) return "";
        if (windowOwns) return "Answering presses: this window.";
        if (background) return "Answering presses: the background copy.";
        return stagehandInstalled ? "Answering presses: not running — open Stagehand."
                                  : "Answering presses: not running — " + StagehandMissing;
    }

    /// <summary>Settings, when Stagehand (which keeps the background copy running) isn't installed.</summary>
    public const string StagehandMissing = "Install Stagehand so dashboard buttons keep working when JB Theatre Tools is closed.";
    /// <summary>Settings: what keeps the buttons working with the window closed.</summary>
    public const string BackgroundNote =
        "Dashboard buttons keep working when JB Theatre Tools is closed: Stagehand keeps a hidden copy running for them.";
}

/// <summary>The short guide behind "How it works…" in Settings (the same words on macOS).</summary>
public static class WebTriggerGuide
{
    public static IReadOnlyList<(string Heading, string Body)> Sections(int port) => new[]
    {
        ("What a button can do",
         "status — one line: show lock, apps installed, updates waiting from the last check (it never goes online) and this launcher's version.\n" +
         "show-lock-on — turns show lock on for this computer, the same as Ctrl+L: installs, updates and downloads stop, and Stagehand and every app see the lock. There is no \"off\" button on purpose: a person at the computer turns it off.\n" +
         "open-<app> — opens an app you ticked under \"Apps a button may open\", e.g. open-dmxtools. If it's already open, nothing new starts."),
        ("Switch it on",
         "Tick \"Switch on the web trigger\". Listen on: 127.0.0.1 when Node-RED runs on this computer (the safest); one of this computer's own addresses when the dashboard runs on another one; 0.0.0.0 (every network) needs a token. " +
         $"Port: {port} unless something else uses it. Token (optional): a shared word the button must send; it's kept in Credential Manager and never shown again.\n" +
         "Dashboard buttons keep working when JB Theatre Tools is closed, and after a restart: Stagehand keeps a hidden copy of the launcher running for them (install Stagehand for this). While JB Theatre Tools is running it answers presses itself; when it quits, the hidden copy takes over within a few seconds. Switching the trigger off stops the hidden copy too.\n" +
         $"If another program — or a copy of JB Theatre Tools that is still closing after an update — has the port, it waits and tries again every {WebTriggerContract.RetrySeconds} seconds; Settings shows \"Waiting for port {port}\" until it gets it."),
        ("The Show Dashboard button",
         $"In Show Dashboard add a Custom control of the kind \"Saved action\": this computer's address (127.0.0.1 when it's the same computer), port {port}, and the action id, e.g. open-dmxtools. " +
         $"It sends POST http://127.0.0.1:{port}/actions/open-dmxtools?reply=text with the body {{}} — or {{\"token\": \"your token\"}} when one is set — and shows the one-line reply on Last command. " +
         $"GET http://127.0.0.1:{port}/actions lists the ids you can use. Give show-lock-on a confirm text, e.g. \"Turn show lock on for this computer?\"."),
        ("What the replies mean",
         "200 — done (or already so: the app was open, the lock was on).\n" +
         "502 — it tried, but something failed (the app wouldn't open). The launcher's log says why.\n" +
         "400 — not a JSON request. Send a JSON body: {} or {\"token\": \"…\"}.\n" +
         "403 — wrong or missing token, or the request came from a web page (refused on purpose).\n" +
         "404 — no action with that id, or that app isn't ticked here.\n" +
         "409 — busy: another press is running, or that app is being installed or updated. Try again in a moment.\n" +
         "422 — it can't be done as things are: that app isn't installed on this computer.\n" +
         "503 — the web trigger has just been switched off.\n" +
         "No answer at all — the trigger is off, JB Theatre Tools isn't running, or the address or port is wrong."),
    };
}
