namespace JBTheatreTools;

public sealed class SettingsDialog : Form
{
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
    private readonly Button _check = new();
    private readonly Button _viewRelease = new();
    private readonly Label _checkResult = new();
    private readonly ComboBox _appearance = new();
    private readonly ComboBox _closeBehavior = new();
    private readonly CheckBox _installToApps = new();

    private readonly string? _downloadServer;

    public SettingsDialog(AppSettings settings, SelfInfo? selfInfo, string currentVersion, string? downloadServer)
    {
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
        ClientSize = new Size(460, 568);

        // --- Download access (auth mode + per-mode credentials) ---
        var tokenHeading = Bold("Download access", new Point(16, 16));

        _authMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _authMode.Items.AddRange(new object[] { "GitHub token", "Download server" });
        _authMode.SelectedIndex = _settings.AuthMode == "server" ? 1 : 0;
        _authMode.Location = new Point(16, 42);
        _authMode.Width = 200;
        _authMode.SelectedIndexChanged += (_, _) =>
        {
            _settings.AuthMode = _authMode.SelectedIndex == 1 ? "server" : "token";
            UpdateAuthPanels();
        };

        // Token panel (y 74–206)
        _tokenState.AutoSize = true;
        _tokenState.Location = new Point(16, 74);
        UpdateTokenState();

        _token.UseSystemPasswordChar = true;
        _token.PlaceholderText = "Paste a fine-grained PAT…";
        _token.Location = new Point(16, 98);
        _token.Width = 428;

        _save.Text = "Save";
        _save.Location = new Point(16, 130);
        _save.Click += (_, _) =>
        {
            var t = _token.Text.Trim();
            if (t.Length == 0) return;
            TokenStore.Save(t);
            _token.Clear();
            UpdateTokenState();
        };

        _remove.Text = "Remove";
        _remove.Location = new Point(_save.Right + 8, 130);
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
        _tokenLink.Location = new Point(16, 164);
        _tokenLink.LinkClicked += (_, _) => OpenUrl("https://github.com/settings/personal-access-tokens/new");

        _tokenHelp.Text = "Contents: Read-only. Only repos it can access appear here.";
        _tokenHelp.AutoSize = false;
        _tokenHelp.Location = new Point(16, 186);
        _tokenHelp.Size = new Size(428, 20);
        _tokenHelp.ForeColor = Theme.Sub(Theme.CurrentDark);

        // Server panel (same band; visibility-swapped with the token panel). The relay URL is
        // built-in (catalog `downloadServer`, with an invisible settings.json override) — the user
        // only ever enters the passphrase.
        _serverState.AutoSize = true;
        _serverState.Location = new Point(16, 74);

        _serverPass.UseSystemPasswordChar = true;
        _serverPass.PlaceholderText = "Suite passphrase…";
        _serverPass.Location = new Point(16, 98);
        _serverPass.Width = 428;

        _serverSave.Text = "Save";
        _serverSave.Location = new Point(16, 130);
        _serverSave.Click += (_, _) =>
        {
            var pass = _serverPass.Text.Trim();
            if (pass.Length == 0 || AuthClient.ResolveServerUrl(_settings, _downloadServer) == null) return;
            TokenStore.SaveServerPass(pass);
            _serverPass.Clear();
            UpdateServerState();
        };

        _serverRemove.Text = "Remove";
        _serverRemove.Location = new Point(_serverSave.Right + 8, 130);
        _serverRemove.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Remove the server passphrase? You'll need to enter it again before you can install or update apps.",
                    "Remove passphrase", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            TokenStore.ClearServerPass();
            UpdateServerState();
        };

        // Forgiving-entry note: the passphrase is normalised (case- and spacing-insensitive) before it's sent.
        _serverHint.AutoSize = true;
        _serverHint.ForeColor = Theme.Sub(Theme.CurrentDark);
        _serverHint.Location = new Point(16, 164);
        _serverHint.Text = "Capitalisation and spaces don't matter — type the phrase however you like.";

        // Surface the effective relay host read-only (audit F2), so a non-default override is visible.
        _serverRelay.AutoSize = true;
        _serverRelay.ForeColor = Theme.Muted(Theme.CurrentDark);
        _serverRelay.Location = new Point(16, 186);
        var relay = AuthClient.ResolveServerUrl(_settings, _downloadServer);
        _serverRelay.Text = Uri.TryCreate(relay, UriKind.Absolute, out var relayUri) ? $"Relay: {relayUri.Host}" : "";

        // --- Updates ---
        var updatesHeading = Bold("Updates", new Point(16, 228));

        _updateMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _updateMode.Items.AddRange(new object[] { "Every launch", "Manual only", "Never" });
        _updateMode.SelectedIndex = _settings.UpdateMode switch { "manual" => 1, "never" => 2, _ => 0 };
        _updateMode.Location = new Point(16, 252);
        _updateMode.Width = 200;
        _updateMode.SelectedIndexChanged += (_, _) =>
        {
            _settings.UpdateMode = _updateMode.SelectedIndex switch { 1 => "manual", 2 => "never", _ => "everyLaunch" };
            _updateHint.Text = UpdateHint();
        };

        _updateHint.AutoSize = false;
        _updateHint.Location = new Point(16, 280);
        _updateHint.Size = new Size(428, 18);
        _updateHint.ForeColor = Theme.Sub(Theme.CurrentDark);
        _updateHint.Text = UpdateHint();

        var versionLabel = new Label
        {
            Text = $"JB Theatre Tools v{_currentVersion}",
            AutoSize = true,
            Location = new Point(16, 308),
        };

        _check.Text = "Check for Updates";
        _check.AutoSize = true;
        _check.Location = new Point(280, 304);
        _check.Click += async (_, _) => await CheckLauncherAsync();

        _viewRelease.Text = "Download Update";
        _viewRelease.AutoSize = true;
        _viewRelease.Visible = false;
        _viewRelease.Click += async (_, _) =>
        {
            if (_selfInfo == null) return;
            _viewRelease.Enabled = false;
            SetResult("Downloading…", Theme.Sub(Theme.CurrentDark));
            try
            {
                var dest = await LauncherUpdate.DownloadAndRevealAsync(_selfInfo, AuthClient.SelfUpdate(_settings, _downloadServer));
                SetResult($"Saved {Path.GetFileName(dest)} to Downloads — quit & replace.", Theme.Ok);
            }
            catch (Exception ex)
            {
                SetResult(ex.Message, Theme.Danger);
            }
            finally
            {
                _viewRelease.Enabled = true;
            }
        };

        _checkResult.AutoSize = false;
        _checkResult.Location = new Point(16, 336);
        _checkResult.Size = new Size(428, 20);
        _checkResult.ForeColor = Theme.Sub(Theme.CurrentDark);

        // --- Appearance ---
        var appearanceHeading = Bold("Appearance", new Point(16, 372));

        _appearance.DropDownStyle = ComboBoxStyle.DropDownList;
        _appearance.Items.AddRange(new object[] { "System", "Light", "Dark" });
        _appearance.SelectedIndex = _settings.Appearance switch { "light" => 1, "dark" => 2, _ => 0 };
        _appearance.Location = new Point(16, 396);
        _appearance.Width = 160;
        _appearance.SelectedIndexChanged += (_, _) =>
            _settings.Appearance = _appearance.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };

        // --- Close behaviour ---
        var closeHeading = Bold("When I close the window", new Point(16, 432));

        _closeBehavior.DropDownStyle = ComboBoxStyle.DropDownList;
        _closeBehavior.Items.AddRange(new object[] { "Quit the app", "Keep running in the tray" });
        _closeBehavior.SelectedIndex = _settings.CloseBehavior == "keepRunning" ? 1 : 0;
        _closeBehavior.Location = new Point(16, 456);
        _closeBehavior.Width = 240;
        _closeBehavior.SelectedIndexChanged += (_, _) =>
            _settings.CloseBehavior = _closeBehavior.SelectedIndex == 1 ? "keepRunning" : "quit";

        // --- Install location ---
        // UseMnemonic=false so the literal "&" renders (otherwise "& D" is eaten as an Alt-shortcut).
        _installToApps.UseMnemonic = false;
        _installToApps.Text = "Install apps to the Start menu & Desktop (launch them without this launcher)";
        _installToApps.AutoSize = false;
        _installToApps.Location = new Point(16, 492);
        _installToApps.Size = new Size(428, 34);
        _installToApps.Checked = _settings.InstallToApplications;
        _installToApps.CheckedChanged += (_, _) => _settings.InstallToApplications = _installToApps.Checked;

        var openLog = new Button
        {
            Text = "Open Log",
            Location = new Point(16, 532),
            AutoSize = true,
        };
        openLog.Click += (_, _) => Log.Open();

        var resetOrder = new Button
        {
            Text = "Reset App Order",
            Location = new Point(110, 532),
            AutoSize = true,
        };
        // Clears the saved order; MainForm re-applies (→ catalog order) when the dialog closes.
        resetOrder.Click += (_, _) => _settings.AppOrder.Clear();

        var showHidden = new Button
        {
            Text = _settings.HiddenApps.Count > 0 ? $"Show Hidden Apps ({_settings.HiddenApps.Count})" : "Show Hidden Apps",
            Location = new Point(228, 532),
            AutoSize = true,
            Enabled = _settings.HiddenApps.Count > 0,
        };
        // Un-hides everything; MainForm re-applies when the dialog closes.
        showHidden.Click += (_, _) => { _settings.HiddenApps.Clear(); showHidden.Enabled = false; showHidden.Text = "Show Hidden Apps"; };

        var done = new Button
        {
            Text = "Done",
            DialogResult = DialogResult.OK,
            Location = new Point(ClientSize.Width - 100, 532),
            Width = 84,
        };
        AcceptButton = done;

        Controls.AddRange(new Control[]
        {
            tokenHeading, _authMode,
            _tokenState, _token, _save, _remove, _tokenLink, _tokenHelp,
            _serverState, _serverPass, _serverHint, _serverSave, _serverRemove, _serverRelay,
            updatesHeading, _updateMode, _updateHint, versionLabel, _check, _viewRelease, _checkResult,
            appearanceHeading, _appearance, closeHeading, _closeBehavior, _installToApps, openLog, resetOrder, showHidden, done
        });

        UpdateServerState();
        UpdateAuthPanels();
        ResumeLayout(false);
        PerformLayout();   // the AutoScale pass runs here
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
            : "Enter the suite passphrase (ask James) — downloads are disabled until you do.";
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
            var info = await client.LatestReleaseAsync(_selfInfo.Owner, _selfInfo.Repo);
            if (Versions.IsNewer(info.TagName, _currentVersion))
            {
                SetResult($"{info.TagName} is available.", Theme.Info);
                _viewRelease.Location = new Point(_checkResult.Left + LogicalToDeviceUnits(160), LogicalToDeviceUnits(332));
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
        UpdateTokenState();
        UpdateServerState();
        if (IsHandleCreated) Theme.ApplyTitleBar(this, dark);
    }
}
