namespace JBTheatreTools;

/// <summary><c>--backup-all</c> / <c>--restore-all</c>: the same work as Settings → "Back Up All Apps…" / "Restore All
/// Apps…", headless. The command line never installs anything: an app in the backup that isn't installed is skipped.</summary>
public static partial class Cli
{
    private static async Task<int> BackupCommandAsync(string cmd, Catalog catalog, string[] args)
    {
        string? path = null;
        HashSet<string>? only = null;
        bool includeSecrets = false, noSecrets = false, noLauncher = false, stdinPass = false;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == cmd) continue;
            switch (a)
            {
                case "--include-passwords": includeSecrets = true; break;
                case "--no-passwords": noSecrets = true; break;
                case "--no-launcher": noLauncher = true; break;
                case "--passphrase-stdin": stdinPass = true; break;
                case "--apps": if (++i < args.Length) only = args[i].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(); break;
                case "--catalog" or "--token" or "--server" or "--server-pass" or "--tag": i++; break;
                default: if (!a.StartsWith("--", StringComparison.Ordinal)) path ??= a; break;
            }
        }
        if (path == null) { Console.Error.WriteLine($"error: give the backup file: {cmd} <file>"); return 64; }
        // The backup's passphrase: one stdin line, else the environment — never the command line.
        string? entered = stdinPass ? Console.In.ReadLine() : Environment.GetEnvironmentVariable("JBTT_SETTINGS_PASSPHRASE");
        string? pass = string.IsNullOrEmpty(entered) ? null : entered;

        // Test home: the launcher's own settings and passwords come from files in it, never the real ones.
        var home = Environment.GetEnvironmentVariable("JBTT_BACKUP_TEST_HOME");
        AppSettings? settings = null;
        ILauncherPrefs prefs;
        ILauncherSecrets secrets;
        if (!string.IsNullOrEmpty(home))
        {
            prefs = new FileLauncherPrefs(Path.Combine(home, "prefs.json"));
            secrets = new FileLauncherSecrets(Path.Combine(home, "secrets.json"));
        }
        else
        {
            settings = AppSettings.Load();
            prefs = new WinLauncherPrefs(settings);
            secrets = new CredentialLauncherSecrets();
        }
        if (cmd == "--restore-all" && (settings?.ShowLock == true || ClaudeSettingsFile.SuiteShowLock() == true))
        {
            Console.Error.WriteLine("error: show lock is on — restores are paused. Turn it off in JB Theatre Tools first.");
            return 1;
        }
        var im = InstallManager.Shared;
        var targets = SuiteBackup.Targets(catalog.Apps.Select(a => a.ToBackupApp()), im.InstalledVersion, im.InstalledPath)
                                 .Where(t => only?.Contains(t.Id) ?? true).ToList();
        var version = typeof(Cli).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        int code = 0;
        void Show(IEnumerable<BackupLine> lines)
        {
            foreach (var l in lines)
            {
                var tag = l.State switch
                {
                    LineState.Ok => "OK", LineState.Attention => "CHECK", LineState.Unsupported => "NOT YET",
                    LineState.Skipped => "SKIPPED", LineState.Retry => "AGAIN", _ => "FAILED",
                };
                Console.WriteLine($"  {Pad(tag, 8)} {l.Name}");
                foreach (var d in l.Detail) Console.WriteLine($"           {d}");
                if (l.State == LineState.Failed) code = 1;
            }
        }
        var file = Path.GetFullPath(path);
        if (cmd == "--backup-all")
        {
            var chosen = new List<(BackupTarget, BackupSlot)>();
            var lines = new List<BackupLine>();
            foreach (var t in targets)
            {
                var (slot, probe) = await SuiteBackup.ProbeAsync(t);
                if (slot != null) { chosen.Add((t, slot)); continue; }
                lines.Add(probe.State == AppProbeState.Failed
                    ? new BackupLine(t.Id, t.Name, LineState.Failed, new() { probe.Message ?? "It couldn't be checked." })
                    : new BackupLine(t.Id, t.Name, LineState.Unsupported, new() { SuiteBackup.UnsupportedMessage }));
            }
            try
            {
                lines.AddRange(await SuiteBackup.BackUpAllAsync(chosen, !noLauncher, prefs, secrets, version, includeSecrets, pass, file));
                Console.WriteLine($"Saved {file}");
            }
            catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); code = 1; }
            Show(lines);
            return code;
        }
        SuiteBundle.Opened opened;
        try { opened = SuiteBundle.Open(file); }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 4; }
        try
        {
            var m = opened.Manifest;
            Console.WriteLine($"Backup from {SuiteBackup.Str(m.Source["machine"]) ?? "another computer"} ({SuiteBackup.Str(m.Source["os"]) ?? "?"}), {m.Created}");
            byte[]? launcher = !noLauncher && m.LauncherFile != null && File.Exists(opened.PathOf(m.LauncherFile))
                ? File.ReadAllBytes(opened.PathOf(m.LauncherFile)) : null;
            // A wrong passphrase stops here, before any app's settings change.
            if (launcher != null)
            {
                try { LauncherSettings.VerifyPassphrase(launcher, pass, !noSecrets); }
                catch (SuiteBackup.Failure ex) when (ex.Kind is SuiteBackup.FailureKind.WrongPassphrase or SuiteBackup.FailureKind.PassphraseNeeded)
                { Console.Error.WriteLine($"error: {ex.Message} Nothing was changed."); return 2; }
                catch (SuiteBackup.Failure) { }
            }
            var lines = new List<BackupLine>();
            foreach (var app in m.Apps.Where(a => only?.Contains(a.Id) ?? true))
            {
                var t = targets.FirstOrDefault(x => x.Id == app.Id);
                if (t == null)
                {
                    lines.Add(new BackupLine(app.Id, app.Name, LineState.Skipped, new() { "Not installed on this PC — install it, then restore again." }));
                    continue;
                }
                var o = await SuiteBackup.RestoreAppAsync(opened.PathOf(app.File), t.Slots, pass, !noSecrets);
                if (o.State == ImportState.Passphrase) code = 2;
                if (o.State == ImportState.AppOpen && code == 0) code = 5;
                lines.Add(SuiteBackup.Line(app.Id, t.Name, o));
            }
            if (launcher != null)
            {
                try
                {
                    var o = LauncherSettings.Restore(launcher, prefs, secrets, pass, !noSecrets);
                    settings?.Save();
                    var detail = SettingsCli.Lines(new System.Text.Json.Nodes.JsonArray(o.Attention.Select(a => (System.Text.Json.Nodes.JsonNode?)a.DeepClone()).ToArray()));
                    lines.Add(new BackupLine(SuiteBackup.LauncherId, SuiteBackup.LauncherName, o.Attention.Count == 0 ? LineState.Ok : LineState.Attention,
                                             detail.Concat(o.Warnings).ToList()));
                }
                catch (Exception ex)
                {
                    lines.Add(new BackupLine(SuiteBackup.LauncherId, SuiteBackup.LauncherName, LineState.Failed, new() { ex.Message }));
                }
            }
            Show(lines);
            return code;
        }
        finally { try { Directory.Delete(opened.Folder, true); } catch (Exception) { } }
    }
}
