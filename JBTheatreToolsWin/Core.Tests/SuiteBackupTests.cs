using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>Back up all / Restore all: the launcher's own settings file, the bundle, reading the apps' answers, and the
/// two files shared with Stagehand. Everything runs in temporary folders — never the real settings or Credential Manager.</summary>
public sealed class SuiteBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jbtt-tests-" + Guid.NewGuid().ToString("N"));

    public SuiteBackupTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

    private FileLauncherPrefs Prefs(string name = "prefs.json") => new(Path.Combine(_dir, name));
    private FileLauncherSecrets Secrets(string name = "secrets.json") => new(Path.Combine(_dir, name));

    [Fact]
    public void LauncherDocumentHasNoPasswordsUnlessAsked()
    {
        var p = Prefs();
        p.Set("appearance", "dark");
        p.Set("pinnedApps", new JsonArray("psntools"));
        var s = Secrets();
        s.Write(new Dictionary<string, string> { ["downloadPassphrase"] = "synthetic-phrase" });
        var doc = LauncherSettings.Document(p, s, "1.32.0", false, null);   // no passwords asked for
        var text = doc.ToJsonString();
        Assert.DoesNotContain("synthetic-phrase", text);
        Assert.Null(doc["secrets"]);
        Assert.Contains("\"secrets\":null", text);
        Assert.Equal("Download passphrase", doc["secret_slots"]![0]!["label"]!.GetValue<string>());
        Assert.Equal("jbtheatretools", doc["app"]!["id"]!.GetValue<string>());
        Assert.Equal("jbtt-settings", doc["format"]!.GetValue<string>());
        Assert.Equal("dark", doc["items"]!["settings"]!["data"]!["appearance"]!.GetValue<string>());
    }

    [Fact]
    public void SealedRoundTripOntoAnotherComputer()
    {
        var p = Prefs();
        p.Set("viewMode", "grid");
        p.Set("collapsedCategories", new JsonArray("pinned", "Networking"));
        p.Set("appVariants", new JsonObject { ["pdftools"] = "full" });
        p.Set("devChannel", true);
        var s = Secrets();
        s.Write(new Dictionary<string, string> { ["downloadPassphrase"] = "synthetic-phrase", ["githubToken"] = "synthetic-token" });
        var data = SuiteBackup.Json(LauncherSettings.Document(p, s, "1.32.0", true, "backup words", rounds: 100_000));
        Assert.DoesNotContain("synthetic-", Encoding.UTF8.GetString(data));

        var p2 = Prefs("new.json");
        var s2 = Secrets("new-secrets.json");
        var e = Assert.Throws<SuiteBackup.Failure>(() => LauncherSettings.Restore(data, p2, s2, "wrong", true));
        Assert.Equal(SuiteBackup.FailureKind.WrongPassphrase, e.Kind);
        Assert.Null(p2.Get("viewMode"));   // a wrong passphrase changes nothing
        Assert.Throws<SuiteBackup.Failure>(() => LauncherSettings.VerifyPassphrase(data, null, true));
        LauncherSettings.VerifyPassphrase(data, null, restoreSecrets: false);

        var o = LauncherSettings.Restore(data, p2, s2, "backup words", true);
        Assert.Equal("grid", p2.Get("viewMode")!.GetValue<string>());
        Assert.Equal(new[] { "pinned", "Networking" }, p2.Get("collapsedCategories")!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("full", p2.Get("appVariants")!["pdftools"]!.GetValue<string>());
        Assert.True(p2.Get("devChannel")!.GetValue<bool>());
        Assert.Equal("synthetic-phrase", s2.Read()["downloadPassphrase"]);
        Assert.Equal("synthetic-token", s2.Read()["githubToken"]);
        Assert.Empty(o.Attention);
    }

    [Fact]
    public void ReadsWhatTheMacLauncherWrites()
    {
        // The same neutral names on both platforms: a file from the Mac (plain passwords) restores here.
        var mac = """
        {"format":"jbtt-settings","schema":1,"app":{"id":"jbtheatretools","name":"JB Theatre Tools","version":"1.32.0"},
         "created":"2026-09-30T21:00:00Z","source":{"machine":"Show-Mac","os":"macOS 14.7","platform":"darwin"},
         "items":{"settings":{"type":"json","label":"Launcher settings","data":{"appearance":"light","quickLaunchIcon":true,"appearanceX":1}}},
         "secret_slots":[{"id":"store:launcher:downloadPassphrase","label":"Download passphrase"}],
         "secrets":{"protected":false,"count":1,"values":{"store:launcher:downloadPassphrase":"synthetic-phrase"}}}
        """;
        var p = Prefs();
        var s = Secrets();
        var o = LauncherSettings.Restore(Encoding.UTF8.GetBytes(mac), p, s, null, true);
        Assert.Equal("light", p.Get("appearance")!.GetValue<string>());
        Assert.True(p.Get("quickLaunchIcon")!.GetValue<bool>());
        Assert.Null(p.Get("appearanceX"));
        Assert.Equal("synthetic-phrase", s.Read()["downloadPassphrase"]);
        Assert.Equal(new[] { "Launcher settings" }, o.Restored);
    }

    [Fact]
    public void MissingPasswordsAreNamedAndUnknownValuesSkipped()
    {
        var doc = """
        {"format":"jbtt-settings","schema":1,"app":{"id":"jbtheatretools"},
         "items":{"settings":{"type":"json","data":{"appearance":"neon","updateMode":"manual"}}},
         "secret_slots":[{"id":"store:launcher:downloadPassphrase","label":"Download passphrase"}],"secrets":null}
        """;
        var p = Prefs();
        var o = LauncherSettings.Restore(Encoding.UTF8.GetBytes(doc), p, Secrets(), null, true);
        Assert.Null(p.Get("appearance"));
        Assert.Equal("manual", p.Get("updateMode")!.GetValue<string>());
        Assert.Single(o.Warnings);
        Assert.Contains("Download passphrase", o.Attention.Single()["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"format":"jbtt-settings","schema":1,"app":{"id":"psntools","name":"PSN Tools"},"items":{}}""", SuiteBackup.FailureKind.WrongApp)]
    [InlineData("""{"format":"jbtt-settings","schema":2,"app":{"id":"jbtheatretools"},"items":{}}""", SuiteBackup.FailureKind.Newer)]
    [InlineData("not json", SuiteBackup.FailureKind.NotABackup)]
    public void BadLauncherFilesAreRefused(string text, SuiteBackup.FailureKind kind)
    {
        var e = Assert.Throws<SuiteBackup.Failure>(() => LauncherSettings.Inspect(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(kind, e.Kind);
    }

    [Fact]
    public void ManifestRefusesPathsAndDuplicates()
    {
        var m = BundleManifest.Parse(Encoding.UTF8.GetBytes("""
        {"format":"jbtt-suite-backup","schema":1,"created":"2026-09-30T21:00:00Z","launcher_version":"1.32.0",
         "source":{"machine":"Show-PC-2"},
         "apps":[{"id":"psntools","name":"PSN Tools","version":"v1.7.0","file":"psntools.jbtt-settings"},
                 {"id":"psntools","name":"Again","version":"v1","file":"other.jbtt-settings"},
                 {"id":"evil","name":"Evil","version":"v1","file":"../evil.jbtt-settings"},
                 {"id":"evil2","name":"Evil","version":"v1","file":"C:\\evil.jbtt-settings"},
                 {"id":"evil3","name":"Evil","version":"v1","file":"notes.txt"}],
         "launcher":{"file":"launcher.jbtt-settings"}}
        """));
        Assert.Equal(new[] { "psntools" }, m.Apps.Select(a => a.Id));
        Assert.Equal("launcher.jbtt-settings", m.LauncherFile);
        Assert.Throws<SuiteBackup.Failure>(() => BundleManifest.Parse(Encoding.UTF8.GetBytes("""{"format":"jbtt-suite-backup","schema":2}""")));
        Assert.Throws<SuiteBackup.Failure>(() => BundleManifest.Parse(Encoding.UTF8.GetBytes("{}")));
    }

    [Fact]
    public void BundleWriteAndOpenRoundTrip()
    {
        var staging = Path.Combine(_dir, "staging");
        Directory.CreateDirectory(staging);
        var manifest = new BundleManifest("2026-09-30T21:00:00Z", new JsonObject { ["machine"] = "test" }, "1.32.0",
            new() { new BundleApp("psntools", "PSN Tools", "v1.7.0", "psntools.jbtt-settings", "full") }, null);
        SuiteBackup.AtomicWrite(Path.Combine(staging, "manifest.json"), SuiteBackup.Json(manifest.ToJson()));
        File.WriteAllText(Path.Combine(staging, "psntools.jbtt-settings"),
            """{"format":"jbtt-settings","schema":1,"app":{"id":"psntools"},"items":{},"secrets":{"protected":true}}""");
        var dest = Path.Combine(_dir, SuiteBackup.BundleFileName());
        SuiteBundle.Write(staging, dest);
        var opened = SuiteBundle.Open(dest);
        try
        {
            Assert.Equal(manifest.Apps, opened.Manifest.Apps);
            var info = SuiteBundle.Info(opened.PathOf("psntools.jbtt-settings"));
            Assert.Equal("psntools", info.AppId);
            Assert.True(info.Protected);
        }
        finally { Directory.Delete(opened.Folder, true); }
        Assert.StartsWith("JB Theatre Tools backup ", Path.GetFileName(dest));
        Assert.EndsWith(".jbtt-backup", dest);
        Assert.Throws<SuiteBackup.Failure>(() => SuiteBundle.Open(Path.Combine(staging, "manifest.json")));

        // A zip with a path inside is refused before anything is unpacked.
        var evil = Path.Combine(_dir, "evil.jbtt-backup");
        using (var z = System.IO.Compression.ZipFile.Open(evil, System.IO.Compression.ZipArchiveMode.Create))
        {
            z.CreateEntryFromFile(Path.Combine(staging, "manifest.json"), "manifest.json");
            z.CreateEntryFromFile(Path.Combine(staging, "manifest.json"), "../x.jbtt-settings");
        }
        Assert.Throws<SuiteBackup.Failure>(() => SuiteBundle.Open(evil));
    }

    private static CliRun Run(int? code, string? json, bool timedOut = false) =>
        new(code, json == null ? null : JsonNode.Parse(json)!.AsObject(), timedOut, null);

    [Fact]
    public void DescribeAnswers()
    {
        var ready = SettingsCli.Describe(Run(0, """{"ok":true,"items":["Settings","Presets (3)"],"secrets":{"count":2}}"""));
        Assert.Equal(AppProbeState.Ready, ready.State);
        Assert.Equal(new[] { "Settings", "Presets (3)" }, ready.Items);
        Assert.Equal(2, ready.SecretCount);
        // An app too old for the flags opens its window (stopped at the timeout) or exits without answering.
        Assert.Equal(AppProbeState.Unsupported, SettingsCli.Describe(Run(null, null, timedOut: true)).State);
        Assert.Equal(AppProbeState.Unsupported, SettingsCli.Describe(Run(0, null)).State);
        Assert.Equal(AppProbeState.Unsupported, SettingsCli.Describe(Run(64, """{"ok":false,"code":"usage","error":"x"}""")).State);
        var failed = SettingsCli.Describe(Run(1, """{"ok":false,"error":"Couldn't read"}"""));
        Assert.Equal(AppProbeState.Failed, failed.State);
        Assert.Equal("Couldn't read", failed.Message);
    }

    [Fact]
    public void ImportAnswers()
    {
        var ok = SettingsCli.Import(Run(0, """{"ok":true,"attention":[{"label":"NIC","message":"Check the NIC."}],"warnings":["w"],"restart_needed":true}"""));
        Assert.Equal(ImportState.Ok, ok.State);
        Assert.Equal(new[] { "Check the NIC." }, ok.Attention);
        Assert.True(ok.RestartNeeded);
        Assert.Equal(LineState.Attention, SuiteBackup.Line("a", "A", ok).State);
        var wrong = SettingsCli.Import(Run(2, """{"ok":false,"code":"wrong_passphrase","error":"No."}"""));
        Assert.Equal(ImportState.Passphrase, wrong.State);
        Assert.True(wrong.WrongPassphrase);
        var open = SettingsCli.Import(Run(5, """{"ok":false,"code":"running","error":"Quit PSN Tools, then restore its settings."}"""));
        Assert.Equal(ImportState.AppOpen, open.State);
        Assert.Equal(LineState.Retry, SuiteBackup.Line("a", "A", open).State);
        Assert.Equal(ImportState.Failed, SettingsCli.Import(Run(3, """{"ok":false,"code":"wrong_app","error":"For DMX Tools."}""")).State);
        Assert.Equal(ImportState.Unsupported, SettingsCli.Import(Run(null, null, timedOut: true)).State);
    }

    private string Script(string name, string unix, string windows)
    {
        var path = Path.Combine(_dir, OperatingSystem.IsWindows() ? name + ".cmd" : name);
        File.WriteAllText(path, OperatingSystem.IsWindows() ? windows.Replace("\n", "\r\n") : unix);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task ARunThatNeverAnswersIsStopped()
    {
        var exe = Script("old-app", "#!/bin/sh\nsleep 30\n", "@echo off\nping -n 30 127.0.0.1 >nul\n");
        var started = DateTime.UtcNow;
        var r = await SettingsCli.RunAsync(exe, new[] { "--settings-describe" }, null, TimeSpan.FromSeconds(1));
        Assert.True(r.TimedOut);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
        Assert.Equal(AppProbeState.Unsupported, SettingsCli.Describe(r).State);
    }

    [Fact]
    public async Task ThePassphraseGoesOnStdinNeverTheCommandLine()
    {
        var exe = Script("echo-app",
            "#!/bin/sh\nread line\nout=\"\"\nwhile [ $# -gt 0 ]; do if [ \"$1\" = \"--result\" ]; then out=\"$2\"; fi; shift; done\n" +
            "printf '{\"ok\": true, \"stdin\": \"%s\"}' \"$line\" > \"$out\"\n",
            "@echo off\nset /p line=\n:loop\nif \"%~1\"==\"\" goto done\nif \"%~1\"==\"--result\" set out=%~2\nshift\ngoto loop\n:done\n" +
            ">\"%out%\" echo {\"ok\": true, \"stdin\": \"%line%\", \"env\": \"%JBTT_SETTINGS_PASSPHRASE%\"}\n");
        var r = await SettingsCli.RunAsync(exe, new[] { "--settings-export", "x" }, "synthetic words", TimeSpan.FromSeconds(20));
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("synthetic words", SuiteBackup.Str(r.Result?["stdin"]));
        if (OperatingSystem.IsWindows()) Assert.Equal("synthetic words", SuiteBackup.Str(r.Result?["env"]));
    }

    [Fact]
    public void ConnectorsListOnlyConnectorBuildsFullFirst()
    {
        var apps = new[]
        {
            new Connectors.App("pdftools", "PDF Tools", "v0.10.0", new[] { ("pdftools@full", (string?)"full"), ("pdftools", (string?)"light") }),
            new Connectors.App("dmxtools", "DMX Tools", "v1.5.0", new[] { ("dmxtools", (string?)null) }),
            new Connectors.App("stagehand", "Stagehand", "v0.1.0", new[] { ("stagehand", (string?)null) }),
            new Connectors.App("helocontrol", "HELO Control", null, new[] { ("helocontrol", (string?)null) }),
        };
        var versions = new Dictionary<string, string>
        {
            ["pdftools"] = "v0.10.0", ["pdftools@full"] = "v0.10.1-dev.2", ["dmxtools"] = "v1.4.0", ["stagehand"] = "v0.1.0-dev.1", ["helocontrol"] = "v2.0.0",
        };
        var entries = Connectors.Entries(apps, k => versions.GetValueOrDefault(k), k => $"C:\\apps\\{k}\\app.exe");
        var e = Assert.Single(entries);   // DMX is older than its first connector build; Stagehand's dev build predates v0.1.0
        Assert.Equal("pdftools", e.Id);
        Assert.Equal("full", e.Edition);
        var doc = Connectors.Document(entries, x => $"C:\\stable\\{x.Id}\\app.exe");
        Assert.Equal(1, doc["schema"]!.GetValue<int>());
        Assert.Equal("C:\\stable\\pdftools\\app.exe", doc["apps"]!["pdftools"]!["command"]!.GetValue<string>());
        Assert.Equal("--mcp", doc["apps"]!["pdftools"]!["args"]![0]!.GetValue<string>());
        var path = Path.Combine(_dir, "connectors.json");
        Connectors.Write(path, doc);
        var stamp = File.GetLastWriteTimeUtc(path);
        Connectors.Write(path, doc);   // unchanged → not rewritten
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void SuiteShowLockKeepsTheRestOfClaudeJson()
    {
        var path = Path.Combine(_dir, "claude.json");
        Assert.Null(ClaudeSettingsFile.SuiteShowLock(path));
        ClaudeSettingsFile.SetSuiteShowLock(true, path);
        Assert.True(ClaudeSettingsFile.SuiteShowLock(path));
        File.WriteAllText(path, """{"schema":1,"enabled":true,"showLock":true,"apps":{"dmxtools":{"showLock":true,"perms":{"output":"ask"}}}}""");
        ClaudeSettingsFile.SetSuiteShowLock(false, path);
        var d = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.False(d["showLock"]!.GetValue<bool>());
        Assert.True(d["enabled"]!.GetValue<bool>());
        Assert.True(d["apps"]!["dmxtools"]!["showLock"]!.GetValue<bool>());
        // A damaged file never blocks the switch: it becomes a fresh, disconnected one.
        File.WriteAllText(path, "{broken");
        ClaudeSettingsFile.SetSuiteShowLock(true, path);
        var fresh = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.True(fresh["showLock"]!.GetValue<bool>());
        Assert.False(fresh["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void TargetsSkipExcludedAndUninstalledApps()
    {
        var exe = Path.Combine(_dir, "app.exe");
        File.WriteAllText(exe, "x");
        var apps = new[]
        {
            new BackupApp("psntools", "PSN Tools", new[] { ("psntools@full", (string?)"full"), ("psntools", (string?)"light") }, false),
            new BackupApp("convert", "Convert Tools", new[] { ("convert", (string?)null) }, true),
            new BackupApp("dmxtools", "DMX Tools", new[] { ("dmxtools", (string?)null) }, false),
        };
        var t = SuiteBackup.Targets(apps, k => k is "psntools" or "convert" ? "v1" : null, _ => exe);
        var only = Assert.Single(t);
        Assert.Equal("psntools", only.Id);
        Assert.Equal("light", Assert.Single(only.Slots).Edition);
    }
}
