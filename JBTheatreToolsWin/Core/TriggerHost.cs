using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JBTheatreTools;

/// <summary>What a launch of this program is: the headless trigger host, the command line, or the window.</summary>
public enum LaunchMode { TriggerHost, Cli, Gui }

/// <summary>The launcher's registration with Stagehand, which keeps a hidden <c>--trigger-host</c> copy running so
/// Dashboard buttons keep working when JB Theatre Tools is closed (and after a restart). Plain files in Stagehand's state
/// folder (the same as TriggerHost.swift on macOS):
/// <list type="bullet">
/// <item><c>triggers\jbtheatretools.json</c> = {app_id, name, exe, args, registered} — written when the trigger is switched
/// on (and at every start while it's on, so <c>exe</c> follows an update), removed when it's switched off;</item>
/// <item><c>triggers\_jbtheatretools-host.json</c> = {pid, port, started} — written by the hidden copy while IT answers
/// presses, so Settings can say who is answering (Stagehand skips files that start with "_").</item>
/// </list>
/// Both are written atomically; on POSIX readable by this user only (0600) — on Windows the per-user profile folder.</summary>
public static class TriggerRegistration
{
    public const string AppId = "jbtheatretools";
    public const string Name = "JB Theatre Tools";
    public const string HostArg = "--trigger-host";

    /// <summary><c>--trigger-host</c> wins over everything (Stagehand starts it with exactly that); then the command line
    /// (a verb, or any <c>-</c> option); else the window.</summary>
    public static LaunchMode ModeOf(IReadOnlyList<string> args, ISet<string> cliCommands)
    {
        if (args.Contains(HostArg)) return LaunchMode.TriggerHost;
        if (args.Any(cliCommands.Contains) || args.Any(a => a.StartsWith('-'))) return LaunchMode.Cli;
        return LaunchMode.Gui;
    }

    /// <summary>Stagehand's state folder: %LOCALAPPDATA%\JBTheatreTools\stagehand\, or &lt;STAGEHAND_HOME&gt;\stagehand.</summary>
    public static string StateDir(string? stagehandHome, string localAppData) =>
        !string.IsNullOrEmpty(stagehandHome) ? Path.Combine(stagehandHome, "stagehand")
                                             : Path.Combine(localAppData, "JBTheatreTools", "stagehand");

    public static string StateDir() =>
        StateDir(Environment.GetEnvironmentVariable("STAGEHAND_HOME"),
                 Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string TriggersDir(string stateDir) => Path.Combine(stateDir, "triggers");
    public static string RegistrationPath(string stateDir) => Path.Combine(TriggersDir(stateDir), AppId + ".json");
    public static string HostMarkPath(string stateDir) => Path.Combine(TriggersDir(stateDir), "_" + AppId + "-host.json");
    public static string WindowMarkPath(string stateDir) => Path.Combine(TriggersDir(stateDir), "_" + AppId + "-window.json");

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static JsonObject Document(string exe, DateTimeOffset now) => new()
    {
        ["app_id"] = AppId, ["name"] = Name, ["exe"] = exe, ["args"] = new JsonArray(HostArg), ["registered"] = Iso(now),
    };

    /// <summary>Writes the registration. False when it couldn't be written.</summary>
    public static bool Register(string exe, string stateDir, DateTimeOffset? now = null)
    {
        try
        {
            var json = Document(exe, now ?? DateTimeOffset.UtcNow).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            WritePrivate(Encoding.UTF8.GetBytes(json), RegistrationPath(stateDir));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Removes the registration (switched off). True if there was one.</summary>
    public static bool Unregister(string stateDir)
    {
        var path = RegistrationPath(stateDir);
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Atomic and private: a temp file beside it (not ending ".json", so Stagehand never reads a half file),
    /// created 0600 on POSIX, then moved over the old one.</summary>
    public static void WritePrivate(byte[] data, string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.tmp{Environment.ProcessId}");
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var f = new FileStream(tmp, options)) f.Write(data);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    // ── Who is answering presses ──

    public static void MarkHost(int pid, int port, string stateDir, DateTimeOffset? now = null)
    {
        var doc = new JsonObject { ["pid"] = pid, ["port"] = port, ["started"] = Iso(now ?? DateTimeOffset.UtcNow) };
        try { WritePrivate(Encoding.UTF8.GetBytes(doc.ToJsonString()), HostMarkPath(stateDir)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Settings then says "not running" */ }
    }

    private static int? MarkedPid(string stateDir) => MarkedPidAt(HostMarkPath(stateDir));

    private static int? MarkedPidAt(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path))?["pid"]?.GetValue<int>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
                                       or FormatException) { return null; }
    }

    /// <summary>Removes the mark only when it is still this process's.</summary>
    public static void ClearHost(int pid, string stateDir)
    {
        if (MarkedPid(stateDir) != pid) return;
        try { File.Delete(HostMarkPath(stateDir)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The launcher itself is running (window open, or kept in the tray): it answers presses, so the hidden copy
    /// lets go of the port — the window's show lock, downloads and installs are then never bypassed.</summary>
    public static void MarkWindow(int pid, string stateDir)
    {
        try { WritePrivate(Encoding.UTF8.GetBytes(new JsonObject { ["pid"] = pid }.ToJsonString()), WindowMarkPath(stateDir)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the hidden copy then keeps the port */ }
    }

    public static void ClearWindow(int pid, string stateDir)
    {
        if (MarkedPidAt(WindowMarkPath(stateDir)) != pid) return;
        try { File.Delete(WindowMarkPath(stateDir)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A launcher (not this process) is running: its mark is there and <paramref name="isLauncher"/> says that pid is
    /// still this launcher's program (a crashed launcher's pid reused by something else doesn't count).</summary>
    public static bool WindowRunning(string stateDir, int ownPid, Func<int, bool> isLauncher) =>
        MarkedPidAt(WindowMarkPath(stateDir)) is { } pid && pid != ownPid && isLauncher(pid);

    public static bool PidAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>The hidden copy is listening right now (its mark is there and its process is alive).</summary>
    public static bool BackgroundAnswering(string stateDir, Func<int, bool>? alive = null) =>
        MarkedPid(stateDir) is { } pid && (alive ?? PidAlive)(pid);
}

/// <summary>Stagehand, the suite app that keeps the hidden copies running.</summary>
public static class StagehandLink
{
    public static readonly string[] EnsureArgs = { "--trigger-hosts", "--ensure" };

    /// <summary>Asks Stagehand to make sure its supervisor runs (<c>--trigger-hosts --ensure</c>): started with no window
    /// and left to itself, never waited on, never fatal. False when it couldn't even start.</summary>
    public static bool Ensure(string exe, Action<string, string[]>? spawn = null, Action<string>? log = null)
    {
        log ??= _ => { };
        try
        {
            (spawn ?? SpawnDetached)(exe, EnsureArgs);
            log("web trigger: asked Stagehand to keep the background copy running");
            return true;
        }
        catch (Exception ex)
        {
            log($"web trigger: couldn't ask Stagehand to keep the background copy running ({ex.Message})");
            return false;
        }
    }

    public static void SpawnDetached(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);   // nothing waits on it
    }
}

/// <summary>The trigger's settings as saved by the window, read fresh by the hidden copy.</summary>
public sealed record TriggerSettings(bool Enabled, string Bind, int Port);

/// <summary>The hidden copy's loop: every few seconds it re-reads the saved settings — switched off → it lets go of the
/// port and ends (exit 0); otherwise it keeps owning the port (waiting while the launcher's window has it, taking over
/// when the window closes). Marks itself in Stagehand's folder while it answers presses.</summary>
public sealed class TriggerHostRunner
{
    private readonly Func<TriggerSettings> _settings;
    private readonly Func<string> _readToken;
    private readonly string _stateDir;
    private readonly int _pid;
    private readonly Action<string> _log;
    private readonly Func<bool> _windowOpen;
    private readonly object _markGate = new();
    private bool _marked;
    private string _lastNote = "";

    public WebTriggerOwner Owner { get; }

    public TriggerHostRunner(WebTriggerOwner owner, Func<TriggerSettings> settings, Func<string> readToken, string stateDir,
                             int? pid = null, Action<string>? log = null, Func<bool>? windowOpen = null)
    {
        Owner = owner;
        _windowOpen = windowOpen ?? (() => false);
        _settings = settings;
        _readToken = readToken;
        _stateDir = stateDir;
        _pid = pid ?? Environment.ProcessId;
        _log = log ?? (_ => { });
        owner.OnChange = _ => SyncMark();
    }

    private void Note(string line)
    {
        if (line != _lastNote && line.Length > 0) _log(line);
        _lastNote = line;
    }

    /// <summary>One pass. null = keep going; a number = end with that exit code.</summary>
    public int? Tick()
    {
        var s = _settings();
        if (!s.Enabled)
        {
            Owner.Stop();
            SyncMark();
            _log("the web trigger is switched off — the background copy stops");
            return 0;
        }
        if (_windowOpen())   // the launcher is running: it answers presses itself
        {
            Owner.Stop();
            SyncMark();
            Note("the launcher is running — it answers presses");
            return null;
        }
        var word = _readToken();
        var bind = (s.Bind ?? "").Trim();
        var problem = WebTriggerContract.BindProblem(bind, s.Port.ToString(CultureInfo.InvariantCulture), word.Length > 0);
        if (problem.Length > 0)
        {
            Owner.Stop();
            SyncMark();
            Note("not listening — " + problem);
            return null;
        }
        Note(Owner.Own(bind, s.Port));
        SyncMark();
        return null;
    }

    public int Run(TimeSpan every, CancellationToken ct = default)
    {
        _log($"background copy started (pid {_pid})");
        while (true)
        {
            if (Tick() is { } code) return code;
            if (ct.WaitHandle.WaitOne(every)) { Owner.Stop(); SyncMark(); return 0; }
        }
    }

    private void SyncMark()
    {
        lock (_markGate)
        {
            bool owning = Owner.State == WebTriggerOwner.OwnerState.Owning && Owner.Server.Running;
            if (owning && !_marked) { TriggerRegistration.MarkHost(_pid, Owner.Server.Port, _stateDir); _marked = true; }
            else if (!owning && _marked) { TriggerRegistration.ClearHost(_pid, _stateDir); _marked = false; }
        }
    }
}

/// <summary>What the actions see of the launcher in the hidden copy (the same as HeadlessTriggerHost on macOS): no window —
/// the saved settings, the install manifest and the catalog, each read again on every press through the delegates.
/// Opening an app launches it as the window's Open does; show lock on is the shared lock.</summary>
public sealed class HeadlessActionHost(
    IReadOnlyList<LauncherActions.App> apps,
    Func<bool> showLock,
    Action turnShowLockOn,
    Func<IReadOnlyCollection<string>> tickedApps,
    Func<string, string?> installedVersion,
    Func<string, bool> isOpen,
    Action<string> launch,
    string version,
    Action<string> log) : ILauncherActionHost
{
    public bool ShowLock => showLock();

    public int TurnShowLockOn(string source)
    {
        turnShowLockOn();
        log($"show lock on ({source})");
        return 0;   // the hidden copy runs no downloads
    }

    public IReadOnlyList<LauncherActions.App> Apps() => apps;
    public IReadOnlyCollection<string> TickedApps() => tickedApps();
    public string? InstalledVersion(string slotKey) => installedVersion(slotKey);
    public bool SlotBusy(string slotKey) => false;
    public bool IsOpen(string slotKey) => isOpen(slotKey);
    public void Launch(string slotKey) => launch(slotKey);

    public LauncherActions.Facts Status() =>
        new(ShowLock, apps.Count(a => a.Slots.Any(s => installedVersion(s.Key) != null)), 0, null, version);

    public void Log(string line) => log(line);
}

/// <summary>The hidden copy's press gate, used as <see cref="LauncherTrigger"/>'s onWindow: switched off in the window →
/// 503 from the very next press (the loop lets go within 3 s); otherwise the manifest is read again (the window may have
/// installed or removed apps) and the action runs.</summary>
public static class HeadlessTrigger
{
    public static (int, JsonObject) Off() =>
        (503, new JsonObject { ["ok"] = false, ["error"] = "The web trigger is off.", ["summary"] = "the web trigger is off" });

    public static Func<Func<(int, JsonObject)>, (int, JsonObject)> Gate(Func<bool> enabled, Action reload) => work =>
    {
        if (!enabled()) return Off();
        reload();
        return work();
    };

    /// <summary>The word the hidden copy expects, read again on every pass: '' (none saved), the token, or — when it is
    /// saved but can't be read — an unguessable stand-in that refuses every press.</summary>
    public static string Word(Func<string?> load)
    {
        try { return load() ?? ""; }
        catch (Exception) { return Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"); }
    }
}
