namespace JBTheatreTools;

/// <summary>What Settings needs from the main window for the Storage and Support panels.</summary>
public sealed record SettingsExtras(
    Func<Task<(long Installed, long Cache)>> Storage,
    Func<bool> CanClearCache,
    Func<Task> ClearCache,
    Action<IWin32Window> CopyDiagnostics);   // owner: the Settings dialog, so its message sits on top

public sealed class SettingsDialog : Form
{
    /// <summary>The user pressed Update (launcher update): the main window runs it once Settings has closed.</summary>
    public bool RequestedLauncherUpdate { get; private set; }

    private readonly AppSettings _settings;
    private readonly SelfInfo? _selfInfo;
    private readonly string _currentVersion;

    private readonly ComboBox _authMode = new();
    private readonly TextBox _token = new();
    private readonly Label _tokenState = new();
    private readonly Button _save = new();
    private readonly Button _remove = new();
    private readonly LinkLabel _tokenLink = new();
    private readonly Label _tokenHelp = new();
    private readonly Label _serverState = new();
    private readonly TextBox _serverPass = new();
    private readonly Label _serverHint = new();    // forgiving-entry note (case & spaces don't matter)
    private readonly Label _serverRelay = new();   // read-only effective relay host (audit F2)
    private readonly Button _serverSave = new();
    private readonly Button _serverRemove = new();
    private readonly ComboBox _updateMode = new();
    private readonly Label _updateHint = new();
    private readonly Label _intervalLabel = new();   // "While open, check again" — under the check-mode dropdown
    private readonly ComboBox _interval = new();
    private readonly Button _check = new();
    private readonly Button _viewRelease = new();
    private readonly Label _checkResult = new();
    private readonly CheckBox _devChannel = new();
    private readonly ComboBox _appearance = new();
    private readonly ComboBox _closeBehavior = new();
    private readonly CheckBox _installToApps = new();

    private readonly string? _downloadServer;

    // Right-hand column: show lock, when updates are found, quick launch, storage, support.
    private readonly CheckBox _showLock = new();
    private readonly CheckBox _notify = new();
    private readonly CheckBox _autoInstall = new();
    private readonly CheckBox _tray = new();
    private readonly Label _storage = new();
    private readonly Button _clearCache = new();
    private readonly List<Label> _hints = new();
    private readonly SettingsExtras? _extras;

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
        // DPI: this dialog is laid out ONCE at fixed 96-DPI positions (the designer pattern), so the
        // framework's AutoScale pass — which runs on the first layout, after ResumeLayout below — scales
        // the client size and every control here. Anything positioned later at runtime must go through
        // LogicalToDeviceUnits (see CheckLauncherAsync).
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(900, 616);   // two columns: the original settings, then the v1.30 ones

        // --- Download access (auth mode + per-mode credentials) ---
        var tokenHeading = Bold("Download access", new Point(16, 16));

        _authMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _authMode.Items.AddRange(new object[] { "GitHub token", "Download server" });
        _authMode.SelectedIndex = _settings.AuthMode == "server" ? 1 : 0;
        _authMode.Location = new Point(16, 40);
        _authMode.Width = 200;
        _authMode.SelectedIndexChanged += (_, _) =>
        {
            _settings.AuthMode = _authMode.SelectedIndex == 1 ? "server" : "token";
            UpdateAuthPanels();
        };

        // Token panel (y 70–208). Every status / hint line in this column is a fixed-width label that wraps (two
        // lines' room) instead of an AutoSize one: a long line used to run on under the right-hand column.
        _tokenState.AutoSize = false;
        _tokenState.Location = new Point(16, 70);
        _tokenState.Size = new Size(428, 34);
        UpdateTokenState();

        _token.UseSystemPasswordChar = true;
        _token.PlaceholderText = "Paste a fine-grained PAT…";
        _token.Location = new Point(16, 108);
        _token.Width = 428;

        _save.Text = "Save";
        _save.Location = new Point(16, 136);
        _save.Click += (_, _) =>
        {
            var t = _token.Text.Trim();
            if (t.Length == 0) return;
            TokenStore.Save(t);
            _token.Clear();
            UpdateTokenState();
        };

        _remove.Text = "Remove";
        _remove.Location = new Point(_save.Right + 8, 136);
        _remove.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Remove the saved token? You'll need to paste one again before you can install or update apps.",
                    "Remove token", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            TokenStore.Clear();
            UpdateTokenState();
        };

        _tokenLink.Text = "Create a fine-grained token on GitHub →";
        _tokenLink.AutoSize = true;
        _tokenLink.Location = new Point(16, 166);
        _tokenLink.LinkClicked += (_, _) => OpenUrl("https://github.com/settings/personal-access-tokens/new");

        _tokenHelp.Text = "Contents: Read-only. Only repos it can access appear here.";
        _tokenHelp.AutoSize = false;
        _tokenHelp.Location = new Point(16, 188);
        _tokenHelp.Size = new Size(428, 20);
        _tokenHelp.ForeColor = Theme.Sub(Theme.CurrentDark);

        // Server panel (same band; visibility-swapped with the token panel). The relay URL is
        // built-in (catalog `downloadServer`, with an invisible settings.json override) — the user
        // only ever enters the passphrase.
        _serverState.AutoSize = false;
        _serverState.Location = new Point(16, 70);
        _serverState.Size = new Size(428, 34);

        _serverPass.UseSystemPasswordChar = true;
        _serverPass.PlaceholderText = "Suite passphrase…";
        _serverPass.Location = new Point(16, 108);
        _serverPass.Width = 428;

        _serverSave.Text = "Save";
        _serverSave.Location = new Point(16, 136);
        _serverSave.Click += (_, _) =>
        {
            var pass = _serverPass.Text.Trim();
            if (pass.Length == 0 || AuthClient.ResolveServerUrl(_settings, _downloadServer) == null) return;
            TokenStore.SaveServerPass(pass);
            _serverPass.Clear();
            UpdateServerState();
        };

        _serverRemove.Text = "Remove";
        _serverRemove.Location = new Point(_serverSave.Right + 8, 136);
        _serverRemove.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Remove the server passphrase? You'll need to enter it again before you can install or update apps.",
                    "Remove passphrase", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            TokenStore.ClearServerPass();
            UpdateServerState();
        };

        // Forgiving-entry note: the passphrase is normalised (case- and spacing-insensitive) before it's sent.
        _serverHint.AutoSize = false;
        _serverHint.ForeColor = Theme.Sub(Theme.CurrentDark);
        _serverHint.Location = new Point(16, 166);
        _serverHint.Size = new Size(428, 34);
        _serverHint.Text = "Capitalisation and spaces don't matter — type the phrase however you like.";

        // Surface the effective relay host read-only (audit F2), so a non-default override is visible.
        _serverRelay.AutoSize = true;
        _serverRelay.ForeColor = Theme.Muted(Theme.CurrentDark);
        _serverRelay.Location = new Point(16, 202);
        var relay = AuthClient.ResolveServerUrl(_settings, _downloadServer);
        _serverRelay.Text = Uri.TryCreate(relay, UriKind.Absolute, out var relayUri) ? $"Relay: {relayUri.Host}" : "";

        // --- Updates ---
        var updatesHeading = Bold("Updates", new Point(16, 230));

        _updateMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _updateMode.Items.AddRange(new object[] { "Every launch", "Manual only", "Never" });
        _updateMode.SelectedIndex = _settings.UpdateMode switch { "manual" => 1, "never" => 2, _ => 0 };
        _updateMode.Location = new Point(16, 252);
        _updateMode.Width = 200;
        _updateMode.SelectedIndexChanged += (_, _) =>
        {
            _settings.UpdateMode = _updateMode.SelectedIndex switch { 1 => "manual", 2 => "never", _ => "everyLaunch" };
            _updateHint.Text = UpdateHint();
            UpdateIntervalEnabled();
        };

        _updateHint.AutoSize = false;
        _updateHint.Location = new Point(16, 280);
        _updateHint.Size = new Size(428, 34);
        _updateHint.ForeColor = Theme.Sub(Theme.CurrentDark);
        _updateHint.Text = UpdateHint();

        // "While open, check again": right under the check-mode dropdown (parity with the mac Settings), shown only
        // in "Every launch" mode — scheduled checks never run otherwise (see UpdateIntervalEnabled).
        _intervalLabel.Text = "While open, check again";
        _intervalLabel.AutoSize = true;
        _intervalLabel.Location = new Point(16, 324);
        _interval.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var (_, label) in UpdatePolicy.Intervals) _interval.Items.Add(label);
        int intervalIdx = Array.FindIndex(UpdatePolicy.Intervals, i => i.Raw == _settings.AutoCheckInterval);
        // An unknown saved value reads as the default interval (as UpdatePolicy.Interval does), not the first item.
        if (intervalIdx < 0) intervalIdx = Array.FindIndex(UpdatePolicy.Intervals, i => i.Raw == UpdatePolicy.DefaultInterval);
        _interval.SelectedIndex = Math.Max(0, intervalIdx);
        _interval.Location = new Point(176, 320);
        _interval.Width = 150;
        _interval.SelectedIndexChanged += (_, _) =>
            _settings.AutoCheckInterval = UpdatePolicy.Intervals[Math.Max(0, _interval.SelectedIndex)].Raw;

        var versionLabel = new Label
        {
            Text = $"JB Theatre Tools v{_currentVersion}",
            AutoSize = true,
            Location = new Point(16, 356),
        };
        // Hidden Dev channel: seven clicks on the version reveal "Development builds" (like Android's
        // developer options). Off by default; a normal user never sees it.
        // Seven clicks within three seconds; nothing about the reveal is stored (see below).
        var versionClicks = new List<DateTime>();
        versionLabel.Click += (_, _) =>
        {
            var now = DateTime.UtcNow;
            versionClicks.RemoveAll(t => (now - t).TotalSeconds >= 3);
            versionClicks.Add(now);
            if (versionClicks.Count >= 7) RevealDevChannel();
        };
        _devChannel.Text = "Development builds on this PC (pre-releases marked \u201cdev\u201d; mac + Android only for now)";
        _devChannel.AutoSize = true;
        _devChannel.Visible = false;
        _devChannel.Checked = _settings.DevChannel;
        _devChannel.CheckedChanged += (_, _) => _settings.DevChannel = _devChannel.Checked;

        _check.Text = "Check for Updates";
        _check.AutoSize = true;
        _check.Location = new Point(280, 352);
        _check.Click += async (_, _) => await CheckLauncherAsync();

        _viewRelease.Text = "Update";
        _viewRelease.AutoSize = true;
        _viewRelease.Visible = false;
        // The update restarts the launcher, so it runs from the main window: Settings closes (keeping its changes) and
        // hands over — the main window's banner shows the download and its outcome.
        _viewRelease.Click += (_, _) =>
        {
            if (_selfInfo == null) return;
            RequestedLauncherUpdate = true;
            DialogResult = DialogResult.OK;
        };

        _checkResult.AutoSize = false;
        _checkResult.Location = new Point(16, 382);
        _checkResult.Size = new Size(428, 34);
        _checkResult.ForeColor = Theme.Sub(Theme.CurrentDark);

        // --- Appearance ---
        var appearanceHeading = Bold("Appearance", new Point(16, 426));

        _appearance.DropDownStyle = ComboBoxStyle.DropDownList;
        _appearance.Items.AddRange(new object[] { "System", "Light", "Dark" });
        _appearance.SelectedIndex = _settings.Appearance switch { "light" => 1, "dark" => 2, _ => 0 };
        _appearance.Location = new Point(16, 448);
        _appearance.Width = 160;
        _appearance.SelectedIndexChanged += (_, _) =>
            _settings.Appearance = _appearance.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };

        // --- Close behaviour ---
        var closeHeading = Bold("When I close the window", new Point(16, 482));

        _closeBehavior.DropDownStyle = ComboBoxStyle.DropDownList;
        _closeBehavior.Items.AddRange(new object[] { "Quit the app", "Keep running in the tray" });
        _closeBehavior.SelectedIndex = _settings.CloseBehavior == "keepRunning" ? 1 : 0;
        _closeBehavior.Location = new Point(16, 504);
        _closeBehavior.Width = 240;
        _closeBehavior.SelectedIndexChanged += (_, _) =>
            _settings.CloseBehavior = _closeBehavior.SelectedIndex == 1 ? "keepRunning" : "quit";

        // --- Install location ---
        // UseMnemonic=false so the literal "&" renders (otherwise "& D" is eaten as an Alt-shortcut).
        _installToApps.UseMnemonic = false;
        _installToApps.Text = "Install apps to the Start menu & Desktop (launch them without this launcher)";
        _installToApps.AutoSize = false;
        _installToApps.Location = new Point(16, 536);
        _installToApps.Size = new Size(428, 34);
        _installToApps.Checked = _settings.InstallToApplications;
        _installToApps.CheckedChanged += (_, _) => _settings.InstallToApplications = _installToApps.Checked;

        var openLog = new Button
        {
            Text = "Open Log",
            Location = new Point(16, 580),
            AutoSize = true,
        };
        openLog.Click += (_, _) => Log.Open();

        var resetOrder = new Button
        {
            Text = "Reset App Order",
            Location = new Point(110, 580),
            AutoSize = true,
        };
        // Clears the saved order; MainForm re-applies (→ catalog order) when the dialog closes.
        resetOrder.Click += (_, _) => _settings.AppOrder.Clear();

        var showHidden = new Button
        {
            Text = _settings.HiddenApps.Count > 0 ? $"Show Hidden Apps ({_settings.HiddenApps.Count})" : "Show Hidden Apps",
            Location = new Point(228, 580),
            AutoSize = true,
            Enabled = _settings.HiddenApps.Count > 0,
        };
        // Un-hides everything; MainForm re-applies when the dialog closes.
        showHidden.Click += (_, _) => { _settings.HiddenApps.Clear(); showHidden.Enabled = false; showHidden.Text = "Show Hidden Apps"; };

        var done = new Button
        {
            Text = "Done",
            DialogResult = DialogResult.OK,
            Location = new Point(ClientSize.Width - 100, 580),
            Width = 84,
        };
        AcceptButton = done;

        var rightColumn = BuildRightColumn();

        // Added in the visual order, and the tab order follows it: the left column top to bottom, the right column,
        // the Development builds switch (revealed above the buttons), then the button row with Done last. (Download
        // Update comes before the result line it sits on, so it stays above it in the z-order.)
        var ordered = new List<Control>
        {
            tokenHeading, _authMode,
            _tokenState, _token, _save, _remove, _tokenLink, _tokenHelp,
            _serverState, _serverPass, _serverSave, _serverRemove, _serverHint, _serverRelay,
            updatesHeading, _updateMode, _updateHint, _intervalLabel, _interval, versionLabel, _check, _viewRelease, _checkResult,
            appearanceHeading, _appearance, closeHeading, _closeBehavior, _installToApps,
        };
        ordered.AddRange(rightColumn);
        ordered.AddRange(new Control[] { _devChannel, openLog, resetOrder, showHidden, done });
        for (int i = 0; i < ordered.Count; i++) ordered[i].TabIndex = i;
        Controls.AddRange(ordered.ToArray());

        UpdateServerState();
        UpdateAuthPanels();
        UpdateIntervalEnabled();
        ResumeLayout(false);
        PerformLayout();   // the AutoScale pass runs here
        // Shown only while dev mode is ON (or after the gesture above, this session only): switch it off and the
        // checkbox is gone next time Settings opens.
        if (_settings.DevChannel) RevealDevChannel();
    }

    /// <summary>The second column (x 480–884): show lock, when updates are found, quick launch, storage and support —
    /// laid out at fixed 96-DPI positions like the first column, all above the button row.</summary>
    private List<Control> BuildRightColumn()
    {
        const int X = 484, W = 400;
        var list = new List<Control>();
        Label Hint(string text, int y, int h = 34)
        {
            var l = new Label { Text = text, AutoSize = false, Location = new Point(X, y), Size = new Size(W, h),
                                ForeColor = Theme.Sub(Theme.CurrentDark), UseMnemonic = false };
            _hints.Add(l);
            return l;
        }
        // A hairline between the columns.
        list.Add(new Panel { Location = new Point(466, 16), Size = new Size(1, 552), BackColor = Theme.Line(Theme.CurrentDark), Tag = "divider" });

        list.Add(Bold("Show lock", new Point(X, 16)));
        _showLock.Text = "Pause installs, updates and uninstalls";
        _showLock.AutoSize = true;
        _showLock.Location = new Point(X, 40);
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
        list.Add(_showLock);
        list.Add(Hint("For show time: launching still works, nothing changes underneath you. Ctrl+L in the main window.", 64));

        // What happens when a check finds updates (the check itself — mode and interval — is under Updates).
        list.Add(Bold("When updates are found", new Point(X, 110)));
        _notify.Text = "Notify me about new updates";
        _notify.AutoSize = true;
        _notify.Location = new Point(X, 134);
        _notify.Checked = _settings.NotifyUpdates;
        _notify.CheckedChanged += (_, _) => _settings.NotifyUpdates = _notify.Checked;
        list.Add(_notify);
        _autoInstall.Text = "Install updates automatically";
        _autoInstall.AutoSize = true;
        _autoInstall.Location = new Point(X, 160);
        _autoInstall.Checked = _settings.AutoInstallUpdates;
        _autoInstall.CheckedChanged += (_, _) => _settings.AutoInstallUpdates = _autoInstall.Checked;
        list.Add(_autoInstall);
        list.Add(Hint("Held apps and apps that are open are left alone, and nothing installs during show lock.", 184));

        list.Add(Bold("Quick launch", new Point(X, 232)));
        _tray.Text = "Always show the icon in the notification area";
        _tray.AutoSize = true;
        _tray.Location = new Point(X, 256);
        _tray.Checked = _settings.AlwaysShowTray;
        _tray.CheckedChanged += (_, _) => _settings.AlwaysShowTray = _tray.Checked;
        list.Add(_tray);
        list.Add(Hint("Right-click it to launch any installed app, or check for updates.", 280));

        list.Add(Bold("Storage", new Point(X, 326)));
        _storage.AutoSize = false;
        _storage.Location = new Point(X, 350);
        _storage.Size = new Size(W, 34);   // wraps a long "couldn't measure" message rather than clipping it
        _storage.Text = _extras == null ? "" : "Calculating…";
        list.Add(_storage);
        _clearCache.Text = "Clear Download Cache";
        _clearCache.AutoSize = true;
        _clearCache.Location = new Point(X, 388);
        _clearCache.Visible = _extras != null;
        _clearCache.Click += async (_, _) =>
        {
            if (_extras == null || !_extras.CanClearCache()) { UpdateCacheButton(); return; }
            _clearCache.Enabled = false;
            await _extras.ClearCache();
            await RefreshStorageAsync();
            UpdateCacheButton();
        };
        list.Add(_clearCache);

        list.Add(Bold("Support", new Point(X, 428)));
        var diag = new Button { Text = "Copy Diagnostics", AutoSize = true, Location = new Point(X, 452), Visible = _extras != null };
        diag.Click += (_, _) => _extras?.CopyDiagnostics(this);
        list.Add(diag);
        list.Add(Hint("Versions, settings and recent log lines for a support question — never passwords or tokens.", 482));

        Shown += async (_, _) => { await RefreshStorageAsync(); UpdateCacheButton(); };
        return list;
    }

    private async Task RefreshStorageAsync()
    {
        if (_extras == null) return;
        try
        {
            var (installed, cache) = await _extras.Storage();
            if (!IsDisposed) _storage.Text = $"Installed apps: {ByteSize.Format(installed)}   ·   Download cache: {ByteSize.Format(cache)}";
        }
        catch (Exception ex) { if (!IsDisposed) _storage.Text = $"Couldn't measure storage: {ex.Message}"; }
    }

    /// <summary>Clearing the cache is off during show lock and while anything is downloading or installing.</summary>
    private void UpdateCacheButton() =>
        _clearCache.Enabled = _extras != null && !_settings.ShowLock && _extras.CanClearCache();

    /// <summary>Scheduled checks only run in "Every launch" mode — the interval is enabled only then. It stays in
    /// place (greyed) otherwise: this fixed layout would leave a blank gap where a hidden row was.</summary>
    private void UpdateIntervalEnabled()
    {
        bool on = _settings.UpdateMode == "everyLaunch";
        _interval.Enabled = on;
        _intervalLabel.Enabled = on;
    }

    /// <summary>Shows the Dev channel checkbox above the button row, growing the dialog to make room. Runs
    /// after the AutoScale pass, so every offset goes through LogicalToDeviceUnits.</summary>
    private void RevealDevChannel()
    {
        if (_devChannel.Visible) return;
        int dy = LogicalToDeviceUnits(30), rowTop = LogicalToDeviceUnits(578);
        foreach (Control c in Controls) if (c != _devChannel && c.Top >= rowTop) c.Top += dy;
        ClientSize = new Size(ClientSize.Width, ClientSize.Height + dy);
        _devChannel.Location = new Point(LogicalToDeviceUnits(16), rowTop);
        _devChannel.Visible = true;
    }

    /// <summary>Shows the token panel or the server panel to match the selected auth mode.</summary>
    private void UpdateAuthPanels()
    {
        bool server = _settings.AuthMode == "server";
        foreach (Control c in new Control[] { _tokenState, _token, _save, _remove, _tokenLink, _tokenHelp })
            c.Visible = !server;
        foreach (Control c in new Control[] { _serverState, _serverPass, _serverHint, _serverSave, _serverRemove, _serverRelay })
            c.Visible = server;
        if (!server) UpdateTokenState(); else UpdateServerState();
    }

    private void UpdateServerState()
    {
        bool has = AuthClient.ResolveServerUrl(_settings, _downloadServer) != null && TokenStore.LoadServerPass() != null;
        _serverState.Text = has
            ? "Passphrase saved in Credential Manager."
            : "Enter the suite passphrase (ask whoever set up your access) — downloads are disabled until you do.";
        _serverState.ForeColor = has ? Theme.Ok : Theme.Sub(Theme.CurrentDark);
        _serverRemove.Visible = has && _settings.AuthMode == "server";
    }

    private async Task CheckLauncherAsync()
    {
        if (_selfInfo == null) { SetResult("No self-update info in catalog.", Theme.Sub(Theme.CurrentDark)); return; }
        _check.Enabled = false;
        SetResult("Checking…", Theme.Sub(Theme.CurrentDark));
        _viewRelease.Visible = false;
        try
        {
            using var client = AuthClient.SelfUpdate(_settings, _downloadServer);   // public repo; never blocks on creds
            var info = await Versions.LauncherTargetAsync(client, _selfInfo.Owner, _selfInfo.Repo, _currentVersion);
            if (info != null)
            {
                SetResult(Versions.IsNewer(info.TagName, _currentVersion) ? $"{info.TagName} is available." : $"Back to the release: {info.TagName}.", Theme.Info);
                _viewRelease.Location = new Point(_checkResult.Left + LogicalToDeviceUnits(160), LogicalToDeviceUnits(378));
                _viewRelease.Visible = true;
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
            _check.Enabled = true;
        }
    }

    private void SetResult(string text, Color color)
    {
        _checkResult.Text = text;
        _checkResult.ForeColor = color;
    }

    private string UpdateHint() => _settings.UpdateMode switch
    {
        "manual" => "Only checks when you press Refresh or Check for Updates.",
        "never" => "Never checks automatically. You can still install/update from the buttons.",
        _ => "Checks all apps and the launcher each time it opens.",
    };

    /// <summary>The kit's panel heading (`.panel > h2`): 10.5px/600 uppercase in the tertiary tone.</summary>
    private static Label Bold(string text, Point location) => new()
    {
        Text = text.ToUpperInvariant(),
        Font = Theme.Ui(Theme.PtLabel, semibold: true),
        ForeColor = Theme.Muted(Theme.CurrentDark),
        AutoSize = true,
        Location = location,
        Tag = "panel-heading",
    };

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
        _remove.Visible = has;
    }

    public void ApplyTheme(bool dark)
    {
        Theme.SetCurrent(dark);
        BackColor = Theme.Bg(dark);
        ForeColor = Theme.Fg(dark);
        // Panel headings and hint prose carry their own tones (the kit's --text-3 / --text-2).
        foreach (Control c in Controls)
            if (c is Label { Tag: "panel-heading" } heading) heading.ForeColor = Theme.Muted(dark);
        _tokenHelp.ForeColor = Theme.Sub(dark);
        _serverHint.ForeColor = Theme.Sub(dark);
        _serverRelay.ForeColor = Theme.Muted(dark);
        _updateHint.ForeColor = Theme.Sub(dark);
        _checkResult.ForeColor = Theme.Sub(dark);
        foreach (var h in _hints) h.ForeColor = Theme.Sub(dark);
        foreach (Control c in Controls)
            if (c is Panel { Tag: "divider" } line) line.BackColor = Theme.Line(dark);
        UpdateTokenState();
        UpdateServerState();
        if (IsHandleCreated) Theme.ApplyTitleBar(this, dark);
    }
}
