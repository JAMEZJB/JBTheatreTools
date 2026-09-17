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
    private readonly Button _downloadAll = new();
    private readonly Button _viewToggle = new();
    private readonly Button _settingsBtn = new();
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly Label _credit = new();

    // Tray support for the "keep running" close behaviour.
    private readonly NotifyIcon _tray = new();
    private bool _reallyQuit;

    // Custom grip drag-to-reorder: the row being dragged and the floating card that follows the cursor.
    private AppRowControl? _dragRow;
    private DragCardForm? _dragCard;

    // Category sections: one header control per group (Pinned + each category), and the set of app ids
    // currently eligible to show (installed or reachable). Displayed visibility = eligible && !hidden &&
    // section not collapsed — computed centrally in ReindexList.
    private readonly Dictionary<string, SectionHeaderControl> _headers = new();
    private readonly HashSet<string> _eligible = new();
    private const string PinnedKey = "pinned";
    private const string Uncategorised = "Other";

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
        // Double-buffer the panel so the live drag-reorder reflow doesn't flicker.
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(_list, true);
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
        _title.Font = Theme.Ui(Theme.PtTitle, semibold: true);   // t-title 15px/600
        _title.AutoSize = true;
        _title.Location = new Point(14, 10);
        _title.UseMnemonic = false;   // render a literal "&" (none here today, but future-proof)

        // UseMnemonic=false so the literal "&" shows (default true eats "& " as an Alt-mnemonic prefix).
        _subtitle.Text = "Install, update & launch the JB tool suite";
        _subtitle.Font = Theme.Ui(Theme.PtSmall);   // t-small 12px
        _subtitle.AutoSize = true;
        _subtitle.Location = new Point(14, 36);
        _subtitle.UseMnemonic = false;

        _refresh.Text = "Refresh";
        _refresh.AutoSize = true;
        _refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _refresh.Click += async (_, _) => await RefreshAllAsync();

        // A single "Download All ▾" dropdown (parity with the macOS header menu): Update all (N),
        // Download all apps, and — when any app ships a Full edition — Download all incl. Full editions.
        _downloadAll.Text = "Download All  ▾";
        _downloadAll.AutoSize = true;
        _downloadAll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _downloadAll.Visible = false;
        _downloadAll.Click += (_, _) => ShowDownloadAllMenu();

        _viewToggle.AutoSize = true;
        _viewToggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _viewToggle.Click += (_, _) => ToggleViewMode();

        _settingsBtn.Text = "Settings";
        _settingsBtn.AutoSize = true;
        _settingsBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _settingsBtn.Click += (_, _) => OpenSettings();

        header.Controls.AddRange(new Control[] { _title, _subtitle, _downloadAll, _viewToggle, _refresh, _settingsBtn });
        header.Resize += (_, _) =>
        {
            _settingsBtn.Location = new Point(header.Width - _settingsBtn.Width - 14, 16);
            _refresh.Location = new Point(_settingsBtn.Left - _refresh.Width - 8, 16);
            _viewToggle.Location = new Point(_refresh.Left - _viewToggle.Width - 8, 16);
            _downloadAll.Location = new Point(_viewToggle.Left - _downloadAll.Width - 8, 16);
        };
        return header;
    }

    private Control BuildUpdateBanner()
    {
        _updateBanner.Dock = DockStyle.Fill;
        _updateBanner.Height = 44;
        _updateBanner.Visible = false;   // colours come from ApplyTheme (the accent wash is theme-dependent)

        _updateBannerText.AutoSize = true;
        _updateBannerText.UseMnemonic = false; // render the literal "&" (e.g. "quit & replace") — default true eats it
        _updateBannerText.Location = new Point(14, 13);
        _updateBannerText.Font = Theme.Ui(Theme.PtTitle, semibold: true);   // t-status 15px/600
        _updateBanner.Paint += (_, e) => BannerEdge(e, _updateBanner, Theme.Accent);

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

    /// <summary>Closes a banner the way the kit's `.banner` does: a 40% hairline of the semantic colour
    /// along the bottom edge (parity with the macOS `bannerTint`), never a shadow.</summary>
    private static void BannerEdge(PaintEventArgs e, Control banner, Color tint)
    {
        using var pen = new Pen(Theme.Blend(tint, banner.BackColor, 0.40));
        e.Graphics.DrawLine(pen, 0, banner.Height - 1, banner.Width, banner.Height - 1);
    }

    private Control BuildTokenBanner()
    {
        _tokenBanner.Dock = DockStyle.Fill;
        _tokenBanner.Height = 44;
        // v2 rule 25: this notice is a WARN — orange, never yellow. Colours come from ApplyTheme.
        _tokenBanner.Visible = false;

        _tokenBannerText.Text = NoCredsMsg;
        _tokenBannerText.AutoSize = true;
        _tokenBannerText.Location = new Point(14, 13);
        _tokenBannerText.Font = Theme.Ui(Theme.PtTitle, semibold: true);   // t-status 15px/600
        _tokenBanner.Paint += (_, e) => BannerEdge(e, _tokenBanner, Theme.Warn);

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
        // Rule 28 — the house credit line, carrying the launcher's own version.
        _credit.Text = $"Created by: James Breedon & Claude Code · v{CurrentVersion()}";
        _credit.Font = Theme.Ui(Theme.PtSmall);   // t-small 12px
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
            row.ReorderStart += OnReorderStart;
            row.ReorderMove += OnReorderMove;
            row.ReorderEnd += OnReorderEnd;
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
            // Installed apps are eligible immediately (launchable pre-refresh); not-installed rows stay hidden
            // until a refresh confirms the token can reach them, so inaccessible apps never flash in.
            if (installed != null) _eligible.Add(app.Id);
            row.Visible = false;   // ReindexList sets displayed visibility from eligibility + collapse
            _rows.Add(row);
            _list.Controls.Add(row);
        }
        _list.Resize += (_, _) =>
        {
            // Section headers are full-width in BOTH modes (in grid a full-width header forces a wrap, so it
            // reads as a section break); grid tiles themselves are fixed-size and don't stretch.
            foreach (var h in _headers.Values) h.Width = _list.ClientSize.Width - 30;
            if (_settings.ViewMode == "grid") return;
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
        // Reflect pin state + refresh installed-eligibility (may have changed in Settings — e.g. "Show all
        // hidden"). Displayed visibility is computed centrally in ReindexList (eligibility + collapse).
        foreach (var r in _rows)
        {
            r.SetPinned(IsPinned(r.App.Id));
            if (InstallManager.Shared.InstalledVersion(InstallKey(r.App)) != null) _eligible.Add(r.App.Id);
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
    // F10: shortcut name comes from the CATALOG (trusted), never the downloaded exe's ProductName.
    private static string ShortcutNameFor(AppRowControl row, string? variantId)
        => row.App.Name + row.App.VariantSuffix(variantId);

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
        var key = GroupKeyOf(row);   // same section (Pinned, or one category) only
        if (up) { for (int k = i - 1; k >= 0; k--) if (_rows[k].Visible && GroupKeyOf(_rows[k]) == key) return k; }
        else { for (int k = i + 1; k < _rows.Count; k++) if (_rows[k].Visible && GroupKeyOf(_rows[k]) == key) return k; }
        return -1;
    }

    private void PersistOrder()
    {
        _settings.AppOrder = _rows.Select(r => r.App.Id).ToList();
        _settings.Save();
    }

    // ── Category sections (Pinned floats to the top; the rest group under their catalog category) ──────

    private string CategoryOf(CatalogApp app)
    {
        var c = app.Category?.Trim() ?? "";
        return c.Length == 0 ? Uncategorised : c;
    }

    private bool IsCollapsed(string key) => _settings.CollapsedCategories.Contains(key);

    /// <summary>A row is eligible to show (installed or reachable) and not user-hidden — "would show",
    /// ignoring collapse. Drives the no-access / empty-state notices and the download-all button.</summary>
    private bool WouldShow(AppRowControl row) => _eligible.Contains(row.App.Id) && !IsHidden(row.App.Id);

    /// <summary>The effective category order: the user's saved order first, then the catalog's order for any
    /// not covered, then leftover categories alphabetically.</summary>
    private List<string> EffectiveCategoryOrder(IEnumerable<AppRowControl> mainRows)
    {
        var order = new List<string>();
        void Add(string c) { if (!order.Contains(c)) order.Add(c); }
        foreach (var c in _settings.CategoryOrder) Add(c);
        foreach (var c in _catalog.Categories ?? new()) Add(c);
        foreach (var c in mainRows.Select(r => CategoryOf(r.App)).Distinct().OrderBy(c => c)) Add(c);
        return order;
    }

    /// <summary>The render groups in order: Pinned first (if any), then each non-empty category. Rows are in
    /// <c>_rows</c> order, restricted to "would show" rows.</summary>
    private List<(string key, string title, List<AppRowControl> rows)> DisplayGroups()
    {
        var groups = new List<(string, string, List<AppRowControl>)>();
        var shown = _rows.Where(WouldShow).ToList();
        var pinned = shown.Where(r => IsPinned(r.App.Id)).ToList();
        if (pinned.Count > 0) groups.Add((PinnedKey, "Pinned", pinned));
        var main = shown.Where(r => !IsPinned(r.App.Id)).ToList();
        foreach (var cat in EffectiveCategoryOrder(main))
        {
            var rows = main.Where(r => CategoryOf(r.App) == cat).ToList();
            if (rows.Count > 0) groups.Add((cat, cat, rows));
        }
        return groups;
    }

    /// <summary>Lays out the FlowLayoutPanel: for each group, its header then (unless collapsed) its rows;
    /// collapsed/hidden rows and unused headers are hidden and parked at the end. <c>_rows</c> stays the
    /// master order, and each row's displayed visibility is (would-show AND section not collapsed).</summary>
    private void ReindexList()
    {
        _list.SuspendLayout();
        var groups = DisplayGroups();
        int firstCat = groups.FindIndex(x => x.key != PinnedKey);
        int lastCat = groups.Count - 1;
        var used = new HashSet<string>();
        int idx = 0;

        for (int gi = 0; gi < groups.Count; gi++)
        {
            var (key, title, rows) = groups[gi];
            bool collapsed = IsCollapsed(key);
            bool pinnedGroup = key == PinnedKey;
            var header = HeaderFor(key);
            header.Configure(key, title, rows.Count, collapsed, pinnedGroup, Theme.IsDark(_settings.Appearance));
            header.SetMoveEnabled(!pinnedGroup && gi > firstCat, !pinnedGroup && gi < lastCat);
            header.Width = _list.ClientSize.Width - 30;
            header.Visible = true;
            _list.Controls.SetChildIndex(header, idx++);
            used.Add(key);

            foreach (var r in rows)
            {
                r.Visible = !collapsed;
                if (!collapsed) _list.Controls.SetChildIndex(r, idx++);
            }
        }

        // Not-shown rows (hidden, collapsed, not eligible) take no space; park them + any unused header.
        foreach (var r in _rows.Where(r => !WouldShow(r) || IsCollapsed(GroupKeyOf(r))))
        {
            r.Visible = false;
            _list.Controls.SetChildIndex(r, idx++);
        }
        foreach (var h in _headers.Values.Where(h => !used.Contains(h.Key)))
        {
            h.Visible = false;
            _list.Controls.SetChildIndex(h, idx++);
        }
        _list.ResumeLayout();
    }

    private string GroupKeyOf(AppRowControl row) => IsPinned(row.App.Id) ? PinnedKey : CategoryOf(row.App);

    /// <summary>The header control for a group key, created on demand and wired to collapse/move handlers.</summary>
    private SectionHeaderControl HeaderFor(string key)
    {
        if (_headers.TryGetValue(key, out var h)) return h;
        h = new SectionHeaderControl();
        h.CollapseToggleRequested += ToggleCollapsed;
        h.MoveSectionRequested += MoveSection;
        _headers[key] = h;
        _list.Controls.Add(h);
        return h;
    }

    private void ToggleCollapsed(string key)
    {
        if (!_settings.CollapsedCategories.Remove(key)) _settings.CollapsedCategories.Add(key);
        _settings.Save();
        ReindexList();
        Log.Write($"{(IsCollapsed(key) ? "collapsed" : "expanded")} section {key}");
    }

    private void MoveSection(string key, bool up)
    {
        if (key == PinnedKey) return;
        var order = DisplayGroups().Select(g => g.key).Where(k => k != PinnedKey).ToList();
        int i = order.IndexOf(key);
        int j = up ? i - 1 : i + 1;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        _settings.CategoryOrder = order;
        _settings.Save();
        ReindexList();
        Log.Write("reordered category sections");
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
        RefreshDownloadAllButton();
        Log.Write($"{(IsPinned(id) ? "pinned" : "unpinned")} {id}");
    }

    private void HideRow(AppRowControl row)
    {
        if (!_settings.HiddenApps.Contains(row.App.Id)) _settings.HiddenApps.Add(row.App.Id);
        _settings.Save();
        ReindexList();   // recomputes displayed visibility from eligibility + hidden + collapse
        Log.Write($"hid {row.App.Id}");
    }

    // ── Custom grip drag-to-reorder (web-style): a floating card follows the cursor, the rows rearrange
    // live underneath, and the order persists on mouse-up. Mirrors the macOS build's feel. ──────────────

    /// <summary>Drag started on a row's grip: mark it as the dashed placeholder and spawn the floating card.</summary>
    private void OnReorderStart(AppRowControl row)
    {
        _dragRow = row;
        row.SetDragPlaceholder(true);
        _dragCard?.Dispose();
        _dragCard = new DragCardForm(row.CurrentIcon, row.DisplayName, Theme.IsDark(_settings.Appearance));
        _dragCard.MoveTo(Cursor.Position);
        _dragCard.Show();
    }

    /// <summary>Cursor moved mid-drag: reposition the floating card and, when the cursor crosses into a new
    /// slot within the same pin group, reorder the rows live (no thrash — only when the index changes).</summary>
    private void OnReorderMove(Point screenPt)
    {
        if (_dragRow == null) return;
        _dragCard?.MoveTo(screenPt);

        var pt = _list.PointToClient(screenPt);
        var target = _rows.FirstOrDefault(r => r.Visible && r != _dragRow && r.Bounds.Contains(pt));
        if (target == null || GroupKeyOf(target) != GroupKeyOf(_dragRow)) return;   // reorder within a section only

        bool after = _settings.ViewMode == "grid"
            ? pt.X > target.Left + target.Width / 2
            : pt.Y > target.Top + target.Height / 2;
        int di = _rows.IndexOf(_dragRow);
        int ti = _rows.IndexOf(target);
        int insert = after ? ti + 1 : ti;
        if (di < insert) insert--;          // removing the dragged row first shifts later indices down
        if (insert == di) return;           // already in place → no reflow (avoids flicker/thrash)
        _rows.RemoveAt(di);
        _rows.Insert(insert, _dragRow);
        ReindexList();
    }

    /// <summary>Drag released: clear the placeholder, remove the floating card, and persist the new order.</summary>
    private void OnReorderEnd()
    {
        if (_dragRow == null) return;
        _dragRow.SetDragPlaceholder(false);
        _dragRow = null;
        _dragCard?.Close();
        _dragCard?.Dispose();
        _dragCard = null;
        PersistOrder();
        Log.Write("reordered by drag");
    }

    /// <summary>Per-app shortcut toggle from the row menu (desktop: true=Desktop, false=Start Menu).</summary>
    private void ToggleShortcut(AppRowControl row, bool desktop, bool add)
    {
        if (desktop) InstallManager.Shared.SetDesktopShortcut(InstallKey(row.App), ShortcutName(row), add);
        else InstallManager.Shared.SetStartMenuShortcut(InstallKey(row.App), ShortcutName(row), add);
        Log.Write($"{(add ? "added" : "removed")} {(desktop ? "desktop" : "start-menu")} shortcut for {row.App.Id}");
    }

    // --- Actions ---

    private enum FetchKind { Releases, NoAccess, Unauthorized, NoRelease, Error }
    private sealed record FetchResult(AppRowControl Row, FetchKind Kind, List<ReleaseInfo>? Releases, string? ErrorMsg);

    private async Task RefreshAllAsync()
    {
        // These handlers run as `async void` (Shown / Refresh.Click), so an exception that escapes here —
        // e.g. AuthClient.Active → Credential Manager P/Invoke throwing on a corrupt blob or a locked-down
        // image — would be an unobserved async-void fault that kills the launcher at startup. Guard the
        // whole body: on failure, log and show the no-credentials notice instead of crashing (audit F15).
        GitHubClient? active;
        try
        {
            active = AuthClient.Active(_settings, _catalog.DownloadServer);
        }
        catch (Exception ex)
        {
            Log.Write($"refresh: credential resolution failed: {ex.Message}");
            ResetRowsNoToken(); ShowNotice(NoCredsMsg); RefreshDownloadAllButton();
            return;
        }
        // No credentials (e.g. just removed in Settings): reset every row to its installed/unknown state
        // and clear stale latest/releases, so no row keeps a live — but silently no-op — Install button.
        if (active == null) { ResetRowsNoToken(); ShowNotice(NoCredsMsg); RefreshDownloadAllButton(); return; }
        ShowNotice(null);

        _refresh.Enabled = false;
        bool unauthorized = false;
        try
        {
            using var client = active;

            // Mark every row "checking" up front, then fetch all apps CONCURRENTLY (HttpClient handles
            // parallel requests; the OS caps connections per host, so this self-throttles). The old
            // sequential loop did ~one network round-trip × 20 in series — seconds of lag on boot.
            foreach (var row in _rows)
            {
                row.SetChecking();
                row.SetResolvedName(InstallManager.Shared.InstalledDisplayName(InstallKey(row.App)));
            }

            var results = await Task.WhenAll(_rows.Select(async row =>
            {
                try
                {
                    var all = await client.ReleasesAsync(row.App.Owner, row.App.Repo);
                    return new FetchResult(row, FetchKind.Releases, all, null);
                }
                catch (GitHubException ge) when (ge.Kind == GitHubErrorKind.NotAccessible)
                    { return new FetchResult(row, FetchKind.NoAccess, null, null); }
                catch (GitHubException ge) when (ge.Kind == GitHubErrorKind.Unauthorized)
                    { return new FetchResult(row, FetchKind.Unauthorized, null, null); }
                catch (GitHubException ge) when (ge.Kind == GitHubErrorKind.NoRelease)
                    { return new FetchResult(row, FetchKind.NoRelease, null, null); }
                catch (Exception ex)
                    { return new FetchResult(row, FetchKind.Error, null, ex.Message); }
            }));

            // Apply outcomes on the UI thread (we're back on it after the await).
            foreach (var res in results)
            {
                var row = res.Row;
                var installed = InstallManager.Shared.InstalledVersion(InstallKey(row.App));
                bool accessible;
                switch (res.Kind)
                {
                    case FetchKind.Releases:
                        accessible = true;
                        row.SetReleases(res.Releases!);
                        var latest = Versions.Latest(res.Releases!);
                        if (latest == null) { row.SetState(installed, null, null, RowStatus.NoRelease); }
                        else
                        {
                            var asset = latest.Assets.FirstOrDefault(a => a.Name == row.App.WindowsAsset(SelectedVariant(row.App)));
                            row.SetState(installed, latest.TagName, asset?.Id, ComputeStatus(installed, latest.TagName, asset != null));
                        }
                        break;
                    case FetchKind.Unauthorized: unauthorized = true; accessible = false; break;
                    case FetchKind.NoRelease:
                        accessible = true; row.SetReleases(new List<ReleaseInfo>());
                        row.SetState(installed, null, null, RowStatus.NoRelease); break;
                    case FetchKind.Error:
                        accessible = true; row.SetState(installed, null, null, RowStatus.Error);
                        Log.Write($"refresh {row.App.Id} error: {res.ErrorMsg}"); break;
                    default: accessible = false; break;   // NoAccess: token can't see this repo → hidden
                }
                // Eligible iff installed locally OR the token reached the repo. ReindexList turns that (minus
                // hidden/collapsed) into displayed visibility.
                if (installed != null || accessible) _eligible.Add(row.App.Id); else _eligible.Remove(row.App.Id);
            }
            ReindexList();   // re-group and set displayed visibility now that eligibility settled

            // One clear notice for the whole-credential states instead of rows full of errors.
            if (unauthorized) { ShowNotice(BadCredsMsg); Log.Write($"refresh: credentials rejected ({_settings.AuthMode} mode)"); }
            else if (!_rows.Any(WouldShow)) ShowNotice(NoAccessMsg);
            else ShowNotice(null);
        }
        finally
        {
            _refresh.Enabled = true;
            RefreshDownloadAllButton();
        }
    }

    /// <summary>Shows the "Download All ▾" dropdown only when there's real work to do — some app (as
    /// shown) isn't installed or has an update — so it disappears once everything is downloaded and up to
    /// date. The per-item Update-all count lives inside the menu, built fresh on each open.</summary>
    private void RefreshDownloadAllButton()
    {
        bool show = _rows.Any(WouldShow)
                    && AuthClient.HasCredentials(_settings, _catalog.DownloadServer)
                    && HasAnyToDownload(false);
        _downloadAll.Visible = show;
        _downloadAll.Location = new Point(_viewToggle.Left - _downloadAll.Width - 8, 16);
    }

    /// <summary>True if any app has a slot to fetch — a not-installed or updatable default slot (and Full
    /// slots when <paramref name="includeFull"/>). Mirrors the macOS hasAnyToDownload.</summary>
    private bool HasAnyToDownload(bool includeFull) => _rows.Any(r => SlotsToDownload(r, includeFull).Any());

    /// <summary>Builds and drops the Download All menu below the button: Update all (N) when any update is
    /// pending, Download all apps, and — when any app ships a Full edition — Download all incl. Full.</summary>
    private void ShowDownloadAllMenu()
    {
        var menu = new ContextMenuStrip();
        int n = _rows.Count(r => r.Status == RowStatus.UpdateAvailable);
        if (n > 0)
        {
            var upd = new ToolStripMenuItem($"Update all ({n})");
            upd.Click += async (_, _) => await UpdateAllAsync();
            menu.Items.Add(upd);
            menu.Items.Add(new ToolStripSeparator());
        }
        var all = new ToolStripMenuItem("Download all apps");
        all.Click += async (_, _) => await DownloadAllAsync(false);
        menu.Items.Add(all);
        if (HasFullVariants)
        {
            var full = new ToolStripMenuItem("Download all — including Full editions");
            full.Click += async (_, _) => await DownloadAllAsync(true);
            menu.Items.Add(full);
        }
        menu.Show(_downloadAll, new Point(0, _downloadAll.Height));
    }

    private bool HasFullVariants => _rows.Any(r => (r.App.Variants?.Count ?? 0) > 1);

    /// <summary>Which variant slots of an app need fetching now: the default slot (and Full slots when
    /// includeFull) whose asset exists in the latest release and isn't already installed at the latest
    /// version. Mirrors the macOS slotsToDownload.</summary>
    private IEnumerable<string?> SlotsToDownload(AppRowControl row, bool includeFull)
    {
        var latest = Versions.Latest(row.Releases);
        if (latest == null) yield break;
        var variants = new List<string?> { row.App.HasVariants ? row.App.Variants?.FirstOrDefault()?.Id : null };
        if (includeFull && row.App.Variants != null)
            variants.AddRange(row.App.Variants.Skip(1).Select(v => (string?)v.Id));
        foreach (var vid in variants)
        {
            var name = row.App.WindowsAsset(vid);
            if (name == null || !latest.Assets.Any(a => a.Name == name)) continue;   // no asset for this arch → skip
            var installed = InstallManager.Shared.InstalledVersion(row.App.InstallKey(vid));
            if (installed == null || Versions.IsNewer(latest.TagName, installed)) yield return vid;
        }
    }

    /// <summary>Download All: install/update every app. <paramref name="includeFull"/> also fetches Full
    /// editions into their own slots (Standard and Full side by side). Skips anything current or with no
    /// asset for this arch.</summary>
    private async Task DownloadAllAsync(bool includeFull)
    {
        _downloadAll.Enabled = false;
        try
        {
            foreach (var row in _rows.ToList())
                foreach (var vid in SlotsToDownload(row, includeFull).ToList())
                    await InstallSlotAsync(row, null, vid);
            Log.Write($"download all{(includeFull ? " (incl. Full)" : "")} complete");
        }
        finally { _downloadAll.Enabled = true; RefreshDownloadAllButton(); }
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
            if (installed != null) _eligible.Add(row.App.Id); else _eligible.Remove(row.App.Id);
        }
        ReindexList();
    }

    private async Task UpdateAllAsync()
    {
        var targets = _rows.Where(r => r.Status == RowStatus.UpdateAvailable).ToList();
        if (targets.Count == 0) return;
        Log.Write($"update all: {targets.Count} app(s)");
        _downloadAll.Enabled = false;
        try { foreach (var r in targets) await InstallVersionAsync(r, null); }
        finally { _downloadAll.Enabled = true; RefreshDownloadAllButton(); }
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

    /// <summary>Row-driven install (event target): installs the row's selected variant at <paramref
    /// name="tag"/> (null = latest).</summary>
    private Task InstallVersionAsync(AppRowControl row, string? tag) => InstallSlotAsync(row, tag, null);

    /// <summary>Installs one app slot. <paramref name="variantOverride"/> installs a specific variant slot
    /// regardless of the row's on-screen selection (used by Download All to fetch Standard and/or Full);
    /// null = the row's selected variant. The row's displayed state is only updated for the SELECTED
    /// variant, so a background Full-edition install doesn't hijack the row's display.</summary>
    private async Task InstallSlotAsync(AppRowControl row, string? tag, string? variantOverride)
    {
        var active = AuthClient.Active(_settings, _catalog.DownloadServer);
        if (active == null) return;
        var variantId = variantOverride ?? SelectedVariant(row.App);
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
            // Only reflect the install in the row when it's the variant currently shown — a Download All
            // that fetches a non-selected Full edition into its own slot mustn't hijack the row's display.
            if (variantId == SelectedVariant(row.App))
            {
                var latest = row.Latest ?? rel.TagName;
                row.SetState(rel.TagName, row.Latest, row.LatestAssetId, ComputeStatus(rel.TagName, latest, true));
                row.SetResolvedName(InstallManager.Shared.InstalledDisplayName(row.App.InstallKey(variantId)));
            }
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
            RefreshDownloadAllButton();
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
            RefreshDownloadAllButton();
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
        Theme.SetCurrent(dark);   // publish the theme first: the token accessors below read it
        BackColor = Theme.Bg(dark);
        ForeColor = Theme.Fg(dark);
        _title.ForeColor = Theme.Fg(dark);
        _subtitle.ForeColor = Theme.Sub(dark);
        _credit.ForeColor = Theme.Muted(dark);
        // Banners follow the kit's `.banner`: a 10% wash of the semantic colour, text in that colour.
        // The launcher-update notice is the accent; the credentials notice is WARN (orange, never yellow).
        _updateBanner.BackColor = Theme.BannerBack(Theme.Accent, dark);
        _updateBannerText.ForeColor = Theme.Accent;
        _tokenBanner.BackColor = Theme.BannerBack(Theme.Warn, dark);
        _tokenBannerText.ForeColor = Theme.Warn;
        foreach (var row in _rows) row.ApplyTheme(dark);
        foreach (var header in _headers.Values) header.ApplyTheme(dark);
        if (IsHandleCreated) Theme.ApplyTitleBar(this, dark);
    }
}
