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
    private readonly Button _updateAll = new();
    private readonly Button _viewToggle = new();
    private readonly Button _settingsBtn = new();
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly Label _credit = new();

    // Tray support for the "keep running" close behaviour.
    private readonly NotifyIcon _tray = new();
    private bool _reallyQuit;

    // Notice-banner messages (the banner doubles as the no-creds / bad-creds / no-access notice).
    // Worded per auth mode: "token" = GitHub PAT, "server" = download-server relay + suite passphrase.
    private string NoCredsMsg => _settings.AuthMode == "server"
        ? "Enter the suite passphrase to enable downloads  —  Settings → Download access (ask James)."
        : "Add a GitHub token to enable downloads  —  Settings → paste a fine-grained PAT.";
    private string BadCredsMsg => _settings.AuthMode == "server"
        ? "The download server rejected the passphrase  —  check it in Settings."
        : "Your GitHub token is invalid or expired  —  open Settings to paste a new one.";
    private string NoAccessMsg => _settings.AuthMode == "server"
        ? "No apps are reachable right now  —  check the passphrase in Settings, or ask James."
        : "This token can’t access any apps  —  check its repository access, or ask James.";

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
        _list.AllowDrop = true;   // drag-to-reorder rows
        _list.DragOver += (_, e) => e.Effect = DragDropEffects.Move;
        _list.DragDrop += OnListDragDrop;
        root.Controls.Add(_list, 0, 3);

        root.Controls.Add(BuildFooter(), 0, 4);

        Controls.Add(root);

        LoadCatalog();
        ApplyTheme();
        SetupTray();
        FormClosing += OnFormClosing;
        Shown += async (_, _) =>
        {
            Log.Write($"launched v{CurrentVersion()}");
            ShowNotice(AuthClient.HasCredentials(_settings, _catalog.DownloadServer) ? null : NoCredsMsg);
            if (_settings.UpdateMode == "everyLaunch")
            {
                await RefreshAllAsync();
                await CheckLauncherUpdateAsync();
            }
        };
    }

    // --- UI construction ---

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, Height = 64, Padding = new Padding(14, 10, 14, 10) };

        _title.Text = "JB Theatre Tools";
        _title.Font = new Font(Font.FontFamily, 13f, FontStyle.Bold);
        _title.AutoSize = true;
        _title.Location = new Point(14, 10);
        _title.UseMnemonic = false;   // render a literal "&" (none here today, but future-proof)

        // UseMnemonic=false so the literal "&" shows (default true eats "& " as an Alt-mnemonic prefix).
        _subtitle.Text = "Install, update & launch the JB tool suite";
        _subtitle.AutoSize = true;
        _subtitle.Location = new Point(14, 36);
        _subtitle.UseMnemonic = false;

        _refresh.Text = "Refresh";
        _refresh.AutoSize = true;
        _refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _refresh.Click += async (_, _) => await RefreshAllAsync();

        _updateAll.Text = "Update All";
        _updateAll.AutoSize = true;
        _updateAll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _updateAll.Visible = false;
        _updateAll.Click += async (_, _) => await UpdateAllAsync();

        _viewToggle.AutoSize = true;
        _viewToggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _viewToggle.Click += (_, _) => ToggleViewMode();

        _settingsBtn.Text = "Settings";
        _settingsBtn.AutoSize = true;
        _settingsBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _settingsBtn.Click += (_, _) => OpenSettings();

        header.Controls.AddRange(new Control[] { _title, _subtitle, _updateAll, _viewToggle, _refresh, _settingsBtn });
        header.Resize += (_, _) =>
        {
            _settingsBtn.Location = new Point(header.Width - _settingsBtn.Width - 14, 16);
            _refresh.Location = new Point(_settingsBtn.Left - _refresh.Width - 8, 16);
            _viewToggle.Location = new Point(_refresh.Left - _viewToggle.Width - 8, 16);
            _updateAll.Location = new Point(_viewToggle.Left - _updateAll.Width - 8, 16);
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
        _updateBannerText.UseMnemonic = false; // render the literal "&" (e.g. "quit & replace") — default true eats it
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
                var dest = await LauncherUpdate.DownloadAndRevealAsync(_catalog.Self, AuthClient.SelfUpdate(_settings, _catalog.DownloadServer));
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

        _tokenBannerText.Text = NoCredsMsg;
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
        _credit.Text = $"Created by: James Breedon & Claude Code  ·  v{CurrentVersion()}";
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

        InstallManager.Shared.MigrateVariantSlots(_catalog.Apps);   // v1.15.0 → per-variant install slots
        _list.Controls.Clear();
        _rows.Clear();
        foreach (var app in _catalog.Apps)
        {
            var row = new AppRowControl(app) { Width = Math.Max(400, _list.ClientSize.Width - 30) };
            row.InstallRequested += InstallAsync;
            row.InstallVersionRequested += InstallVersionAsync;
            row.UninstallRequested += Uninstall;
            row.LaunchRequested += Launch;
            row.MoveRequested += MoveRow;
            row.CanMove = CanMoveRow;
            row.ShortcutToggleRequested += ToggleShortcut;
            row.HasDesktopShortcut = r => InstallManager.Shared.HasDesktopShortcut(InstallKey(r.App));
            row.HasStartMenuShortcut = r => InstallManager.Shared.HasStartMenuShortcut(InstallKey(r.App));
            row.PinToggleRequested += TogglePin;
            row.HideRequested += HideRow;
            row.IsPinnedQuery = r => IsPinned(r.App.Id);
            row.VariantChangeRequested += (r, vid) => SetVariant(r, vid);
            row.SelectedVariantQuery = r => SelectedVariant(r.App);
            row.SetSelectedVariant(SelectedVariant(app));
            var slot = InstallKey(app);
            var installed = InstallManager.Shared.InstalledVersion(slot);
            row.SetState(installed, null, null, installed != null ? RowStatus.Installed : RowStatus.Unknown);
            row.SetResolvedName(InstallManager.Shared.InstalledDisplayName(slot));
            row.SetPinned(IsPinned(app.Id));
            // Installed apps show immediately (launchable pre-refresh); not-installed rows stay hidden
            // until a refresh confirms the token can reach them, so inaccessible apps never flash in.
            // Hidden apps never show.
            row.Visible = installed != null && !IsHidden(app.Id);
            _rows.Add(row);
            _list.Controls.Add(row);
        }
        _list.Resize += (_, _) =>
        {
            if (_settings.ViewMode == "grid") return;   // grid tiles are fixed-size; don't stretch them
            foreach (var r in _rows) r.Width = _list.ClientSize.Width - 30;
        };
        ApplyRowOrder();
        ApplyViewMode();
    }

    // --- Row ordering (per-machine, persisted in settings.json) ---

    /// <summary>Reorders the rows to the saved order: saved ids first (in saved order), then apps the
    /// saved list doesn't know (added since) in catalog order. Empty saved list = catalog order.</summary>
    private void ApplyRowOrder()
    {
        var ordered = new List<AppRowControl>();
        foreach (var id in _settings.AppOrder)
        {
            var row = _rows.FirstOrDefault(r => r.App.Id == id);
            if (row != null && !ordered.Contains(row)) ordered.Add(row);
        }
        foreach (var row in _rows)
            if (!ordered.Contains(row)) ordered.Add(row);
        _rows.Clear();
        _rows.AddRange(ordered);
        // Reflect pin + hidden state (they may have changed in Settings — e.g. "Show all hidden").
        foreach (var r in _rows)
        {
            r.SetPinned(IsPinned(r.App.Id));
            if (IsHidden(r.App.Id)) r.Visible = false;
            else if (!r.Visible && InstallManager.Shared.InstalledVersion(InstallKey(r.App)) != null) r.Visible = true;
        }
        ReindexList();
    }

    private bool IsPinned(string id) => _settings.PinnedApps.Contains(id);
    private bool IsHidden(string id) => _settings.HiddenApps.Contains(id);

    // ── Variants (apps that ship more than one download, e.g. NDI Standard/Full) ──────────────

    /// <summary>The selected variant id for an app (persisted), defaulting to its first variant.</summary>
    private string? SelectedVariant(CatalogApp app)
    {
        if (!app.HasVariants || app.Variants == null) return null;
        if (_settings.AppVariants.TryGetValue(app.Id, out var v) && app.Variants.Any(x => x.Id == v)) return v;
        return app.Variants.FirstOrDefault()?.Id;
    }

    /// <summary>Changes the selected variant, persists it, and re-resolves the row's asset/status.</summary>
    private void SetVariant(AppRowControl row, string variantId)
    {
        _settings.AppVariants[row.App.Id] = variantId;
        _settings.Save();
        row.SetSelectedVariant(variantId);
        // Switch the row to this variant's install slot: re-read what's installed there, then re-resolve.
        var key = InstallKey(row.App);
        row.SetState(InstallManager.Shared.InstalledVersion(key), row.Latest, row.LatestAssetId, row.Status);
        row.SetResolvedName(InstallManager.Shared.InstalledDisplayName(key));
        RecomputeRow(row);
        Log.Write($"variant for {row.App.Id} → {variantId}");
    }

    /// <summary>Re-derives a row's latest-asset id + status from its cached releases (after a variant change).</summary>
    private void RecomputeRow(AppRowControl row)
    {
        var latest = Versions.Latest(row.Releases);
        if (latest == null)
        {
            // No cached releases yet (pre-refresh): the row just reflects whether the slot is installed.
            row.SetState(row.Installed, null, null, row.Installed != null ? RowStatus.Installed : RowStatus.Unknown);
            return;
        }
        var asset = latest.Assets.FirstOrDefault(a => a.Name == row.App.WindowsAsset(SelectedVariant(row.App)));
        row.SetState(row.Installed, latest.TagName, asset?.Id, ComputeStatus(row.Installed, latest.TagName, asset != null));
    }

    /// <summary>The install-manifest key of the row's SELECTED variant slot. Every variant is its own
    /// slot, so Standard and Full can both be installed; the toggle just picks which slot the row shows.</summary>
    private string InstallKey(CatalogApp app) => app.InstallKey(SelectedVariant(app));

    /// <summary>Shortcut base name for a slot: the installed exe's product name (or the catalog name)
    /// plus the variant suffix (" (Full)") so the two variants' shortcuts don't collide.</summary>
    private static string ShortcutNameFor(AppRowControl row, string? variantId)
    {
        var path = InstallManager.Shared.InstalledPath(row.App.InstallKey(variantId));
        var product = path != null ? InstallManager.TryProductName(path) : null;
        return (product ?? row.App.Name) + row.App.VariantSuffix(variantId);
    }

    private string ShortcutName(AppRowControl row) => ShortcutNameFor(row, SelectedVariant(row.App));

    /// <summary>Every install slot — each app once, or once per variant for variant apps — as (key, shortcut name).</summary>
    private IEnumerable<(string key, string name)> AllSlots()
    {
        foreach (var r in _rows)
        {
            if (r.App.HasVariants && r.App.Variants != null)
                foreach (var v in r.App.Variants) yield return (r.App.InstallKey(v.Id), ShortcutNameFor(r, v.Id));
            else
                yield return (r.App.Id, ShortcutNameFor(r, null));
        }
    }

    // ── View mode (detailed list vs compact icon grid) ───────────────────────────────────────

    private void ToggleViewMode()
    {
        _settings.ViewMode = _settings.ViewMode == "grid" ? "list" : "grid";
        _settings.Save();
        ApplyViewMode();
    }

    /// <summary>Applies the current view mode: list = full-width detailed rows; grid = fixed-size tiles
    /// that wrap into a grid.</summary>
    private void ApplyViewMode()
    {
        bool grid = _settings.ViewMode == "grid";
        _viewToggle.Text = grid ? "List view" : "Grid view";
        _list.SuspendLayout();
        _list.WrapContents = grid;
        _list.FlowDirection = grid ? FlowDirection.LeftToRight : FlowDirection.TopDown;
        foreach (var r in _rows)
        {
            r.SetCompact(grid);
            if (!grid) r.Width = _list.ClientSize.Width - 30;
        }
        _list.ResumeLayout();
        ReindexList();
    }

    /// <summary>True when the row has a same-group VISIBLE neighbour in that direction.</summary>
    private bool CanMoveRow(AppRowControl row, bool up) => GroupNeighbour(row, up) >= 0;

    /// <summary>Moves the row past its nearest same-group neighbour and persists the new order.</summary>
    private void MoveRow(AppRowControl row, bool up)
    {
        int i = _rows.IndexOf(row);
        int j = GroupNeighbour(row, up);
        if (i < 0 || j < 0) return;
        (_rows[i], _rows[j]) = (_rows[j], _rows[i]);
        PersistOrder();
        ReindexList();
        Log.Write($"moved {row.App.Id} {(up ? "up" : "down")}");
    }

    /// <summary>Nearest neighbour in `up`/down that is visible AND in the same pin group.</summary>
    private int GroupNeighbour(AppRowControl row, bool up)
    {
        int i = _rows.IndexOf(row);
        if (i < 0) return -1;
        bool pinned = IsPinned(row.App.Id);
        if (up) { for (int k = i - 1; k >= 0; k--) if (_rows[k].Visible && IsPinned(_rows[k].App.Id) == pinned) return k; }
        else { for (int k = i + 1; k < _rows.Count; k++) if (_rows[k].Visible && IsPinned(_rows[k].App.Id) == pinned) return k; }
        return -1;
    }

    private void PersistOrder()
    {
        _settings.AppOrder = _rows.Select(r => r.App.Id).ToList();
        _settings.Save();
    }

    /// <summary>Sets the FlowLayoutPanel child order: visible PINNED rows first, then visible unpinned,
    /// then the hidden/invisible ones (which take no space). <c>_rows</c> stays the master order.</summary>
    private void ReindexList()
    {
        _list.SuspendLayout();
        int idx = 0;
        foreach (var r in _rows.Where(r => r.Visible && IsPinned(r.App.Id))) _list.Controls.SetChildIndex(r, idx++);
        foreach (var r in _rows.Where(r => r.Visible && !IsPinned(r.App.Id))) _list.Controls.SetChildIndex(r, idx++);
        foreach (var r in _rows.Where(r => !r.Visible)) _list.Controls.SetChildIndex(r, idx++);
        _list.ResumeLayout();
    }

    // --- Pin / hide toggles + drag-to-reorder ---

    private void TogglePin(AppRowControl row)
    {
        var id = row.App.Id;
        if (_settings.PinnedApps.Remove(id)) { }
        else _settings.PinnedApps.Add(id);
        _settings.Save();
        row.SetPinned(IsPinned(id));
        ReindexList();
        RefreshUpdateAllButton();
        Log.Write($"{(IsPinned(id) ? "pinned" : "unpinned")} {id}");
    }

    private void HideRow(AppRowControl row)
    {
        if (!_settings.HiddenApps.Contains(row.App.Id)) _settings.HiddenApps.Add(row.App.Id);
        _settings.Save();
        row.Visible = false;
        ReindexList();
        Log.Write($"hid {row.App.Id}");
    }

    /// <summary>Drop handler for drag-to-reorder: moves the dragged row next to the drop target within
    /// the same pin group, persists, and re-lays out. Cross-group drops are ignored (use Pin/Unpin).</summary>
    private void OnListDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(typeof(AppRowControl)) is not AppRowControl dragged) return;
        var pt = _list.PointToClient(new Point(e.X, e.Y));
        var target = _rows.FirstOrDefault(r => r.Visible && r.Bounds.Contains(pt));
        if (target == null || target == dragged) return;
        if (IsPinned(dragged.App.Id) != IsPinned(target.App.Id)) return;   // don't cross the pin boundary
        // Grid tiles flow left-to-right (wrap), so decide insert side by X; the vertical list uses Y.
        bool after = _settings.ViewMode == "grid"
            ? pt.X > target.Left + target.Width / 2
            : pt.Y > target.Top + target.Height / 2;
        _rows.Remove(dragged);
        int ti = _rows.IndexOf(target);
        _rows.Insert(after ? ti + 1 : ti, dragged);
        PersistOrder();
        ReindexList();
        Log.Write($"dragged {dragged.App.Id} into place");
    }

    /// <summary>Per-app shortcut toggle from the row menu (desktop: true=Desktop, false=Start Menu).</summary>
    private void ToggleShortcut(AppRowControl row, bool desktop, bool add)
    {
        if (desktop) InstallManager.Shared.SetDesktopShortcut(InstallKey(row.App), ShortcutName(row), add);
        else InstallManager.Shared.SetStartMenuShortcut(InstallKey(row.App), ShortcutName(row), add);
        Log.Write($"{(add ? "added" : "removed")} {(desktop ? "desktop" : "start-menu")} shortcut for {row.App.Id}");
    }

    // --- Actions ---

    private async Task RefreshAllAsync()
    {
        var active = AuthClient.Active(_settings, _catalog.DownloadServer);
        // No credentials (e.g. just removed in Settings): reset every row to its installed/unknown state
        // and clear stale latest/releases, so no row keeps a live — but silently no-op — Install button.
        if (active == null) { ResetRowsNoToken(); ShowNotice(NoCredsMsg); RefreshUpdateAllButton(); return; }
        ShowNotice(null);

        _refresh.Enabled = false;
        bool unauthorized = false;
        try
        {
            using var client = active;
            foreach (var row in _rows)
            {
                // Don't force the row visible here — leave it as-is during the check so a not-installed
                // row that turns out inaccessible never flashes into view. We set visibility from the
                // outcome at the end of the iteration.
                row.SetChecking();
                var slot = InstallKey(row.App);
                var installed = InstallManager.Shared.InstalledVersion(slot);
                row.SetResolvedName(InstallManager.Shared.InstalledDisplayName(slot));
                bool accessible = false;
                try
                {
                    var all = await client.ReleasesAsync(row.App.Owner, row.App.Repo);
                    accessible = true;
                    row.SetReleases(all);
                    var latest = Versions.Latest(all);
                    if (latest == null)
                    {
                        row.SetState(installed, null, null, RowStatus.NoRelease);
                    }
                    else
                    {
                        var asset = latest.Assets.FirstOrDefault(a => a.Name == row.App.WindowsAsset(SelectedVariant(row.App)));
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
                catch (Exception ex)
                {
                    accessible = true;   // transient error — show it so the user can Retry
                    row.SetState(installed, null, null, RowStatus.Error);
                    Log.Write($"refresh {row.App.Id} error: {ex.Message}");
                }
                // Visible iff (installed locally OR the token reached the repo) AND not hidden by the user.
                row.Visible = (installed != null || accessible) && !IsHidden(row.App.Id);
            }
            ReindexList();   // re-group pinned-first now that visibility settled

            // One clear notice for the whole-credential states instead of rows full of errors.
            if (unauthorized) { ShowNotice(BadCredsMsg); Log.Write($"refresh: credentials rejected ({_settings.AuthMode} mode)"); }
            else if (_rows.All(r => !r.Visible)) ShowNotice(NoAccessMsg);
            else ShowNotice(null);
        }
        finally
        {
            _refresh.Enabled = true;
            RefreshUpdateAllButton();
        }
    }

    private void RefreshUpdateAllButton()
    {
        int n = _rows.Count(r => r.Status == RowStatus.UpdateAvailable);
        _updateAll.Text = n > 0 ? $"Update All ({n})" : "Update All";
        _updateAll.Visible = n > 0;
        _updateAll.Location = new Point(_refresh.Left - _updateAll.Width - 8, 16);
    }

    /// <summary>Reverts every row to its pre-refresh state (installed → Installed, else Unknown) and
    /// clears any cached latest/releases — used when there's no token, so no row is left showing a live
    /// Install/Update button that would silently do nothing.</summary>
    private void ResetRowsNoToken()
    {
        foreach (var row in _rows)
        {
            var slot = InstallKey(row.App);
            var installed = InstallManager.Shared.InstalledVersion(slot);
            row.SetReleases(new List<ReleaseInfo>());
            row.SetState(installed, null, null, installed != null ? RowStatus.Installed : RowStatus.Unknown);
            row.SetResolvedName(InstallManager.Shared.InstalledDisplayName(slot));
            row.Visible = installed != null && !IsHidden(row.App.Id);
        }
        ReindexList();
    }

    private async Task UpdateAllAsync()
    {
        var targets = _rows.Where(r => r.Status == RowStatus.UpdateAvailable).ToList();
        if (targets.Count == 0) return;
        Log.Write($"update all: {targets.Count} app(s)");
        _updateAll.Enabled = false;
        try { foreach (var r in targets) await InstallVersionAsync(r, null); }
        finally { _updateAll.Enabled = true; RefreshUpdateAllButton(); }
    }

    /// <summary>Shows the notice banner with <paramref name="text"/>, or hides it when null.</summary>
    private void ShowNotice(string? text)
    {
        if (text != null) _tokenBannerText.Text = text;
        _tokenBanner.Visible = text != null;
    }

    // --- Close behaviour (quit vs. keep running in the system tray) ---

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
        var active = AuthClient.Active(_settings, _catalog.DownloadServer);
        if (active == null) return;
        var variantId = SelectedVariant(row.App);
        var assetName = row.App.WindowsAsset(variantId);
        if (assetName == null) return;

        row.SetBusy(true);
        try
        {
            using var client = active;
            var releases = row.Releases.Count > 0
                ? row.Releases
                : await client.ReleasesAsync(row.App.Owner, row.App.Repo);
            var rel = tag != null
                ? releases.FirstOrDefault(r => r.TagName == tag)
                : Versions.Latest(releases);
            if (rel == null) throw new Exception($"Version {tag ?? "latest"} not found.");
            var asset = rel.Assets.FirstOrDefault(a => a.Name == assetName)
                ?? throw new Exception($"No Windows asset in {rel.TagName}.");

            var cache = Path.Combine(InstallManager.Shared.CacheDir, $"{row.App.Id}-{rel.TagName}-{assetName}");
            var progress = new Progress<double>(p => row.SetProgress(p));
            await client.DownloadAssetAsync(row.App.Owner, row.App.Repo, asset.Id, cache, progress);
            var verification = await InstallManager.VerifyDownloadAsync(cache, asset, rel, row.App.Owner, row.App.Repo, client);
            // Strict for current releases: a latest install (tag == null) MUST checksum-verify — every
            // current release ships a correct SHA256SUMS, so a missing/incomplete manifest here is
            // anomalous → abort. Explicit older-tag installs (version picker) stay verify-if-present. A
            // hash MISMATCH always aborts (it throws from VerifyDownloadAsync) regardless of tag.
            if (tag == null && verification != VerifyResult.Verified)
            {
                InstallManager.TryDelete(cache);
                var reason = InstallManager.StrictFailureReason(verification, assetName);
                Log.Write($"install {row.App.Id} {rel.TagName}: BLOCKED (strict) — {reason}");
                throw new Exception($"Couldn't verify the download — {reason}. Install aborted for safety.");
            }
            InstallManager.Shared.Install(row.App, rel.TagName, cache, assetName, _settings.InstallToApplications, variantId);
            InstallManager.TryDelete(cache);   // verified copy is now installed; mirror the macOS zip cleanup
            var latest = row.Latest ?? rel.TagName;
            row.SetState(rel.TagName, row.Latest, row.LatestAssetId, ComputeStatus(rel.TagName, latest, true));
            row.SetResolvedName(InstallManager.Shared.InstalledDisplayName(row.App.InstallKey(variantId)));
            Log.Write(verification switch
            {
                VerifyResult.Verified => $"verified {row.App.Id} {rel.TagName} (signed sha256)",
                VerifyResult.Unsigned => $"install {row.App.Id} {rel.TagName}: older tag — sha256 ok but manifest UNSIGNED",
                VerifyResult.NoManifest => $"install {row.App.Id} {rel.TagName}: unverified older tag (no SHA256SUMS)",
                _ => $"install {row.App.Id} {rel.TagName}: unverified older tag (asset not in SHA256SUMS)",
            });
            Log.Write($"installed {row.App.Id} {rel.TagName}{(_settings.InstallToApplications ? " (+shortcuts)" : "")}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            row.SetState(row.Installed, row.Latest, row.LatestAssetId, RowStatus.Error);
            Log.Write($"install {row.App.Id} FAILED: {ex.Message}");
        }
        finally
        {
            row.SetBusy(false);
            RefreshUpdateAllButton();
        }
    }

    private void Uninstall(AppRowControl row)
    {
        if (MessageBox.Show(this, $"Uninstall {row.DisplayName}?", "Uninstall",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            // Uninstalls the SELECTED variant's slot only (a sibling variant, if installed, stays).
            InstallManager.Shared.Uninstall(InstallKey(row.App));
            var status = row.LatestAssetId != null
                ? RowStatus.NotInstalled
                : (row.Latest == null ? RowStatus.Unknown : RowStatus.MissingAsset);
            row.SetState(null, row.Latest, row.LatestAssetId, status);
            row.SetResolvedName(null);
            Log.Write($"uninstalled {row.App.Id}");
            RefreshUpdateAllButton();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Uninstall failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Log.Write($"uninstall {row.App.Id} FAILED: {ex.Message}");
        }
    }

    private void Launch(AppRowControl row)
    {
        try
        {
            InstallManager.Shared.Launch(InstallKey(row.App));
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
            using var client = AuthClient.SelfUpdate(_settings, _catalog.DownloadServer);   // launcher repo is public; never blocks on creds
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
        catch (Exception ex) { Log.Write($"self-update check failed: {ex.Message}"); }
    }

    private void OpenSettings()
    {
        bool prevInstallLoc = _settings.InstallToApplications;
        using var dlg = new SettingsDialog(_settings, _catalog.Self, CurrentVersion(), _catalog.DownloadServer);
        dlg.ApplyTheme(Theme.IsDark(_settings.Appearance));
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings.Save();
            ApplyTheme();
            ApplyRowOrder();   // covers "Reset App Order" (and is a cheap no-op otherwise)
            if (_settings.InstallToApplications != prevInstallLoc)
                ReconcileInstallLocation(_settings.InstallToApplications);
            _ = RefreshAllAsync();
        }
    }

    /// <summary>When the install-location setting changes, offer to add/remove shortcuts for ALL
    /// installed apps so they don't end up split. (Windows: the exe never moves — only its shortcuts.)</summary>
    private void ReconcileInstallLocation(bool toApplications)
    {
        // Consider every installed slot (both variants of a variant app), not just the selected ones.
        var affected = AllSlots().Where(s =>
            InstallManager.Shared.InstalledVersion(s.key) != null &&
            InstallManager.Shared.NeedsShortcutSync(s.key, toApplications)).ToList();
        if (affected.Count == 0) return;

        var verb = toApplications
            ? "add Start menu & Desktop shortcuts for"
            : "remove the Start menu & Desktop shortcuts for";
        if (MessageBox.Show(this,
                $"Do you want to {verb} your {affected.Count} installed app(s)?",
                "Install location changed", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        foreach (var s in affected)
            InstallManager.Shared.SyncShortcuts(s.key, s.name, toApplications);
    }

    // --- Helpers ---

    private static string CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private static RowStatus ComputeStatus(string? installed, string latest, bool hasAsset)
    {
        if (!hasAsset) return RowStatus.MissingAsset;
        if (installed == null) return RowStatus.NotInstalled;
        // Up-to-date ⇔ the latest release is NOT strictly newer than what's installed. Using the numeric
        // comparator (not string equality) fixes two defects: 1.2 vs 1.2.0 (any differing segment count)
        // no longer reads as a perpetual "Update available", and a republished OLDER release is never
        // offered as an "update" that would silently downgrade.
        return Versions.IsNewer(latest, installed) ? RowStatus.UpdateAvailable : RowStatus.UpToDate;
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
