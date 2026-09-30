using System.Text.Json.Nodes;

namespace JBTheatreTools;

// Settings → "Back Up All Apps…" / "Restore All Apps…" (the work itself is Core/SuiteBackup.cs, shared with the command line).

/// <summary>The launcher's settings (settings.json) by the neutral names a backup uses — the same names the Mac writes.</summary>
internal sealed class WinLauncherPrefs : ILauncherPrefs
{
    private readonly AppSettings _s;
    public WinLauncherPrefs(AppSettings s) => _s = s;

    private static JsonArray Arr(IEnumerable<string> v) => new(v.Select(x => (JsonNode?)x).ToArray());
    private static List<string> List(JsonNode v) => v.AsArray().Select(x => x!.GetValue<string>()).ToList();

    public JsonNode? Get(string name) => name switch
    {
        "appearance" => _s.Appearance,
        "viewMode" => _s.ViewMode,
        "updateMode" => _s.UpdateMode,
        "autoCheckInterval" => _s.AutoCheckInterval,
        "notifyUpdates" => _s.NotifyUpdates,
        "autoInstallUpdates" => _s.AutoInstallUpdates,
        "closeBehavior" => _s.CloseBehavior,
        "installToApplications" => _s.InstallToApplications,
        "quickLaunchIcon" => _s.AlwaysShowTray,
        "authMode" => _s.AuthMode,
        "devChannel" => _s.DevChannel,
        "appOrder" => Arr(_s.AppOrder),
        "pinnedApps" => Arr(_s.PinnedApps),
        "hiddenApps" => Arr(_s.HiddenApps),
        "heldApps" => Arr(_s.HeldApps),
        "categoryOrder" => Arr(_s.CategoryOrder),
        "collapsedCategories" => Arr(_s.CollapsedCategories),
        "appVariants" => new JsonObject(_s.AppVariants.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        _ => null,
    };

    public void Set(string name, JsonNode v)
    {
        switch (name)
        {
            case "appearance": _s.Appearance = v.GetValue<string>(); break;
            case "viewMode": _s.ViewMode = v.GetValue<string>(); break;
            case "updateMode": _s.UpdateMode = v.GetValue<string>(); break;
            case "autoCheckInterval": _s.AutoCheckInterval = v.GetValue<string>(); break;
            case "notifyUpdates": _s.NotifyUpdates = v.GetValue<bool>(); break;
            case "autoInstallUpdates": _s.AutoInstallUpdates = v.GetValue<bool>(); break;
            case "closeBehavior": _s.CloseBehavior = v.GetValue<string>(); break;
            case "installToApplications": _s.InstallToApplications = v.GetValue<bool>(); break;
            case "quickLaunchIcon": _s.AlwaysShowTray = v.GetValue<bool>(); break;
            case "authMode": _s.AuthMode = v.GetValue<string>(); break;
            case "devChannel": _s.DevChannel = v.GetValue<bool>(); break;
            case "appOrder": _s.AppOrder = List(v); break;
            case "pinnedApps": _s.PinnedApps = List(v); break;
            case "hiddenApps": _s.HiddenApps = List(v); break;
            case "heldApps": _s.HeldApps = List(v); break;
            case "categoryOrder": _s.CategoryOrder = List(v); break;
            case "collapsedCategories": _s.CollapsedCategories = List(v); break;
            case "appVariants": _s.AppVariants = v.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()); break;
        }
    }
}

/// <summary>The download passphrase and the GitHub token in Credential Manager. Only read when the user includes
/// passwords in a backup.</summary>
internal sealed class CredentialLauncherSecrets : ILauncherSecrets
{
    public IReadOnlyList<(string Name, string Label)> Present()
    {
        var list = new List<(string, string)>();
        try { if (!string.IsNullOrEmpty(TokenStore.LoadServerPass())) list.Add(("downloadPassphrase", "Download passphrase")); } catch (Exception) { }
        try { if (!string.IsNullOrEmpty(TokenStore.Load())) list.Add(("githubToken", "GitHub token")); } catch (Exception) { }
        return list;
    }

    public Dictionary<string, string> Read()
    {
        var d = new Dictionary<string, string>();
        if (TokenStore.LoadServerPass() is { Length: > 0 } p) d["downloadPassphrase"] = p;
        if (TokenStore.Load() is { Length: > 0 } t) d["githubToken"] = t;
        return d;
    }

    public void Write(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("downloadPassphrase", out var p) && p.Length > 0 && !TokenStore.SaveServerPass(p))
            throw new IOException("Couldn't save the download passphrase in Credential Manager.");
        if (values.TryGetValue("githubToken", out var t) && t.Length > 0 && !TokenStore.Save(t))
            throw new IOException("Couldn't save the GitHub token in Credential Manager.");
    }
}

/// <summary>A scrolling column of tick boxes, each with a smaller line under it.</summary>
internal sealed class ChoiceList : FlowLayoutPanel
{
    private readonly List<(HouseCheckBox Box, Label Detail)> _rows = new();

    public ChoiceList()
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoScroll = true;
        Dock = DockStyle.Fill;
        Resize += (_, _) => Fit();
    }

    public (HouseCheckBox Box, Label Detail) Add(string title, string detail, bool dark)
    {
        var box = new HouseCheckBox { Text = title, Margin = new Padding(0, 6, 0, 0), BackColor = Theme.Surface(dark) };
        var line = new Label
        {
            Text = detail, AutoSize = false, UseMnemonic = false, Margin = new Padding(24, 0, 0, 4),
            ForeColor = Theme.Sub(dark), BackColor = Theme.Surface(dark),
        };
        _rows.Add((box, line));
        Controls.Add(box);
        Controls.Add(line);
        Fit();
        return (box, line);
    }

    /// <summary>Widths follow the panel (the text wraps); heights follow the text.</summary>
    public void Fit()
    {
        int w = Math.Max(100, ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8);
        SuspendLayout();
        foreach (var (box, line) in _rows)
        {
            box.Width = w;
            box.Height = box.GetPreferredSize(new Size(w, 0)).Height;
            int lw = w - line.Margin.Left;
            line.Width = lw;
            line.Height = line.Text.Length == 0 ? 0 : TextRenderer.MeasureText(line.Text, line.Font, new Size(lw, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 2;
        }
        ResumeLayout(true);
    }
}

/// <summary>The combined result: one line per app and what to check, in a read-only reader.</summary>
internal static class BackupResults
{
    public static void Show(RichTextBox reader, IEnumerable<BackupLine> lines, string? intro)
    {
        reader.Clear();
        bool dark = Theme.CurrentDark;
        using var bold = Theme.Ui(Theme.PtBody, semibold: true);
        using var body = Theme.Ui(Theme.PtSmall);
        if (intro != null) DialogKit.Append(reader, intro + "\n\n", bold, Theme.Fg(dark));
        foreach (var l in lines)
        {
            var (mark, what, color) = l.State switch
            {
                LineState.Ok => ("✓", "done", Theme.Ok),
                LineState.Attention => ("!", "done, check these in its Settings", Theme.Warn),
                LineState.Unsupported => ("–", "not yet", Theme.Muted(dark)),
                LineState.Skipped => ("–", "skipped", Theme.Muted(dark)),
                LineState.Retry => ("↻", "not done yet", Theme.Warn),
                _ => ("×", "failed", Theme.Danger),
            };
            DialogKit.Append(reader, mark + "  ", bold, color);
            DialogKit.Append(reader, $"{l.Name} — {what}\n", bold, Theme.Fg(dark));
            foreach (var d in l.Detail) DialogKit.Append(reader, "     " + d + "\n", body, Theme.Sub(dark));
            DialogKit.Append(reader, "\n", body, Theme.Sub(dark));
        }
        reader.SelectionStart = 0;
    }

    public static string DocumentsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JB Theatre Tools");
}

/// <summary>Settings → "Back Up All Apps…": every installed app ticked (the ones whose version can back up), the
/// launcher's own settings, "Include saved passwords" with an optional passphrase, then a file to save.</summary>
internal sealed class BackupAllDialog : Form
{
    private readonly List<(BackupTarget Target, HouseCheckBox Box, Label Detail)> _rows = new();
    private readonly Dictionary<string, (BackupSlot? Slot, AppProbe Probe)> _probes = new();
    private readonly HouseCheckBox _launcher;
    private readonly HouseCheckBox _secrets = new();
    private readonly HouseTextField _pass = new(), _confirm = new();
    private readonly Label _note = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 4, 0, 0) };
    private readonly Label _status = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 4, 0, 0) };
    private readonly HouseButton _backUp = DialogKit.Button("Back Up…", primary: true);
    private readonly HouseButton _cancel = DialogKit.Button("Cancel", DialogResult.Cancel);
    private readonly HouseButton _show = DialogKit.Button("Show in Explorer");
    private readonly RichTextBox _reader;
    private readonly ChoiceList _list = new();
    private readonly FlowLayoutPanel _options = new()
    {
        Dock = DockStyle.Bottom, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 10, 14, 0),
    };
    private readonly AppSettings _settings;
    private readonly string _version;
    private readonly int _launcherSecrets;
    private string? _saved;
    private bool _busy;

    public BackupAllDialog(IReadOnlyList<BackupTarget> targets, AppSettings settings, string version, bool dark)
    {
        _settings = settings;
        _version = version;
        Text = "Back Up All Apps";
        DialogKit.Style(this, dark, new Size(560, 540), sizable: true);
        var intro = new Label
        {
            Text = "Saves each app's settings, and this launcher's, into one file you can restore on this PC or a new show computer. Nothing is changed.",
            Dock = DockStyle.Top, AutoSize = false, Height = 52, UseMnemonic = false, Padding = new Padding(16, 14, 14, 0),
            ForeColor = Theme.Sub(dark),
        };
        try { _launcherSecrets = new CredentialLauncherSecrets().Present().Count; } catch (Exception) { _launcherSecrets = 0; }
        _launcher = _list.Add("JB Theatre Tools", "This launcher's settings: list order, pins, editions, updates, appearance", dark).Box;
        _launcher.Checked = true;
        _launcher.CheckedChanged += (_, _) => UpdateState();
        foreach (var t in targets)
        {
            var (box, detail) = _list.Add(t.Name, "Checking…", dark);
            box.Enabled = false;
            box.CheckedChanged += (_, _) => UpdateState();
            _rows.Add((t, box, detail));
        }
        if (targets.Count == 0) _list.Add("No apps are installed", "Only the launcher's settings can be backed up.", dark).Box.Enabled = false;
        _reader = DialogKit.Reader(dark);
        _reader.Visible = false;
        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(_list);
        host.Controls.Add(_reader);

        _secrets.Visible = false;
        _secrets.CheckedChanged += (_, _) => UpdateState();
        foreach (var (f, cue, name) in new[] { (_pass, "Passphrase (optional)", "Passphrase"), (_confirm, "Confirm passphrase", "Confirm passphrase") })
        {
            f.Placeholder = cue;
            f.Box.UseSystemPasswordChar = true;
            f.Box.AccessibleName = name;
            f.Size = new Size(240, 28);
            f.Margin = new Padding(0, 6, 8, 0);
            f.ApplyTheme(dark);
            f.Box.TextChanged += (_, _) => UpdateState();
        }
        var fields = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        fields.Controls.Add(_pass);
        fields.Controls.Add(_confirm);
        _options.Controls.Add(_secrets);
        _options.Controls.Add(fields);
        _options.Controls.Add(_note);
        _options.Controls.Add(_status);
        _options.Resize += (_, _) =>
        {
            int w = Math.Max(100, _options.ClientSize.Width - _options.Padding.Horizontal);
            _secrets.Width = w;
            _secrets.Height = _secrets.GetPreferredSize(new Size(w, 0)).Height;
            _note.MaximumSize = _status.MaximumSize = new Size(w, 0);
        };

        var buttons = DialogKit.ButtonRow();
        buttons.Controls.Add(_backUp);
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_show);
        _show.Visible = false;
        _show.Click += (_, _) => { if (_saved != null) OpenInExplorer(_saved); };
        _backUp.Click += async (_, _) => await BackUpAsync();
        _cancel.Click += (_, _) => { if (!_busy) Close(); };
        // House rule: the action is filled; the safe choice has the focus and Enter.
        AcceptButton = _cancel;
        CancelButton = _cancel;
        Load += (_, _) => ActiveControl = _cancel;
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };

        Controls.Add(DialogKit.Padded(host, dark));
        Controls.Add(intro);
        Controls.Add(_options);
        Controls.Add(buttons);
        ResumeLayout(false);
        UpdateState();
        Shown += async (_, _) => await ProbeAllAsync();
    }

    private static void OpenInExplorer(string file)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"show backup: {ex.Message}"); }
    }

    /// <summary>Asks each installed app what it would save — three at a time, so a backup doesn't start every app at once.</summary>
    private async Task ProbeAllAsync()
    {
        using var gate = new SemaphoreSlim(3);
        var tasks = _rows.Select(async r =>
        {
            await gate.WaitAsync();
            try { return (r.Target.Id, await Task.Run(() => SuiteBackup.ProbeAsync(r.Target))); }
            finally { gate.Release(); }
        }).ToList();
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            var (id, result) = await done;
            if (IsDisposed) return;
            _probes[id] = result;
            var row = _rows.First(r => r.Target.Id == id);
            var p = result.Probe;
            row.Detail.Text = p.State switch
            {
                AppProbeState.Ready => (p.Items.Count == 0 ? "Nothing saved yet" : string.Join(", ", p.Items))
                                       + (p.SecretCount > 0 ? $" · {p.SecretCount} saved password{(p.SecretCount == 1 ? "" : "s")}" : ""),
                AppProbeState.Unsupported => SuiteBackup.UnsupportedMessage,
                _ => p.Message ?? "It couldn't be checked.",
            };
            row.Box.Enabled = p.State == AppProbeState.Ready;
            row.Box.Checked = p.State == AppProbeState.Ready;
            _list.Fit();
            UpdateState();
        }
    }

    private bool Checking => _rows.Any(r => !_probes.ContainsKey(r.Target.Id));

    private int SecretCount => (_launcher.Checked ? _launcherSecrets : 0)
        + _rows.Where(r => r.Box.Checked && _probes.TryGetValue(r.Target.Id, out var p) && p.Probe.State == AppProbeState.Ready)
               .Sum(r => _probes[r.Target.Id].Probe.SecretCount);

    private string? PassProblem =>
        !_secrets.Checked || (_pass.Box.Text.Length == 0 && _confirm.Box.Text.Length == 0) ? null
        : _pass.Box.Text != _confirm.Box.Text ? "The two passphrases don't match." : null;

    private void UpdateState()
    {
        if (_busy) return;
        bool dark = Theme.CurrentDark;
        int n = SecretCount;
        _secrets.Visible = n > 0;
        _secrets.Text = $"Include saved passwords ({n})";
        if (n == 0) _secrets.Checked = false;
        _pass.Parent!.Visible = _secrets.Checked;
        _note.Visible = _secrets.Checked;
        _note.Text = _pass.Box.Text.Length == 0
            ? "Leave blank to save the passwords unprotected — anyone with the file can read them."
            : "You'll need this passphrase to restore the passwords. It isn't saved anywhere.";
        _note.ForeColor = _pass.Box.Text.Length == 0 ? Theme.Warn : Theme.Sub(dark);
        var problem = PassProblem;
        _status.Visible = problem != null || Checking;
        _status.Text = problem ?? "Checking which apps can back up…";
        _status.ForeColor = problem != null ? Theme.Danger : Theme.Sub(dark);
        _backUp.Enabled = !Checking && problem == null && (_launcher.Checked || _rows.Any(r => r.Box.Checked));
        _options.PerformLayout();
    }

    private async Task BackUpAsync()
    {
        Directory.CreateDirectory(BackupResults.DocumentsFolder);
        using var dlg = new SaveFileDialog
        {
            Title = "Back Up All Apps", FileName = SuiteBackup.BundleFileName(), InitialDirectory = BackupResults.DocumentsFolder,
            Filter = "JB Theatre Tools backup (*.jbtt-backup)|*.jbtt-backup", DefaultExt = SuiteBackup.BundleExt, AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var chosen = _rows.Where(r => r.Box.Checked && _probes.TryGetValue(r.Target.Id, out var p) && p.Slot != null)
                          .Select(r => (r.Target, _probes[r.Target.Id].Slot!)).ToList();
        bool secrets = _secrets.Checked;
        string? pass = secrets && _pass.Box.Text.Length > 0 ? _pass.Box.Text : null;
        _busy = true;
        _backUp.Enabled = _cancel.Enabled = false;
        _status.Visible = true;
        _status.ForeColor = Theme.Sub(Theme.CurrentDark);
        var progress = new Progress<string>(m => _status.Text = m);
        try
        {
            var lines = await Task.Run(() => SuiteBackup.BackUpAllAsync(chosen, _launcher.Checked, new WinLauncherPrefs(_settings),
                new CredentialLauncherSecrets(), _version, secrets, pass, dlg.FileName, ((IProgress<string>)progress).Report));
            Log.Write($"backed up {chosen.Count} app(s){(_launcher.Checked ? " + the launcher" : "")} to {Path.GetFileName(dlg.FileName)}");
            _saved = dlg.FileName;
            _pass.Box.Text = _confirm.Box.Text = "";
            _list.Visible = false;
            _reader.Visible = true;
            BackupResults.Show(_reader, lines, $"Saved {Path.GetFileName(dlg.FileName)}.");
            _options.Visible = false;
            _backUp.Visible = false;
            _show.Visible = true;
            _cancel.Text = "Done";
            _busy = false;
            _cancel.Enabled = true;
            ActiveControl = _cancel;
        }
        catch (Exception ex)
        {
            Log.Write($"back up all FAILED: {ex.Message}");
            _busy = false;
            _cancel.Enabled = true;
            UpdateState();
            _status.Visible = true;
            _status.Text = ex.Message;
            _status.ForeColor = Theme.Danger;
        }
    }
}

/// <summary>Settings → "Restore All Apps…": the apps in the backup, each installed / not installed (install it first,
/// or skip) / open (quit it first), the launcher's own settings (restored last), passwords and the passphrase (asked
/// once), then one combined result.</summary>
internal sealed class RestoreAllDialog : Form
{
    private sealed class Row
    {
        public required BundleApp App;
        public required string File;
        public required SuiteBundle.AppFileInfo Info;
        public CatalogApp? Catalog;
        public BackupTarget? Target;
        public required HouseCheckBox Box;
        public required Label Detail;
        public HouseCheckBox? InstallFirst;
        public bool Open;
    }

    private readonly SuiteBundle.Opened _opened;
    private readonly List<Row> _rows = new();
    private readonly HouseCheckBox? _launcher;
    private readonly byte[]? _launcherData;
    private readonly LauncherSettings.Info? _launcherInfo;
    private readonly HouseCheckBox _restoreSecrets = new() { Text = "Restore saved passwords", Checked = true };
    private readonly HouseTextField _pass = new();
    private readonly Label _error = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 4, 0, 0) };
    private readonly Label _note = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 6, 0, 0),
        Text = "Each app keeps a copy of its current settings first, so its own Settings can undo the restore." };
    private readonly HouseButton _restore = DialogKit.Button("Restore", primary: true);
    private readonly HouseButton _cancel = DialogKit.Button("Cancel", DialogResult.Cancel);
    private readonly HouseButton _checkAgain = DialogKit.Button("Check Again");
    private readonly RichTextBox _reader;
    private readonly ChoiceList _list = new();
    private readonly FlowLayoutPanel _options = new()
    {
        Dock = DockStyle.Bottom, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 10, 14, 0),
    };
    private readonly AppSettings _settings;
    private readonly Func<string, string?, Task> _install;
    private readonly Func<bool> _locked;
    private readonly List<BackupLine> _lines = new();
    private readonly HashSet<string> _finished = new();
    private bool _busy, _done;

    /// <summary>The launcher's own settings were restored: the main window re-reads them when this closes.</summary>
    public bool LauncherRestored { get; private set; }

    public RestoreAllDialog(SuiteBundle.Opened opened, IReadOnlyList<CatalogApp> apps, IReadOnlyList<BackupTarget> targets,
                            AppSettings settings, Func<string, string?, Task> install, Func<bool> locked, bool dark)
    {
        _opened = opened;
        _settings = settings;
        _install = install;
        _locked = locked;
        Text = "Restore All Apps";
        DialogKit.Style(this, dark, new Size(560, 560), sizable: true);
        var m = opened.Manifest;
        var from = SuiteBackup.Str(m.Source["machine"]) ?? "another computer";
        if (SuiteBackup.Str(m.Source["os"]) is { } os) from += " · " + os;
        if (DateTime.TryParse(m.Created, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var when))
            from += " · " + when.ToLocalTime().ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        var intro = new Label { Text = "From " + from, Dock = DockStyle.Top, AutoSize = false, Height = 40, UseMnemonic = false,
                                Padding = new Padding(16, 14, 14, 0), ForeColor = Theme.Sub(dark) };
        foreach (var a in m.Apps)
        {
            var file = opened.PathOf(a.File);
            var cat = apps.FirstOrDefault(x => x.Id == a.Id);
            var (box, detail) = _list.Add(cat?.Name ?? a.Name, "", dark);
            box.Checked = true;
            var row = new Row { App = a, File = file, Info = SuiteBundle.Info(file), Catalog = cat,
                                Target = targets.FirstOrDefault(t => t.Id == a.Id), Box = box, Detail = detail };
            if (row.Target == null && cat != null)
            {
                row.InstallFirst = _list.Add("Install it first", "", dark).Box;
                row.InstallFirst.Margin = new Padding(24, 0, 0, 4);
                row.InstallFirst.Checked = true;
                row.InstallFirst.CheckedChanged += (_, _) => UpdateState();
            }
            box.CheckedChanged += (_, _) => UpdateState();
            _rows.Add(row);
        }
        if (m.LauncherFile != null && File.Exists(opened.PathOf(m.LauncherFile)))
        {
            try
            {
                var data = File.ReadAllBytes(opened.PathOf(m.LauncherFile));
                _launcherInfo = LauncherSettings.Inspect(data).Info;
                _launcherData = data;
                _launcher = _list.Add("JB Theatre Tools", "This launcher's settings — restored last", dark).Box;
                _launcher.Checked = true;
                _launcher.CheckedChanged += (_, _) => UpdateState();
            }
            catch (SuiteBackup.Failure ex) { Log.Write($"restore: launcher settings skipped: {ex.Message}"); }
        }
        _reader = DialogKit.Reader(dark);
        _reader.Visible = false;
        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(_list);
        host.Controls.Add(_reader);

        _restoreSecrets.CheckedChanged += (_, _) => UpdateState();
        _pass.Placeholder = "Backup passphrase";
        _pass.Box.UseSystemPasswordChar = true;
        _pass.Box.AccessibleName = "Backup passphrase";
        _pass.Size = new Size(300, 28);
        _pass.Margin = new Padding(0, 6, 0, 0);
        _pass.ApplyTheme(dark);
        _pass.Box.TextChanged += (_, _) => UpdateState();
        _error.ForeColor = Theme.Danger;
        _note.ForeColor = Theme.Sub(dark);
        _options.Controls.Add(_restoreSecrets);
        _options.Controls.Add(_pass);
        _options.Controls.Add(_error);
        _options.Controls.Add(_note);
        _options.Resize += (_, _) =>
        {
            int w = Math.Max(100, _options.ClientSize.Width - _options.Padding.Horizontal);
            _restoreSecrets.Width = w;
            _restoreSecrets.Height = _restoreSecrets.GetPreferredSize(new Size(w, 0)).Height;
            _error.MaximumSize = _note.MaximumSize = new Size(w, 0);
        };

        var buttons = DialogKit.ButtonRow();
        buttons.Controls.Add(_restore);
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_checkAgain);
        _checkAgain.Click += (_, _) => { RefreshOpen(); UpdateState(); };
        _restore.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => { if (!_busy) Close(); };
        AcceptButton = _cancel;   // the safe choice has the focus and Enter; Restore is the filled action
        CancelButton = _cancel;
        Load += (_, _) => ActiveControl = _cancel;
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        FormClosed += (_, _) => { try { Directory.Delete(_opened.Folder, true); } catch (Exception) { } };

        Controls.Add(DialogKit.Padded(host, dark));
        Controls.Add(intro);
        Controls.Add(_options);
        Controls.Add(buttons);
        ResumeLayout(false);
        RefreshOpen();
        UpdateState();
    }

    private void RefreshOpen()
    {
        foreach (var r in _rows)
            r.Open = r.Target?.Slots.Any(s => InstallManager.Shared.RunningInstances(s.InstallKey).Length > 0) ?? false;
    }

    private bool NeedsPassphrase => _restoreSecrets.Checked &&
        (_rows.Any(r => r.Box.Checked && r.Info.Protected) || (_launcher?.Checked == true && _launcherInfo?.Protected == true));
    private bool HasSecrets => _rows.Any(r => r.Box.Checked && r.Info.HasSecrets) || (_launcher?.Checked == true && _launcherInfo?.HasSecrets == true);

    private string Status(Row r)
    {
        var from = r.App.Version.Length == 0 ? "" : $"Backed up from {r.App.Version}. ";
        if (!r.Info.Readable) return "This part of the backup can't be read.";
        if (r.Catalog == null) return from + "Not in this launcher's list — it will be skipped.";
        if (r.Target == null) return from + (r.InstallFirst?.Checked == true ? "Not installed — it will be installed first." : "Not installed — it will be skipped.");
        if (r.Open) return "Open — quit it before restoring.";
        return from + "Installed.";
    }

    private void UpdateState()
    {
        if (_busy) return;
        bool dark = Theme.CurrentDark;
        foreach (var r in _rows)
        {
            r.Detail.Text = Status(r);
            r.Detail.ForeColor = r.Open ? Theme.Warn : Theme.Sub(dark);
            if (r.InstallFirst != null) r.InstallFirst.Visible = r.Box.Checked && r.Target == null;
        }
        _list.Fit();
        _restoreSecrets.Visible = HasSecrets && !_done;
        bool canRun = !_done || _lines.Any(l => l.State == LineState.Retry) || _error.Text.Length > 0;
        _pass.Visible = NeedsPassphrase && canRun;
        _error.Visible = _error.Text.Length > 0;
        _note.Visible = !_done;
        _checkAgain.Visible = !_done && _rows.Any(r => r.Open);
        bool locked = _locked();
        _restore.Enabled = !locked && (_launcher?.Checked == true || _rows.Any(r => r.Box.Checked))
                           && (!NeedsPassphrase || _pass.Box.Text.Length > 0);
        if (locked && !_done) { _error.Text = "Restoring is paused while show lock is on."; _error.Visible = true; }
        _options.PerformLayout();
    }

    private void SetLine(BackupLine line)
    {
        int i = _lines.FindIndex(l => l.Id == line.Id);
        if (i >= 0) _lines[i] = line; else _lines.Add(line);
        if (line.State != LineState.Retry) _finished.Add(line.Id);
    }

    private void ShowResults()
    {
        _done = true;
        _list.Visible = false;
        _reader.Visible = true;
        BackupResults.Show(_reader, _lines, null);
        bool retry = _lines.Any(l => l.State == LineState.Retry);
        _restore.Text = "Try Again";
        _restore.Visible = retry || _error.Text.Length > 0;
        _cancel.Text = "Done";
        _busy = false;
        _cancel.Enabled = true;
        UpdateState();
        ActiveControl = _cancel;
    }

    private async Task RunAsync()
    {
        _error.Text = "";
        string? pass = _pass.Box.Text.Length > 0 ? _pass.Box.Text : null;
        bool secrets = _restoreSecrets.Checked;
        // A wrong passphrase stops before anything changes (checked on the launcher's own sealed passwords).
        if (_launcher?.Checked == true && _launcherData != null && !_finished.Contains(SuiteBackup.LauncherId))
        {
            try { LauncherSettings.VerifyPassphrase(_launcherData, pass, secrets); }
            catch (SuiteBackup.Failure ex) { _error.Text = ex.Message; UpdateState(); return; }
        }
        _busy = true;
        _restore.Enabled = _cancel.Enabled = _checkAgain.Enabled = false;
        _note.Visible = true;
        void Say(string s) { _note.Text = s; _note.ForeColor = Theme.Sub(Theme.CurrentDark); }
        RefreshOpen();
        foreach (var r in _rows)
        {
            if (!r.Box.Checked || _finished.Contains(r.App.Id)) continue;
            var name = r.Catalog?.Name ?? r.App.Name;
            if (!r.Info.Readable || r.Info.AppId != r.App.Id)
            { SetLine(new BackupLine(r.App.Id, name, LineState.Failed, new() { "This part of the backup can't be read." })); continue; }
            if (r.Catalog == null)
            { SetLine(new BackupLine(r.App.Id, name, LineState.Skipped, new() { "This app isn't in this launcher's list." })); continue; }
            if (r.Target == null)
            {
                if (r.InstallFirst?.Checked != true)
                { SetLine(new BackupLine(r.App.Id, name, LineState.Skipped, new() { "Not installed here, so it was skipped." })); continue; }
                Say($"Installing {name}…");
                var edition = r.App.Edition != null && r.Catalog.Variants?.Any(v => v.Id == r.App.Edition) == true ? r.App.Edition : null;
                try { await _install(r.Catalog.Id, edition); } catch (Exception ex) { Log.Write($"restore install {r.App.Id}: {ex.Message}"); }
                r.Target = SuiteBackup.Targets(new[] { r.Catalog.ToBackupApp() }, InstallManager.Shared.InstalledVersion,
                                               InstallManager.Shared.InstalledPath).FirstOrDefault();
                if (r.Target == null)
                {
                    SetLine(new BackupLine(r.App.Id, name, LineState.Failed,
                        new() { "It couldn't be installed, so its settings weren't restored. Install it from the list, then restore again." }));
                    continue;
                }
            }
            var target = r.Target;
            if (target.Slots.Any(s => InstallManager.Shared.RunningInstances(s.InstallKey).Length > 0))
            {
                r.Open = true;
                SetLine(new BackupLine(r.App.Id, name, LineState.Retry, new() { $"{name} is open. Quit it, then press Try Again." }));
                continue;
            }
            Say($"Restoring {name}…");
            var outcome = await Task.Run(() => SuiteBackup.RestoreAppAsync(r.File, target.Slots, pass, secrets));
            if (outcome.State == ImportState.Passphrase)
            {
                // Nothing changed for this app; ask again and carry on from here.
                _error.Text = outcome.Message ?? "That passphrase doesn't open this backup.";
                _busy = false;
                _cancel.Enabled = _checkAgain.Enabled = true;
                if (_lines.Count > 0) ShowResults(); else { _note.Text = ""; UpdateState(); }
                return;
            }
            Log.Write($"restore {r.App.Id}: {outcome.State}");
            SetLine(SuiteBackup.Line(r.App.Id, name, outcome));
        }
        if (_launcher?.Checked == true && _launcherData != null && !_finished.Contains(SuiteBackup.LauncherId))
        {
            Say("Restoring JB Theatre Tools…");
            try
            {
                var o = LauncherSettings.Restore(_launcherData, new WinLauncherPrefs(_settings), new CredentialLauncherSecrets(), pass, secrets);
                _settings.Save();
                LauncherRestored = true;
                SetLine(new BackupLine(SuiteBackup.LauncherId, SuiteBackup.LauncherName,
                    o.Attention.Count == 0 ? LineState.Ok : LineState.Attention,
                    SettingsCli.Lines(new JsonArray(o.Attention.Select(a => (JsonNode?)a.DeepClone()).ToArray())).Concat(o.Warnings).ToList()));
                Log.Write("launcher settings restored from a backup");
            }
            catch (Exception ex)
            {
                SetLine(new BackupLine(SuiteBackup.LauncherId, SuiteBackup.LauncherName, LineState.Failed, new() { ex.Message }));
            }
        }
        _pass.Box.Text = "";
        ShowResults();
    }
}
