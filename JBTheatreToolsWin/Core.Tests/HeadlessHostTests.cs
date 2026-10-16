using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>The hidden copy's REAL action host and press gate (HeadlessActionHost + HeadlessTrigger) through the real
/// listener on 127.0.0.1 (port 0), with stand-in settings and a stubbed launch — never the real settings or apps.</summary>
public sealed class HeadlessHostTests
{
    private static (int Status, string Head, string Body) Post(int port, string path)
    {
        using var c = new TcpClient();
        c.Connect(IPAddress.Loopback, port);
        c.ReceiveTimeout = 10000;
        var s = c.GetStream();
        s.Write(Encoding.UTF8.GetBytes($"POST {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{{}}"));
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var text = Encoding.UTF8.GetString(ms.ToArray());
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return (int.Parse(text.Split(' ')[1]), text[..split], text[(split + 4)..]);
    }

    [Fact]
    public void PressesReadSettingsEachTimeLockOpenAndTurnedOff()
    {
        // Stand-in saved state, changed between presses the way the window would change it on disk.
        var ticked = new List<string>();
        bool enabled = true, lockOn = false;
        var installed = new Dictionary<string, string> { ["dmxtools"] = "v1.12.0" };
        var open = new HashSet<string>();
        var launched = new List<string>();
        int reloads = 0;
        var apps = new List<LauncherActions.App>
        {
            new("dmxtools", "DMX Tools", new[] { new LauncherActions.Slot("dmxtools", "") }),
            new("psntools", "PSN Tools", new[] { new LauncherActions.Slot("psntools", "") }),
        };
        var host = new HeadlessActionHost(apps, () => lockOn, () => lockOn = true, () => ticked.ToList(),
            k => installed.TryGetValue(k, out var v) ? v : null, open.Contains, launched.Add, "1.37.0", _ => { });
        var trigger = new LauncherTrigger(host, HeadlessTrigger.Gate(() => enabled, () => reloads++), () => "");
        Assert.Equal("", trigger.Server.Start("127.0.0.1", 0));
        int port = trigger.Server.Port;
        try
        {
            Assert.Equal(404, Post(port, "/actions/open-dmxtools").Status);   // nothing ticked yet
            ticked.Add("dmxtools");
            ticked.Add("psntools");
            var opened = Post(port, "/actions/open-dmxtools");
            Assert.Equal(200, opened.Status);
            Assert.True(JsonNode.Parse(opened.Body)!["started"]!.GetValue<bool>());
            Assert.Equal(new[] { "dmxtools" }, launched);
            open.Add("dmxtools");
            var again = Post(port, "/actions/open-dmxtools");
            Assert.Equal(200, again.Status);
            Assert.False(JsonNode.Parse(again.Body)!["started"]!.GetValue<bool>());   // already open: nothing starts
            Assert.Single(launched);
            Assert.Equal(422, Post(port, "/actions/open-psntools").Status);       // ticked but not installed
            Assert.True(reloads >= 4, "the manifest is read again for every press");

            var locked = Post(port, "/actions/show-lock-on");
            Assert.Equal(200, locked.Status);
            Assert.True(lockOn);
            var status = JsonNode.Parse(Post(port, "/actions/status").Body)!;
            Assert.True(status["show_lock"]!.GetValue<bool>());
            Assert.Equal(1, status["apps_installed"]!.GetValue<int>());

            enabled = false;   // switched off in the window → refused from the very next press
            var off = Post(port, "/actions/status");
            Assert.Equal(503, off.Status);
            Assert.StartsWith("HTTP/1.1 503 Service Unavailable", off.Head);
            Assert.Equal("the web trigger is off", JsonNode.Parse(off.Body)!["summary"]!.GetValue<string>());
        }
        finally { trigger.Server.Stop(); }
    }

    [Fact]
    public void TokenWordForTheHiddenCopy()
    {
        Assert.Equal("", HeadlessTrigger.Word(() => null));
        Assert.Equal("synthetic-trigger-word", HeadlessTrigger.Word(() => "synthetic-trigger-word"));
        var a = HeadlessTrigger.Word(() => throw new InvalidOperationException("synthetic"));
        var b = HeadlessTrigger.Word(() => throw new InvalidOperationException("synthetic"));
        Assert.Equal(64, a.Length);
        Assert.NotEqual(a, b);   // unreadable → an unguessable stand-in refuses every press
    }
}
