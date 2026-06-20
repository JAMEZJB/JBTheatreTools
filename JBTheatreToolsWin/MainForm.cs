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
        Shown += async (_, _) =>
        {
            _tokenBanner.Visible = TokenStore.Load() == null;
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
        _updateBanner.BackColor = Color.FromArgb(219, 234, 254);
        _updateBanner.Visible = false;

        _updateBannerText.AutoSize = true;
        _updateBannerText.Location = new Point(14, 13);
        _updateBannerText.ForeColor = Color.FromArgb(30, 64, 120);

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

        _tokenBannerText.Text = "Add a GitHub token to enable downloads  —  Settings → paste a fine-grained PAT.";
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
            row.SetState(InstallManager.Shared.InstalledVersion(app.Id), null, null, RowStatus.Unknown);
            row.SetResolvedName(InstallManager.Shared.InstalledDisplayName(app.Id));
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
        _tokenBanner.Visible = token == null;
        if (token == null) return;

        _refresh.Enabled = false;
        try
        {
            using var client = new GitHubClient(token);
            foreach (var row in _rows)
            {
                row.SetChecking();
                var installed = InstallManager.Shared.InstalledVersion(row.App.Id);
                row.SetResolvedName(InstallManager.Shared.InstalledDisplayName(row.App.Id));
                try
                {
                    var all = await client.ReleasesAsync(row.App.Owner, row.App.Repo);
                    row.SetReleases(all);
                    var latest = all.FirstOrDefault(r => !r.Prerelease) ?? all.FirstOrDefault();
                    if (latest == null)
                    {
                        row.SetState(installed, null, null, RowStatus.NoRelease);
                        continue;
                    }
                    var asset = latest.Assets.FirstOrDefault(a => a.Name == row.App.WindowsAssetName);
                    row.SetState(installed, latest.TagName, asset?.Id, ComputeStatus(installed, latest.TagName, asset != null));
                }
                catch (GitHubException ge) when (ge.Kind == GitHubErrorKind.NoRelease)
                {
                    row.SetReleases(new List<ReleaseInfo>());
                    row.SetState(installed, null, null, RowStatus.NoRelease);
                }
                catch
                {
                    row.SetState(installed, null, null, RowStatus.Error);
                }
            }
        }
        finally
        {
            _refresh.Enabled = true;
        }
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
            InstallManager.Shared.Install(row.App, rel.TagName, cache, assetName);
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
        using var dlg = new SettingsDialog(_settings, _catalog.Self, CurrentVersion());
        dlg.ApplyTheme(Theme.IsDark(_settings.Appearance));
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings.Save();
            ApplyTheme();
            _ = RefreshAllAsync();
        }
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
