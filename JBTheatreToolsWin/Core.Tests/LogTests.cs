using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>Points the shared log at a temporary folder before anything in this test run can write to it.</summary>
internal static class LogTestsSetup
{
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("JBTT_LOG_DIR", Path.Combine(Path.GetTempPath(), "jbtt-test-logs-" + Environment.ProcessId));
}

/// <summary>The launcher's log: the switch, the header, the line format, rotation, secrets and volume. A temporary folder,
/// and a loopback listener where something has to be talked to.</summary>
public sealed class LogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jbtt-log-" + Guid.NewGuid().ToString("N"));
    private bool _saved;          // the stand-in for the settings file
    private readonly List<LogFile> _made = new();

    private LogFile Make()
    {
        var l = new LogFile(_dir, () => _saved, v => _saved = v);
        _made.Add(l);
        return l;
    }

    private static string Text(LogFile l)
    {
        l.Flush();
        return File.Exists(l.FilePath) ? File.ReadAllText(l.FilePath) : "";
    }

    public void Dispose()
    {
        foreach (var l in _made) l.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void TheSwitchChangesTheLevelAtOnceAndSurvivesARestart()
    {
        var log = Make();
        Assert.False(log.Verbose);
        log.Debug("test", "before the switch");
        log.SetVerbose(true);
        log.Debug("test", "after the switch");
        var t = Text(log);
        Assert.DoesNotContain("before the switch", t);
        Assert.Contains("after the switch", t);
        Assert.Contains("[settings] verbose logging off → on", t);
        // A new process reads the saved setting.
        var again = Make();
        Assert.True(again.Verbose);
        again.SetVerbose(false);
        again.Debug("test", "now off again");
        Assert.DoesNotContain("now off again", Text(again));
        Assert.False(Make().Verbose);
    }

    [Fact]
    public void AnotherProcessChangingTheSettingIsFollowedWithinSeconds()
    {
        var log = Make();
        Assert.False(log.Verbose);
        _saved = true;
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!log.Verbose && DateTime.UtcNow < until) Thread.Sleep(250);
        Assert.True(log.Verbose);
    }

    [Fact]
    public void TheHeaderIsAlwaysWrittenAndNamesTheRunningThing()
    {
        var log = Make();
        log.WriteHeader("window", "9.9.9", new[] { ("download access", "server"), ("show lock", "off") });
        var t = Text(log);
        Assert.Contains("==== JB Theatre Tools 9.9.9 (window) ====", t);
        Assert.Contains("system: ", t);
        Assert.Contains("running as: ", t);
        Assert.Contains("setting download access: server", t);
        Assert.Contains("verbose logging: off", t);
        log.SetVerbose(true);
        log.WriteHeader("window", "9.9.9", Array.Empty<(string, string)>());
        Assert.Contains("verbose logging: ON", Text(log));
    }

    [Fact]
    public void LineFormat()
    {
        var log = Make();
        log.Info("github", "one event");
        log.Warn("github", "a warning");
        log.Error("github", "it failed", new TimeoutException("synthetic timeout"));
        var lines = Text(log).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var re = new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  (DEBUG|INFO |WARN |ERROR)  \[[a-z-]+\] .+");
        Assert.Matches(re, lines[0]);
        Assert.Contains("  INFO   [github] one event", lines[0]);
        Assert.Contains("  WARN   [github] a warning", lines[1]);
        Assert.Contains("  ERROR  [github] it failed", lines[2]);
        Assert.StartsWith("    TimeoutException: synthetic timeout", lines[3]);
    }

    [Fact]
    public void AnOldStyleLineGetsAnAreaAndALevel()
    {
        var log = Make();
        log.Write("install helo FAILED: no space");
        log.Write("web trigger: not listening");
        var t = Text(log);
        Assert.Contains("WARN   [install] install helo FAILED", t);
        Assert.Contains("INFO   [web-trigger] web trigger: not listening", t);
    }

    [Fact]
    public void ALongLineIsCut()
    {
        var log = Make();
        log.Info("test", new string('x', 5000));
        var line = Text(log).Split('\n')[0];
        Assert.True(line.Length <= LogFile.MaxLine + 60);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void TheLogRollsAndKeepsTwoOlderFiles()
    {
        var log = Make();
        var chunk = new string('y', 1500);
        for (int i = 0; i < 2600; i++) log.Info("test", chunk);   // about 3.9 MB, 1 MB limit
        log.Flush(30000);
        Assert.True(File.Exists(log.FilePath + ".1"));
        Assert.True(File.Exists(log.FilePath + ".2"));
        Assert.False(File.Exists(log.FilePath + ".3"));
        Assert.True(new FileInfo(log.FilePath).Length <= LogFile.NormalLimit);
    }

    [Fact]
    public void BurstsOfDebugWriteNothingWhenVerboseIsOff()
    {
        var log = Make();
        for (int i = 0; i < 5000; i++) log.Debug("test", $"packet {i}");
        Assert.Equal("", Text(log));
    }

    [Fact]
    public void TailReturnsTheLastLinesAndHonoursTheFilter()
    {
        var log = Make();
        for (int i = 0; i < 500; i++) log.Info("test", $"line {i}");
        log.Info("other", "needle here");
        Assert.Equal(400, log.Tail(1000).Count);
        Assert.Single(log.Tail(5, "needle"));
        Assert.Contains("needle here", log.Tail(3).Last());
    }

    [Fact]
    public void KnownSecretShapesNeverReachTheFile()
    {
        var log = Make();
        log.SetVerbose(true);
        log.RegisterSecret("placeholder-passphrase-words");
        var token = "ghp_placeholderplaceholder1234";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("placeholder-passphrase-words"));
        var samples = new[]
        {
            $"Authorization: Bearer {token}",
            "GET https://example.invalid/x?token=synthetic-secret-value&a=1",
            $"credential {token} in an exception message",
            "--server-pass placeholder-cli-value --token synthetic-cli-token",
            $"the passphrase was placeholder-passphrase-words and its base64 {b64}",
            "https://s3.example.invalid/f?X-Amz-Signature=syntheticsig&X-Amz-Credential=syntheticcred",
        };
        foreach (var s in samples)
        {
            log.Info("test", s); log.Debug("test", s);
            log.Error("test", s, new InvalidOperationException(s));
        }
        var t = Text(log);
        foreach (var secret in new[] { "ghp_placeholder", "synthetic-secret-value", "placeholder-cli-value", "synthetic-cli-token",
                                       "placeholder-passphrase-words", "syntheticsig", "syntheticcred", b64 })
            Assert.False(t.Contains(secret), $"{secret} reached the log");
        Assert.Contains("<redacted>", t);
    }

    [Fact]
    public void TheCommandLineHeaderHidesCredentialValues() =>
        Assert.Equal("--list --token <redacted> --server-pass <redacted> --json",
            LogFile.MaskedArguments(new[] { "--list", "--token", "synthetic-x", "--server-pass", "placeholder-y", "--json" }));

    [Fact]
    public void ARefusedConnectionIsExplainedInPlainWords()
    {
        // A loopback port nothing listens on: open one, note it, close it.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var http = new HttpClient();
        var ex = Assert.ThrowsAny<Exception>(() => http.GetAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult());
        Assert.Contains("refused: nothing is listening at that address and port", LogFile.Explain(ex));
    }

    [Fact]
    public void TheWebTriggerLogsWhoAskedAndHowItEnded()
    {
        var shared = Log.Instance;
        bool was = shared.Verbose;
        shared.SetVerbose(true);
        var server = new WebTriggerServer((_, _) => (200, new JsonObject { ["ok"] = true, ["summary"] = "done" }),
                                          () => Array.Empty<(string, string)>(), () => "");
        try
        {
            Assert.Equal("", server.Start("127.0.0.1", 0));
            using var c = new TcpClient();
            c.Connect(IPAddress.Loopback, server.Port);
            var s = c.GetStream();
            var body = Encoding.UTF8.GetBytes("{}");
            s.Write(Encoding.UTF8.GetBytes($"POST /actions/status?reply=text&token=synthetic-query-secret HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n"));
            s.Write(body);
            s.CopyTo(Stream.Null);
            string t = "";
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until)
            {
                Log.Flush();
                t = File.Exists(Log.FilePath) ? File.ReadAllText(Log.FilePath) : "";
                if (t.Contains("[web-trigger]")) break;
                Thread.Sleep(100);
            }
            Assert.Contains("[web-trigger] POST /actions/status from 127.0.0.1 → 200", t);
            Assert.DoesNotContain("synthetic-query-secret", t);
        }
        finally { server.Stop(); shared.SetVerbose(was); }
    }
}
