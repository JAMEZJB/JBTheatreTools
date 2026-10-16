using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>The hidden <c>--trigger-host</c> copy and its Stagehand registration — the same cases as TriggerHostTests.swift.
/// Loopback (127.0.0.1, port 0) and a temp folder standing in for STAGEHAND_HOME only: never the real state folder, never
/// the real Stagehand.</summary>
public sealed class TriggerHostTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "jbtt-host-" + Guid.NewGuid().ToString("N"));
    private string StateDir => TriggerRegistration.StateDir(_home, "/unused-local-app-data");

    public TriggerHostTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    // ── Where Stagehand looks ──

    [Fact]
    public void StateDirFollowsStagehandHome()
    {
        Assert.Equal(Path.Combine("/tmp/sandbox", "stagehand"), TriggerRegistration.StateDir("/tmp/sandbox", "/synthetic/Local"));
        Assert.Equal(Path.Combine("/synthetic/Local", "JBTheatreTools", "stagehand"), TriggerRegistration.StateDir(null, "/synthetic/Local"));
        Assert.Equal(Path.Combine("/synthetic/Local", "JBTheatreTools", "stagehand"), TriggerRegistration.StateDir("", "/synthetic/Local"));
    }

    // ── Registration ──

    [Fact]
    public void RegisterWritesThePrivateFileAndUnregisterRemovesIt()
    {
        const string exe = @"C:\Users\synthetic\AppData\Local\JBTheatreTools\JBTheatreTools.exe";
        Assert.True(TriggerRegistration.Register(exe, StateDir, new DateTimeOffset(2027, 1, 15, 8, 0, 0, TimeSpan.Zero)));
        var path = TriggerRegistration.RegistrationPath(StateDir);
        Assert.Equal(Path.Combine(StateDir, "triggers", "jbtheatretools.json"), path);
        var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("jbtheatretools", doc["app_id"]!.GetValue<string>());
        Assert.Equal("JB Theatre Tools", doc["name"]!.GetValue<string>());
        Assert.Equal(exe, doc["exe"]!.GetValue<string>());
        Assert.Equal(new[] { "--trigger-host" }, doc["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal("2027-01-15T08:00:00Z", doc["registered"]!.GetValue<string>());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(new[] { "jbtheatretools.json" }, Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));

        // Re-registering (every start while it's on) refreshes the path.
        Assert.True(TriggerRegistration.Register(@"C:\newer\JBTheatreTools.exe", StateDir));
        Assert.Equal(@"C:\newer\JBTheatreTools.exe", JsonNode.Parse(File.ReadAllText(path))!["exe"]!.GetValue<string>());

        Assert.True(TriggerRegistration.Unregister(StateDir));
        Assert.False(File.Exists(path));
        Assert.False(TriggerRegistration.Unregister(StateDir));   // nothing left to remove
    }

    [Fact]
    public void HostMarkSaysWhoIsAnswering()
    {
        Assert.False(TriggerRegistration.BackgroundAnswering(StateDir));
        TriggerRegistration.MarkHost(4242, 42154, StateDir);
        Assert.True(TriggerRegistration.BackgroundAnswering(StateDir, pid => pid == 4242));
        Assert.False(TriggerRegistration.BackgroundAnswering(StateDir, _ => false));   // a dead copy isn't answering
        Assert.StartsWith("_", Path.GetFileName(TriggerRegistration.HostMarkPath(StateDir)));   // Stagehand skips it
        TriggerRegistration.ClearHost(1, StateDir);
        Assert.True(File.Exists(TriggerRegistration.HostMarkPath(StateDir)));   // only its own mark is removed
        TriggerRegistration.ClearHost(4242, StateDir);
        Assert.False(File.Exists(TriggerRegistration.HostMarkPath(StateDir)));
    }

    [Fact]
    public void AnsweringWords()
    {
        Assert.Equal("", LauncherTrigger.Answering(false, false, false, true));
        Assert.Equal("Answering presses: this window.", LauncherTrigger.Answering(true, true, true, true));
        Assert.Equal("Answering presses: the background copy.", LauncherTrigger.Answering(true, false, true, true));
        Assert.Equal("Answering presses: not running — open Stagehand.", LauncherTrigger.Answering(true, false, false, true));
        Assert.EndsWith("Install Stagehand so dashboard buttons keep working when JB Theatre Tools is closed.",
                        LauncherTrigger.Answering(true, false, false, false));
        Assert.Equal("Dashboard buttons keep working when JB Theatre Tools is closed: Stagehand keeps a hidden copy running for them.",
                     LauncherTrigger.BackgroundNote);
        var idle = new WebTriggerServer((_, _) => (200, new JsonObject()), () => Array.Empty<(string, string)>(), () => "");
        Assert.False(LauncherTrigger.Describe(true, "127.0.0.1", idle, waiting: true, port: 42154, background: true).Problem);
    }

    // ── Asking Stagehand (stubbed — the real Stagehand never runs here) ──

    [Fact]
    public void EnsureRunsStagehandWithTheEnsureArgsAndNeverThrows()
    {
        var calls = new List<(string Exe, string[] Args)>();
        const string exe = @"C:\apps\stagehand\Stagehand.exe";
        Assert.True(StagehandLink.Ensure(exe, (e, a) => calls.Add((e, a))));
        Assert.Single(calls);
        Assert.Equal(exe, calls[0].Exe);
        Assert.Equal(new[] { "--trigger-hosts", "--ensure" }, calls[0].Args);
        var lines = new List<string>();
        Assert.False(StagehandLink.Ensure(exe, (_, _) => throw new FileNotFoundException("synthetic"), lines.Add));
        Assert.Single(lines);
    }

    // ── Dispatch ──

    [Fact]
    public void TriggerHostDispatchComesFirst()
    {
        var cli = new HashSet<string> { "--list", "--install" };
        Assert.Equal(LaunchMode.TriggerHost, TriggerRegistration.ModeOf(new[] { "--trigger-host" }, cli));
        Assert.Equal(LaunchMode.TriggerHost, TriggerRegistration.ModeOf(new[] { "--list", "--trigger-host" }, cli));
        Assert.Equal(LaunchMode.Cli, TriggerRegistration.ModeOf(new[] { "--list" }, cli));
        Assert.Equal(LaunchMode.Cli, TriggerRegistration.ModeOf(new[] { "--typo" }, cli));
        Assert.Equal(LaunchMode.Gui, TriggerRegistration.ModeOf(Array.Empty<string>(), cli));
        Assert.Equal(LaunchMode.Gui, TriggerRegistration.ModeOf(new[] { "/restart" }, cli));
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;

    /// <summary>The hidden copy never builds the window or the tray: its source names neither, and Program dispatches it
    /// before the window exists.</summary>
    [Fact]
    public void TriggerHostNeverTouchesTheWindowOrTheTray()
    {
        var win = Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!;
        var host = File.ReadAllText(Path.Combine(win, "TriggerHostMain.cs"));
        foreach (var banned in new[] { "MainForm", "NotifyIcon", "Application.Run", "ApplicationConfiguration", "Form(", "AllocConsole" })
            Assert.DoesNotContain(banned, host);
        var program = File.ReadAllText(Path.Combine(win, "Program.cs"));
        Assert.True(program.IndexOf("TriggerHostMain.Run()", StringComparison.Ordinal) <
                    program.IndexOf("new MainForm()", StringComparison.Ordinal));
    }

    // ── The hidden copy's loop (loopback, port 0) ──

    private static WebTriggerServer PlainServer() =>
        new((_, _) => (200, new JsonObject { ["ok"] = true, ["summary"] = "synthetic" }), () => Array.Empty<(string, string)>(), () => "");

    private static bool Eventually(Func<bool> done)
    {
        for (int i = 0; i < 100; i++)
        {
            if (done()) return true;
            Thread.Sleep(50);
        }
        return done();
    }

    private static int Health(int port)
    {
        using var c = new TcpClient();
        c.Connect(IPAddress.Loopback, port);
        c.ReceiveTimeout = 10000;
        var s = c.GetStream();
        s.Write(Encoding.UTF8.GetBytes("GET /health HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return int.Parse(Encoding.UTF8.GetString(ms.ToArray()).Split(' ')[1]);
    }

    /// <summary>The window holds the port → the hidden copy waits; the window closes → the copy takes over and marks
    /// itself; the trigger is switched off in settings → it lets go and ends with 0.</summary>
    [Fact]
    public void HostWaitsForTheWindowTakesOverAndStopsWhenSwitchedOff()
    {
        var window = PlainServer();
        Assert.Equal("", window.Start("127.0.0.1", 0));
        int port = window.Port;
        var settings = new TriggerSettings(true, "127.0.0.1", port);
        var lines = new List<string>();
        var runner = new TriggerHostRunner(new WebTriggerOwner(PlainServer(), TimeSpan.FromMilliseconds(100)), () => settings,
                                           () => "", StateDir, log: l => { lock (lines) lines.Add(l); });
        try
        {
            Assert.Null(runner.Tick());
            Assert.Equal(WebTriggerOwner.OwnerState.Waiting, runner.Owner.State);
            Assert.False(TriggerRegistration.BackgroundAnswering(StateDir));

            window.Stop();   // the window closes
            Assert.True(Eventually(() => runner.Owner.State == WebTriggerOwner.OwnerState.Owning));
            Assert.Equal(port, runner.Owner.Server.Port);
            Assert.Equal(200, Health(port));
            Assert.True(Eventually(() => TriggerRegistration.BackgroundAnswering(StateDir)));   // Settings can tell

            Assert.Null(runner.Tick());   // still on: another pass changes nothing
            Assert.True(runner.Owner.Server.Running);

            settings = settings with { Enabled = false };
            Assert.Equal(0, runner.Tick());
            Assert.False(runner.Owner.Server.Running);
            Assert.False(TriggerRegistration.BackgroundAnswering(StateDir));
            lock (lines) Assert.Contains(lines, l => l.Contains("switched off"));
        }
        finally { runner.Owner.Stop(); window.Stop(); }
    }

    [Fact]
    public void RunEndsWithZeroWhenTheTriggerIsOff()
    {
        var runner = new TriggerHostRunner(new WebTriggerOwner(PlainServer(), TimeSpan.FromMilliseconds(100)),
                                           () => new TriggerSettings(false, "127.0.0.1", 0), () => "", StateDir);
        Assert.Equal(0, runner.Run(TimeSpan.FromMilliseconds(50)));
    }

    /// <summary>A bad saved address (every network without a token) isn't served; the copy keeps re-reading settings.</summary>
    [Fact]
    public void HostRefusesWhatTheWindowWouldRefuse()
    {
        var lines = new List<string>();
        var runner = new TriggerHostRunner(new WebTriggerOwner(PlainServer(), TimeSpan.FromMilliseconds(100)),
                                           () => new TriggerSettings(true, "0.0.0.0", 0), () => "", StateDir, log: lines.Add);
        Assert.Null(runner.Tick());
        Assert.False(runner.Owner.Server.Running);
        Assert.Contains(lines, l => l.Contains("needs a token"));
        runner.Owner.Stop();
    }

    [Fact]
    public void HostLetsTheRunningLauncherAnswer()
    {
        var probe = PlainServer();
        Assert.Equal("", probe.Start("127.0.0.1", 0));
        int port = probe.Port;
        probe.Stop();
        bool launcherUp = false;
        var runner = new TriggerHostRunner(new WebTriggerOwner(PlainServer(), TimeSpan.FromMilliseconds(100)),
                                           () => new TriggerSettings(true, "127.0.0.1", port), () => "", StateDir,
                                           windowOpen: () => launcherUp);
        try
        {
            Assert.Null(runner.Tick());
            Assert.Equal(WebTriggerOwner.OwnerState.Owning, runner.Owner.State);
            launcherUp = true;
            Assert.Null(runner.Tick());
            Assert.False(runner.Owner.Server.Running);
            Assert.False(TriggerRegistration.BackgroundAnswering(StateDir));
            launcherUp = false;
            Assert.Null(runner.Tick());
            Assert.Equal(WebTriggerOwner.OwnerState.Owning, runner.Owner.State);
        }
        finally { runner.Owner.Stop(); }
    }

    [Fact]
    public void WindowMark()
    {
        Assert.False(TriggerRegistration.WindowRunning(StateDir, 1, _ => true));
        TriggerRegistration.MarkWindow(4242, StateDir);
        Assert.True(TriggerRegistration.WindowRunning(StateDir, 1, pid => pid == 4242));
        Assert.False(TriggerRegistration.WindowRunning(StateDir, 4242, _ => true));
        Assert.False(TriggerRegistration.WindowRunning(StateDir, 1, _ => false));
        TriggerRegistration.ClearWindow(1, StateDir);
        Assert.True(TriggerRegistration.WindowRunning(StateDir, 1, _ => true));
        TriggerRegistration.ClearWindow(4242, StateDir);
        Assert.False(TriggerRegistration.WindowRunning(StateDir, 1, _ => true));
    }
}
