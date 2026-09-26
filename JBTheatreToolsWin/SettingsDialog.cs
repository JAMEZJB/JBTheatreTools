namespace JBTheatreTools;

/// <summary>What Settings needs from the main window for the Storage and Support panels.</summary>
public sealed record SettingsExtras(
    Func<Task<(long Installed, long Cache)>> Storage,
    Func<bool> CanClearCache,
    Func<Task> ClearCache,
    Action<IWin32Window> CopyDiagnostics);   // owner: the Settings dialog, so its message sits on top

/// <summary>Settings, laid out like the macOS Settings sheet (SettingsView.swift): two equal columns of rounded panels,
/// each under a caps micro-label, with the same grouping — Download access, Updates, Appearance, When I close the window
/// and Quick launch on the left; Show lock, When updates are found, Install location, Storage and Support on the right —
/// and a bottom bar with Open Log / Reset App Order / Show Hidden Apps and Done. The window is as tall as the panels
/// need, up to the screen's working area; beyond that the panel area scrolls (a 1366×768 laptop) and Done stays in view.
///
/// Layout is done here in device pixels from the dialog's DeviceDpi (every number is a 96-DPI design value through S()),
/// re-run whenever something changes height — the auth mode, a status line, the launcher check's result, the Development
/// builds reveal — so a hidden line never leaves a gap. Tab order follows the reading order: the left column, the right
/// column, then the bottom bar with Done last.</summary>
public sealed class SettingsDialog : Form
{
    /// <summary>The user pressed Update (launcher update): the main window runs it once Settings has closed.</summary>
    public bool RequestedLauncherUpdate { get; private set; }

    private readonly AppSettings _settings;
    private readonly SelfInfo? _selfInfo;
    private readonly string _currentVersion;

    private readonly HouseComboBox _authMode = new();
    // The two secret fields are house text fields (a sunken well, not the stock white box); _token / _serverPass are
    // the TextBoxes inside them, so masking, Clear() and Text work as before.
    private readonly HouseTextField _tokenField = new();
    private TextBox _token => _tokenField.Box;
    private readonly Label _tokenState = new();
    private readonly HouseButton _save = new() { AutoSize = true };
    private readonly HouseButton _remove = new() { AutoSize = true };
    private readonly LinkLabel _tokenLink = new();
    private readonly Label _tokenHelp = new();
    private readonly Label _serverState = new();
    private readonly HouseTextField _serverPassField = new();
    private TextBox _serverPass => _serverPassField.Box;
    private readonly Label _serverHint = new();    // forgiving-entry note (case & spaces don't matter)
    private readonly Label _serverRelay = new();   // read-only effective relay host (audit F2)
    private readonly HouseButton _serverSave = new() { AutoSize = true };
    private readonly HouseButton _serverRemove = new() { AutoSize = true };
    private readonly HouseComboBox _updateMode = new();
    private readonly Label _updateHint = new();
    private readonly Label _intervalLabel = new();   // "While open, check again" — under the check-mode dropdown
    private readonly HouseComboBox _interval = new();
    private readonly Label _versionLabel = new();
    private readonly HouseButton _check = new() { AutoSize = true };
    private readonly HouseButton _viewRelease = new(HouseRole.Primary) { AutoSize = true };
    private readonly Label _checkResult = new();
    private readonly HouseCheckBox _devChannel = new();
    // Appearance is a genuine System / Light / Dark switch: the house segmented control, as on the mac.
    private readonly HouseSegmented _appearance = new(new[] { "System", "Light", "Dark" }, large: true) { AccessibleName = "Appearance" };
    private readonly HouseComboBox _closeBehavior = new();
    private readonly Label _closeHint = new();
    private readonly HouseCheckBox _installToApps = new();
    private readonly Label _installHint = new();

    private readonly string? _downloadServer;

    // Right-hand column: show lock, when updates are found, install location, storage, support.
    private readonly HouseCheckBox _showLock = new();
    private readonly HouseCheckBox _notify = new();
    private readonly HouseCheckBox _autoInstall = new();
    private readonly HouseCheckBox _tray = new();
    private readonly Label _storageInstalledLabel = new() { Text = "Installed apps" };
    private readonly Label _storageInstalled = new();
    private readonly Label _storageCacheLabel = new() { Text = "Download cache" };
    private readonly Label _storageCache = new();
    private readonly Label _storageNote = new();
    private readonly HouseButton _clearCache = new() { AutoSize = true };
    private readonly HouseButton _diag = new() { AutoSize = true };
    private readonly SettingsExtras? _extras;

    // Bottom bar.
    private readonly HouseButton _openLog = new() { Text = "Open Log", AutoSize = true };
    private readonly HouseButton _resetOrder = new() { Text = "Reset App Order", AutoSize = true };
    private readonly HouseButton _showHidden = new() { AutoSize = true };
    private readonly HouseButton _done = new(HouseRole.Primary) { Text = "Done", DialogResult = DialogResult.OK };

    // Layout: the scrolling panel area, the two columns of sections, and the bar.
    private readonly Panel _scroll = new() { AutoScroll = true };
    private readonly Panel _content = new();
    private readonly Panel _bar = new();
    private readonly List<Section> _left = new(), _right = new();
    /// <summary>Hint / secondary prose and values (the Sub tone), recoloured by ApplyTheme.</summary>
    private readonly List<Label> _subLabels = new();
    /// <summary>What each control should show as, independent of the form being on screen yet (Control.Visible reads
    /// false for every child until the dialog is shown, so the layout can't go by it).</summary>
    private readonly Dictionary<Control, bool> _shown = new();
    /// <summary>Controls named by the line builders since the last section — NewSection moves them into its panel.</summary>
    private readonly List<Control> _pendingControls = new();
    private readonly List<Font> _fonts = new();
    private int _fontDpi;
    private bool _built, _fitting;
    private int S(int v) => Theme.Px(v, DeviceDpi);

    /// <summary>One panel: its heading, its rounded body and its lines. A line places its controls at (x, y) within
    /// width w and returns the height it used — 0 when it shows nothing, and then it takes no gap either.</summary>
    private sealed record Section(HouseHeading Heading, HousePanel Body, List<Func<int, int, int, int>> Lines);

    public SettingsDialog(AppSettings settings, SelfInfo? selfInfo, string currentVersion, string? downloadServer,
                          SettingsExtras? extras = null)
    {
        _extras = extras;
        _settings = settings;
        _selfInfo = selfInfo;
        _currentVersion = currentVersion;
        _downloadServer = downloadServer;

        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        // DPI: this dialog lays itself out (LayoutAll) in device pixels from DeviceDpi, so the framework's AutoScale
        // pass is off — one source of truth, as in the main window (see Theme.Px).
        AutoScaleMode = AutoScaleMode.None;
        SuspendLayout();

        // --- Download access (auth mode + per-mode credentials) ---
        _authMode.Items.AddRange(new object[] { "GitHub token", "Download server" });
        _authMode.SelectedIndex = _settings.AuthMode == "server" ? 1 : 0;
        _authMode.AccessibleName = "Downloads via";
        _authMode.SelectedIndexChanged += (_, _) =>
        {
            _settings.AuthMode = _authMode.SelectedIndex == 1 ? "server" : "token";
            UpdateAuthPanels();
        };

        _token.UseSystemPasswordChar = true;
        _token.AccessibleName = "GitHub token";
        _tokenField.Placeholder = "Paste a fine-grained PAT…";

        _save.Text = "Save";
        _save.Click += (_, _) =>
        {
            var t = _token.Text.Trim();
            if (t.Length == 0) return;
            TokenStore.Save(t);
            _token.Clear();
            UpdateTokenState();
        };

        _remove.Text = "Remove";
        _remove.Click += (_, _) =>
        {
            if (HouseMessage.Show(this,
                    "Remove the saved token? You'll need to paste one again before you can install or update apps.",
                    "Remove token", MessageBoxButtons.YesNo, MessageBoxIcon.Question, destructive: true) != DialogResult.Yes) return;
            TokenStore.Clear();
            UpdateTokenState();
        };

        _tokenLink.Text = "Create a fine-grained token on GitHub →";
        _tokenLink.AutoSize = true;
        _tokenLink.LinkBehavior = LinkBehavior.HoverUnderline;
        _tokenLink.LinkClicked += (_, _) => OpenUrl("https://github.com/settings/personal-access-tokens/new");

        _tokenHelp.Text = "Contents: Read-only. Only repos it can access appear here.";

        // Server mode: the relay URL is built-in (catalog `downloadServer`, with an invisible settings.json override) —
        // the user only ever enters the passphrase.
        _serverPass.UseSystemPasswordChar = true;
        _serverPass.AccessibleName = "Suite passphrase";
        _serverPassField.Placeholder = "Suite passphrase…";

        _serverSave.Text = "Save";
        _serverSave.Click += (_, _) =>
        {
            var pass = _serverPass.Text.Trim();
            if (pass.Length == 0 || AuthClient.ResolveServerUrl(_settings, _downloadServer) == null) return;
            TokenStore.SaveServerPass(pass);
            _serverPass.Clear();
            UpdateServerState();
        };

        _serverRemove.Text = "Remove";
        _serverRemove.Click += (_, _) =>
        {
            if (HouseMessage.Show(this,
                    "Remove the server passphrase? You'll need to enter it again before you can install or update apps.",
                    "Remove passphrase", MessageBoxButtons.YesNo, MessageBoxIcon.Question, destructive: true) != DialogResult.Yes) return;
            TokenStore.ClearServerPass();
            UpdateServerState();
        };

        // Forgiving-entry note: the passphrase is normalised (case- and spacing-insensitive) before it's sent.
        _serverHint.Text = "Capitalisation and spaces don't matter — type the phrase however you like.";

        // Surface the effective relay host read-only (audit F2), so a non-default override is visible.
        var relay = AuthClient.ResolveServerUrl(_settings, _downloadServer);
        _serverRelay.Text = Uri.TryCreate(relay, UriKind.Absolute, out var relayUri) ? $"Relay: {relayUri.Host}" : "";

        // --- Updates ---
        _updateMode.Items.AddRange(new object[] { "Every launch", "Manual only", "Never" });
        _updateMode.SelectedIndex = _settings.UpdateMode switch { "manual" => 1, "never" => 2, _ => 0 };
        _updateMode.AccessibleName = "Check for updates";
        _updateMode.SelectedIndexChanged += (_, _) =>
        {
            _settings.UpdateMode = _updateMode.SelectedIndex switch { 1 => "manual", 2 => "never", _ => "everyLaunch" };
            _updateHint.Text = UpdateHint();
            UpdateIntervalEnabled();
            LayoutAll();
        };
        _updateHint.Text = UpdateHint();

        // "While open, check again": right under the check-mode dropdown (parity with the mac Settings), enabled only in
        // "Every launch" mode — scheduled checks never run otherwise (see UpdateIntervalEnabled).
        _intervalLabel.Text = "While open, check again";
        foreach (var (_, label) in UpdatePolicy.Intervals) _interval.Items.Add(label);
        int intervalIdx = Array.FindIndex(UpdatePolicy.Intervals, i => i.Raw == _settings.AutoCheckInterval);
        // An unknown saved value reads as the default interval (as UpdatePolicy.Interval does), not the first item.
        if (intervalIdx < 0) intervalIdx = Array.FindIndex(UpdatePolicy.Intervals, i => i.Raw == UpdatePolicy.DefaultInterval);
        _interval.SelectedIndex = Math.Max(0, intervalIdx);
        _interval.AccessibleName = "While open, check again";
        _interval.SelectedIndexChanged += (_, _) =>
            _settings.AutoCheckInterval = UpdatePolicy.Intervals[Math.Max(0, _interval.SelectedIndex)].Raw;

        _versionLabel.Text = $"JB Theatre Tools v{_currentVersion}";
        // Hidden Dev channel: seven clicks on the version within three seconds reveal "Development builds" (like
        // Android's developer options). Off by default; a normal user never sees it, and nothing about the reveal is
        // stored (see the end of the constructor).
        var versionClicks = new List<DateTime>();
        _versionLabel.Click += (_, _) =>
        {
            var now = DateTime.UtcNow;
            versionClicks.RemoveAll(t => (now - t).TotalSeconds >= 3);
            versionClicks.Add(now);
            if (versionClicks.Count >= 7) RevealDevChannel();
        };
        _devChannel.Text = "Development builds on this PC (pre-releases marked “dev”)";
        _devChannel.Checked = _settings.DevChannel;
        _devChannel.CheckedChanged += (_, _) => _settings.DevChannel = _devChannel.Checked;

        _check.Text = "Check for Updates";
        _check.Click += async (_, _) =>
        {
            try { await CheckLauncherAsync(); }
            catch (Exception ex) { Log.Write($"settings: launcher check failed: {ex.Message}"); }
        };

        _viewRelease.Text = "Update";
        // The update restarts the launcher, so it runs from the main window: Settings closes (keeping its changes) and
        // hands over — the main window's banner shows the download and its outcome.
        _viewRelease.Click += (_, _) =>
        {
            if (_selfInfo == null) return;
            RequestedLauncherUpdate = true;
            DialogResult = DialogResult.OK;
        };

        // --- Appearance ---
        _appearance.SelectedIndex = _settings.Appearance switch { "light" => 1, "dark" => 2, _ => 0 };
        _appearance.SelectedIndexChanged += (_, _) =>
            _settings.Appearance = _appearance.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };

        // --- Close behaviour ---
        _closeBehavior.Items.AddRange(new object[] { "Quit the app", "Keep running in the tray" });
        _closeBehavior.SelectedIndex = _settings.CloseBehavior == "keepRunning" ? 1 : 0;
        _closeBehavior.AccessibleName = "When I close the window";
        _closeBehavior.SelectedIndexChanged += (_, _) =>
        {
            _settings.CloseBehavior = _closeBehavior.SelectedIndex == 1 ? "keepRunning" : "quit";
            _closeHint.Text = CloseHint();
            LayoutAll();
        };
        _closeHint.Text = CloseHint();

        // --- Quick launch ---
        _tray.Text = "Always show the icon in the notification area";
        _tray.Checked = _settings.AlwaysShowTray;
        _tray.CheckedChanged += (_, _) => _settings.AlwaysShowTray = _tray.Checked;

        // --- Show lock ---
        _showLock.Text = "Pause installs, updates and uninstalls";
        _showLock.Checked = _settings.ShowLock;
        _showLock.CheckedChanged += (_, _) =>
        {
            // Turning the lock OFF asks, as everywhere else (a stray click mustn't re-enable installs mid-show).
            if (!_showLock.Checked && _settings.ShowLock && !DialogKit.ConfirmTurnOffShowLock(this))
            {
                _showLock.Checked = true;   // re-enters with Checked == true: no second prompt
                return;
            }
            _settings.ShowLock = _showLock.Checked;
            UpdateCacheButton();
        };

        // --- When updates are found (the check itself — mode and interval — is under Updates) ---
        _notify.Text = "Notify me when updates are available";
        _notify.Checked = _settings.NotifyUpdates;
        _notify.CheckedChanged += (_, _) => _settings.NotifyUpdates = _notify.Checked;
        _autoInstall.Text = "Install updates automatically";
        _autoInstall.Checked = _settings.AutoInstallUpdates;
        _autoInstall.CheckedChanged += (_, _) => _settings.AutoInstallUpdates = _autoInstall.Checked;

        // --- Install location ---
        _installToApps.Text = "Install apps to the Start menu & Desktop";
        _installToApps.Checked = _settings.InstallToApplications;
        _installToApps.CheckedChanged += (_, _) => _settings.InstallToApplications = _installToApps.Checked;
        _installHint.Text = "Adds shortcuts for each app you install, so you can launch it without this launcher.";

        // --- Storage ---
        _storageInstalled.Text = _storageCache.Text = _extras == null ? "—" : "Calculating…";
        _storageInstalled.TextAlign = _storageCache.TextAlign = ContentAlignment.TopRight;
        _storageNote.Text = "The cache holds leftover downloads; clearing it never touches installed apps.";
        _clearCache.Text = "Clear Download Cache";
        _clearCache.Click += async (_, _) =>
        {
            try
            {
                if (_extras == null || !_extras.CanClearCache()) { UpdateCacheButton(); return; }
                _clearCache.Enabled = false;
                await _extras.ClearCache();
                await RefreshStorageAsync();
                UpdateCacheButton();
            }
            catch (Exception ex) { Log.Write($"settings: clear cache failed: {ex.Message}"); UpdateCacheButton(); }
        };

        // --- Support ---
        _diag.Text = "Copy Diagnostics";
        _diag.Click += (_, _) => _extras?.CopyDiagnostics(this);

        // --- Bottom bar ---
        _openLog.Click += (_, _) => Log.Open();
        // Clears the saved order; MainForm re-applies (→ catalog order) when the dialog closes.
        _resetOrder.Click += (_, _) => _settings.AppOrder.Clear();
        _showHidden.Text = _settings.HiddenApps.Count > 0 ? $"Show Hidden Apps ({_settings.HiddenApps.Count})" : "Show Hidden Apps";
        _showHidden.Enabled = _settings.HiddenApps.Count > 0;
        // Un-hides everything; MainForm re-applies when the dialog closes.
        _showHidden.Click += (_, _) => { _settings.HiddenApps.Clear(); _showHidden.Enabled = false; _showHidden.Text = "Show Hidden Apps"; LayoutBar(); };
        AcceptButton = _done;

        BuildSections();
        SetShown(_viewRelease, false);
        SetShown(_clearCache, _extras != null);
        SetShown(_diag, _extras != null);
        // Shown only while dev mode is ON (or after the gesture above, this session only): switch it off and the
        // checkbox is gone next time Settings opens.
        SetShown(_devChannel, _settings.DevChannel);

        // Tab order = reading order: the panel area (left column, then right), then the bar with Done last.
        _scroll.Controls.Add(_content);
        _bar.Controls.AddRange(new Control[] { _openLog, _resetOrder, _showHidden, _done });
        for (int i = 0; i < _bar.Controls.Count; i++) _bar.Controls[i].TabIndex = i;
        _bar.Paint += (_, e) => { using var pen = new Pen(Theme.Line(Theme.CurrentDark)); e.Graphics.DrawLine(pen, 0, 0, _bar.Width, 0); };
        _bar.Resize += (_, _) => LayoutBar();
        _scroll.ClientSizeChanged += (_, _) => LayoutColumns();   // a scrollbar appearing narrows the columns
        _scroll.HandleCreated += (_, _) => HouseDraw.NativeTheme(_scroll, Theme.CurrentDark);
        Controls.Add(_scroll);
        Controls.Add(_bar);
        _scroll.TabIndex = 0;
        _bar.TabIndex = 1;

        UpdateServerState();
        UpdateAuthPanels();
        UpdateIntervalEnabled();

        _built = true;
        ApplyDpi();
        SizeToContent(Screen.PrimaryScreen?.WorkingArea);   // a first guess; OnLoad fits it to the dialog's own screen
        ResumeLayout(false);

        // ApplyTheme runs before the window exists: give the title bar its theme once there is one.
        HandleCreated += (_, _) => Theme.ApplyTitleBar(this, Theme.CurrentDark);
        Shown += async (_, _) =>
        {
            Theme.ApplyTitleBar(this, Theme.CurrentDark);
            try { await RefreshStorageAsync(); UpdateCacheButton(); }
            catch (Exception ex) { Log.Write($"settings: storage failed: {ex.Message}"); }
        };
    }

    // ── Sections ───────────────────────────────────────────────────────────────────────────────────────────────

    private void BuildSections()
    {
        bool Token() => _settings.AuthMode != "server";
        bool Server() => _settings.AuthMode == "server";
        _left.Add(NewSection("Download access",
            Fixed(_authMode),
            Full(_tokenState, Token), Full(_tokenField, Token), Row(Token, _save, _remove),
            Full(_tokenLink, Token), Full(_tokenHelp, Token),
            Full(_serverState, Server), Full(_serverPassField, Server), Full(_serverHint, Server),
            Row(Server, _serverSave, _serverRemove), Full(_serverRelay, () => Server() && _serverRelay.Text.Length > 0)));
        _left.Add(NewSection("Updates",
            Fixed(_updateMode), Full(_updateHint), Split(_intervalLabel, _interval),
            Rule(), Split(_versionLabel, _check), CheckResult(), Full(_devChannel)));
        _left.Add(NewSection("Appearance", Fixed(_appearance)));
        _left.Add(NewSection("When I close the window", Fixed(_closeBehavior), Full(_closeHint)));
        _left.Add(NewSection("Quick launch", Full(_tray),
            Full(Hint("Right-click the icon to launch any installed app, or check for updates."))));

        _right.Add(NewSection("Show lock", Full(_showLock),
            Full(Hint("For show time: launching still works, nothing changes underneath you. Ctrl+L in the main window."))));
        _right.Add(NewSection("When updates are found", Full(_notify), Full(_autoInstall),
            Full(Hint("Held apps and apps that are open are left alone, and nothing installs during show lock."))));
        _right.Add(NewSection("Install location", Full(_installToApps), Full(_installHint)));
        _right.Add(NewSection("Storage", Split(_storageInstalledLabel, _storageInstalled), Split(_storageCacheLabel, _storageCache),
            Row(null, _clearCache), Full(_storageNote)));
        _right.Add(NewSection("Support", Row(null, _diag),
            Full(Hint("Versions, settings and recent log lines for a support question — never passwords or tokens."))));

        _subLabels.AddRange(new[] { _tokenHelp, _serverHint, _serverRelay, _updateHint, _checkResult, _closeHint, _installHint,
                                    _storageInstalled, _storageCache, _storageNote });
        foreach (var l in new[] { _tokenState, _serverState, _serverHint, _serverRelay, _tokenHelp, _updateHint, _intervalLabel,
                                  _versionLabel, _checkResult, _closeHint, _installHint, _storageInstalledLabel, _storageInstalled,
                                  _storageCacheLabel, _storageCache, _storageNote })
        {
            l.AutoSize = false;
            l.UseMnemonic = false;
        }

        // The left column's panels, then the right column's: the tab order runs down one column, then the other.
        int tab = 0;
        foreach (var s in _left.Concat(_right))
        {
            _content.Controls.Add(s.Heading);
            _content.Controls.Add(s.Body);
            s.Heading.TabIndex = tab++;
            s.Body.TabIndex = tab++;
            for (int i = 0; i < s.Body.Controls.Count; i++) s.Body.Controls[i].TabIndex = i;
        }
    }

    private Label Hint(string text)
    {
        var l = new Label { Text = text, AutoSize = false, UseMnemonic = false };
        _subLabels.Add(l);
        return l;
    }

    private Section NewSection(string title, params Func<int, int, int, int>[] lines)
    {
        var body = new HousePanel();
        // The lines' controls live in the panel, in line order (= the tab order within the panel).
        foreach (var c in _pendingControls) body.Controls.Add(c);
        _pendingControls.Clear();
        return new Section(new HouseHeading(title), body, lines.ToList());
    }

    private void Own(params Control[] controls)
    {
        foreach (var c in controls)
        {
            _pendingControls.Add(c);
            _shown.TryAdd(c, true);
        }
    }

    private bool IsShown(Control c) => !_shown.TryGetValue(c, out var v) || v;

    private void SetShown(Control c, bool show)
    {
        _shown[c] = show;
        c.Visible = show;
    }

    /// <summary>The height <paramref name="c"/> wants at width <paramref name="w"/>: wrapping text (labels, check
    /// boxes) is measured at that width; everything else keeps its own height.</summary>
    private static int WantHeight(Control c, int w) => c switch
    {
        Label { AutoSize: false } l => l.Text.Length == 0 ? 0 : l.GetPreferredSize(new Size(w, 0)).Height,
        HouseCheckBox cb => cb.GetPreferredSize(new Size(w, 0)).Height,
        HouseButton { AutoSize: true } b => b.GetPreferredSize(Size.Empty).Height,
        LinkLabel { AutoSize: true } link => link.PreferredSize.Height,
        _ => c.Height,
    };

    /// <summary>A control across the panel's inner width (wrapping text, text fields, check boxes).</summary>
    private Func<int, int, int, int> Full(Control c, Func<bool>? when = null)
    {
        Own(c);
        return (x, y, w) =>
        {
            bool show = (when?.Invoke() ?? true) && IsShown(c);
            c.Visible = show;
            if (!show) return 0;
            if (c is LinkLabel { AutoSize: true }) { c.Location = new Point(x, y); return c.Height; }
            int h = c is HouseTextField ? S(28) : WantHeight(c, w);
            c.Bounds = new Rectangle(x, y, w, h);
            return h;
        };
    }

    /// <summary>Every dropdown in a column is this wide (220 px, less in a narrow column).</summary>
    private int DropdownWidth(int w) => Math.Min(S(220), w * 3 / 5);

    /// <summary>A dropdown / segmented control at the left edge; every dropdown in a column shares one width.</summary>
    private Func<int, int, int, int> Fixed(Control c)
    {
        Own(c);
        return (x, y, w) =>
        {
            if (c is HouseComboBox) c.Width = DropdownWidth(w);
            c.Location = new Point(x, y);
            return c.Height;
        };
    }

    /// <summary>A label on the left and a control (or a value) right-aligned at the panel's inner edge, on one centre
    /// line; a dropdown here gets the column's dropdown width.</summary>
    private Func<int, int, int, int> Split(Label label, Control right)
    {
        Own(label, right);
        return (x, y, w) =>
        {
            if (right is HouseComboBox) right.Width = DropdownWidth(w);
            if (right is HouseButton { AutoSize: true } b) b.Size = b.GetPreferredSize(Size.Empty);
            if (right is Label { AutoSize: false } value) value.Width = Math.Max(S(40), w / 2);
            int labelW = Math.Max(S(40), w - right.Width - S(12));
            int lh = WantHeight(label, labelW), rh = right is Label rl ? WantHeight(rl, right.Width) : right.Height;
            int h = Math.Max(lh, rh);
            label.Bounds = new Rectangle(x, y + (h - lh) / 2, labelW, lh);
            right.Bounds = new Rectangle(x + w - right.Width, y + (h - rh) / 2, right.Width, rh);
            return h;
        };
    }

    /// <summary>Buttons left to right, 8 apart (hidden ones take no room); <paramref name="when"/> gates the line.</summary>
    private Func<int, int, int, int> Row(Func<bool>? when, params HouseButton[] buttons)
    {
        Own(buttons);
        return (x, y, w) =>
        {
            bool line = when?.Invoke() ?? true;
            int h = 0, cx = x;
            foreach (var b in buttons)
            {
                bool show = line && IsShown(b);
                b.Visible = show;
                if (!show) continue;
                b.Size = b.GetPreferredSize(Size.Empty);
                b.Location = new Point(cx, y);
                cx = b.Right + S(8);
                h = Math.Max(h, b.Height);
            }
            return h;
        };
    }

    /// <summary>A hairline across the panel (the mac Divider between the checks and the launcher's own version).</summary>
    private Func<int, int, int, int> Rule()
    {
        var line = new Panel { Height = 1, Tag = "rule", TabStop = false };
        Own(line);
        return (x, y, w) =>
        {
            line.Bounds = new Rectangle(x, y + S(2), w, 1);
            return S(5);
        };
    }

    /// <summary>The launcher check's outcome: "vX is available. [Update]" on one line, or a wrapping status line.</summary>
    private Func<int, int, int, int> CheckResult()
    {
        Own(_checkResult, _viewRelease);
        return (x, y, w) =>
        {
            if (_checkResult.Text.Length == 0) { _checkResult.Visible = _viewRelease.Visible = false; return 0; }
            _checkResult.Visible = true;
            bool update = IsShown(_viewRelease);
            _viewRelease.Visible = update;
            if (!update)
            {
                int h = WantHeight(_checkResult, w);
                _checkResult.Bounds = new Rectangle(x, y, w, h);
                return h;
            }
            _viewRelease.Size = _viewRelease.GetPreferredSize(Size.Empty);
            int textW = Math.Max(S(40), w - _viewRelease.Width - S(10));
            int th = WantHeight(_checkResult, textW), rh = Math.Max(th, _viewRelease.Height);
            _checkResult.Bounds = new Rectangle(x, y + (rh - th) / 2, textW, th);
            _viewRelease.Location = new Point(x + w - _viewRelease.Width, y + (rh - _viewRelease.Height) / 2);
            return rh;
        };
    }

    // ── Layout ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Builds the dialog's fonts for its current DPI — the form font every label, check box, dropdown and text
    /// field inherits (body, 13px), and the t-small step (12px) for the secondary prose — then re-lays everything out.</summary>
    private void ApplyDpi()
    {
        if (_fontDpi != DeviceDpi)
        {
            _fontDpi = DeviceDpi;
            var old = _fonts.ToList();
            _fonts.Clear();
            var body = Theme.Ui(Theme.PtBody, HouseWeight.Regular, _fontDpi);
            var small = Theme.Ui(Theme.PtSmall, HouseWeight.Regular, _fontDpi);
            _fonts.Add(body);
            _fonts.Add(small);
            Font = body;
            foreach (var l in _subLabels) if (l != _storageInstalled && l != _storageCache) l.Font = small;
            _tokenLink.Font = small;
            foreach (var combo in new[] { _authMode, _updateMode, _interval, _closeBehavior }) combo.ItemHeight = S(20);
            foreach (var s in _left.Concat(_right)) s.Heading.Rescale();
            _appearance.Rescale();
            // Control.Font keeps the old instance when the new one Equals it: never dispose a font that is still in use.
            foreach (var f in old)
                if (!ReferenceEquals(Font, f) && !ReferenceEquals(_tokenLink.Font, f) && !_subLabels.Any(l => ReferenceEquals(l.Font, f)))
                    f.Dispose();
        }
        LayoutAll();
    }

    /// <summary>Lays out the columns and the bar, then fits the window's height (cheap; run after anything that changes
    /// a line's height).</summary>
    private void LayoutAll()
    {
        if (!_built) return;
        LayoutColumns();
        LayoutBar();
        FitHeight();
    }

    private void LayoutColumns()
    {
        if (!_built) return;
        _content.SuspendLayout();
        int pad = S(20), gap = S(16);
        int width = _scroll.ClientSize.Width;
        int colW = Math.Max(S(200), (width - 2 * pad - gap) / 2);
        int leftBottom = LayoutColumn(_left, pad, pad, colW);
        int rightBottom = LayoutColumn(_right, pad + colW + gap, pad, colW);
        _content.Bounds = new Rectangle(_content.Left, _content.Top, width, Math.Max(leftBottom, rightBottom) + pad);
        _content.ResumeLayout(false);
    }

    /// <summary>Stacks a column's sections from <paramref name="y"/>: heading, 6 px, the panel (12 px padding, 8 px
    /// between lines), 16 px to the next heading. Returns the column's bottom.</summary>
    private int LayoutColumn(List<Section> sections, int x, int y, int w)
    {
        int inner = S(12), lineGap = S(8), headGap = S(6), sectionGap = S(16);
        foreach (var s in sections)
        {
            s.Heading.Bounds = new Rectangle(x + S(2), y, w - S(2), s.Heading.Height);
            y = s.Heading.Bottom + headGap;
            int py = inner, lines = 0;
            foreach (var place in s.Lines)
            {
                int gap = lines > 0 ? lineGap : 0;
                int h = place(inner, py + gap, w - 2 * inner);
                if (h <= 0) continue;
                py += gap + h;
                lines++;
            }
            s.Body.Bounds = new Rectangle(x, y, w, py + inner);
            y = s.Body.Bottom + sectionGap;
        }
        return y - sectionGap;
    }

    /// <summary>The bar: Open Log · Reset App Order · Show Hidden Apps on the left, Done on the right.</summary>
    private void LayoutBar()
    {
        if (!_built) return;
        int pad = S(20), h = _openLog.GetPreferredSize(Size.Empty).Height;
        _bar.Height = h + 2 * S(14);
        int y = (_bar.Height - h) / 2, x = pad;
        foreach (var b in new[] { _openLog, _resetOrder, _showHidden })
        {
            b.Size = b.GetPreferredSize(Size.Empty);
            b.Location = new Point(x, y);
            x = b.Right + S(8);
        }
        _done.Size = new Size(Math.Max(S(84), _done.GetPreferredSize(Size.Empty).Width), h);
        _done.Location = new Point(_bar.Width - pad - _done.Width, y);
    }

    /// <summary>The window: two 430 px columns wide (the mac sheet's width) and as tall as the panels need.</summary>
    private void SizeToContent(Rectangle? workingArea)
    {
        int width = S(2 * 430 + 16 + 2 * 20);
        ClientSize = new Size(width, ClientSize.Height);
        _scroll.Bounds = new Rectangle(0, 0, width, Math.Max(S(200), ClientSize.Height - _bar.Height));
        LayoutColumns();
        LayoutBar();
        FitHeight(workingArea);
    }

    /// <summary>Grows or shrinks the window to the panels' height — never past the working area of its screen, where
    /// the panel area scrolls instead — keeping the window on screen and the bar at the bottom.</summary>
    private void FitHeight(Rectangle? workingArea = null)
    {
        if (_fitting || !_built) return;
        var area = workingArea ?? (IsHandleCreated ? Screen.FromHandle(Handle).WorkingArea : (Rectangle?)null);
        if (area is not { } wa) return;
        _fitting = true;
        try
        {
            int chrome = Height - ClientSize.Height;   // the title bar and borders
            int want = _content.Height + _bar.Height;
            int h = Math.Min(want, Math.Max(S(360), wa.Height - chrome - S(16)));
            if (h != ClientSize.Height || _bar.Bottom != h)
            {
                ClientSize = new Size(ClientSize.Width, h);
                _bar.Bounds = new Rectangle(0, h - _bar.Height, ClientSize.Width, _bar.Height);
                _scroll.Bounds = new Rectangle(0, 0, ClientSize.Width, h - _bar.Height);
                if (IsHandleCreated && Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
            }
        }
        finally { _fitting = false; }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // The handle now exists on its real monitor: its DPI and working area are known.
        ApplyDpi();
        SizeToContent(Screen.FromHandle(Handle).WorkingArea);
        CenterToParent();
        ActiveControl = _authMode;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyDpi();
        SizeToContent(Screen.FromHandle(Handle).WorkingArea);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { foreach (var f in _fonts) f.Dispose(); _fonts.Clear(); }
    }

    // ── Behaviour ──────────────────────────────────────────────────────────────────────────────────────────────

    private async Task RefreshStorageAsync()
    {
        if (_extras == null) return;
        try
        {
            var (installed, cache) = await _extras.Storage();
            if (IsDisposed) return;
            _storageInstalled.Text = ByteSize.Format(installed);
            _storageCache.Text = ByteSize.Format(cache);
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            _storageInstalled.Text = _storageCache.Text = "—";
            _storageNote.Text = $"Couldn't measure storage: {ex.Message}";
        }
        LayoutAll();
    }

    /// <summary>Clearing the cache is off during show lock and while anything is downloading or installing.</summary>
    private void UpdateCacheButton() =>
        _clearCache.Enabled = _extras != null && !_settings.ShowLock && _extras.CanClearCache();

    /// <summary>Scheduled checks only run in "Every launch" mode — the interval is enabled only then. It stays in place
    /// (greyed: its label in the tertiary tone, not the system's etched disabled text) so the panel keeps its shape.</summary>
    private void UpdateIntervalEnabled()
    {
        bool on = _settings.UpdateMode == "everyLaunch";
        _interval.Enabled = on;
        _intervalLabel.ForeColor = on ? Theme.Fg(Theme.CurrentDark) : Theme.Muted(Theme.CurrentDark);
    }

    /// <summary>Shows the Development builds switch at the foot of the Updates panel (the window grows for it, or the
    /// panel area scrolls to it).</summary>
    private void RevealDevChannel()
    {
        if (IsShown(_devChannel)) return;
        SetShown(_devChannel, true);
        LayoutAll();
        _scroll.ScrollControlIntoView(_devChannel);
    }

    /// <summary>Shows the token lines or the server lines to match the selected auth mode.</summary>
    private void UpdateAuthPanels()
    {
        if (_settings.AuthMode != "server") UpdateTokenState(); else UpdateServerState();
        LayoutAll();
    }

    private void UpdateServerState()
    {
        bool has = AuthClient.ResolveServerUrl(_settings, _downloadServer) != null && TokenStore.LoadServerPass() != null;
        _serverState.Text = has
            ? "Passphrase saved in Credential Manager."
            : "Enter the suite passphrase (ask whoever set up your access) — downloads are disabled until you do.";
        _serverState.ForeColor = has ? Theme.Ok : Theme.Sub(Theme.CurrentDark);
        SetShown(_serverRemove, has);
        LayoutAll();
    }

    private async Task CheckLauncherAsync()
    {
        if (_selfInfo == null) { SetResult("No self-update info in catalog.", Theme.Sub(Theme.CurrentDark)); return; }
        _check.Enabled = false;
        SetShown(_viewRelease, false);
        SetResult("Checking…", Theme.Sub(Theme.CurrentDark));
        try
        {
            using var client = AuthClient.SelfUpdate(_settings, _downloadServer);   // public repo; never blocks on creds
            var info = await Versions.LauncherTargetAsync(client, _selfInfo.Owner, _selfInfo.Repo, _currentVersion);
            if (IsDisposed) return;
            if (info != null)
            {
                SetShown(_viewRelease, true);
                SetResult(Versions.IsNewer(info.TagName, _currentVersion) ? $"{info.TagName} is available." : $"Back to the release: {info.TagName}.", Theme.Info);
            }
            else
            {
                SetResult($"You're up to date (v{_currentVersion}).", Theme.Ok);
            }
        }
        catch (GitHubException ge) when (ge.Kind == GitHubErrorKind.NoRelease)
        {
            SetResult("No launcher release published yet.", Theme.Sub(Theme.CurrentDark));
        }
        catch (Exception ex)
        {
            SetResult(ex.Message, Theme.Sub(Theme.CurrentDark));
        }
        finally
        {
            if (!IsDisposed) _check.Enabled = true;
        }
    }

    private void SetResult(string text, Color color)
    {
        if (IsDisposed) return;
        _checkResult.Text = text;
        _checkResult.ForeColor = color;
        LayoutAll();
    }

    private string UpdateHint() => _settings.UpdateMode switch
    {
        "manual" => "Only checks when you press Refresh or Check for Updates.",
        "never" => "Never checks automatically. You can still install/update from the buttons.",
        _ => "Checks all apps and the launcher each time it opens.",
    };

    private string CloseHint() => _settings.CloseBehavior == "keepRunning"
        ? "Closing the window keeps JB Theatre Tools running in the notification area — double-click its icon to reopen it."
        : "Closing the window quits JB Theatre Tools.";

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { /* non-fatal */ }
    }

    private void UpdateTokenState()
    {
        bool has = TokenStore.Load() != null;
        _tokenState.Text = has
            ? "A token is saved in Credential Manager."
            : "No token saved — downloads are disabled until you add one.";
        _tokenState.ForeColor = has ? Theme.Ok : Theme.Sub(Theme.CurrentDark);
        SetShown(_remove, has);
        LayoutAll();
    }

    public void ApplyTheme(bool dark)
    {
        Theme.SetCurrent(dark);
        BackColor = Theme.Bg(dark);
        ForeColor = Theme.Fg(dark);
        _scroll.BackColor = _content.BackColor = _bar.BackColor = Theme.Bg(dark);
        // The panels are the Raised step (their children inherit it); prose inside them carries its own tone.
        foreach (var s in _left.Concat(_right))
        {
            s.Body.BackColor = Theme.Raised(dark);
            foreach (Control c in s.Body.Controls)
                if (c is Panel { Tag: "rule" } rule) rule.BackColor = Theme.Line(dark);
            s.Heading.Invalidate();
        }
        // The house dropdowns and buttons paint from the tokens; the dropdowns' open lists and the text fields' native
        // boxes need their colours set.
        foreach (var combo in new[] { _authMode, _updateMode, _interval, _closeBehavior }) combo.ApplyTheme(dark);
        _tokenField.ApplyTheme(dark);
        _serverPassField.ApplyTheme(dark);
        foreach (var l in _subLabels) l.ForeColor = Theme.Sub(dark);
        _tokenLink.LinkColor = _tokenLink.ActiveLinkColor = _tokenLink.VisitedLinkColor = Theme.Accent;
        HouseDraw.NativeTheme(_scroll, dark);
        UpdateIntervalEnabled();
        UpdateTokenState();
        UpdateServerState();
        if (IsHandleCreated) Theme.ApplyTitleBar(this, dark);
        Invalidate(true);
    }
}
