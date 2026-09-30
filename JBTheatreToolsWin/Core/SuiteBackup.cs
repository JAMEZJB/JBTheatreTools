using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JBTheatreTools;

/// <summary>"Back up all apps" / "Restore all apps": the launcher's half of the suite's settings backup.
///
/// Each suite app backs up its own settings through its command line:
/// <code>
///   &lt;app&gt; --settings-describe                                      [--result FILE]
///   &lt;app&gt; --settings-export FILE [--include-secrets] --passphrase-stdin --result FILE
///   &lt;app&gt; --settings-import FILE [--no-secrets]      --passphrase-stdin --result FILE
/// </code>
/// (exit codes: 0 ok · 1 error · 2 passphrase needed or wrong · 3 wrong app · 4 not a backup · 5 the app is open ·
/// 64 bad usage). The launcher runs that for every installed app, adds its own settings in the same <c>.jbtt-settings</c>
/// format, and zips the lot into one <c>.jbtt-backup</c> with a <c>manifest.json</c>.</summary>
public static class SuiteBackup
{
    public const string LauncherId = "jbtheatretools";
    public const string LauncherName = "JB Theatre Tools";
    public const string SettingsFormat = "jbtt-settings";
    public const int SettingsSchema = 1;
    public const string SettingsExt = "jbtt-settings";
    public const string BundleFormat = "jbtt-suite-backup";
    public const int BundleSchema = 1;
    public const string BundleExt = "jbtt-backup";
    public const string LauncherFile = "launcher.jbtt-settings";
    public const string ManifestFile = "manifest.json";
    public const long MaxFileBytes = 64L << 20;
    public static readonly TimeSpan DescribeTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(180);
    public const string UnsupportedMessage = "This app's version can't back up its settings yet — update it.";

    public static byte[] Aad(string appId) => Encoding.UTF8.GetBytes($"{SettingsFormat}|{SettingsSchema}|{appId}");

    public static string IsoNow() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Where a backup came from, shown before a restore. The host name matches the apps' own backups.</summary>
    public static JsonObject Source()
    {
        string machine;
        try { machine = System.Net.Dns.GetHostName(); } catch { machine = Environment.MachineName; }
        if (machine.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) machine = machine[..^6];
        string os, platform;
        if (OperatingSystem.IsWindows())
        {
            var v = Environment.OSVersion.Version;
            os = $"Windows {(v.Build >= 22000 ? "11" : "10")} ({v.Major}.{v.Minor}.{v.Build})";
            platform = "win32";
        }
        else if (OperatingSystem.IsMacOS()) { os = "macOS " + Environment.OSVersion.Version; platform = "darwin"; }
        else { os = System.Runtime.InteropServices.RuntimeInformation.OSDescription; platform = "linux"; }
        return new JsonObject { ["machine"] = string.IsNullOrEmpty(machine) ? "this computer" : machine, ["os"] = os, ["platform"] = platform };
    }

    public static string BundleFileName(DateTime? when = null) =>
        $"JB Theatre Tools backup {(when ?? DateTime.Now).ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}.{BundleExt}";

    public enum FailureKind { NotABackup, WrongApp, Newer, PassphraseNeeded, WrongPassphrase, Other }

    public sealed class Failure : Exception
    {
        public FailureKind Kind { get; }
        public Failure(FailureKind kind, string? message = null) : base(message ?? kind switch
        {
            FailureKind.PassphraseNeeded => "The backup's passwords are protected. Enter its passphrase.",
            FailureKind.WrongPassphrase => "That passphrase doesn't open this backup.",
            _ => "That file isn't a JB Theatre Tools backup.",
        }) { Kind = kind; }
    }

    internal static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static JsonObject? ParseObject(byte[] data)
    {
        try { return JsonNode.Parse(data) as JsonObject; } catch (JsonException) { return null; }
    }

    public static string? Str(JsonNode? n) { try { return n?.GetValue<string>(); } catch (Exception) { return null; } }
    public static bool? Bool(JsonNode? n) { try { return n?.GetValue<bool>(); } catch (Exception) { return null; } }
    public static int? Int(JsonNode? n) { try { return n?.GetValue<int>(); } catch (Exception) { return null; } }

    /// <summary>Written to a temporary file beside it, then moved over.</summary>
    public static void AtomicWrite(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public static byte[] Json(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(Pretty));

    // ── Back up all / Restore all ───────────────────────────────────────────────────────────────────

    /// <summary>Asks each installed edition in turn (Full first) until one answers; the one that did is used.</summary>
    public static async Task<(BackupSlot? Slot, AppProbe Probe)> ProbeAsync(BackupTarget target)
    {
        AppProbe last = AppProbe.Unsupported();
        foreach (var slot in target.Slots)
        {
            var r = await SettingsCli.RunAsync(slot.Executable, new[] { "--settings-describe" }, null, DescribeTimeout).ConfigureAwait(false);
            var p = SettingsCli.Describe(r);
            if (p.State == AppProbeState.Ready) return (slot, p);
            last = p;
        }
        return (null, last);
    }

    /// <summary>Runs every chosen app's export into one bundle at <paramref name="dest"/>, plus the launcher's own
    /// settings when asked. A line per app; throws only when nothing at all could be saved.</summary>
    public static async Task<List<BackupLine>> BackUpAllAsync(IReadOnlyList<(BackupTarget Target, BackupSlot Slot)> chosen,
        bool includeLauncher, ILauncherPrefs prefs, ILauncherSecrets secrets, string launcherVersion, bool includeSecrets,
        string? passphrase, string dest, Action<string, string>? progress = null)
    {
        var staging = Path.Combine(Path.GetTempPath(), "jbtt-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var lines = new List<BackupLine>();
            var entries = new List<BundleApp>();
            var pass = string.IsNullOrEmpty(passphrase) ? null : passphrase;
            foreach (var (target, slot) in chosen)
            {
                progress?.Invoke(target.Id, "Backing up…");
                var file = $"{target.Id}.{SettingsExt}";
                var path = Path.Combine(staging, file);
                var args = new List<string> { "--settings-export", path };
                if (includeSecrets) args.Add("--include-secrets");
                var r = await SettingsCli.RunAsync(slot.Executable, args, includeSecrets ? pass ?? "" : null, RunTimeout).ConfigureAwait(false);
                var o = SettingsCli.Export(r);
                if (o.State == ExportState.Ok && o.Secrets > 0 && pass != null && !o.Protected)
                {
                    // Asked for a passphrase but the app wrote its passwords in plain text: never keep that file.
                    TryDelete(path);
                    lines.Add(new BackupLine(target.Id, target.Name, LineState.Failed,
                        new() { "It didn't protect its passwords with the passphrase, so it was left out. Update it, or back up without passwords." }));
                }
                else if (o.State == ExportState.Ok)
                {
                    entries.Add(new BundleApp(target.Id, target.Name, slot.Version, file, slot.Edition));
                    var note = new List<string>();
                    if (o.Secrets > 0)
                        note.Add($"{o.Secrets} saved password{(o.Secrets == 1 ? "" : "s")}, {(o.Protected ? "locked with the passphrase" : "not protected")}");
                    lines.Add(new BackupLine(target.Id, target.Name, o.Empty ? LineState.Empty : LineState.Ok, note));
                }
                else
                {
                    TryDelete(path);
                    lines.Add(o.State == ExportState.Unsupported
                        ? new BackupLine(target.Id, target.Name, LineState.Unsupported, new() { UnsupportedMessage })
                        : new BackupLine(target.Id, target.Name, LineState.Failed, new() { o.Message ?? "It stopped with an error." }));
                }
            }
            string? launcherFile = null;
            if (includeLauncher)
            {
                progress?.Invoke(LauncherId, "Backing up…");
                try
                {
                    var doc = LauncherSettings.Document(prefs, secrets, launcherVersion, includeSecrets, pass);
                    AtomicWrite(Path.Combine(staging, LauncherFile), Json(doc));
                    launcherFile = LauncherFile;
                    int n = Int(doc["secrets"]?["count"]) ?? 0;
                    lines.Add(new BackupLine(LauncherId, LauncherName, LineState.Ok, n == 0 ? new() :
                        new() { $"{n} saved password{(n == 1 ? "" : "s")}, {(pass != null ? "locked with the passphrase" : "not protected")}" }));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lines.Add(new BackupLine(LauncherId, LauncherName, LineState.Failed, new() { ex.Message }));
                }
            }
            if (entries.Count == 0 && launcherFile == null)
                throw new Failure(FailureKind.Other, "Nothing could be backed up, so no file was saved.");
            var manifest = new BundleManifest(IsoNow(), Source(), launcherVersion, entries, launcherFile);
            AtomicWrite(Path.Combine(staging, ManifestFile), Json(manifest.ToJson()));
            progress?.Invoke("", "Saving the backup…");
            SuiteBundle.Write(staging, dest);
            return lines;
        }
        finally { try { Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>Restores one app from its file in an opened bundle, trying each installed edition until one answers.</summary>
    public static async Task<ImportOutcome> RestoreAppAsync(string file, IReadOnlyList<BackupSlot> slots, string? passphrase, bool restoreSecrets)
    {
        ImportOutcome last = ImportOutcome.Unsupported();
        foreach (var slot in slots)
        {
            var args = new List<string> { "--settings-import", file };
            if (!restoreSecrets) args.Add("--no-secrets");
            var r = await SettingsCli.RunAsync(slot.Executable, args, passphrase ?? "", RunTimeout).ConfigureAwait(false);
            var o = SettingsCli.Import(r);
            if (o.State != ImportState.Unsupported) return o;
            last = o;
        }
        return last;
    }

    /// <summary>The combined-result line for one app's restore.</summary>
    public static BackupLine Line(string id, string name, ImportOutcome o) => o.State switch
    {
        ImportState.Ok => new BackupLine(id, name, o.Attention.Count == 0 && !o.RestartNeeded ? LineState.Ok : LineState.Attention,
            o.Attention.Concat(o.Warnings).Concat(o.RestartNeeded ? new[] { $"Restart {name} to use the restored settings." } : Array.Empty<string>()).ToList()),
        ImportState.Passphrase or ImportState.AppOpen => new BackupLine(id, name, LineState.Retry, new() { o.Message ?? "" }),
        ImportState.Unsupported => new BackupLine(id, name, LineState.Unsupported,
            new() { "This app's version can't restore its settings yet — update it, then try again." }),
        _ => new BackupLine(id, name, LineState.Failed, new() { o.Message ?? "It stopped with an error." }),
    };

    /// <summary>Every installed app that can take part, in catalog order.</summary>
    public static List<BackupTarget> Targets(IEnumerable<BackupApp> apps, Func<string, string?> installedVersion, Func<string, string?> installedPath)
    {
        var list = new List<BackupTarget>();
        foreach (var app in apps)
        {
            if (app.Excluded) continue;
            var slots = new List<BackupSlot>();
            foreach (var (key, edition) in app.Slots)
            {
                if (installedVersion(key) is not { } v || installedPath(key) is not { } exe || !File.Exists(exe)) continue;
                slots.Add(new BackupSlot(key, v, edition, exe));
            }
            if (slots.Count > 0) list.Add(new BackupTarget(app.Id, app.Name, slots));
        }
        return list;
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A catalog app as Back up all sees it: its install slots, the Full edition first.</summary>
public sealed record BackupApp(string Id, string Name, IReadOnlyList<(string Key, string? Edition)> Slots, bool Excluded);
public sealed record BackupSlot(string InstallKey, string Version, string? Edition, string Executable);
public sealed record BackupTarget(string Id, string Name, IReadOnlyList<BackupSlot> Slots);

public enum LineState { Ok, Attention, Unsupported, Failed, Skipped, Retry, Empty }
public sealed record BackupLine(string Id, string Name, LineState State, List<string> Detail);

// ── The launcher's own settings (launcher.jbtt-settings) ─────────────────────────────────────────────

/// <summary>The launcher's preferences by their platform-neutral names (see <see cref="LauncherSettings.Fields"/>).</summary>
public interface ILauncherPrefs
{
    JsonNode? Get(string name);
    void Set(string name, JsonNode value);
}

/// <summary>The launcher's saved passwords (the download passphrase, a GitHub token). Names only until the user ticks
/// "Include saved passwords" — only then is a value read.</summary>
public interface ILauncherSecrets
{
    IReadOnlyList<(string Name, string Label)> Present();
    Dictionary<string, string> Read();
    void Write(IReadOnlyDictionary<string, string> values);
}

/// <summary>Preferences in a JSON file keyed by the neutral names (tests and the command line's test home).</summary>
public sealed class FileLauncherPrefs : ILauncherPrefs
{
    private readonly string _path;
    private readonly JsonObject _values;
    public FileLauncherPrefs(string path)
    {
        _path = path;
        _values = File.Exists(path) ? SuiteBackup.ParseObject(File.ReadAllBytes(path)) ?? new JsonObject() : new JsonObject();
    }
    public JsonNode? Get(string name) => _values[name]?.DeepClone();
    public void Set(string name, JsonNode value)
    {
        _values[name] = value.DeepClone();
        SuiteBackup.AtomicWrite(_path, SuiteBackup.Json(_values));
    }
}

/// <summary>Passwords in a JSON file (tests and the command line's test home only).</summary>
public sealed class FileLauncherSecrets : ILauncherSecrets
{
    private readonly string _path;
    public FileLauncherSecrets(string path) => _path = path;
    public Dictionary<string, string> Read()
    {
        var o = File.Exists(_path) ? SuiteBackup.ParseObject(File.ReadAllBytes(_path)) : null;
        var d = new Dictionary<string, string>();
        if (o != null) foreach (var (k, v) in o) if (SuiteBackup.Str(v) is { Length: > 0 } s) d[k] = s;
        return d;
    }
    public IReadOnlyList<(string Name, string Label)> Present()
    {
        var have = Read();
        return LauncherSettings.SecretLabels.Where(x => have.ContainsKey(x.Name)).ToList();
    }
    public void Write(IReadOnlyDictionary<string, string> values)
    {
        var d = Read();
        foreach (var (k, v) in values) d[k] = v;
        var o = new JsonObject();
        foreach (var (k, v) in d) o[k] = v;
        SuiteBackup.AtomicWrite(_path, SuiteBackup.Json(o));
    }
}

/// <summary>The launcher's settings in a platform-neutral shape, so a Windows backup restores onto a Mac and back.
/// Machine-specific values (install homes, x64 choices, the relay override, notifications already sent) and state (show
/// lock, history, the log) are never included.</summary>
public static class LauncherSettings
{
    public enum Kind { String, Bool, Strings, Map }

    /// <summary>Neutral name, kind, and the allowed values of a string.</summary>
    public static readonly (string Name, Kind Kind, string[]? Allowed)[] Fields =
    {
        ("appearance", Kind.String, new[] { "system", "light", "dark" }),
        ("viewMode", Kind.String, new[] { "list", "grid" }),
        ("updateMode", Kind.String, new[] { "everyLaunch", "manual", "never" }),
        ("autoCheckInterval", Kind.String, UpdatePolicy.Intervals.Select(i => i.Raw).ToArray()),
        ("notifyUpdates", Kind.Bool, null),
        ("autoInstallUpdates", Kind.Bool, null),
        ("closeBehavior", Kind.String, new[] { "quit", "keepRunning" }),
        ("installToApplications", Kind.Bool, null),
        ("quickLaunchIcon", Kind.Bool, null),
        ("authMode", Kind.String, new[] { "token", "server" }),
        ("devChannel", Kind.Bool, null),
        ("appOrder", Kind.Strings, null),
        ("pinnedApps", Kind.Strings, null),
        ("hiddenApps", Kind.Strings, null),
        ("heldApps", Kind.Strings, null),
        ("categoryOrder", Kind.Strings, null),
        ("collapsedCategories", Kind.Strings, null),
        ("appVariants", Kind.Map, null),
    };

    public static readonly (string Name, string Label)[] SecretLabels =
        { ("downloadPassphrase", "Download passphrase"), ("githubToken", "GitHub token") };

    private const string StorePrefix = "store:launcher:";

    private static bool Valid(JsonNode? v, Kind kind, string[]? allowed)
    {
        try
        {
            switch (kind)
            {
                case Kind.String: var s = v!.GetValue<string>(); return allowed == null || allowed.Contains(s);
                case Kind.Bool: v!.GetValue<bool>(); return true;
                case Kind.Strings: return v is JsonArray a && a.All(x => x?.GetValueKind() == JsonValueKind.String);
                default: return v is JsonObject o && o.All(x => x.Value?.GetValueKind() == JsonValueKind.String);
            }
        }
        catch (Exception) { return false; }
    }

    public static JsonObject Export(ILauncherPrefs prefs)
    {
        var data = new JsonObject();
        foreach (var f in Fields)
            if (prefs.Get(f.Name) is { } v && Valid(v, f.Kind, null)) data[f.Name] = v.DeepClone();
        return data;
    }

    /// <summary>Writes what the file holds that this launcher understands; unknown or out-of-range values are left as
    /// they are and reported.</summary>
    public static (List<JsonObject> Attention, List<string> Warnings) Apply(JsonObject data, ILauncherPrefs prefs)
    {
        var attention = new List<JsonObject>();
        var warnings = new List<string>();
        foreach (var f in Fields)
        {
            if (!data.TryGetPropertyValue(f.Name, out var v) || v == null) continue;
            if (!Valid(v, f.Kind, f.Allowed))
            {
                warnings.Add($"A launcher setting ({f.Name}) had a value this version doesn't know, so it was left as it is.");
                continue;
            }
            prefs.Set(f.Name, v.DeepClone());
        }
        return (attention, warnings);
    }

    public static JsonObject Document(ILauncherPrefs prefs, ILauncherSecrets secrets, string version, bool includeSecrets,
                                      string? passphrase, int rounds = SettingsSeal.Pbkdf2Rounds)
    {
        var present = secrets.Present();
        var slots = new JsonArray();
        foreach (var (name, label) in present) slots.Add(new JsonObject { ["id"] = StorePrefix + name, ["label"] = label });
        var doc = new JsonObject
        {
            ["format"] = SuiteBackup.SettingsFormat, ["schema"] = SuiteBackup.SettingsSchema,
            ["app"] = new JsonObject { ["id"] = SuiteBackup.LauncherId, ["name"] = SuiteBackup.LauncherName, ["version"] = version },
            ["created"] = SuiteBackup.IsoNow(),
            ["source"] = SuiteBackup.Source(),
            ["items"] = new JsonObject
            {
                ["settings"] = new JsonObject { ["type"] = "json", ["label"] = "Launcher settings", ["data"] = Export(prefs) },
            },
            ["secret_slots"] = slots,
            ["secrets"] = null,
        };
        if (includeSecrets && present.Count > 0)
        {
            var wanted = present.Select(p => p.Name).ToHashSet();
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in secrets.Read()) if (wanted.Contains(k) && v.Length > 0) values[StorePrefix + k] = v;
            if (values.Count > 0)
            {
                if (!string.IsNullOrEmpty(passphrase))
                {
                    var plain = new JsonObject();
                    foreach (var (k, v) in values) plain[k] = v;
                    var box = SettingsSeal.Seal(Encoding.UTF8.GetBytes(plain.ToJsonString()), passphrase,
                                                SuiteBackup.Aad(SuiteBackup.LauncherId), rounds: rounds);
                    box["protected"] = true;
                    box["count"] = values.Count;
                    doc["secrets"] = box;
                }
                else
                {
                    var plain = new JsonObject();
                    foreach (var (k, v) in values) plain[k] = v;
                    doc["secrets"] = new JsonObject { ["protected"] = false, ["count"] = values.Count, ["values"] = plain };
                }
            }
        }
        return doc;
    }

    public sealed record Info(string Version, string? Created, string? Machine, bool HasSecrets, bool Protected, List<string> SecretLabels);

    public static (JsonObject Doc, Info Info) Inspect(byte[] data)
    {
        if (data.Length > SuiteBackup.MaxFileBytes || SuiteBackup.ParseObject(data) is not { } doc
            || SuiteBackup.Str(doc["format"]) != SuiteBackup.SettingsFormat || doc["items"] is not JsonObject)
            throw new SuiteBackup.Failure(SuiteBackup.FailureKind.NotABackup, "The launcher's settings in this backup can't be read.");
        if (SuiteBackup.Int(doc["schema"]) is not { } schema || schema > SuiteBackup.SettingsSchema)
            throw new SuiteBackup.Failure(SuiteBackup.FailureKind.Newer, "This backup was made by a newer JB Theatre Tools. Update it, then restore.");
        if (SuiteBackup.Str(doc["app"]?["id"]) != SuiteBackup.LauncherId)
            throw new SuiteBackup.Failure(SuiteBackup.FailureKind.WrongApp,
                $"This file is for {SuiteBackup.Str(doc["app"]?["name"]) ?? "another app"}, not JB Theatre Tools.");
        var sec = doc["secrets"] as JsonObject;
        var labels = (doc["secret_slots"] as JsonArray ?? new JsonArray()).Select(s => SuiteBackup.Str(s?["label"])).OfType<string>().ToList();
        return (doc, new Info(SuiteBackup.Str(doc["app"]?["version"]) ?? "", SuiteBackup.Str(doc["created"]),
            SuiteBackup.Str(doc["source"]?["machine"]), sec != null, SuiteBackup.Bool(sec?["protected"]) == true, labels));
    }

    /// <summary>Checks the passphrase against the launcher's sealed passwords without changing anything, so a restore
    /// stops before the first app is touched.</summary>
    public static void VerifyPassphrase(byte[] data, string? passphrase, bool restoreSecrets)
    {
        var (doc, info) = Inspect(data);
        if (!restoreSecrets || !info.Protected || doc["secrets"] is not JsonObject sec) return;
        if (string.IsNullOrEmpty(passphrase)) throw new SuiteBackup.Failure(SuiteBackup.FailureKind.PassphraseNeeded);
        Open(sec, passphrase);
    }

    private static Dictionary<string, string> Open(JsonObject sec, string passphrase)
    {
        try
        {
            var plain = SettingsSeal.Unseal(sec, passphrase, SuiteBackup.Aad(SuiteBackup.LauncherId));
            var o = SuiteBackup.ParseObject(plain) ?? new JsonObject();
            return o.Where(x => SuiteBackup.Str(x.Value) != null).ToDictionary(x => x.Key, x => SuiteBackup.Str(x.Value)!);
        }
        catch (SettingsSeal.SealException e) when (e.Kind == SettingsSeal.Failure.WrongPassphrase)
        {
            throw new SuiteBackup.Failure(SuiteBackup.FailureKind.WrongPassphrase);
        }
        catch (SettingsSeal.SealException e) { throw new SuiteBackup.Failure(SuiteBackup.FailureKind.Other, e.Message); }
    }

    public sealed record Outcome(List<string> Restored, List<JsonObject> Attention, List<string> Warnings);

    /// <summary>Restores the launcher's settings from a file. A wrong passphrase changes nothing.</summary>
    public static Outcome Restore(byte[] data, ILauncherPrefs prefs, ILauncherSecrets secrets, string? passphrase, bool restoreSecrets)
    {
        var (doc, _) = Inspect(data);
        var values = new Dictionary<string, string>();
        if (restoreSecrets && doc["secrets"] is JsonObject sec)
        {
            if (SuiteBackup.Bool(sec["protected"]) == true)
            {
                if (string.IsNullOrEmpty(passphrase)) throw new SuiteBackup.Failure(SuiteBackup.FailureKind.PassphraseNeeded);
                values = Open(sec, passphrase);
            }
            else if (sec["values"] is JsonObject plain)
                foreach (var (k, v) in plain) if (SuiteBackup.Str(v) is { } s) values[k] = s;
        }
        var restored = new List<string>();
        var attention = new List<JsonObject>();
        var warnings = new List<string>();
        if (doc["items"]?["settings"]?["data"] is JsonObject d)
        {
            var notes = Apply(d, prefs);
            attention.AddRange(notes.Attention);
            warnings.AddRange(notes.Warnings);
            restored.Add("Launcher settings");
        }
        var toWrite = new Dictionary<string, string>();
        foreach (var (k, v) in values)
            if (k.StartsWith(StorePrefix, StringComparison.Ordinal) && v.Length > 0) toWrite[k[StorePrefix.Length..]] = v;
        if (toWrite.Count > 0)
        {
            try { secrets.Write(toWrite); } catch (Exception ex) { warnings.Add(ex.Message); }
        }
        var haveNow = secrets.Present().Select(p => p.Name).ToHashSet();
        var missing = new List<string>();
        foreach (var slot in doc["secret_slots"] as JsonArray ?? new JsonArray())
        {
            var id = SuiteBackup.Str(slot?["id"]);
            if (id == null || !id.StartsWith(StorePrefix, StringComparison.Ordinal)) continue;
            var name = id[StorePrefix.Length..];
            if (!toWrite.ContainsKey(name) && !haveNow.Contains(name)) missing.Add(SuiteBackup.Str(slot?["label"]) ?? name);
        }
        if (missing.Count > 0)
            attention.Add(new JsonObject { ["label"] = "Passwords", ["message"] = "Enter them again in Settings: " + string.Join("; ", missing) + "." });
        return new Outcome(restored, attention, warnings);
    }
}

// ── The bundle (.jbtt-backup) ──────────────────────────────────────────────────────────────────────

public sealed record BundleApp(string Id, string Name, string Version, string File, string? Edition)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["id"] = Id, ["name"] = Name, ["version"] = Version, ["file"] = File };
        if (Edition != null) o["edition"] = Edition;
        return o;
    }
}

public sealed record BundleManifest(string Created, JsonObject Source, string LauncherVersion, List<BundleApp> Apps, string? LauncherFile)
{
    public JsonObject ToJson()
    {
        var apps = new JsonArray();
        foreach (var a in Apps) apps.Add(a.ToJson());
        var o = new JsonObject
        {
            ["format"] = SuiteBackup.BundleFormat, ["schema"] = SuiteBackup.BundleSchema, ["created"] = Created,
            ["source"] = Source.DeepClone(), ["launcher_version"] = LauncherVersion, ["apps"] = apps,
        };
        if (LauncherFile != null) o["launcher"] = new JsonObject { ["file"] = LauncherFile };
        return o;
    }

    /// <summary>A file inside the bundle is only ever a plain name ending .jbtt-settings — never a path.</summary>
    public static bool SafeName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 128 && !name.StartsWith('.') && name.EndsWith("." + SuiteBackup.SettingsExt, StringComparison.Ordinal)
        && name.IndexOfAny(new[] { '/', '\\', ':', '\0' }) < 0;

    public static BundleManifest Parse(byte[] data)
    {
        if (SuiteBackup.ParseObject(data) is not { } d || SuiteBackup.Str(d["format"]) != SuiteBackup.BundleFormat)
            throw new SuiteBackup.Failure(SuiteBackup.FailureKind.NotABackup);
        if (SuiteBackup.Int(d["schema"]) is not { } schema || schema > SuiteBackup.BundleSchema)
            throw new SuiteBackup.Failure(SuiteBackup.FailureKind.Newer, "This backup was made by a newer JB Theatre Tools. Update it, then restore.");
        var apps = new List<BundleApp>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in d["apps"] as JsonArray ?? new JsonArray())
        {
            var id = SuiteBackup.Str(a?["id"]);
            var file = SuiteBackup.Str(a?["file"]);
            if (string.IsNullOrEmpty(id) || !SafeName(file) || !seen.Add(id)) continue;
            apps.Add(new BundleApp(id, SuiteBackup.Str(a?["name"]) ?? id, SuiteBackup.Str(a?["version"]) ?? "", file!, SuiteBackup.Str(a?["edition"])));
        }
        var launcher = SuiteBackup.Str(d["launcher"]?["file"]);
        return new BundleManifest(SuiteBackup.Str(d["created"]) ?? "", d["source"] as JsonObject ?? new JsonObject(),
            SuiteBackup.Str(d["launcher_version"]) ?? "", apps, SafeName(launcher) ? launcher : null);
    }
}

public static class SuiteBundle
{
    /// <summary>Zips the files at the top level of <paramref name="staging"/> into <paramref name="dest"/>.</summary>
    public static void Write(string staging, string dest)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dest))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(dest) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                foreach (var f in Directory.EnumerateFiles(staging).OrderBy(f => f, StringComparer.Ordinal))
                    zip.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.Optimal);
            File.Move(tmp, dest, overwrite: true);
        }
        finally { SuiteBackup.TryDelete(tmp); }
    }

    public sealed record Opened(string Folder, BundleManifest Manifest)
    {
        public string PathOf(string file) => Path.Combine(Folder, file);
    }

    /// <summary>Unpacks a backup into a fresh temporary folder, refusing any entry that isn't a plain top-level file.</summary>
    public static Opened Open(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length > 512L << 20) throw new SuiteBackup.Failure(SuiteBackup.FailureKind.NotABackup);
        var folder = Path.Combine(Path.GetTempPath(), "jbtt-restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.GetEntry(SuiteBackup.ManifestFile) == null) throw new SuiteBackup.Failure(SuiteBackup.FailureKind.NotABackup);
            Directory.CreateDirectory(folder);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal) || e.FullName.EndsWith('/')) continue;
                if (e.FullName != SuiteBackup.ManifestFile && !BundleManifest.SafeName(e.FullName))
                    throw new SuiteBackup.Failure(SuiteBackup.FailureKind.NotABackup,
                        $"This backup holds an unexpected file ({e.FullName}), so it wasn't opened.");
                if (e.Length > SuiteBackup.MaxFileBytes) throw new SuiteBackup.Failure(SuiteBackup.FailureKind.NotABackup, "This backup is too large.");
                e.ExtractToFile(Path.Combine(folder, e.FullName), overwrite: false);
            }
            var manifest = BundleManifest.Parse(File.ReadAllBytes(Path.Combine(folder, SuiteBackup.ManifestFile)));
            return new Opened(folder, manifest);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            try { Directory.Delete(folder, true); } catch (Exception) { }
            throw new SuiteBackup.Failure(SuiteBackup.FailureKind.NotABackup, "This backup can't be unpacked (it may be damaged).");
        }
        catch
        {
            try { Directory.Delete(folder, true); } catch (Exception) { }
            throw;
        }
    }

    public sealed record AppFileInfo(string? AppId, bool Protected, bool HasSecrets, bool Readable);

    public static AppFileInfo Info(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > SuiteBackup.MaxFileBytes) return new AppFileInfo(null, false, false, false);
            if (SuiteBackup.ParseObject(File.ReadAllBytes(path)) is not { } d || SuiteBackup.Str(d["format"]) != SuiteBackup.SettingsFormat)
                return new AppFileInfo(null, false, false, false);
            var sec = d["secrets"] as JsonObject;
            return new AppFileInfo(SuiteBackup.Str(d["app"]?["id"]), SuiteBackup.Bool(sec?["protected"]) == true, sec != null, true);
        }
        catch (Exception) { return new AppFileInfo(null, false, false, false); }
    }
}

// ── Running an app's settings command line ─────────────────────────────────────────────────────────

public sealed record CliRun(int? ExitCode, JsonObject? Result, bool TimedOut, string? LaunchError)
{
    public bool Ok => ExitCode == 0 && SuiteBackup.Bool(Result?["ok"]) == true;
    public string? Message => SuiteBackup.Str(Result?["error"]);
    public string? Code => SuiteBackup.Str(Result?["code"]);
}

public enum AppProbeState { Ready, Unsupported, Failed }
public sealed record AppProbe(AppProbeState State, List<string> Items, int SecretCount, string? Message)
{
    public static AppProbe Unsupported() => new(AppProbeState.Unsupported, new(), 0, null);
}

public enum ExportState { Ok, Unsupported, Failed }
public sealed record ExportOutcome(ExportState State, int Secrets, bool Protected, string? Message, bool Empty = false);

public enum ImportState { Ok, Passphrase, AppOpen, Unsupported, Failed }
public sealed record ImportOutcome(ImportState State, List<string> Attention, List<string> Warnings, bool RestartNeeded,
                                   bool WrongPassphrase, string? Message)
{
    public static ImportOutcome Unsupported() => new(ImportState.Unsupported, new(), new(), false, false, null);
}

public static class SettingsCli
{
    /// <summary>Runs <paramref name="exe"/> with <paramref name="args"/> + <c>--result &lt;tmp&gt;</c> (and
    /// <c>--passphrase-stdin</c>, the passphrase on one stdin line, when <paramref name="passphrase"/> isn't null) with no
    /// window. An app too old for these flags opens its window instead of answering, so the run is bounded: past the
    /// timeout the process tree started here is stopped and reported as timed out.</summary>
    public static async Task<CliRun> RunAsync(string exe, IEnumerable<string> args, string? passphrase, TimeSpan timeout)
    {
        var result = Path.Combine(Path.GetTempPath(), "jbtt-result-" + Guid.NewGuid().ToString("N") + ".json");
        var psi = new ProcessStartInfo
        {
            FileName = exe, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (passphrase != null) psi.ArgumentList.Add("--passphrase-stdin");
        psi.ArgumentList.Add("--result");
        psi.ArgumentList.Add(result);
        psi.Environment.Remove("JBTT_SETTINGS_PASSPHRASE");
        // A windowed Windows build may start without a usable stdin; the suite's module then reads the same value from
        // this variable (never from the command line, which other users can see).
        if (passphrase is { Length: > 0 } && OperatingSystem.IsWindows()) psi.Environment["JBTT_SETTINGS_PASSPHRASE"] = passphrase;
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("It didn't start."); }
        catch (Exception ex) { return new CliRun(null, null, false, ex.Message); }
        using (p)
        {
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                if (passphrase != null) await p.StandardInput.WriteLineAsync(passphrase).ConfigureAwait(false);
                p.StandardInput.Close();
            }
            catch (IOException) { /* it exited already (an old app) */ }
            bool timedOut = false;
            using (var cts = new CancellationTokenSource(timeout))
            {
                try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                    try { p.WaitForExit(5000); } catch (Exception) { }
                }
            }
            JsonObject? json = null;
            try { if (File.Exists(result)) json = SuiteBackup.ParseObject(File.ReadAllBytes(result)); }
            catch (IOException) { }
            finally { SuiteBackup.TryDelete(result); }
            return new CliRun(timedOut ? null : p.ExitCode, json, timedOut, null);
        }
    }

    /// <summary>No answer, one that isn't the suite's result, or "bad usage": the app is too old for the command line.</summary>
    private static bool LooksUnsupported(CliRun r) => r.LaunchError == null && (r.Result == null || r.ExitCode == 64 || r.Code == "usage");

    private static string Err(CliRun r) => r.Message ?? $"It stopped with an error ({r.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}).";

    public static AppProbe Describe(CliRun r)
    {
        if (r.Ok)
        {
            var items = (r.Result?["items"] as JsonArray ?? new JsonArray()).Select(SuiteBackup.Str).OfType<string>().ToList();
            return new AppProbe(AppProbeState.Ready, items, SuiteBackup.Int(r.Result?["secrets"]?["count"]) ?? 0, null);
        }
        if (r.LaunchError != null) return new AppProbe(AppProbeState.Failed, new(), 0, $"It couldn't be started ({r.LaunchError}).");
        if (LooksUnsupported(r)) return AppProbe.Unsupported();
        return new AppProbe(AppProbeState.Failed, new(), 0, Err(r));
    }

    public static ExportOutcome Export(CliRun r)
    {
        if (r.Ok) return new ExportOutcome(ExportState.Ok, SuiteBackup.Int(r.Result?["secrets"]) ?? 0, SuiteBackup.Bool(r.Result?["protected"]) == true, null,
                                           Empty: r.Result?["items"] is JsonArray { Count: 0 });
        if (r.LaunchError != null) return new ExportOutcome(ExportState.Failed, 0, false, $"It couldn't be started ({r.LaunchError}).");
        if ((r.TimedOut && r.Result == null) || LooksUnsupported(r)) return new ExportOutcome(ExportState.Unsupported, 0, false, null);
        return new ExportOutcome(ExportState.Failed, 0, false, Err(r));
    }

    /// <summary>Attention entries are <c>{label, message}</c> objects (or plain strings) → display lines.</summary>
    public static List<string> Lines(JsonNode? v)
    {
        var list = new List<string>();
        foreach (var e in v as JsonArray ?? new JsonArray())
        {
            if (SuiteBackup.Str(e) is { } s) { list.Add(s); continue; }
            if (e is JsonObject o)
            {
                var msg = SuiteBackup.Str(o["message"]) ?? "";
                var label = SuiteBackup.Str(o["label"]) ?? "";
                if (msg.Length > 0) list.Add(msg); else if (label.Length > 0) list.Add(label);
            }
        }
        return list;
    }

    public static ImportOutcome Import(CliRun r)
    {
        if (r.Ok)
            return new ImportOutcome(ImportState.Ok, Lines(r.Result?["attention"]), Lines(r.Result?["warnings"]),
                SuiteBackup.Bool(r.Result?["restart_needed"]) == true, false, null);
        if (r.LaunchError != null)
            return new ImportOutcome(ImportState.Failed, new(), new(), false, false, $"It couldn't be started ({r.LaunchError}).");
        if (r.ExitCode == 2)
            return new ImportOutcome(ImportState.Passphrase, new(), new(), false, r.Code == "wrong_passphrase",
                r.Message ?? "This backup's passwords are protected. Enter its passphrase.");
        if (r.ExitCode == 5)
            return new ImportOutcome(ImportState.AppOpen, new(), new(), false, false, r.Message ?? "Quit the app, then restore its settings.");
        if ((r.TimedOut && r.Result == null) || LooksUnsupported(r)) return ImportOutcome.Unsupported();
        return new ImportOutcome(ImportState.Failed, new(), new(), false, false, Err(r));
    }
}
