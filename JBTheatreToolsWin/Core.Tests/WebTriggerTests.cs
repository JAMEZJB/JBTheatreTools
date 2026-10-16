using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>The launcher's web trigger end to end on 127.0.0.1 (port 0: the OS picks a free one), through the real
/// listener and the real actions against a stand-in window — never the real settings, apps or Credential Manager.</summary>
public sealed class WebTriggerTests : IDisposable
{
    private const string Word = "synthetic-trigger-word";

    private sealed class FakeHost : ILauncherActionHost
    {
        public bool Lock;
        public int Downloads = 2;
        public HashSet<string> Ticked = new() { "dmxtools", "nditools", "psntools" };
        public Dictionary<string, string> Installed = new() { ["dmxtools"] = "v1.12.0", ["nditools"] = "v0.4.0", ["nditools@full"] = "v0.4.0" };
        public HashSet<string> Busy = new();
        public HashSet<string> Open = new();
        public List<string> Launched = new();
        public List<string> Lines = new();
        public string? LaunchError;
        public ManualResetEventSlim? Hold;
        public ManualResetEventSlim Entered = new();
        public DateTimeOffset? LastCheck = DateTimeOffset.UtcNow.AddMinutes(-14);

        public bool ShowLock => Lock;
        public int TurnShowLockOn(string source) { Lock = true; Lines.Add($"show lock on ({source})"); var n = Downloads; Downloads = 0; return n; }
        public IReadOnlyList<LauncherActions.App> Apps() => new[]
        {
            new LauncherActions.App("dmxtools", "DMX Tools", new[] { new LauncherActions.Slot("dmxtools", "") }),
            new LauncherActions.App("nditools", "NDI Tools", new[] { new LauncherActions.Slot("nditools@full", " (Full)"), new LauncherActions.Slot("nditools", "") }),
            new LauncherActions.App("psntools", "PSN Tools", new[] { new LauncherActions.Slot("psntools", "") }),
            new LauncherActions.App("powercalc", "PowerCalc", new[] { new LauncherActions.Slot("powercalc", "") }),
        };
        public IReadOnlyCollection<string> TickedApps() => Ticked;
        public string? InstalledVersion(string slotKey) => Installed.TryGetValue(slotKey, out var v) ? v : null;
        public bool SlotBusy(string slotKey) => Busy.Contains(slotKey);
        public bool IsOpen(string slotKey) => Open.Contains(slotKey);
        public void Launch(string slotKey)
        {
            Entered.Set();
            Hold?.Wait(TimeSpan.FromSeconds(10));
            if (LaunchError != null) throw new InvalidOperationException(LaunchError);
            Launched.Add(slotKey);
        }
        public LauncherActions.Facts Status() => new(Lock, 11, 2, LastCheck, "1.36.0");
        public void Log(string line) => Lines.Add(line);
    }

    private readonly FakeHost _host = new();
    private readonly List<string> _log = new();
    private string _token = "";
    private readonly LauncherTrigger _trigger;

    public WebTriggerTests()
    {
        _trigger = new LauncherTrigger(_host, f => f(), () => _token, l => { lock (_log) _log.Add(l); });
        Assert.Equal("", _trigger.Server.Start("127.0.0.1", 0));
    }

    public void Dispose() => _trigger.Server.Stop();

    private int Port => _trigger.Server.Port;

    /// <summary>Sends one raw HTTP request; returns (status, body).</summary>
    private static (int Status, string Body) Raw(int port, string request, byte[]? body = null)
    {
        using var c = new TcpClient();
        c.Connect(IPAddress.Loopback, port);
        c.ReceiveTimeout = 10000;
        var s = c.GetStream();
        s.Write(Encoding.UTF8.GetBytes(request));
        if (body != null) s.Write(body);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var text = Encoding.UTF8.GetString(ms.ToArray());
        int status = int.Parse(text.Split(' ')[1]);
        return (status, text[(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
    }

    private (int Status, string Body) Post(string path, string json = "{}", string extraHeaders = "", string contentType = "application/json",
                                         string host = "127.0.0.1")
    {
        var b = Encoding.UTF8.GetBytes(json);
        return Raw(Port, $"POST {path} HTTP/1.1\r\nHost: {host}:{Port}\r\nContent-Type: {contentType}\r\nContent-Length: {b.Length}\r\n{extraHeaders}\r\n", b);
    }

    private (int Status, string Body) Get(string path, string extraHeaders = "") =>
        Raw(Port, $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{Port}\r\n{extraHeaders}\r\n");

    private static JsonObject J(string body) => JsonNode.Parse(body)!.AsObject();

    // ── The contract's pure parts ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("status", true)]
    [InlineData("open-dmxtools", true)]
    [InlineData("show-lock-on", true)]
    [InlineData("a.b_c-1", true)]
    [InlineData("", false)]
    [InlineData("-x", false)]
    [InlineData("Open-dmx", false)]
    [InlineData("open dmx", false)]
    [InlineData("open/dmx", false)]
    [InlineData("é", false)]
    public void ActionIds(string id, bool ok) => Assert.Equal(ok, WebTriggerContract.ValidActionId(id));

    [Fact]
    public void ActionIdsAreAtMost64()
    {
        Assert.True(WebTriggerContract.ValidActionId(new string('a', 64)));
        Assert.False(WebTriggerContract.ValidActionId(new string('a', 65)));
    }

    [Theory]
    [InlineData("127.0.0.1", "42154", false, "")]
    [InlineData("192.0.2.5", "42154", false, "")]
    [InlineData("::1", "42154", false, "")]
    [InlineData("0.0.0.0", "42154", true, "")]
    [InlineData("0.0.0.0", "42154", false, "Listening on every network (0.0.0.0) needs a token: set one first.")]
    [InlineData("::", "42154", false, "Listening on every network (0.0.0.0) needs a token: set one first.")]
    [InlineData("localhost", "42154", false, "The trigger address must be an IP address of this computer, e.g. 127.0.0.1.")]
    [InlineData("127.1", "42154", false, "The trigger address must be an IP address of this computer, e.g. 127.0.0.1.")]
    [InlineData("127.0.0.01", "42154", false, "The trigger address must be an IP address of this computer, e.g. 127.0.0.1.")]
    [InlineData("", "42154", false, "The trigger address must be an IP address of this computer, e.g. 127.0.0.1.")]
    [InlineData("127.0.0.1", "port", false, "The trigger port must be a number.")]
    [InlineData("127.0.0.1", "80", false, "The trigger port must be between 1024 and 65535.")]
    [InlineData("127.0.0.1", "65536", false, "The trigger port must be between 1024 and 65535.")]
    public void BindProblems(string bind, string port, bool token, string want) =>
        Assert.Equal(want, WebTriggerContract.BindProblem(bind, port, token));

    [Theory]
    [InlineData("", true)]
    [InlineData("127.0.0.1:42154", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost:42154", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("[::1]:42154", true)]
    [InlineData("192.0.2.20:42154", true)]
    [InlineData("evil.example:42154", false)]
    [InlineData("evil.example", false)]
    [InlineData("127.0.0.1.nip.example", false)]
    public void HostHeader(string host, bool ok) => Assert.Equal(ok, WebTriggerContract.HostOk(host));

    [Fact]
    public void TokenRules()
    {
        Assert.Equal("", WebTriggerContract.TokenProblem("eight-ch"));
        Assert.NotEqual("", WebTriggerContract.TokenProblem("short"));
        Assert.NotEqual("", WebTriggerContract.TokenProblem("two\nlines-here"));
        Assert.NotEqual("", WebTriggerContract.TokenProblem(new string('x', 201)));
        Assert.True(WebTriggerContract.TokenMatches(Word, Word));
        Assert.False(WebTriggerContract.TokenMatches("", Word));
        Assert.False(WebTriggerContract.TokenMatches(Word + "x", Word));
    }

    [Fact]
    public void StatusLineWording()
    {
        var now = new DateTimeOffset(2026, 10, 16, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal("Show lock off · 11 apps installed · 2 updates waiting (last check 14 min ago) · launcher 1.36.0",
            LauncherActions.StatusLine(new(false, 11, 2, now.AddMinutes(-14), "1.36.0"), now));
        Assert.Equal("Show lock on · 1 app installed · no update check yet · launcher 1.36.0",
            LauncherActions.StatusLine(new(true, 1, 0, null, "v1.36.0"), now));
        Assert.Equal("Show lock off · 0 apps installed · 1 update waiting (last check just now) · launcher 1.36.0",
            LauncherActions.StatusLine(new(false, 0, 1, now.AddSeconds(-20), "1.36.0"), now));
        Assert.Equal("no updates", LauncherActions.StatusLine(new(false, 3, 0, now.AddHours(-3), "1.36.0"), now).Split(" · ")[2][..10]);
        Assert.Equal("3 hours ago", LauncherActions.CheckAge(now.AddHours(-3), now));
        Assert.Equal("1 hour ago", LauncherActions.CheckAge(now.AddMinutes(-61), now));
        Assert.Equal("2 days ago", LauncherActions.CheckAge(now.AddDays(-2), now));
    }

    // ── End to end ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HealthAndListing()
    {
        var (s, body) = Get("/health");
        Assert.Equal(200, s);
        Assert.Equal("JB Theatre Tools web trigger", J(body)["summary"]!.GetValue<string>());
        (s, body) = Get("/actions");
        Assert.Equal(200, s);
        var ids = J(body)["actions"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "status", "show-lock-on", "open-dmxtools", "open-nditools", "open-psntools" }, ids);   // powercalc isn't ticked
        Assert.Equal("Open DMX Tools", J(body)["actions"]![2]!["name"]!.GetValue<string>());
        (s, body) = Get("/actions?reply=text");
        Assert.Equal("status, show-lock-on, open-dmxtools, open-nditools, open-psntools\n", body);
        Assert.Equal(404, Get("/nothing").Status);
    }

    [Fact]
    public void NothingTickedListsOnlyTheFixedActions()
    {
        _host.Ticked.Clear();
        var ids = J(Get("/actions").Body)["actions"]!.AsArray().Select(a => a!["id"]!.GetValue<string>());
        Assert.Equal(new[] { "status", "show-lock-on" }, ids);
        Assert.Equal(404, Post("/actions/open-dmxtools").Status);
    }

    [Fact]
    public void StatusIsReadOnly()
    {
        var (s, body) = Post("/actions/status?reply=text");
        Assert.Equal(200, s);
        Assert.Equal("Show lock off · 11 apps installed · 2 updates waiting (last check 14 min ago) · launcher 1.36.0\n", body);
        var j = J(Post("/actions/status").Body);
        Assert.True(j["ok"]!.GetValue<bool>());
        Assert.Equal("status", j["action"]!.GetValue<string>());
        Assert.Equal(11, j["apps_installed"]!.GetValue<int>());
        Assert.Equal("1.36.0", j["launcher_version"]!.GetValue<string>());
        Assert.False(_host.Lock);
        Assert.Empty(_host.Launched);
    }

    [Fact]
    public void ShowLockOnAndAlreadyOn()
    {
        var (s, body) = Post("/actions/show-lock-on?reply=text");
        Assert.Equal(200, s);
        Assert.Equal("Show lock on (2 downloads stopped)\n", body);
        Assert.True(_host.Lock);
        Assert.Contains(_host.Lines, l => l == "show lock on (web trigger from 127.0.0.1)");
        (s, body) = Post("/actions/show-lock-on?reply=text");
        Assert.Equal(200, s);
        Assert.Equal("Show lock is already on\n", body);
        Assert.Equal(404, Post("/actions/show-lock-off").Status);   // there is no "off", on purpose
    }

    [Fact]
    public void OpenStartsTheFullEditionFirst()
    {
        var (s, body) = Post("/actions/open-nditools?reply=text");
        Assert.Equal(200, s);
        Assert.Equal("Opened NDI Tools (Full) 0.4.0\n", body);
        Assert.Equal(new[] { "nditools@full" }, _host.Launched);
        Assert.Contains(_host.Lines, l => l == "launched nditools@full (web trigger from 127.0.0.1)");
        var j = J(Post("/actions/open-dmxtools").Body);
        Assert.True(j["started"]!.GetValue<bool>());
        Assert.Equal("Opened DMX Tools 1.12.0", j["summary"]!.GetValue<string>());
    }

    [Fact]
    public void OpenWhenAlreadyOpenStartsNothing()
    {
        _host.Open.Add("nditools");   // the Light edition is open: a press never opens a second copy
        var (s, body) = Post("/actions/open-nditools?reply=text");
        Assert.Equal(200, s);
        Assert.Equal("NDI Tools is already open\n", body);
        Assert.Empty(_host.Launched);
    }

    [Fact]
    public void OpenRefusals()
    {
        var (s, body) = Post("/actions/open-psntools?reply=text");                 // ticked but not installed
        Assert.Equal(422, s);
        Assert.Equal("PSN Tools isn't installed on this computer\n", body);
        Assert.Equal(404, Post("/actions/open-powercalc").Status);                 // installed or not, it isn't ticked
        Assert.Equal(404, Post("/actions/open-nosuchapp").Status);
        _host.Busy.Add("dmxtools");
        Assert.Equal(409, Post("/actions/open-dmxtools").Status);                  // mid-install
        _host.Busy.Clear();
        _host.LaunchError = "synthetic failure";
        (s, body) = Post("/actions/open-dmxtools?reply=text");
        Assert.Equal(502, s);
        Assert.Equal("Couldn't open DMX Tools: synthetic failure\n", body);
        Assert.Empty(_host.Launched);
    }

    [Fact]
    public async Task OneRunAtATime()
    {
        _host.Hold = new ManualResetEventSlim(false);
        var first = Task.Run(() => Post("/actions/open-dmxtools"));
        Assert.True(_host.Entered.Wait(TimeSpan.FromSeconds(10)));   // the first press is inside its action
        var (s, body) = Post("/actions/status?reply=text");
        Assert.Equal(409, s);
        Assert.Equal("busy: another action is running\n", body);
        _host.Hold.Set();
        Assert.Equal(200, (await first).Status);
        Assert.Equal(200, Post("/actions/status").Status);   // free again
    }

    [Fact]
    public void TokenInBodyHeaderOrBearer_NeverEchoedOrLogged()
    {
        _token = Word;
        Assert.Equal(403, Post("/actions/status").Status);
        Assert.Equal(403, Post("/actions/status", "{\"token\":\"wrong-word-here\"}").Status);
        var (s, body) = Post("/actions/status", "{\"token\":\"" + Word + "\"}");
        Assert.Equal(200, s);
        Assert.DoesNotContain(Word, body);
        Assert.Equal(200, Post("/actions/status", "{}", $"X-Token: {Word}\r\n").Status);
        Assert.Equal(200, Post("/actions/status", "{}", $"Authorization: Bearer {Word}\r\n").Status);
        Assert.Equal(403, Post("/actions/status", "{}", "Authorization: Bearer nope-nope-nope\r\n").Status);
        (s, body) = Post("/actions/status", "{\"token\":\"wrong-word-here\"}");
        Assert.Equal(403, s);
        Assert.DoesNotContain("wrong-word-here", body);
        lock (_log)
        {
            Assert.Contains(_log, l => l.Contains("refused /actions/status from 127.0.0.1 (wrong or no token)"));
            Assert.DoesNotContain(_log, l => l.Contains(Word) || l.Contains("wrong-word-here"));
        }
        Assert.DoesNotContain(_host.Lines, l => l.Contains(Word));
    }

    [Fact]
    public void BrowserShapedRequestsAreRefused()
    {
        Assert.Equal(403, Post("/actions/show-lock-on", "{}", "Origin: http://evil.example\r\n").Status);
        Assert.Equal(403, Post("/actions/show-lock-on", host: "evil.example").Status);
        Assert.Equal(400, Post("/actions/show-lock-on", "token=x", contentType: "application/x-www-form-urlencoded").Status);
        Assert.Equal(400, Post("/actions/show-lock-on", "{}", contentType: "text/plain").Status);
        Assert.Equal(400, Post("/actions/show-lock-on", "{not json").Status);
        Assert.Equal(403, Get("/actions", "Origin: http://evil.example\r\n").Status);
        Assert.False(_host.Lock);
    }

    [Fact]
    public void BodyLimitsAndOddRequests()
    {
        var big = "{\"pad\":\"" + new string('x', WebTriggerContract.MaxBody) + "\"}";
        Assert.Equal(400, Post("/actions/status", big).Status);
        Assert.Equal(404, Post("/actions/status/extra").Status);
        Assert.Equal(404, Post("/run/status").Status);
        Assert.Equal(501, Raw(Port, $"DELETE /actions/status HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n").Status);
        Assert.Equal(200, Post("/actions/status", "").Status);   // an empty JSON body reads as {}
        Assert.Equal(404, Post("/actions/" + new string('a', 70)).Status);
    }

    [Fact]
    public void ABugInAnActionNeverStopsTheListener()
    {
        var t = new LauncherTrigger(_host, _ => throw new InvalidOperationException("synthetic bug"), () => "");
        Assert.Equal("", t.Server.Start("127.0.0.1", 0));
        try
        {
            var (s, body) = Raw(t.Server.Port, "POST /actions/status?reply=text HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}");
            Assert.Equal(502, s);
            Assert.Equal("status: failed unexpectedly (see the app's log)\n", body);
            Assert.Equal(200, Raw(t.Server.Port, "GET /health HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n").Status);
        }
        finally { t.Server.Stop(); }
    }

    [Fact]
    public void StopClosesThePort()
    {
        int port = Port;
        _trigger.Server.Stop();
        Assert.False(_trigger.Server.Running);
        using var c = new TcpClient();
        Assert.ThrowsAny<SocketException>(() => c.Connect(IPAddress.Loopback, port));
        lock (_log) Assert.Contains(_log, l => l.StartsWith("web trigger stopped (127.0.0.1:", StringComparison.Ordinal));
    }

    [Fact]
    public void ASecondListenerOnTheSamePortIsRefused()
    {
        var other = new WebTriggerServer((_, _) => (200, new JsonObject()), () => Array.Empty<(string, string)>(), () => "");
        var err = other.Start("127.0.0.1", Port);
        Assert.StartsWith($"The web trigger couldn't listen on 127.0.0.1:{Port} (", err);
        Assert.False(other.Running);
    }

    private static Socket Other(bool reuse)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        if (reuse) s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        return s;
    }

    private static void Refused(Socket s, IPEndPoint at)
    {
        var ex = Assert.ThrowsAny<SocketException>(() => s.Bind(at));
        Assert.True(ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied, ex.SocketErrorCode.ToString());
    }

    /// <summary>The bind is exclusive (SO_EXCLUSIVEADDRUSE): on Windows no other program can take the port — with or
    /// without SO_REUSEADDR — on the same address, nor slip in on 127.0.0.1 underneath a listener on every address, which a
    /// plain TcpListener / a listener that sets SO_REUSEADDR both allow (the controls below). The every-address case is
    /// bound but never listening, so it raises no firewall question.</summary>
    [Fact]
    public void TheBindIsExclusive()
    {
        if (!OperatingSystem.IsWindows()) return;
        // 1. Ours, listening on 127.0.0.1: the same address and port can't be taken.
        foreach (var reuse in new[] { false, true })
        {
            using var s = Other(reuse);
            Refused(s, new IPEndPoint(IPAddress.Loopback, Port));
        }
        Assert.Equal(200, Get("/health").Status);   // and ours still answers

        // 2. Ours on every address (0.0.0.0): nobody binds 127.0.0.1 or 0.0.0.0 on that port underneath it.
        var every = WebTriggerServer.NewListener(IPAddress.Any, 0).Server;   // the listener's own socket, as configured
        try
        {
            every.Bind(new IPEndPoint(IPAddress.Any, 0));
            int port = ((IPEndPoint)every.LocalEndPoint!).Port;
            foreach (var reuse in new[] { false, true })
                foreach (var at in new[] { IPAddress.Loopback, IPAddress.Any })
                {
                    using var s = Other(reuse);
                    Refused(s, new IPEndPoint(at, port));
                }
        }
        finally { every.Close(); }

        // 3. The controls: a plain bind on every address lets a second program in on 127.0.0.1, and a bind that sets
        //    SO_REUSEADDR lets a second one share its very address — what the exclusive bind prevents.
        using (var plain = Other(reuse: false))
        {
            plain.Bind(new IPEndPoint(IPAddress.Any, 0));
            using var s = Other(reuse: false);
            s.Bind(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)plain.LocalEndPoint!).Port));
        }
        using (var shared = Other(reuse: true))
        {
            shared.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            using var s = Other(reuse: true);
            s.Bind(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)shared.LocalEndPoint!).Port));
        }
    }

    [Fact]
    public void DescribeForSettings()
    {
        Assert.Equal(("Off. Nothing listens until you switch it on.", false), LauncherTrigger.Describe(false, "127.0.0.1", _trigger.Server));
        var (text, problem) = LauncherTrigger.Describe(true, "127.0.0.1", _trigger.Server);
        Assert.False(problem);
        Assert.Equal($"Listening on 127.0.0.1:{Port}. Try: POST http://127.0.0.1:{Port}/actions/status?reply=text", text);
        var off = new WebTriggerServer((_, _) => (200, new JsonObject()), () => Array.Empty<(string, string)>(), () => "") { Error = "synthetic reason" };
        Assert.Equal(("synthetic reason", true), LauncherTrigger.Describe(true, "127.0.0.1", off));
    }

    [Fact]
    public void TheTokenIsASecretInTheLauncherBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jbtt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var p = new FileLauncherPrefs(Path.Combine(dir, "prefs.json"));
            p.Set("webTriggerApps", new JsonArray("dmxtools", "showdashboard"));
            var s = new FileLauncherSecrets(Path.Combine(dir, "secrets.json"));
            s.Write(new Dictionary<string, string> { ["webTriggerToken"] = Word });
            Assert.Contains(("webTriggerToken", "Web trigger token"), s.Present());
            var doc = LauncherSettings.Document(p, s, "1.36.0", false, null);
            Assert.DoesNotContain(Word, doc.ToJsonString());
            Assert.Equal("Web trigger token", doc["secret_slots"]![0]!["label"]!.GetValue<string>());
            Assert.Equal(2, doc["items"]!["settings"]!["data"]!["webTriggerApps"]!.AsArray().Count);
            // A sealed backup restores the token (and the ticks) onto another computer; the switch itself never travels.
            var data = SuiteBackup.Json(LauncherSettings.Document(p, s, "1.36.0", true, "backup words", rounds: 100_000));
            Assert.DoesNotContain(Word, Encoding.UTF8.GetString(data));
            var p2 = new FileLauncherPrefs(Path.Combine(dir, "p2.json"));
            var s2 = new FileLauncherSecrets(Path.Combine(dir, "s2.json"));
            LauncherSettings.Restore(data, p2, s2, "backup words", true);
            Assert.Equal(Word, s2.Read()["webTriggerToken"]);
            Assert.Equal("showdashboard", p2.Get("webTriggerApps")!.AsArray()[1]!.GetValue<string>());
            Assert.DoesNotContain(LauncherSettings.Fields, f => f.Name.StartsWith("webTrigger", StringComparison.Ordinal) && f.Name != "webTriggerApps");
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    [Fact]
    public void GuideNamesEveryStatusAndThePort()
    {
        var all = string.Join("\n", WebTriggerGuide.Sections(42154).Select(s => s.Heading + "\n" + s.Body));
        foreach (var code in new[] { "200", "502", "400", "403", "404", "409", "422" }) Assert.Contains(code + " —", all);
        Assert.Contains("42154", all);
        Assert.Contains("show-lock-on", all);
        Assert.Equal(42154, WebTriggerContract.DefaultPort);
    }
}
