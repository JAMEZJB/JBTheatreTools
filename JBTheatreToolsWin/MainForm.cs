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
    private readonly Panel _header = new();
    private readonly Panel _footer = new();
    private readonly Button _updateBtn = new();
    private readonly Button _tokenBtn = new();

    // DPI: every pixel number in this form is a 96-DPI design value scaled through S() at the form's
    // DeviceDpi (Theme.Px); the house-font labels are built in device pixels for the same DPI. The
    // framework's AutoScale pass is switched OFF (AutoScaleMode.None) so there is exactly one source of
    // truth — RescaleChrome/RescaleAll — which also runs on a Per-Monitor V2 DPI change.
    private int _chromeDpi;
    private readonly List<Font> _chromeFonts = new();
    private int S(int v) => Theme.Px(v, DeviceDpi);
    /// <summary>Width the list's rows / section headers stretch to (the list's client width less its
    /// padding and a little air for the scrollbar).</summary>
    private int ListInnerWidth => _list.ClientSize.Width - S(30);

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
        AutoScaleMode = AutoScaleMode.None;   // see the DPI note on the fields above
        ClientSize = new Size(S(680), S(520));
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
        // Double-buffer the panel so the live drag-reorder reflow doesn't flicker.
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(_list, true);
        root.Controls.Add(_list, 0, 3);

        root.Controls.Add(BuildFooter(), 0, 4);

        Controls.Add(root);

        RescaleChrome();   // fonts, heights, paddings and fixed positions from the DPI (before the rows measure the list)
        Versions.DevChannel = _settings.DevChannel;   // before any release is picked
        LoadCatalog();
        if (WhatsNewCache.Load() is { } cachedNotes) ApplyWhatsNew(cachedNotes);   // last relay copy, for offline starts
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
        var header = _header;
        header.Dock = DockStyle.Fill;   // height, padding and the label positions come from RescaleChrome

        _title.Text = "JB Theatre Tools";
        _title.AutoSize = true;
        _title.UseMnemonic = false;   // render a literal "&" (none here today, but future-proof)

        // UseMnemonic=false so the literal "&" shows (default true eats "& " as an Alt-mnemonic prefix).
        _subtitle.Text = "Install, update & launch the JB tool suite";
        _subtitle.AutoSize = true;
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
        header.Resize += (_, _) => LayoutHeaderButtons();
        return header;
    }

    /// <summary>Right-aligns the header buttons (Settings, Refresh, view toggle, Download All).</summary>
    private void LayoutHeaderButtons()
    {
        int y = S(16);
        _settingsBtn.Location = new Point(_header.Width - _settingsBtn.Width - S(14), y);
        _refresh.Location = new Point(_settingsBtn.Left - _refresh.Width - S(8), y);
        _viewToggle.Location = new Point(_refresh.Left - _viewToggle.Width - S(8), y);
        _downloadAll.Location = new Point(_viewToggle.Left - _downloadAll.Width - S(8), y);
    }

    private Control BuildUpdateBanner()
    {
        _updateBanner.Dock = DockStyle.Fill;   // height + positions come from RescaleChrome / LayoutBanner
        _updateBanner.Visible = false;   // colours come from ApplyTheme (the accent wash is theme-dependent)

        _updateBannerText.AutoSize = true;
        _updateBannerText.UseMnemonic = false; // render the literal "&" (e.g. "quit & replace") — default true eats it
        _updateBanner.Paint += (_, e) => BannerEdge(e, _updateBanner, Theme.Accent);

        var download = _updateBtn;
        download.Text = "Download Update";
        download.AutoSize = true;
        download.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        download.Click += async (_, _) =>
        {
            if (_catalog.Self == null) return;
            download.Enabled = false;
            var prev = download.Text;
            download.Text = "Downloading…";
            try
            {
                var dest = await LauncherUpdate.DownloadAndRevealAsync(_catalog.Self, AuthClient.SelfUpdate(_settings, _catalog.DownloadServer), CurrentVersion());
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
        _updateBanner.Resize += (_, _) => LayoutBanner(_updateBanner, _updateBannerText, download);
        return _updateBanner;
    }

    /// <summary>A banner's height and the placement of its text + action button, from the DPI: the text
    /// sits at (14, 13) and the button right-aligned at y=8 in the 44px design. The height is floored so
    /// the banner never clips its text if a pixel-rounded font outgrows the scaled height (inert at 100%
    /// and at the standard scales — a safety net, not the design).</summary>
    private void LayoutBanner(Panel banner, Label text, Button action)
    {
        text.Location = new Point(S(14), S(13));
        banner.Height = Math.Max(S(44), text.Bottom + S(10));
        action.Location = new Point(banner.Width - action.Width - S(14), S(8));
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
        _tokenBanner.Dock = DockStyle.Fill;   // height + positions come from RescaleChrome / LayoutBanner
        // v2 rule 25: this notice is a WARN — orange, never yellow. Colours come from ApplyTheme.
        _tokenBanner.Visible = false;

        _tokenBannerText.Text = NoCredsMsg;
        _tokenBannerText.AutoSize = true;
        _tokenBanner.Paint += (_, e) => BannerEdge(e, _tokenBanner, Theme.Warn);

        var open = _tokenBtn;
        open.Text = "Open Settings";
        open.AutoSize = true;
        open.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        open.Click += (_, _) => OpenSettings();

        _tokenBanner.Controls.Add(_tokenBannerText);
        _tokenBanner.Controls.Add(open);
        _tokenBanner.Resize += (_, _) => LayoutBanner(_tokenBanner, _tokenBannerText, open);
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
        var footer = _footer;
        footer.Dock = DockStyle.Fill;   // height comes from RescaleChrome
        // UseMnemonic=false so the literal "&" renders (default true treats it as an Alt-shortcut
        // prefix, eating the "&" and the following space → a double space).
        _credit.UseMnemonic = false;
        // Rule 28 — the house credit line, carrying the launcher's own version.
        _credit.Text = $"Created by: James Breedon & Claude Code · v{CurrentVersion()}";
        _credit.AutoSize = true;
        _credit.Anchor = AnchorStyles.None;
        footer.Controls.Add(_credit);
        footer.Resize += (_, _) => LayoutFooter();
        return footer;
    }

    private void LayoutFooter() => _credit.Location = new Point((_footer.Width - _credit.Width) / 2, S(5));

    // ── DPI ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Derives the window chrome — the house fonts, the header / banner / footer heights and
    /// paddings, the minimum size and every fixed label position — from the form's current DPI. Runs from
    /// the constructor and again after a DPI change. Heights are the 96-DPI design values scaled, floored
    /// at what the (pixel-rounded) text actually needs.</summary>
    private void RescaleChrome()
    {
        if (DeviceDpi != _chromeDpi)
        {
            _chromeDpi = DeviceDpi;
            var old = _chromeFonts.ToList();
            _chromeFonts.Clear();
            Font F(float pt, bool semibold = false) { var f = Theme.Ui(pt, semibold, _chromeDpi); _chromeFonts.Add(f); return f; }
            _title.Font = F(Theme.PtTitle, semibold: true);              // t-title 15px/600
            _subtitle.Font = F(Theme.PtSmall);                           // t-small 12px
            _updateBannerText.Font = F(Theme.PtTitle, semibold: true);   // t-status 15px/600
            _tokenBannerText.Font = F(Theme.PtTitle, semibold: true);    // t-status 15px/600
            _credit.Font = F(Theme.PtSmall);                             // t-small 12px
            foreach (var f in old) f.Dispose();
        }
        MinimumSize = new Size(S(560), S(440));
        _list.Padding = new Padding(S(10));

        _header.Padding = new Padding(S(14), S(10), S(14), S(10));
        _title.Location = new Point(S(14), S(10));
        _subtitle.Location = new Point(S(14), Math.Max(S(36), _title.Bottom + S(5)));   // floors: never overlap / clip
        _header.Height = Math.Max(S(64), _subtitle.Bottom + S(11));
        LayoutHeaderButtons();

        LayoutBanner(_updateBanner, _updateBannerText, _updateBtn);
        LayoutBanner(_tokenBanner, _tokenBannerText, _tokenBtn);

        _footer.Height = S(28);
        LayoutFooter();
    }

    /// <summary>Everything on the form from the current DPI: the chrome, then every row and section header
    /// (each re-derives its own fonts + geometry), then the list widths.</summary>
    private void RescaleAll()
    {
        SuspendLayout();
        _list.SuspendLayout();
        try
        {
            RescaleChrome();
            foreach (var r in _rows) r.Rescale();
            foreach (var h in _headers.Values) h.Rescale();
            int w = ListInnerWidth;
            foreach (var h in _headers.Values) h.Width = w;
            if (_settings.ViewMode != "grid") foreach (var r in _rows) r.Width = w;
        }
        finally
        {
            _list.ResumeLayout(true);
            ResumeLayout(true);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Per-Monitor V2: the window may open on a monitor whose DPI differs from the system DPI the
        // constructor laid out for (the framework refreshes DeviceDpi at handle creation).
        if (DeviceDpi != _chromeDpi) RescaleAll();
    }

    /// <summary>Per-Monitor V2 DPI change (the window crossed to a monitor with different scaling). The
    /// framework first scales its own controls — the ambient font the buttons/combos inherit, the window
    /// to the suggested rectangle — then we re-derive everything we own from the new DPI. The rows and
    /// headers also hear WM_DPICHANGED_AFTERPARENT themselves; running their Rescale twice is harmless
    /// because it is absolute, not incremental.</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        RescaleAll();
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
            var row = new AppRowControl(app) { Width = Math.Max(S(400), ListInnerWidth) };
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
            // Suspended: without it each of the ~27 Width sets ran the FlowLayoutPanel's whole flow engine (and a
            // scrollbar recompute) — 27 panel layouts per resize tick instead of one (same idiom as RescaleAll).
            int w = ListInnerWidth;
            _list.SuspendLayout();
            try
            {
                foreach (var h in _headers.Values) h.Width = w;
                if (_settings.ViewMode != "grid") foreach (var r in _rows) r.Width = w;
            }
            finally { _list.ResumeLayout(true); }
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

    // ── Variants (apps that ship more than one download, e.g. NDI Light/Full) ──────────────

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
        RecomputeRow(row);
        Log.Write($"variant for {row.App.Id} → {variantId}");
    }

    /// <summary>Re-derives a row's latest-asset id + status from its cached releases (after a variant change).</summary>
    private void RecomputeRow(AppRowControl row)
    {
        var latest = row.LatestRelease;
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
    /// slot, so Light and Full can both be installed; the toggle just picks which slot the row shows.</summary>
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
            if (!grid) r.Width = ListInnerWidth;
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

    /// <summary>Overlays the relay's editable "New in" lines on every row (catalog line where the relay has
    /// none). One layout pass: a changed line changes the row's height, which re-flows the list.</summary>
    private void ApplyWhatsNew(WhatsNewNotes notes)
    {
        _list.SuspendLayout();
        try
        {
            foreach (var row in _rows)
            {
                var (line, version) = notes.Resolve(row.App.Id, row.App.WhatsNew, row.App.WhatsNewVersion);
                row.SetWhatsNew(line, version);
            }
        }
        finally { _list.ResumeLayout(true); }
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
            header.Width = ListInnerWidth;
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
        _dragCard = new DragCardForm(row.CurrentIcon, row.DisplayName, Theme.IsDark(_settings.Appearance), row.DeviceDpi);
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
            }

            // The relay's editable "New in" lines ride along with the update check (server mode only; null =
            // keep what we have). Applied after the release results, under one SuspendLayout.
            var notesTask = client.WhatsNewNotesAsync();
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
                        if (latest == null)
                        {
                            // Nothing released on this PC's channel (only dev builds so far, or none): only the
                            // Development builds switch shows it, so everyone else never sees a dead row.
                            accessible = Versions.DevChannel;
                            row.SetState(installed, null, null, RowStatus.NoRelease);
                        }
                        else
                        {
                            var asset = latest.Assets.FirstOrDefault(a => a.Name == row.App.WindowsAsset(SelectedVariant(row.App)));
                            row.SetState(installed, latest.TagName, asset?.Id, ComputeStatus(installed, latest.TagName, asset != null));
                        }
                        break;
                    case FetchKind.Unauthorized: unauthorized = true; accessible = false; break;
                    case FetchKind.NoRelease:
                        accessible = Versions.DevChannel; row.SetReleases(new List<ReleaseInfo>());
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

            var notes = await notesTask;
            if (notes != null)
            {
                ApplyWhatsNew(notes.Value.Notes);
                WhatsNewCache.Save(notes.Value.Raw);
                Log.Write($"what's-new: applied {notes.Value.Notes.Count} relay line(s)");
            }

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
        int updates = UpdatesAvailable();
        bool show = _rows.Any(WouldShow)
                    && AuthClient.HasCredentials(_settings, _catalog.DownloadServer)
                    && (updates > 0 || HasAnyToDownload(false));
        _downloadAll.Text = updates > 0 ? $"Update All ({updates})  ▾" : "Download All  ▾";
        _downloadAll.Visible = show;
        LayoutHeaderButtons();   // the label width changed
    }

    /// <summary>True if any app has a slot to fetch — a not-installed or updatable default slot (and Full
    /// slots when <paramref name="includeFull"/>). Mirrors the macOS hasAnyToDownload.</summary>
    private bool HasAnyToDownload(bool includeFull) => _rows.Any(r => SlotsToDownload(r, includeFull).Any());

    /// <summary>Builds and drops the Download All menu below the button: Update all (N) when any update is
    /// pending, Download all apps, and — when any app ships a Full edition — Download all incl. Full.</summary>
    private void ShowDownloadAllMenu()
    {
        var menu = new ContextMenuStrip();
        int n = UpdatesAvailable();
        if (n > 0)
        {
            var upd = new ToolStripMenuItem($"Update {n} installed app{(n == 1 ? "" : "s")} — incl. Full editions");
            upd.Click += async (_, _) => await UpdateAllAsync();
            menu.Items.Add(upd);
            menu.Items.Add(new ToolStripSeparator());
        }
        var all = new ToolStripMenuItem("Install every app");
        all.Click += async (_, _) => await DownloadAllAsync(false);
        menu.Items.Add(all);
        if (HasFullVariants)
        {
            var full = new ToolStripMenuItem("Install every app — plus the Full editions");
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
        var latest = row.LatestRelease;
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
    /// editions into their own slots (Light and Full side by side). Skips anything current or with no
    /// asset for this arch.</summary>
    private async Task DownloadAllAsync(bool includeFull)
    {
        var work = OrderedSlots(_rows.ToList().SelectMany(r => SlotsToDownload(r, includeFull).ToList().Select(v => (r, v))));
        Log.Write($"download all{(includeFull ? " (incl. Full)" : "")}: {work.Count} slot(s)");
        _downloadAll.Enabled = false;
        try { await RunSlotsAsync(work, "Download All"); }
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
            if (installed != null) _eligible.Add(row.App.Id); else _eligible.Remove(row.App.Id);
        }
        ReindexList();
    }

    /// <summary>Installed slots of a row's app (default + every Full edition) whose latest release is newer than
    /// what's on disk. Update All used to act on <c>Status == UpdateAvailable</c>, which reflects only the
    /// SELECTED variant — so an installed Full edition was never updated unless its toggle happened to be on.</summary>
    private IEnumerable<string?> SlotsToUpdate(AppRowControl row)
    {
        var latest = row.LatestRelease;
        if (latest == null) yield break;
        var variants = new List<string?> { row.App.HasVariants ? row.App.Variants?.FirstOrDefault()?.Id : null };
        if (row.App.Variants != null) variants.AddRange(row.App.Variants.Skip(1).Select(v => (string?)v.Id));
        foreach (var vid in variants)
        {
            var name = row.App.WindowsAsset(vid);
            if (name == null || !latest.Assets.Any(a => a.Name == name)) continue;
            var installed = InstallManager.Shared.InstalledVersion(row.App.InstallKey(vid));
            if (installed != null && Versions.IsNewer(latest.TagName, installed)) yield return vid;
        }
    }

    /// <summary>Count of installed slots (not rows) with an update — drives the "Update All (N)" label.</summary>
    private int UpdatesAvailable() => _rows.Sum(r => SlotsToUpdate(r).Count());

    private async Task UpdateAllAsync()
    {
        var work = _rows.Select(r => (row: r, slots: SlotsToUpdate(r).ToList())).Where(w => w.slots.Count > 0).ToList();
        if (work.Count == 0) return;
        Log.Write($"update all: {work.Sum(w => w.slots.Count)} slot(s) across {work.Count} app(s)");
        var flat = OrderedSlots(work.SelectMany(w => w.slots.Select(v => (w.row, v))));
        _downloadAll.Enabled = false;
        try { await RunSlotsAsync(flat, "Update All"); }
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
    /// regardless of the row's on-screen selection (used by Download All to fetch Light and/or Full);
    /// null = the row's selected variant. The row's displayed state is only updated for the SELECTED
    /// variant, so a background Full-edition install doesn't hijack the row's display.</summary>
    /// <summary>Everything phase 1 hands to phase 2: the resolved release/asset and the (verified-later) cache file.</summary>
    private sealed record Downloaded(AppRowControl Row, GitHubClient Client, ReleaseInfo Rel, ReleaseAsset Asset,
                                     string Cache, string AssetName, string? VariantId, string? Tag);

    /// <summary>Installs one slot: phase 1 (download) then phase 2 (verify + extract). Returns the exception on
    /// failure (already recorded on the row + logged), or null. <paramref name="interactive"/> shows the failure
    /// dialog immediately; batch runs pass false and show ONE summary at the end instead of halting the batch
    /// on a modal box nobody is there to click.</summary>
    private async Task<Exception?> InstallSlotAsync(AppRowControl row, string? tag, string? variantOverride, bool interactive = true)
    {
        var d = await DownloadSlotAsync(row, tag, variantOverride, interactive);
        return d == null ? null : await InstallDownloadedAsync(d, interactive);
    }

    /// <summary>Phase 1 — resolve the release/asset and download it into the cache (network-bound). Marks the
    /// row busy for the whole slot; on failure records it and ends busy. Returns null on failure or a silent skip.</summary>
    private async Task<Downloaded?> DownloadSlotAsync(AppRowControl row, string? tag, string? variantOverride, bool interactive)
    {
        var client = AuthClient.Active(_settings, _catalog.DownloadServer);
        if (client == null) return null;
        var variantId = variantOverride ?? SelectedVariant(row.App);
        var assetName = row.App.WindowsAsset(variantId);
        if (assetName == null) { client.Dispose(); return null; }

        row.SetBusy(true);
        row.SetPhase("Downloading…");
        try
        {
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
            return new Downloaded(row, client, rel, asset, cache, assetName, variantId, tag);
        }
        catch (Exception ex)
        {
            client.Dispose();
            FailSlot(row, ex, interactive);
            row.SetBusy(false);
            RefreshDownloadAllButton();
            return null;
        }
    }

    /// <summary>Phase 2 — verify (size + signed SHA256SUMS + hash), extract/copy off the UI thread, reflect it in
    /// the row. Always ends the row's busy state and disposes the client.</summary>
    private async Task<Exception?> InstallDownloadedAsync(Downloaded d, bool interactive)
    {
        var (row, rel, asset, cache, assetName, variantId, tag) = (d.Row, d.Rel, d.Asset, d.Cache, d.AssetName, d.VariantId, d.Tag);
        try
        {
            using var client = d.Client;
            row.SetPhase("Verifying…", indeterminate: true);
            var verification = await InstallManager.VerifyDownloadAsync(cache, asset, rel, row.App.Owner, row.App.Repo, client);
            // Strict for current releases: a latest install (tag == null) MUST checksum-verify — every
            // current release ships a correct SHA256SUMS, so a missing/incomplete manifest here is
            // anomalous → abort. Explicit older-tag installs (version picker) stay verify-if-present. A
            // hash MISMATCH always aborts (it throws from VerifyDownloadAsync) regardless of tag.
            if (tag == null && verification != VerifyResult.Verified)
            {
                _ = Task.Run(() => InstallManager.TryDelete(cache));   // 300-450 MB unlink: never on the UI thread
                var reason = InstallManager.StrictFailureReason(verification, assetName);
                Log.Write($"install {row.App.Id} {rel.TagName}: BLOCKED (strict) — {reason}");
                throw new Exception($"Couldn't verify the download — {reason}. Install aborted for safety.");
            }
            // The app is open: ASK instead of failing with "file in use". Yes → close it (it may prompt to
            // save) and wait for it to exit; No → skip quietly, the row keeps its Update button.
            var running = InstallManager.Shared.RunningInstances(row.App.InstallKey(variantId));
            if (running.Length > 0)
            {
                var name = row.App.Name + row.App.VariantSuffix(variantId);
                row.SetPhase("Waiting…", indeterminate: true);
                var answer = MessageBox.Show(this,
                    $"{name} is open. Close it to install the update?\n\nIf it has unsaved work it will ask you first.",
                    $"{name} is open", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    row.SetPhase(null);
                    _ = Task.Run(() => InstallManager.TryDelete(cache));
                    Log.Write($"install {row.App.Id} {rel.TagName}: postponed — {name} is open");
                    return null;
                }
                bool closed = await Task.Run(() =>
                {
                    foreach (var p in running) { try { p.CloseMainWindow(); } catch { } }
                    return running.All(p => { try { return p.WaitForExit(15_000); } catch { return true; } });
                });
                if (!closed)
                {
                    _ = Task.Run(() => InstallManager.TryDelete(cache));
                    throw new Exception($"{name} didn't close — close it, then try again.");
                }
            }
            row.SetPhase("Installing…", indeterminate: true);
            // Everything disk-heavy OFF the UI thread in one Task.Run: the extract/copy (seconds for a Full
            // edition), the unlink of the cache file (the heaviest synchronous call that was left at the row
            // flip), and the fresh exe's icon so the post-install repaint is a cache hit.
            var appRef = row.App; var tagRef = rel.TagName; var toApps = _settings.InstallToApplications;
            await Task.Run(() =>
            {
                var exe = InstallManager.Shared.Install(appRef, tagRef, cache, assetName, toApps, variantId);
                InstallManager.TryDelete(cache);   // verified copy is now installed; mirror the macOS zip cleanup
                AppRowControl.PrewarmInstalledIcon(exe);
                // Install() invalidated the manifest snapshot + path/name caches; re-read installed.json and the
                // exe's version resource HERE so the row flip below is all cache hits (the mac does the same in
                // its detached install task).
                InstallManager.Shared.InstalledDisplayName(appRef.InstallKey(variantId));
                InstallManager.Shared.InstalledPath(appRef.Id);
            });
            row.SetPhase(null);
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
            return null;
        }
        catch (Exception ex)
        {
            FailSlot(row, ex, interactive);
            return ex;
        }
        finally
        {
            row.SetBusy(false);
            RefreshDownloadAllButton();
        }
    }

    private void FailSlot(AppRowControl row, Exception ex, bool interactive)
    {
        row.SetPhase(null);   // never leave a stale "Installing…" over the Error badge
        row.SetState(row.Installed, row.Latest, row.LatestAssetId, RowStatus.Error);
        Log.Write($"install {row.App.Id} FAILED: {ex.Message}");
        if (interactive) MessageBox.Show(this, ex.Message, "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>Errors worth ONE automatic retry in a batch: network/transport and I/O hiccups. Verification
    /// failures (strict-verify BLOCKED, checksum/size mismatch, minisign) throw plain Exception and are
    /// deliberately NOT retried; nor are auth/access errors or "no asset".</summary>
    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or IOException or OperationCanceledException
        || (ex is GitHubException ge && ge.Kind == GitHubErrorKind.Http);

    /// <summary>Runs install slots with ONE download of lookahead: while slot N verifies + extracts (CPU/disk),
    /// slot N+1 is already downloading (network) — serially the network sat idle through every hash + extract.
    /// Downloads stay serial (venue Wi-Fi is the bottleneck) and at most two cache files exist at once. A failed
    /// slot gets one retry if the error was transient, and the batch CONTINUES; failures are summarised in one
    /// dialog at the end (a per-slot modal used to halt the whole batch until someone clicked OK).</summary>
    private async Task RunSlotsAsync(List<(AppRowControl row, string? vid)> work, string title)
    {
        var failures = new List<(string name, string msg)>();
        Task<Downloaded?>? next = null;
        for (int i = 0; i < work.Count; i++)
        {
            var (row, vid) = work[i];
            var current = next ?? DownloadSlotAsync(row, null, vid, interactive: false);
            var d = await current;                                   // download N done (or failed + recorded)
            next = null;
            if (i + 1 < work.Count)                                  // start download N+1 now…
            {
                var (nrow, nvid) = work[i + 1];
                next = DownloadSlotAsync(nrow, null, nvid, interactive: false);
            }
            Exception? err;
            if (d != null) err = await InstallDownloadedAsync(d, interactive: false);   // …verify + extract N meanwhile
            else err = row.Status == RowStatus.Error ? new Exception("download failed") : null;   // a silent skip isn't a failure
            if (err != null && IsTransient(err))
            {
                Log.Write($"install {row.App.Id}: retrying once after a transient error: {err.Message}");
                await Task.Delay(2000);
                err = await InstallSlotAsync(row, null, vid, interactive: false);
            }
            if (err != null)
                failures.Add((vid == null ? row.DisplayName : $"{row.DisplayName} ({row.App.VariantLabel(vid)})", err.Message));
        }
        Log.Write($"{title} complete ({failures.Count} failed)");
        if (failures.Count > 0)
            MessageBox.Show(this,
                $"{failures.Count} app{(failures.Count == 1 ? "" : "s")} could not be installed:\n\n" +
                string.Join("\n", failures.Select(f => $"• {f.name} — {f.msg}")),
                title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>Default editions first, then Full editions — so consecutive slots are (almost always) different
    /// apps and the pipelined runner overlaps two apps rather than two slots of one row.</summary>
    private static List<(AppRowControl row, string? vid)> OrderedSlots(IEnumerable<(AppRowControl row, string? vid)> slots)
    {
        var list = slots.ToList();
        return list.Where(s => s.row.App.IsDefaultVariant(s.vid)).Concat(list.Where(s => !s.row.App.IsDefaultVariant(s.vid))).ToList();
    }

    private async void Uninstall(AppRowControl row)
    {
        if (MessageBox.Show(this, $"Uninstall {row.DisplayName}?", "Uninstall",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        // Uninstalls the SELECTED variant's slot only (a sibling variant, if installed, stays). The delete runs
        // off the UI thread: a one-dir Full edition is thousands of files (300–450 MB), and Directory.Delete
        // of that froze the whole window for seconds. The row shows the same marquee it uses for Verifying.
        var key = InstallKey(row.App);
        row.SetBusy(true);
        row.SetPhase("Removing…", indeterminate: true);
        try
        {
            await Task.Run(() => InstallManager.Shared.Uninstall(key));
            row.SetPhase(null);
            var status = row.LatestAssetId != null
                ? RowStatus.NotInstalled
                : (row.Latest == null ? RowStatus.Unknown : RowStatus.MissingAsset);
            row.SetState(null, row.Latest, row.LatestAssetId, status);
            row.SetResolvedName(null);
            Log.Write($"uninstalled {row.App.Id}");
        }
        catch (Exception ex)
        {
            row.SetPhase(null);
            MessageBox.Show(this, ex.Message, "Uninstall failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Log.Write($"uninstall {row.App.Id} FAILED: {ex.Message}");
        }
        finally
        {
            row.SetBusy(false);
            RefreshDownloadAllButton();
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
            var info = await Versions.LauncherTargetAsync(client, s.Owner, s.Repo, CurrentVersion());
            if (info != null)
            {
                _updateBannerText.Text = Versions.IsNewer(info.TagName, CurrentVersion())
                    ? $"JB Theatre Tools {info.TagName} is available (you have v{CurrentVersion()})."
                    : $"Back to the release: JB Theatre Tools {info.TagName} (you have the development build v{CurrentVersion()}).";
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
            Versions.DevChannel = _settings.DevChannel;
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
        // InformationalVersion carries a dev build's full tag (build-win.sh stamps JBTT_VERSION, e.g.
        // 1.30.0-dev.1); the SDK may append "+<commit>", which is dropped. Else the numeric assembly version.
        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0];
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
