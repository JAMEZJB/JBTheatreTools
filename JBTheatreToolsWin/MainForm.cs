using System.Reflection;

namespace JBTheatreTools;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings = AppSettings.Load();
    private Catalog _catalog = new();
    private readonly List<AppRowControl> _rows = new();

    private readonly FlowLayoutPanel _list = new();
    private readonly Panel _tokenBanner = new();
    private readonly Label _tokenBannerText = new();
    private readonly Panel _updateBanner = new();
    private readonly Label _updateBannerText = new();
    private readonly Button _refresh = new();
    private readonly Button _settingsBtn = new();
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly Label _credit = new();

    // Tray support for the "keep running" close behaviour.
    private readonly NotifyIcon _tray = new();
    private bool _reallyQuit;

    // Notice-banner messages (the banner doubles as the no-token / bad-token / no-access notice).
    private const string NoTokenMsg = "Add a GitHub token to enable downloads  —  Settings → paste a fine-grained PAT.";
    private const string BadTokenMsg = "Your GitHub token is invalid or expired  —  open Settings to paste a new one.";
    private const string NoAccessMsg = "This token can’t access any apps  —  check its repository access, or ask James.";

    public MainForm()
    {
        Text = "JB Theatre Tools";
        ClientSize = new Size(680, 520);
        MinimumSize = new Size(560, 440);
        StartPosition = FormStartPosition.CenterScreen;
        TryLoadIcon();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // header
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // launcher-update banner
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // token banner
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // list
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // footer (credit)

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildUpdateBanner(), 0, 1);
        root.Controls.Add(BuildTokenBanner(), 0, 2);

        _list.Dock = DockStyle.Fill;
        _list.FlowDirection = FlowDirection.TopDown;
        _list.WrapContents = false;
        _list.AutoScroll = true;
        _list.Padding = new Padding(10);
        root.Controls.Add(_list, 0, 3);

        root.Controls.Add(BuildFooter(), 0, 4);

        Controls.Add(root);

        LoadCatalog();
        ApplyTheme();
        SetupTray();
        FormClosing += OnFormClosing;
        Shown += async (_, _) =>
        {
            ShowNotice(TokenStore.Load() == null ? NoTokenMsg : null);
            if (_settings.UpdateMode == "everyLaunch")
            {
                await RefreshAllAsync();
                await CheckLauncherUpdateAsync();
            }
        };
    }

    // MARK: - UI construction

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, Height = 64, Padding = new Padding(14, 10, 14, 10) };

        _title.Text = "JB Theatre Tools";
        _title.Font = new Font(Font.FontFamily, 13f, FontStyle.Bold);
        _title.AutoSize = true;
        _title.Location = new Point(14, 10);

        _subtitle.Text = "Install, update & launch the JB tool suite";
        _subtitle.AutoSize = true;
        _subtitle.Location = new Point(14, 36);

        _refresh.Text = "Refresh";
        _refresh.AutoSize = true;
        _refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _refresh.Click += async (_, _) => await RefreshAllAsync();

        _settingsBtn.Text = "Settings";
        _settingsBtn.AutoSize = true;
        _settingsBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _settingsBtn.Click += (_, _) => OpenSettings();

        header.Controls.AddRange(new Control[] { _title, _subtitle, _refresh, _settingsBtn });
        header.Resize += (_, _) =>
        {
            _settingsBtn.Location = new Point(header.Width - _settingsBtn.Width - 14, 16);
            _refresh.Location = new Point(_settingsBtn.Left - _refresh.Width - 8, 16);
        };
        return header;
    }

    private Control BuildUpdateBanner()
    {
        _updateBanner.Dock = DockStyle.Fill;
        _updateBanner.Height = 44;
        _updateBanner.BackColor = Color.FromArgb(243, 232, 252);   // light tint of the suite accent #AF52DE
        _updateBanner.Visible = false;

        _updateBannerText.AutoSize = true;
        _updateBannerText.Location = new Point(14, 13);
        _updateBannerText.ForeColor = Color.FromArgb(96, 40, 140);   // deep purple, readable on the tint

        var download = new Button { Text = "Download Update", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        download.Click += async (_, _) =>
        {
            if (_catalog.Self == null) return;
            download.Enabled = false;
            var prev = download.Text;
            download.Text = "Downloading…";
            try
            {
                var dest = await LauncherUpdate.DownloadAndRevealAsync(_catalog.Self);
                _updateBannerText.Text = $"Saved {Path.GetFileName(dest)} to Downloads — quit & replace JB Theatre Tools.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Download failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                download.Text = prev;
                download.Enabled = true;
            }
        };

        _updateBanner.Controls.Add(_updateBannerText);
        _updateBanner.Controls.Add(download);
        _updateBanner.Resize += (_, _) => download.Location = new Point(_updateBanner.Width - download.Width - 14, 8);
        return _updateBanner;
    }

    private Control BuildTokenBanner()
    {
        _tokenBanner.Dock = DockStyle.Fill;
        _tokenBanner.Height = 44;
        _tokenBanner.BackColor = Color.FromArgb(255, 244, 214);
        _tokenBanner.Visible = false;

        _tokenBannerText.Text = NoTokenMsg;
        _tokenBannerText.AutoSize = true;
        _tokenBannerText.Location = new Point(14, 13);
        _tokenBannerText.ForeColor = Color.FromArgb(120, 80, 0);

        var open = new Button { Text = "Open Settings", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        open.Click += (_, _) => OpenSettings();

        _tokenBanner.Controls.Add(_tokenBannerText);
        _tokenBanner.Controls.Add(open);
        _tokenBanner.Resize += (_, _) => open.Location = new Point(_tokenBanner.Width - open.Width - 14, 8);
        return _tokenBanner;
    }

    private void TryLoadIcon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
            if (name == null) return;
            using var s = asm.GetManifestResourceStream(name);
            if (s != null) Icon = new Icon(s);
        }
        catch { /* generic icon is fine */ }
    }

    private Control BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Fill, Height = 28 };
        // UseMnemonic=false so the literal "&" renders (default true treats it as an Alt-shortcut
        // prefix, eating the "&" and the following space → a double space).
        _credit.UseMnemonic = false;
        _credit.Text = "Created by: James Breedon & Claude Code";
        _credit.ForeColor = SystemColors.GrayText;
        _credit.AutoSize = true;
        _credit.Anchor = AnchorStyles.None;
        footer.Controls.Add(_credit);
        footer.Resize += (_, _) =>
            _credit.Location = new Point((footer.Width - _credit.Width) / 2, 5);
        return footer;
    }

    private void LoadCatalog()
    {
        try
        {
            _catalog = Catalog.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load app catalog:\n{ex.Message}", "JB Theatre Tools",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _list.Controls.Clear();
        _rows.Clear();
        foreach (var app in _catalog.Apps)
        {
            var row = new AppRowControl(app) { Width = Math.Max(400, _list.ClientSize.Width - 30) };
            row.InstallRequested += InstallAsync;
            row.InstallVersionRequested += InstallVersionAsync;
            row.UninstallRequested += Uninstall;
            row.LaunchRequested += Launch;
            var installed = InstallManager.Shared.InstalledVersion(app.Id);
            row.SetState(installed, null, null, installed != null ? RowStatus.Installed : RowStatus.Unknown);
            row.SetResolvedName(InstallManager.Shared.InstalledDisplayName(app.Id));
            // Installed apps show immediately (launchable pre-refresh); not-installed rows stay hidden
            // until a refresh confirms the token can reach them, so inaccessible apps never flash in.
            row.Visible = installed != null;
            _rows.Add(row);
            _list.Controls.Add(row);
        }
        _list.Resize += (_, _) =>
        {
            foreach (var r in _rows) r.Width = _list.ClientSize.Width - 30;
        };
    }

    // MARK: - Actions

    private async Task RefreshAllAsync()
    {
        var token = TokenStore.Load();
        if (token == null) { ShowNotice(NoTokenMsg); return; }
        ShowNotice(null);

        _refresh.Enabled = false;
        bool unauthorized = false;
        try
        {
            using var client = new GitHubClient(token);
            foreach (var row in _rows)
            {
                // Don't force the row visible here — leave it as-is during the check so a not-installed
                // row that turns out inaccessible never flashes into view. We set visibility from the
                // outcome at the end of the iteration.
                row.SetChecking();
                var installed = InstallManager.Shared.InstalledVersion(row.App.Id);
                row.SetResolvedName(InstallManager.Shared.InstalledDisplayName(row.App.Id));
                bool accessible = false;
                try
                {
                    var all = await client.ReleasesAsync(row.App.Owner, row.App.Repo);
                    accessible = true;
                    row.SetReleases(all);
                    var latest = all.FirstOrDefault(r => !r.Prerelease) ?? all.FirstOrDefault();
                    if (latest == null)
                    {
                        row.SetState(installed, null, null, RowStatus.NoRelease);
                    }
                    else
                    {
                        var asset = latest.Assets.FirstOrDefault(a => a.Name == row.App.WindowsAssetName);
                        row.SetState(installed, latest.TagName, asset?.Id, ComputeStatus(installed, latest.TagName, asset != null));
                    }
                }
                catch (GitHubException ge) when (ge.Kind == GitHubErrorKind.NotAccessible)
                {
                    accessible = false;   // token can't see this repo
                }
                catch (GitHubException ge) when (ge.Kind == GitHubErrorKind.Unauthorized)
                {
                    unauthorized = true;
                    accessible = false;
                }
                catch (GitHubException ge) when (ge.Kind == GitHubErrorKind.NoRelease)
                {
                    accessible = true;
                    row.SetReleases(new List<ReleaseInfo>());
                    row.SetState(installed, null, null, RowStatus.NoRelease);
                }
                catch
                {
                    accessible = true;   // transient error — show it so the user can Retry
                    row.SetState(installed, null, null, RowStatus.Error);
                }
                // Visible iff installed locally OR the token reached the repo.
                row.Visible = installed != null || accessible;
            }

            // One clear notice for the whole-token states instead of rows full of errors.
            if (unauthorized) ShowNotice(BadTokenMsg);
            else if (_rows.All(r => !r.Visible)) ShowNotice(NoAccessMsg);
            else ShowNotice(null);
        }
        finally
        {
            _refresh.Enabled = true;
        }
    }

    /// <summary>Shows the notice banner with <paramref name="text"/>, or hides it when null.</summary>
    private void ShowNotice(string? text)
    {
        if (text != null) _tokenBannerText.Text = text;
        _tokenBanner.Visible = text != null;
    }

    // MARK: - Close behaviour (quit vs. keep running in the system tray)

    private void SetupTray()
    {
        _tray.Text = "JB Theatre Tools";
        _tray.Icon = Icon ?? SystemIcons.Application;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open JB Theatre Tools", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { _reallyQuit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.Visible = false;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        _tray.Visible = false;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // House convention: X quits by default. If the user opted into "keep running", minimise to the
        // tray instead — unless we're genuinely quitting (tray "Quit" item, or a real OS shutdown).
        if (!_reallyQuit && e.CloseReason == CloseReason.UserClosing && _settings.CloseBehavior == "keepRunning")
        {
            e.Cancel = true;
            Hide();
            _tray.Visible = true;
            return;
        }
        _tray.Visible = false;
        _tray.Dispose();
    }

    private Task InstallAsync(AppRowControl row) => InstallVersionAsync(row, null);

    private async Task InstallVersionAsync(AppRowControl row, string? tag)
    {
        var token = TokenStore.Load();
        if (token == null) return;
        var assetName = row.App.WindowsAssetName;
        if (assetName == null) return;

        row.SetBusy(true);
        try
        {
            using var client = new GitHubClient(token);
            var releases = row.Releases.Count > 0
                ? row.Releases
                : await client.ReleasesAsync(row.App.Owner, row.App.Repo);
            var rel = tag != null
                ? releases.FirstOrDefault(r => r.TagName == tag)
                : (releases.FirstOrDefault(r => !r.Prerelease) ?? releases.FirstOrDefault());
            if (rel == null) throw new Exception($"Version {tag ?? "latest"} not found.");
            var asset = rel.Assets.FirstOrDefault(a => a.Name == assetName)
                ?? throw new Exception($"No Windows asset in {rel.TagName}.");

            var cache = Path.Combine(InstallManager.Shared.CacheDir, $"{row.App.Id}-{rel.TagName}-{assetName}");
            var progress = new Progress<double>(p => row.SetProgress(p));
            await client.DownloadAssetAsync(row.App.Owner, row.App.Repo, asset.Id, cache, progress);
            InstallManager.Shared.Install(row.App, rel.TagName, cache, assetName, _settings.InstallToApplications);
            var latest = row.Latest ?? rel.TagName;
            row.SetState(rel.TagName, row.Latest, row.LatestAssetId, ComputeStatus(rel.TagName, latest, true));
            row.SetResolvedName(InstallManager.Shared.InstalledDisplayName(row.App.Id));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            row.SetState(row.Installed, row.Latest, row.LatestAssetId, RowStatus.Error);
        }
        finally
        {
            row.SetBusy(false);
        }
    }

    private void Uninstall(AppRowControl row)
    {
        if (MessageBox.Show(this, $"Uninstall {row.DisplayName}?", "Uninstall",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            InstallManager.Shared.Uninstall(row.App.Id);
            var status = row.LatestAssetId != null
                ? RowStatus.NotInstalled
                : (row.Latest == null ? RowStatus.Unknown : RowStatus.MissingAsset);
            row.SetState(null, row.Latest, row.LatestAssetId, status);
            row.SetResolvedName(null);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Uninstall failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Launch(AppRowControl row)
    {
        try
        {
            InstallManager.Shared.Launch(row.App);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task CheckLauncherUpdateAsync()
    {
        var s = _catalog.Self;
        if (s == null) return;
        try
        {
            using var client = new GitHubClient(TokenStore.Load());   // launcher repo is public; token optional
            var info = await client.LatestReleaseAsync(s.Owner, s.Repo);
            if (Versions.IsNewer(info.TagName, CurrentVersion()))
            {
                _updateBannerText.Text = $"JB Theatre Tools {info.TagName} is available (you have v{CurrentVersion()}).";
                _updateBanner.Visible = true;
            }
            else
            {
                _updateBanner.Visible = false;
            }
        }
        catch { /* ignore — self-update check is best-effort */ }
    }

    private void OpenSettings()
    {
        bool prevInstallLoc = _settings.InstallToApplications;
        using var dlg = new SettingsDialog(_settings, _catalog.Self, CurrentVersion());
        dlg.ApplyTheme(Theme.IsDark(_settings.Appearance));
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings.Save();
            ApplyTheme();
            if (_settings.InstallToApplications != prevInstallLoc)
                ReconcileInstallLocation(_settings.InstallToApplications);
            _ = RefreshAllAsync();
        }
    }

    /// <summary>When the install-location setting changes, offer to add/remove shortcuts for ALL
    /// installed apps so they don't end up split. (Windows: the exe never moves — only its shortcuts.)</summary>
    private void ReconcileInstallLocation(bool toApplications)
    {
        var affected = _rows.Where(r =>
            InstallManager.Shared.InstalledVersion(r.App.Id) != null &&
            InstallManager.Shared.NeedsShortcutSync(r.App.Id, toApplications)).ToList();
        if (affected.Count == 0) return;

        var verb = toApplications
            ? "add Start menu & Desktop shortcuts for"
            : "remove the Start menu & Desktop shortcuts for";
        if (MessageBox.Show(this,
                $"Do you want to {verb} your {affected.Count} installed app(s)?",
                "Install location changed", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        foreach (var r in affected)
            InstallManager.Shared.SyncShortcuts(r.App, toApplications);
    }

    // MARK: - Helpers

    private static string CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private static RowStatus ComputeStatus(string? installed, string latest, bool hasAsset)
    {
        if (!hasAsset) return RowStatus.MissingAsset;
        if (installed == null) return RowStatus.NotInstalled;
        return Versions.Equal(installed, latest) ? RowStatus.UpToDate : RowStatus.UpdateAvailable;
    }

    private void ApplyTheme()
    {
        bool dark = Theme.IsDark(_settings.Appearance);
        BackColor = Theme.Bg(dark);
        ForeColor = Theme.Fg(dark);
        _title.ForeColor = Theme.Fg(dark);
        _subtitle.ForeColor = Theme.Sub(dark);
        _credit.ForeColor = Theme.Sub(dark);
        foreach (var row in _rows) row.ApplyTheme(dark);
        if (IsHandleCreated) Theme.ApplyTitleBar(this, dark);
    }
}
