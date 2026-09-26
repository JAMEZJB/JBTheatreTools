using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

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
    private readonly Button _moreBtn = new();

    // Show lock (installs / updates / removals paused) and the one-time "Updated to vX" notice.
    private readonly Panel _lockBanner = new();
    private readonly Label _lockBannerText = new();
    private readonly Button _unlockBtn = new();
    private readonly Panel _whatsNewBanner = new();
    private readonly Label _whatsNewText = new();
    private readonly Button _whatsNewBtn = new();
    private readonly Button _whatsNewDismiss = new();

    // Find & filter bar, and the empty-result line shown in the list.
    private readonly Panel _filterBar = new();
    private readonly TextBox _search = new();
    private readonly ComboBox _statusFilter = new();
    private readonly Label _filterCount = new();
    private readonly Label _noMatches = new();

    // Scheduled checks, batch control and per-row download cancellation.
    private readonly System.Windows.Forms.Timer _scheduler = new() { Interval = 60_000 };
    private DateTimeOffset? _lastCheck;
    private bool _refreshing;
    private bool _batchRunning;
    private CancellationTokenSource? _batchCts;
    private readonly Dictionary<AppRowControl, CancellationTokenSource> _rowCts = new();
    private bool _hiddenToTray;
    private bool _balloonShowing;
    private bool _historyOpening;
    private readonly ToolTip _headerTip = new();

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
        ? "Enter the suite passphrase to enable downloads  —  Settings → Download access (ask whoever set up your access)."
        : "Add a GitHub token to enable downloads  —  Settings → paste a fine-grained PAT.";
    private string BadCredsMsg => _settings.AuthMode == "server"
        ? "The download server rejected the passphrase  —  check it in Settings."
        : "Your GitHub token is invalid or expired  —  open Settings to paste a new one.";
    private string NoAccessMsg => _settings.AuthMode == "server"
        ? "No apps are reachable right now  —  check the passphrase in Settings, or ask whoever set up your access."
        : "This token can’t access any apps  —  check its repository access, or ask whoever set up your access.";

    public MainForm()
    {
        Text = "JB Theatre Tools";
        AutoScaleMode = AutoScaleMode.None;   // see the DPI note on the fields above
        ClientSize = new Size(S(760), S(560));
        StartPosition = FormStartPosition.CenterScreen;
        TryLoadIcon();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // header
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // launcher-update banner
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // "updated to vX" banner
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // show-lock banner
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // token banner
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // find & filter bar
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // list
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // footer (credit)

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildUpdateBanner(), 0, 1);
        root.Controls.Add(BuildWhatsNewBanner(), 0, 2);
        root.Controls.Add(BuildLockBanner(), 0, 3);
        root.Controls.Add(BuildTokenBanner(), 0, 4);
        root.Controls.Add(BuildFilterBar(), 0, 5);

        _list.Dock = DockStyle.Fill;
        _list.FlowDirection = FlowDirection.TopDown;
        _list.WrapContents = false;
        _list.AutoScroll = true;
        // Double-buffer the panel so the live drag-reorder reflow doesn't flicker.
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(_list, true);
        root.Controls.Add(_list, 0, 6);

        root.Controls.Add(BuildFooter(), 0, 7);

        Controls.Add(root);

        RescaleChrome();   // fonts, heights, paddings and fixed positions from the DPI (before the rows measure the list)
        Versions.DevChannel = _settings.DevChannel;   // before any release is picked
        LoadCatalog();
        if (WhatsNewCache.Load() is { } cachedNotes) ApplyWhatsNew(cachedNotes);   // last relay copy, for offline starts
        ApplyTheme();
        SetupTray();
        FormClosing += OnFormClosing;
        _scheduler.Tick += async (_, _) => await ScheduledTickAsync();
        Shown += async (_, _) =>
        {
            Log.Write($"launched v{CurrentVersion()}");
            PrepareLauncherWhatsNew();
            ApplyLock();
            _scheduler.Start();
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
        _downloadAll.Click += (_, _) => { if (_batchRunning) StopBatch(); else ShowDownloadAllMenu(); };

        _viewToggle.AutoSize = true;
        _viewToggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _viewToggle.Click += (_, _) => ToggleViewMode();

        // Everything that isn't per-app: show lock, activity, setup files, diagnostics.
        _moreBtn.Text = "More  ▾";
        _moreBtn.AutoSize = true;
        _moreBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _moreBtn.Click += (_, _) => ShowMoreMenu();

        _settingsBtn.Text = "Settings";
        _settingsBtn.AutoSize = true;
        _settingsBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _settingsBtn.Click += (_, _) => OpenSettings();

        header.Controls.AddRange(new Control[] { _title, _subtitle, _downloadAll, _viewToggle, _moreBtn, _refresh, _settingsBtn });
        header.Resize += (_, _) => LayoutHeaderButtons();
        return header;
    }

    /// <summary>Right-aligns the header buttons (Settings, Refresh, More, view toggle, Download All).</summary>
    private void LayoutHeaderButtons()
    {
        int y = S(16);
        _settingsBtn.Location = new Point(_header.Width - _settingsBtn.Width - S(14), y);
        _refresh.Location = new Point(_settingsBtn.Left - _refresh.Width - S(8), y);
        _moreBtn.Location = new Point(_refresh.Left - _moreBtn.Width - S(8), y);
        _viewToggle.Location = new Point(_moreBtn.Left - _viewToggle.Width - S(8), y);
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
        _updateBannerText.TextChanged += (_, _) => LayoutBanner(_updateBanner, _updateBannerText, download);   // new text, new height
        return _updateBanner;
    }

    /// <summary>A banner's height and the placement of its text + action button, from the DPI: the text
    /// sits at (14, 13) and the button right-aligned at y=8 in the 44px design. The text wraps within the
    /// space left of the buttons (a long notice ran on under them at the default and minimum widths), and
    /// the banner grows to fit it: its height is the 44px design, floored at what the wrapped text needs.</summary>
    private void LayoutBanner(Panel banner, Label text, params Button[] actions)
    {
        int x = banner.Width - S(14);
        foreach (var action in actions)   // right to left, in the order given
        {
            action.Location = new Point(x - action.Width, S(8));
            x = action.Left - S(8);
        }
        text.Location = new Point(S(14), S(13));
        // AutoSize + a maximum width = a label that wraps and grows downwards.
        text.MaximumSize = new Size(Math.Max(S(120), x - S(12) - text.Left), 0);
        banner.Height = Math.Max(S(44), text.Bottom + S(10));
    }

    /// <summary>Closes a banner the way the kit's `.banner` does: a 40% hairline of the semantic colour
    /// along the bottom edge (parity with the macOS `bannerTint`), never a shadow.</summary>
    private static void BannerEdge(PaintEventArgs e, Control banner, Color tint)
    {
        using var pen = new Pen(Theme.Blend(tint, banner.BackColor, 0.40));
        e.Graphics.DrawLine(pen, 0, banner.Height - 1, banner.Width, banner.Height - 1);
    }

    /// <summary>The show-lock notice: shown while installs, updates and removals are paused.</summary>
    private Control BuildLockBanner()
    {
        _lockBanner.Dock = DockStyle.Fill;
        _lockBanner.Visible = false;
        _lockBannerText.AutoSize = true;
        _lockBannerText.UseMnemonic = false;
        _lockBannerText.Text = "Show lock is on — installs, updates and uninstalls are paused. Launching still works.";
        _lockBanner.Paint += (_, e) => BannerEdge(e, _lockBanner, Theme.Info);
        _unlockBtn.Text = "Turn Off";
        _unlockBtn.AutoSize = true;
        _unlockBtn.Click += (_, _) => RequestShowLock(false);
        _lockBanner.Controls.Add(_lockBannerText);
        _lockBanner.Controls.Add(_unlockBtn);
        _lockBanner.Resize += (_, _) => LayoutBanner(_lockBanner, _lockBannerText, _unlockBtn);
        _lockBannerText.TextChanged += (_, _) => LayoutBanner(_lockBanner, _lockBannerText, _unlockBtn);
        return _lockBanner;
    }

    /// <summary>The one-time "Updated to vX" notice after the launcher itself was updated.</summary>
    private Control BuildWhatsNewBanner()
    {
        _whatsNewBanner.Dock = DockStyle.Fill;
        _whatsNewBanner.Visible = false;
        _whatsNewText.AutoSize = true;
        _whatsNewText.UseMnemonic = false;
        _whatsNewBanner.Paint += (_, e) => BannerEdge(e, _whatsNewBanner, Theme.Accent);
        _whatsNewBtn.Text = "What's new";
        _whatsNewBtn.AutoSize = true;
        _whatsNewBtn.Click += async (_, _) => await ShowLauncherWhatsNewAsync();
        _whatsNewDismiss.Text = "Dismiss";
        _whatsNewDismiss.AutoSize = true;
        _whatsNewDismiss.Click += (_, _) => DismissLauncherWhatsNew();
        _whatsNewBanner.Controls.Add(_whatsNewText);
        _whatsNewBanner.Controls.Add(_whatsNewBtn);
        _whatsNewBanner.Controls.Add(_whatsNewDismiss);
        _whatsNewBanner.Resize += (_, _) => LayoutBanner(_whatsNewBanner, _whatsNewText, _whatsNewDismiss, _whatsNewBtn);
        _whatsNewText.TextChanged += (_, _) => LayoutBanner(_whatsNewBanner, _whatsNewText, _whatsNewDismiss, _whatsNewBtn);
        return _whatsNewBanner;
    }

    /// <summary>Find &amp; filter: a search box, a status dropdown (house rule: mode selectors are dropdowns) and a
    /// count. Narrowing the list shows collapsed sections open and pauses reordering (see <see cref="FilterActive"/>).</summary>
    private Control BuildFilterBar()
    {
        _filterBar.Dock = DockStyle.Fill;
        _search.PlaceholderText = "Search apps  (Ctrl+F)";
        _search.TextChanged += (_, _) => ReindexList();
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && _search.Text.Length > 0) { _search.Clear(); e.Handled = e.SuppressKeyPress = true; }
        };
        _statusFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var f in Enum.GetValues<StatusFilter>()) _statusFilter.Items.Add(f == StatusFilter.All ? "All apps" : AppFilter.Label(f));
        _statusFilter.SelectedIndex = 0;
        _statusFilter.SelectedIndexChanged += (_, _) => ReindexList();
        _filterCount.AutoSize = true;
        _filterCount.UseMnemonic = false;
        _filterBar.Controls.AddRange(new Control[] { _search, _statusFilter, _filterCount });
        _filterBar.Resize += (_, _) => LayoutFilterBar();

        _noMatches.AutoSize = true;
        _noMatches.UseMnemonic = false;
        _noMatches.Text = "No apps match — clear the search or pick another filter.";
        _noMatches.Visible = false;
        return _filterBar;
    }

    private void LayoutFilterBar()
    {
        _search.Location = new Point(S(14), S(6));
        _search.Width = S(260);
        _statusFilter.Location = new Point(_search.Right + S(8), S(6));
        _statusFilter.Width = S(140);
        _filterBar.Height = Math.Max(_search.Height, _statusFilter.Height) + S(12);
        _filterCount.Location = new Point(_statusFilter.Right + S(12), (_filterBar.Height - _filterCount.Height) / 2);
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
        _tokenBannerText.TextChanged += (_, _) => LayoutBanner(_tokenBanner, _tokenBannerText, open);
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
            _lockBannerText.Font = F(Theme.PtTitle, semibold: true);     // t-status 15px/600
            _whatsNewText.Font = F(Theme.PtTitle, semibold: true);       // t-status 15px/600
            _filterCount.Font = F(Theme.PtSmall);                        // t-small 12px
            _noMatches.Font = F(Theme.PtBody);                           // body 13px
            _credit.Font = F(Theme.PtSmall);                             // t-small 12px
            foreach (var f in old) f.Dispose();
        }
        MinimumSize = new Size(S(640), S(460));
        _list.Padding = new Padding(S(10));

        _header.Padding = new Padding(S(14), S(10), S(14), S(10));
        _title.Location = new Point(S(14), S(10));
        _subtitle.Location = new Point(S(14), Math.Max(S(36), _title.Bottom + S(5)));   // floors: never overlap / clip
        _header.Height = Math.Max(S(64), _subtitle.Bottom + S(11));
        LayoutHeaderButtons();

        LayoutBanner(_updateBanner, _updateBannerText, _updateBtn);
        LayoutBanner(_tokenBanner, _tokenBannerText, _tokenBtn);
        LayoutBanner(_lockBanner, _lockBannerText, _unlockBtn);
        LayoutBanner(_whatsNewBanner, _whatsNewText, _whatsNewDismiss, _whatsNewBtn);
        LayoutFilterBar();
        _noMatches.Margin = new Padding(S(6), S(16), 0, 0);

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
            row.InstallPickedVersionRequested += (r, t) => InstallSlotAsync(r, t, null, lenient: true);
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
            row.CancelRequested += CancelRow;
            row.HoldToggleRequested += ToggleHold;
            row.IsHeldQuery = r => IsHeld(r.App.Id);
            row.DetailsRequested += ShowDetails;
            row.ReleaseNotesRequested += ShowReleaseNotes;
            row.RollbackTagQuery = RollbackTag;
            row.RollbackRequested += async (r, tag) => await RollbackAsync(r, tag);
            row.LaunchBlockedQuery = r => _slotsInstalling.Contains(InstallKey(r.App));
            row.SelectedAssetQuery = r => AssetFor(r.App, SelectedVariant(r.App));
            row.CanChooseX64Query = r => r.App.CanChooseX64(SelectedVariant(r.App));
            row.PrefersX64Query = r => _settings.X64Slots.Contains(InstallKey(r.App));
            row.RunsEmulatedQuery = RunsEmulated;
            row.X64ToggleRequested += SetX64Async;
            row.SetLocked(_settings.ShowLock);
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
        _list.Controls.Add(_noMatches);   // the empty-result line; ReindexList places or parks it
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
        var assetName = AssetFor(row.App, SelectedVariant(row.App));   // the slot's chosen build (x64 / ARM64)
        var latest = row.LatestRelease == null ? null
            : Versions.LatestFor(row.Releases, assetName);   // per edition
        if (latest == null)
        {
            // No cached releases yet (pre-refresh): the row just reflects whether the slot is installed.
            row.SetState(row.Installed, null, null, row.Installed != null ? RowStatus.Installed : RowStatus.Unknown);
            return;
        }
        var asset = latest.Assets.FirstOrDefault(a => a.Name == assetName);
        row.SetState(row.Installed, latest.TagName, asset?.Id, ComputeStatus(row.Installed, latest.TagName, asset != null));
    }

    /// <summary>The install-manifest key of the row's SELECTED variant slot. Every variant is its own
    /// slot, so Light and Full can both be installed; the toggle just picks which slot the row shows.</summary>
    private string InstallKey(CatalogApp app) => app.InstallKey(SelectedVariant(app));

    // ── x64 build on ARM64 PCs (Windows runs it through its built-in emulation) ─────────────

    /// <summary>The asset this PC installs for a slot: the slot's x64 / ARM64 choice applied, and the x64 build when an
    /// edition has no ARM64 one (see Platform.Pick). Every install / update / verify decision goes through here.</summary>
    private string? AssetFor(CatalogApp app, string? variantId) => app.WindowsAsset(variantId, _settings.X64Slots);

    /// <summary>The row's selected edition runs emulated: set to use the x64 build, or it only has an x64 build.</summary>
    private bool RunsEmulated(AppRowControl row) =>
        row.App.WindowsPick(SelectedVariant(row.App), _settings.X64Slots)?.Emulated ?? false;

    private void StoreX64(string slot, bool on)
    {
        _settings.X64Slots.Remove(slot);
        if (on) _settings.X64Slots.Add(slot);
        _settings.Save();
    }

    /// <summary>Switches the row's selected edition between its ARM64 and x64 builds (⋯ → "Use the x64 build
    /// (emulated)"). Not installed, or already installed as that build: only the choice is stored, and the next install
    /// uses it. Installed as the other build: asked first, then that same version is reinstalled with the other build —
    /// the normal install path, verified against the signed checksums as usual. Nothing changes if the answer is no or
    /// that version has no such build, and the choice is put back whenever the build on disk didn't change (failed,
    /// cancelled, show lock, left open), so the setting always matches what's installed. Parity: macOS setRunsAsIntel.</summary>
    private async Task SetX64Async(AppRowControl row, bool on)
    {
        try
        {
            if (row.IsBusy || BlockedByLock($"x64 build {row.App.Id}")) return;
            var vid = SelectedVariant(row.App);
            var slot = row.App.InstallKey(vid);
            bool before = _settings.X64Slots.Contains(slot);
            if (before == on) return;
            var name = row.DisplayName;
            var kind = on ? "x64" : "ARM64";
            ushort want = on ? PeArch.X64 : PeArch.Arm64;
            var installed = InstallManager.Shared.InstalledVersion(slot);
            var path = installed == null ? null : InstallManager.Shared.InstalledPath(slot);
            // The installed exe's own header (the recorded main exe of a .zip Full install too): disk, so off the UI thread.
            ushort? machine = path == null ? null : await Task.Run(() => PeArch.OfFile(path));
            if (installed != null && path != null && machine != want)
            {
                var asset = Platform.Pick(row.App.AssetsFor(vid), Platform.OsIsArm64, on)?.Name;
                if (row.Releases.Count == 0)
                {
                    MessageBox.Show(this, $"The list of {name} releases hasn't loaded yet. Press Refresh, then try again.",
                                    $"Use the {kind} build", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var rel = row.Releases.FirstOrDefault(r => VersionCompare.Equal(r.TagName, installed));
                // Only a release the strict reinstall can pass: the build is there, signed, and not a hidden dev build.
                if (asset == null || rel == null || !rel.Assets.Any(a => a.Name == asset) || !Versions.HasSignedManifest(rel)
                    || (!Versions.DevChannel && VersionCompare.IsDev(rel.TagName)))
                {
                    MessageBox.Show(this, $"{VersionCompare.Display(installed)} of {name} has no verifiable {kind} build, so it can't be switched. It can switch with a later version.",
                                    $"Use the {kind} build", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var text = on
                    ? $"{VersionCompare.Display(installed)} for x64 replaces the ARM64 build. Windows runs it through emulation — usually a little slower."
                    : $"{VersionCompare.Display(installed)} for ARM64 replaces the x64 build.";
                if (!DialogKit.ConfirmReinstall(this, $"Reinstall {name} as the {kind} build?", text)) return;
                if (row.IsBusy || BlockedByLock($"x64 build {row.App.Id}")) return;   // started / locked while the question was up
                StoreX64(slot, on);   // the install below resolves the chosen build from here
                Log.Write($"{kind} build: reinstalling {slot} {installed}");
                RecomputeRow(row);
                await InstallSlotAsync(row, rel.TagName, vid);
                // Not reinstalled (failed, cancelled, show lock, the app left open): keep the choice matching what's on disk.
                var now = InstallManager.Shared.InstalledPath(slot);
                if (now != null && await Task.Run(() => PeArch.OfFile(now)) != want)
                {
                    StoreX64(slot, before);
                    Log.Write($"{kind} build: {slot} not reinstalled — choice put back");
                }
            }
            else
            {
                StoreX64(slot, on);
                Log.Write($"{kind} build: {slot}");
            }
            // The chosen build may be in a different latest release (and have a different size): re-pick the row.
            RecomputeRow(row);
            RefreshDownloadAllButton();
        }
        catch (Exception ex) { Log.Write($"x64 build {row.App.Id}: {ex.Message}"); }
    }

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

    /// <summary>True when the row has a same-group VISIBLE neighbour in that direction (never while the list is
    /// filtered — moving within a subset would scramble the hidden rows' order).</summary>
    private bool CanMoveRow(AppRowControl row, bool up) => !FilterActive && GroupNeighbour(row, up) >= 0;

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

    // ── Find & filter ──────────────────────────────────────────────────────────────────────────

    private StatusFilter CurrentFilter => _statusFilter.SelectedIndex < 0 ? StatusFilter.All : (StatusFilter)_statusFilter.SelectedIndex;

    /// <summary>The list is narrowed by the search box or the status dropdown.</summary>
    private bool FilterActive => AppFilter.IsActive(_search.Text, CurrentFilter);

    private bool MatchesFilter(AppRowControl r) =>
        AppFilter.MatchesQuery(_search.Text, r.DisplayName, r.App.Blurb, r.App.Category, r.App.Id)
        && AppFilter.MatchesStatus(CurrentFilter, r.Installed != null,
                                   r.Status == RowStatus.UpdateAvailable && !IsHeld(r.App.Id),
                                   r.Status == RowStatus.NotInstalled);

    // ── Hold (keep an app at its installed version) ────────────────────────────────────────────

    private bool IsHeld(string id) => _settings.HeldApps.Contains(id);

    private void ToggleHold(AppRowControl row)
    {
        var id = row.App.Id;
        if (!_settings.HeldApps.Remove(id)) _settings.HeldApps.Add(id);
        _settings.Save();
        row.RefreshHeld();
        RefreshDownloadAllButton();
        ReindexList();
        Log.Write($"{(IsHeld(id) ? "held" : "released hold on")} {id}");
    }

    // ── Show lock (installs / updates / removals paused; Launch still works) ───────────────────

    private bool Locked => _settings.ShowLock;

    /// <summary>From the UI (Ctrl+L, the banner, the More menu): turning the lock ON is one step; turning it OFF asks
    /// (the same confirmation as the Settings checkbox), so a stray keystroke or click mid-show can't re-enable
    /// installs and automatic updates.</summary>
    private void RequestShowLock(bool on)
    {
        if (on == Locked) return;
        if (!on && !DialogKit.ConfirmTurnOffShowLock(this)) return;
        SetShowLock(on);
    }

    private void SetShowLock(bool on)
    {
        if (_settings.ShowLock == on) return;
        _settings.ShowLock = on;
        _settings.Save();
        ApplyLock();
        Log.Write($"show lock {(on ? "on" : "off")}");
        if (on) StopEverything();
    }

    /// <summary>Show lock just turned on: stop a batch and cancel every download in flight (a slot that has already
    /// downloaded is dropped before it installs — see InstallDownloadedAsync).</summary>
    private void StopEverything()
    {
        StopBatch();
        foreach (var cts in _rowCts.Values.ToList()) cts.Cancel();
    }

    /// <summary>Reflects the show-lock setting everywhere: the banner, every row, the Download All button.</summary>
    private void ApplyLock()
    {
        _lockBanner.Visible = Locked;
        foreach (var r in _rows) r.SetLocked(Locked);
        RefreshDownloadAllButton();
    }

    /// <summary>The model-level guard behind every install / update / removal path (the UI hides them too).</summary>
    private bool BlockedByLock(string what)
    {
        if (!Locked) return false;
        Log.Write($"{what}: blocked by show lock");
        return true;
    }

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
        var shown = _rows.Where(WouldShow).Where(MatchesFilter).ToList();
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
        bool filtering = FilterActive;
        int firstCat = groups.FindIndex(x => x.key != PinnedKey);
        int lastCat = groups.Count - 1;
        var used = new HashSet<string>();
        var placed = new HashSet<AppRowControl>();
        int idx = 0;

        // A filter that matches nothing says so, rather than leaving an empty list.
        bool noMatches = filtering && groups.Count == 0 && _rows.Any(WouldShow) && _noMatches.Parent == _list;
        _noMatches.Visible = noMatches;
        if (noMatches) _list.Controls.SetChildIndex(_noMatches, idx++);

        for (int gi = 0; gi < groups.Count; gi++)
        {
            var (key, title, rows) = groups[gi];
            // While filtering, collapsed sections show open so a match can never hide behind a fold.
            bool collapsed = IsCollapsed(key) && !filtering;
            bool pinnedGroup = key == PinnedKey;
            var header = HeaderFor(key);
            header.Configure(key, title, rows.Count, collapsed, pinnedGroup, Theme.IsDark(_settings.Appearance));
            header.SetMoveEnabled(!filtering && !pinnedGroup && gi > firstCat, !filtering && !pinnedGroup && gi < lastCat);
            header.Width = ListInnerWidth;
            header.Visible = true;
            _list.Controls.SetChildIndex(header, idx++);
            used.Add(key);

            if (collapsed) continue;
            foreach (var r in rows)
            {
                r.Visible = true;
                _list.Controls.SetChildIndex(r, idx++);
                placed.Add(r);
            }
        }

        // Rows not shown (hidden, collapsed, not eligible, filtered out) take no space; park them + unused headers.
        foreach (var r in _rows.Where(r => !placed.Contains(r)))
        {
            r.Visible = false;
            _list.Controls.SetChildIndex(r, idx++);
        }
        foreach (var h in _headers.Values.Where(h => !used.Contains(h.Key)))
        {
            h.Visible = false;
            _list.Controls.SetChildIndex(h, idx++);
        }
        if (!noMatches && _noMatches.Parent == _list) _list.Controls.SetChildIndex(_noMatches, idx++);
        int total = _rows.Count(WouldShow);
        _filterCount.Text = filtering ? $"{placed.Count} of {total} app{(total == 1 ? "" : "s")}"
                                      : $"{total} app{(total == 1 ? "" : "s")}";
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
        if (FilterActive) return;   // no reordering a filtered subset (the row's own capture ends on mouse-up)
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

    /// <summary>Apps whose check didn't complete in the last run (a network error, or rejected credentials) — they keep
    /// what they were announced at (see AfterCheck), even when a scheduled check left their row as it was.</summary>
    private readonly HashSet<string> _uncheckedIds = new();
    private sealed record FetchResult(AppRowControl Row, FetchKind Kind, List<ReleaseInfo>? Releases, string? ErrorMsg);

    /// <summary>Checks every app for updates (one at a time — a second request while one runs is ignored), then
    /// runs the after-check work: update notifications and, when switched on, automatic updates.</summary>
    /// <param name="announce">Say the result even when nothing is new (a check the user asked for from the tray).</param>
    private async Task<bool> RefreshAllAsync(bool announce = false, bool quiet = false)
    {
        if (_refreshing) return false;
        _refreshing = true;
        bool ok;
        try { ok = await RefreshCoreAsync(quiet); }
        finally { _refreshing = false; }
        _lastCheck = DateTimeOffset.UtcNow;   // an attempt counts: bad credentials mustn't mean a retry every minute
        if (ok) AfterCheck(announce);
        return ok;
    }

    /// <param name="quiet">A scheduled background check: rows keep what they show until a result lands (no
    /// "Checking…", which hid every Install / Update button for the length of the check).</param>
    private async Task<bool> RefreshCoreAsync(bool quiet = false)
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
            return false;
        }
        // No credentials (e.g. just removed in Settings): reset every row to its installed/unknown state
        // and clear stale latest/releases, so no row keeps a live — but silently no-op — Install button.
        if (active == null) { ResetRowsNoToken(); ShowNotice(NoCredsMsg); RefreshDownloadAllButton(); return false; }
        ShowNotice(null);

        _refresh.Enabled = false;
        bool unauthorized = false;
        try
        {
            using var client = active;

            // Mark every row "checking" up front, then fetch all apps CONCURRENTLY (HttpClient handles
            // parallel requests; the OS caps connections per host, so this self-throttles). The old
            // sequential loop did ~one network round-trip × 20 in series — seconds of lag on boot.
            if (!quiet)
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
            _uncheckedIds.Clear();
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
                        var assetName = AssetFor(row.App, SelectedVariant(row.App));
                        var latest = Versions.LatestFor(res.Releases!, assetName);
                        if (latest == null)
                        {
                            // Nothing released on this PC's channel (only dev builds so far, or none): only the
                            // Development builds switch shows it, so everyone else never sees a dead row.
                            accessible = Versions.DevChannel;
                            row.SetState(installed, null, null, RowStatus.NoRelease);
                        }
                        else
                        {
                            var asset = latest.Assets.FirstOrDefault(a => a.Name == assetName);
                            row.SetState(installed, latest.TagName, asset?.Id, ComputeStatus(installed, latest.TagName, asset != null));
                        }
                        break;
                    case FetchKind.Unauthorized: unauthorized = true; accessible = false; _uncheckedIds.Add(row.App.Id); break;
                    case FetchKind.NoRelease:
                        accessible = Versions.DevChannel; row.SetReleases(new List<ReleaseInfo>());
                        row.SetState(installed, null, null, RowStatus.NoRelease); break;
                    case FetchKind.Error:
                        // A scheduled (quiet) check that couldn't reach the feed leaves the row as it was: an offline
                        // show machine shouldn't turn every row into an error with a Retry button every few hours. The
                        // launch check and Refresh still show the error.
                        // Quiet: visibility stays as it was too (a hidden app mustn't appear because the feed was down).
                        accessible = !quiet || _eligible.Contains(row.App.Id); _uncheckedIds.Add(row.App.Id);
                        if (!quiet) row.SetState(installed, null, null, RowStatus.Error);
                        Log.Write($"refresh {row.App.Id} error{(quiet ? " (scheduled check — row left as it was)" : "")}: {res.ErrorMsg}"); break;
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
        return !unauthorized;
    }

    /// <summary>Shows the "Download All ▾" dropdown only when there's real work to do — some app (as
    /// shown) isn't installed or has an update — so it disappears once everything is downloaded and up to
    /// date. The per-item Update-all count lives inside the menu, built fresh on each open.</summary>
    private void RefreshDownloadAllButton()
    {
        int updates = UpdatesAvailable();
        bool show = _batchRunning
                    || (!Locked && _rows.Any(WouldShow)
                        && AuthClient.HasCredentials(_settings, _catalog.DownloadServer)
                        && (updates > 0 || HasAnyToDownload(false)));
        bool stopping = _batchRunning && _batchCts?.IsCancellationRequested == true;
        _downloadAll.Text = stopping ? "Stopping…" : _batchRunning ? "Stop  ■" : updates > 0 ? $"Update All ({updates})  ▾" : "Download All  ▾";
        _downloadAll.Enabled = !stopping;
        _headerTip.SetToolTip(_downloadAll, _batchRunning
            ? "Stop after the app that's installing now — the download in progress is cancelled"
            : "Install or update every app in one go");
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
        DialogKit.DisposeWhenClosed(menu, this);
        int n = UpdatesAvailable();
        if (n > 0)
        {
            var upd = new ToolStripMenuItem($"Update {n} installed app{(n == 1 ? "" : "s")} — incl. Full editions{SizeSuffix(UpdateWork())}");
            upd.Click += async (_, _) => await UpdateAllAsync();
            menu.Items.Add(upd);
            menu.Items.Add(new ToolStripSeparator());
        }
        var all = new ToolStripMenuItem($"Install every app{SizeSuffix(DownloadWork(false))}");
        all.Click += async (_, _) => await DownloadAllAsync(false);
        menu.Items.Add(all);
        if (HasFullVariants)
        {
            var full = new ToolStripMenuItem($"Install every app — plus the Full editions{SizeSuffix(DownloadWork(true))}");
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
            var name = AssetFor(row.App, vid);
            var rel = Versions.LatestFor(row.Releases, name);   // per edition (a dev build may lack one)
            if (name == null || rel == null || !rel.Assets.Any(a => a.Name == name)) continue;   // no asset for this arch → skip
            var installed = InstallManager.Shared.InstalledVersion(row.App.InstallKey(vid));
            // A held app is only ever fetched when that edition isn't installed at all.
            if (installed == null || (!IsHeld(row.App.Id) && Versions.IsNewer(rel.TagName, installed))) yield return vid;
        }
    }

    /// <summary>Download All: install/update every app. <paramref name="includeFull"/> also fetches Full
    /// editions into their own slots (Light and Full side by side). Skips anything current or with no
    /// asset for this arch.</summary>
    private async Task DownloadAllAsync(bool includeFull)
    {
        if (BlockedByLock("download all")) return;
        var work = DownloadWork(includeFull);
        Log.Write($"download all{(includeFull ? " (incl. Full)" : "")}: {work.Count} slot(s)");
        await RunSlotsAsync(work, "Download All");
    }

    /// <summary>The Download All work list: every slot to fetch, default editions first.</summary>
    private List<(AppRowControl row, string? vid, string? tag)> DownloadWork(bool includeFull) =>
        OrderedSlots(_rows.ToList().SelectMany(r => SlotsToDownload(r, includeFull).ToList().Select(v => (r, v, (string?)null))));

    /// <summary>The Update All work list: every installed, non-held slot with an update, default editions first.</summary>
    private List<(AppRowControl row, string? vid, string? tag)> UpdateWork() =>
        OrderedSlots(_rows.ToList().SelectMany(r => SlotsToUpdate(r).ToList().Select(v => (r, v, (string?)null))));

    /// <summary>The download size of a work list's latest assets.</summary>
    private long WorkBytes(IEnumerable<(AppRowControl row, string? vid, string? tag)> work) => ByteSize.Sum(work.Select(w =>
    {
        var name = AssetFor(w.row.App, w.vid);
        var rel = Versions.LatestFor(w.row.Releases, name);
        return rel?.Assets.FirstOrDefault(a => a.Name == name)?.Size ?? 0L;
    }));

    /// <summary>"  (450 MB)" for a menu item, or "" when the size isn't known.</summary>
    private string SizeSuffix(List<(AppRowControl row, string? vid, string? tag)> work)
    {
        long bytes = WorkBytes(work);
        return bytes > 0 ? $"  ({ByteSize.Format(bytes)})" : "";
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
        if (latest == null || IsHeld(row.App.Id)) yield break;   // held: Update All / auto-update leave it alone
        var variants = new List<string?> { row.App.HasVariants ? row.App.Variants?.FirstOrDefault()?.Id : null };
        if (row.App.Variants != null) variants.AddRange(row.App.Variants.Skip(1).Select(v => (string?)v.Id));
        foreach (var vid in variants)
        {
            var name = AssetFor(row.App, vid);
            var rel = Versions.LatestFor(row.Releases, name);   // per edition (a dev build may lack one)
            if (name == null || rel == null || !rel.Assets.Any(a => a.Name == name)) continue;
            var installed = InstallManager.Shared.InstalledVersion(row.App.InstallKey(vid));
            if (installed != null && Versions.IsNewer(rel.TagName, installed)) yield return vid;
        }
    }

    /// <summary>Count of installed slots (not rows) with an update — drives the "Update All (N)" label.</summary>
    private int UpdatesAvailable() => _rows.Sum(r => SlotsToUpdate(r).Count());

    private async Task UpdateAllAsync()
    {
        if (BlockedByLock("update all")) return;
        var flat = UpdateWork();
        if (flat.Count == 0) return;
        Log.Write($"update all: {flat.Count} slot(s) across {flat.Select(w => w.row).Distinct().Count()} app(s)");
        await RunSlotsAsync(flat, "Update All");
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
        menu.Opening += (_, _) => BuildTrayMenu(menu);   // rebuilt on every open: the Launch list is always current
        BuildTrayMenu(menu);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.BalloonTipClicked += (_, _) => { _balloonShowing = false; RestoreFromTray(); };
        // The icon stays after a notification: hiding it removed the notification from Action Center too.
        _tray.BalloonTipClosed += (_, _) => { };
        UpdateTrayVisibility();
    }

    /// <summary>The tray menu: open the window, launch any installed app directly (pinned first), check for
    /// updates (the answer comes back as a notification), quit.</summary>
    private void BuildTrayMenu(ContextMenuStrip menu)
    {
        // Rebuilt on every open: dispose the previous items (and their Launch submenu) rather than just dropping them.
        foreach (var old in menu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
        menu.Items.Clear();
        menu.Items.Add("Open JB Theatre Tools", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new ToolStripSeparator());
        var launch = new ToolStripMenuItem("Launch");
        foreach (var (key, name) in InstalledSlotsForLaunch())
            launch.DropDownItems.Add(name, null, (_, _) => LaunchKey(key, name));
        if (launch.DropDownItems.Count == 0) launch.DropDownItems.Add(new ToolStripMenuItem("No apps installed") { Enabled = false });
        menu.Items.Add(launch);
        menu.Items.Add("Check for updates", null, async (_, _) => await TrayCheckAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { _reallyQuit = true; Close(); });
    }

    /// <summary>The tray's "Check for updates" always answers: the result (see AfterCheck), or why there is none — a
    /// check already running, no download access set up, or a check that failed.</summary>
    private async Task TrayCheckAsync()
    {
        try
        {
            if (_refreshing) { ShowBalloon("JB Theatre Tools", "Already checking for updates."); return; }
            if (!AuthClient.HasCredentials(_settings, _catalog.DownloadServer))
            {
                ShowBalloon("Can't check for updates", _settings.AuthMode == "server"
                    ? "Enter the suite passphrase in Settings → Download access first."
                    : "Add a GitHub token in Settings → Download access first.");
                return;
            }
            if (!await RefreshAllAsync(announce: true))
                ShowBalloon("Couldn't check for updates", _tokenBanner.Visible ? _tokenBannerText.Text : "Try again in a moment.");
        }
        catch (Exception ex)
        {
            Log.Write($"tray check failed: {ex.Message}");
            ShowBalloon("Couldn't check for updates", ex.Message);
        }
    }

    /// <summary>Every installed slot as (install key, name) — pinned apps first, then the list order.</summary>
    private IEnumerable<(string key, string name)> InstalledSlotsForLaunch()
    {
        foreach (var r in _rows.OrderBy(r => IsPinned(r.App.Id) ? 0 : 1))
        {
            IEnumerable<string?> vids = r.App.HasVariants && r.App.Variants != null
                ? r.App.Variants.Select(v => (string?)v.Id) : new string?[] { null };
            foreach (var vid in vids)
            {
                var key = r.App.InstallKey(vid);
                if (InstallManager.Shared.InstalledVersion(key) != null) yield return (key, r.App.Name + r.App.VariantSuffix(vid));
            }
        }
    }

    private void LaunchKey(string key, string name)
    {
        // Never start an app while it's being verified / installed / removed (the tray has no busy state of its own).
        // While it is only downloading it can be opened: the install step asks to quit it (or an automatic update
        // leaves it for later).
        if (_slotsInstalling.Contains(key))
        {
            MessageBox.Show(this, $"{name} is being installed. Open it when that has finished.", "JB Theatre Tools",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { InstallManager.Shared.Launch(key); Log.Write($"launched {key} (tray)"); }
        catch (Exception ex)
        {
            if (IsForeground()) MessageBox.Show(this, ex.Message, "Launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else ShowBalloon($"Couldn't open {name}", ex.Message);
        }
    }

    /// <summary>The icon shows while the window is hidden to the tray, while a notification is up, or always when
    /// the user asked for it (quick launch from the notification area).</summary>
    private void UpdateTrayVisibility() => _tray.Visible = _settings.AlwaysShowTray || _hiddenToTray || _balloonShowing;

    private void ShowBalloon(string title, string text)
    {
        _balloonShowing = true;
        UpdateTrayVisibility();
        _tray.ShowBalloonTip(10_000, title, text, ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        _hiddenToTray = false;
        UpdateTrayVisibility();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // House convention: X quits by default. If the user opted into "keep running", minimise to the
        // tray instead — unless we're genuinely quitting (tray "Quit" item, or a real OS shutdown).
        if (!_reallyQuit && e.CloseReason == CloseReason.UserClosing && _settings.CloseBehavior == "keepRunning")
        {
            e.Cancel = true;
            Hide();
            _hiddenToTray = true;
            UpdateTrayVisibility();
            return;
        }
        _scheduler.Stop();
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
                                     string Cache, string AssetName, string? VariantId, string? Tag, bool Unattended,
                                     bool Lenient = false);

    /// <summary>The most recent failure of each row's install (set by FailSlot, cleared when a download starts) —
    /// how a batch tells a real failure from a silent skip or a cancel.</summary>
    /// <summary>The last failure per install SLOT (app + edition): a batch downloads Light and Full of one app back to
    /// back, and the look-ahead for one edition must never clear the other's failure.</summary>
    private readonly Dictionary<string, Exception> _lastFailure = new();

    /// <summary>Installs one slot: phase 1 (download) then phase 2 (verify + extract). Returns the exception on
    /// failure (already recorded on the row + logged), or null. <paramref name="interactive"/> shows the failure
    /// dialog immediately; batch runs pass false and show ONE summary at the end instead of halting the batch
    /// on a modal box nobody is there to click.</summary>
    private async Task<Exception?> InstallSlotAsync(AppRowControl row, string? tag, string? variantOverride, bool interactive = true,
                                                    bool unattended = false, CancellationToken batchToken = default,
                                                    bool lenient = false)
    {
        var d = await DownloadSlotAsync(row, tag, variantOverride, interactive, unattended, batchToken, lenient && tag != null);
        if (d == null) return null;
        // Only a hand-picked older version may skip the signed-manifest requirement (see InstallPickedVersionRequested).
        if (lenient && tag != null) d = d with { Lenient = true };
        return await InstallDownloadedAsync(d, interactive);
    }

    /// <summary>Cancels the row's in-flight download (Cancel button / menu). Not an error: the row goes back to how
    /// it was, and the partial file is removed.</summary>
    private void CancelRow(AppRowControl row)
    {
        if (_rowCts.TryGetValue(row, out var cts)) { Log.Write($"install {row.App.Id}: cancel requested"); cts.Cancel(); }
    }

    /// <summary>A latest-version slot of an app that's held and already installed (a hold set after the batch started).</summary>
    private bool HeldNow(AppRowControl row, string? tag, string? installed) => tag == null && installed != null && IsHeld(row.App.Id);

    /// <summary>Stops a running batch: cancels the download in flight and starts nothing more (a slot that has
    /// already downloaded still finishes installing).</summary>
    private void StopBatch()
    {
        if (_batchCts == null) return;
        Log.Write("batch: stop requested");
        _batchCts.Cancel();
        RefreshDownloadAllButton();   // "Stopping…" until the app that's installing finishes
    }

    /// <summary>Drops a finished download that won't be installed (a stopped batch).</summary>
    private void DiscardDownloaded(Downloaded d)
    {
        _slotsInFlight.Remove(d.Row.App.InstallKey(d.VariantId));   // a dropped download releases its slot too
        d.Client.Dispose();
        _ = Task.Run(() => InstallManager.TryDelete(d.Cache));
        d.Row.SetPhase(null);
        d.Row.SetBusy(false);
    }

    /// <summary>Phase 1 — resolve the release/asset and download it into the cache (network-bound). Marks the
    /// row busy for the whole slot; on failure records it and ends busy. Returns null on failure or a silent skip.</summary>
    /// <summary>Install slots between "download starts" and "install finished" — one of each at a time (UI thread only).
    /// A second request for the same slot (a row click during Update All, the tray, a refresh re-enabling a button)
    /// used to download to the same cache file concurrently.</summary>
    private readonly HashSet<string> _slotsInFlight = new();

    /// <summary>Install slots being verified, installed or removed (a subset of the in-flight ones; UI thread only) —
    /// the only time Launch is off. A slot that is only downloading can still be opened.</summary>
    private readonly HashSet<string> _slotsInstalling = new();

    private async Task<Downloaded?> DownloadSlotAsync(AppRowControl row, string? tag, string? variantOverride, bool interactive,
                                                      bool unattended = false, CancellationToken batchToken = default,
                                                      bool lenient = false)
    {
        var key = row.App.InstallKey(variantOverride ?? SelectedVariant(row.App));
        if (!_slotsInFlight.Add(key))
        {
            // A skip, not a failure: a batch reads _lastFailure after a null, so an older failure mustn't linger.
            _lastFailure.Remove(key);
            Log.Write($"install {key}: already in progress — skipped");
            return null;
        }
        Downloaded? d = null;
        try { return d = await DownloadSlotCoreAsync(row, tag, variantOverride, interactive, unattended, batchToken, lenient); }
        finally { if (d == null) _slotsInFlight.Remove(key); }   // handed on: InstallDownloadedAsync releases it
    }

    private async Task<Downloaded?> DownloadSlotCoreAsync(AppRowControl row, string? tag, string? variantOverride, bool interactive,
                                                          bool unattended, CancellationToken batchToken, bool lenient)
    {
        var variantId = variantOverride ?? SelectedVariant(row.App);
        _lastFailure.Remove(row.App.InstallKey(variantId));
        if (BlockedByLock($"install {row.App.Id}")) return null;
        GitHubClient? client;
        try { client = AuthClient.Active(_settings, _catalog.DownloadServer); }
        catch (Exception ex)   // it can throw (audit F15): a failed slot, never a stuck batch
        {
            FailSlot(row, ex, interactive, variantId, tag);
            return null;
        }
        if (client == null) return null;
        var assetName = AssetFor(row.App, variantId);
        if (assetName == null) { client.Dispose(); return null; }

        row.SetBusy(true);
        row.SetPhase("Downloading…", cancellable: true);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(batchToken);
        _rowCts[row] = cts;
        try
        {
            var releases = row.Releases.Count > 0
                ? row.Releases
                : await client.ReleasesAsync(row.App.Owner, row.App.Repo);
            var rel = tag != null
                // A development build installs only with Development builds on (a setup file can name one). "1.2.0"
                // and "v1.2.0" are the same version (the Android launcher exports tags without the "v").
                ? releases.FirstOrDefault(r => VersionCompare.Equal(r.TagName, tag) && (Versions.DevChannel || !VersionCompare.IsDev(r.TagName)))
                : Versions.LatestFor(releases, assetName);   // per edition
            if (rel == null) throw new Exception($"Version {tag ?? "latest"} not found.");
            if (tag != null) tag = rel.TagName;   // the release's real tag from here on
            var asset = rel.Assets.FirstOrDefault(a => a.Name == assetName)
                ?? throw new Exception($"No Windows asset in {rel.TagName}.");
            // A strict install (everything but the hand-picked version list) needs signed checksums: say so now
            // rather than after downloading 300–450 MB that could never pass.
            if (!lenient && !Versions.HasSignedManifest(rel))
                throw new Exception($"{rel.TagName} can't be verified: " + InstallManager.StrictFailureReason(
                    rel.Assets.Any(a => a.Name == "SHA256SUMS") ? VerifyResult.Unsigned : VerifyResult.NoManifest, assetName) + ".");

            // Refuse up front rather than failing half-way through an extract on a full disk.
            var shortfall = DiskSpace.Shortfall(DiskSpace.Required(asset.Size, assetName), InstallManager.Shared.FreeSpace());
            if (shortfall != null) throw new Exception(shortfall);

            var cache = Path.Combine(InstallManager.Shared.CacheDir, $"{row.App.Id}-{rel.TagName}-{assetName}");
            var progress = new Progress<double>(p => row.SetProgress(p));
            await client.DownloadAssetAsync(row.App.Owner, row.App.Repo, asset.Id, cache, progress, cts.Token);
            return new Downloaded(row, client, rel, asset, cache, assetName, variantId, tag, unattended);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Cancelled (the row's Cancel, or a stopped batch): not a failure — the row simply goes back.
            client.Dispose();
            row.SetPhase(null);
            row.SetBusy(false);
            RefreshDownloadAllButton();
            Log.Write($"install {row.App.Id}: download cancelled");
            return null;
        }
        catch (Exception ex)
        {
            client.Dispose();
            FailSlot(row, ex, interactive, variantId, tag);
            row.SetBusy(false);
            RefreshDownloadAllButton();
            return null;
        }
        finally
        {
            if (_rowCts.TryGetValue(row, out var mine) && mine == cts) _rowCts.Remove(row);
            cts.Dispose();
        }
    }

    /// <summary>Phase 2 — verify (size + signed SHA256SUMS + hash), extract/copy off the UI thread, reflect it in
    /// the row. Always ends the row's busy state and disposes the client.</summary>
    private async Task<Exception?> InstallDownloadedAsync(Downloaded d, bool interactive)
    {
        var key = d.Row.App.InstallKey(d.VariantId);
        try { return await InstallDownloadedCoreAsync(d, interactive); }
        finally
        {
            _slotsInFlight.Remove(key);
            if (_slotsInstalling.Remove(key)) d.Row.RefreshLaunch();
        }
    }

    private async Task<Exception?> InstallDownloadedCoreAsync(Downloaded d, bool interactive)
    {
        var (row, rel, asset, cache, assetName, variantId, tag) = (d.Row, d.Rel, d.Asset, d.Cache, d.AssetName, d.VariantId, d.Tag);
        var slotKey = row.App.InstallKey(variantId);
        var fromVersion = InstallManager.Shared.InstalledVersion(slotKey);   // for the history line
        if (BlockedByLock($"install {row.App.Id} {rel.TagName}"))
        {
            // Show lock turned on while this was downloading: drop it, install nothing.
            DiscardDownloaded(d);
            RefreshDownloadAllButton();
            return null;
        }
        try
        {
            using var client = d.Client;
            // From here to the end the slot's files are about to change: Launch goes off (it stayed on while this
            // was only downloading).
            _slotsInstalling.Add(slotKey);
            row.RefreshLaunch();
            row.SetPhase("Verifying…", indeterminate: true);
            var verification = await InstallManager.VerifyDownloadAsync(cache, asset, rel, row.App.Owner, row.App.Repo, client);
            // Strict unless the user hand-picked an older version from the ⋯ list (which may predate the signed
            // manifest): latest, roll back, back to release and setup-file installs MUST verify against the signed
            // SHA256SUMS. A hash MISMATCH always aborts (it throws from VerifyDownloadAsync).
            if (!d.Lenient && verification != VerifyResult.Verified)
            {
                _ = Task.Run(() => InstallManager.TryDelete(cache));   // 300-450 MB unlink: never on the UI thread
                var reason = InstallManager.StrictFailureReason(verification, assetName);
                Log.Write($"install {row.App.Id} {rel.TagName}: BLOCKED (strict) — {reason}");
                throw new Exception($"Couldn't verify the download — {reason}. Install aborted for safety.");
            }
            // Show lock turned on while this downloaded or verified: install nothing, and never raise the "close it
            // first?" question below (it brings the launcher to the front, over the show).
            if (BlockedByLock($"install {row.App.Id} {rel.TagName}"))
            {
                row.SetPhase(null);
                _ = Task.Run(() => InstallManager.TryDelete(cache));
                return null;
            }
            // The app is open: ASK instead of failing with "file in use". Yes → close it (it may prompt to
            // save) and wait for it to exit; No → skip quietly, the row keeps its Update button.
            var running = InstallManager.Shared.RunningInstances(row.App.InstallKey(variantId));
            if (running.Length > 0 && d.Unattended)
            {
                // An automatic update never interrupts an open app: leave it for the next check.
                foreach (var p in running) p.Dispose();
                row.SetPhase(null);
                _ = Task.Run(() => InstallManager.TryDelete(cache));
                Log.Write($"install {row.App.Id} {rel.TagName}: left for later — it's open (automatic update)");
                return null;
            }
            if (running.Length > 0)
            {
                var name = row.App.Name + row.App.VariantSuffix(variantId);
                row.SetPhase("Waiting…", indeterminate: true);
                var answer = MessageBox.Show(this,
                    $"{name} is open. Close it to install the update?\n\nIf it has unsaved work it will ask you first.",
                    $"{name} is open", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                // Show lock came on while the question was up: close nothing and install nothing, whatever the answer.
                if (Locked)
                {
                    foreach (var p in running) p.Dispose();
                    row.SetPhase(null);
                    _ = Task.Run(() => InstallManager.TryDelete(cache));
                    Log.Write($"install {row.App.Id} {rel.TagName}: not installed — show lock is on");
                    return null;
                }
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
            // …and again after the "close it first?" wait, immediately before anything on disk changes.
            if (BlockedByLock($"install {row.App.Id} {rel.TagName}"))
            {
                row.SetPhase(null);
                _ = Task.Run(() => InstallManager.TryDelete(cache));
                return null;
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
            History.Add(slotKey, row.App.Name + row.App.VariantSuffix(variantId),
                        ActivityHistory.ActionFor(fromVersion, rel.TagName), fromVersion, rel.TagName);
            return null;
        }
        catch (Exception ex)
        {
            FailSlot(row, ex, interactive, variantId, rel.TagName);
            return ex;
        }
        finally
        {
            row.SetBusy(false);
            RefreshDownloadAllButton();
        }
    }

    private void FailSlot(AppRowControl row, Exception ex, bool interactive, string? variantId = null, string? tag = null)
    {
        _lastFailure[row.App.InstallKey(variantId)] = ex;
        History.Add(row.App.InstallKey(variantId), row.App.Name + row.App.VariantSuffix(variantId), "failed", null, tag, ex.Message);
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
    private async Task<List<(string Name, string Version)>> RunSlotsAsync(List<(AppRowControl row, string? vid, string? tag)> work,
                                                                         string title, bool unattended = false)
    {
        var done = new List<(string Name, string Version)>();
        if (_batchRunning) { Log.Write($"{title}: another batch is running — not started"); return done; }
        _batchRunning = true;
        _batchCts = new CancellationTokenSource();
        var token = _batchCts.Token;
        RefreshDownloadAllButton();   // the header button becomes "Stop"
        var failures = new List<(string name, string msg)>();
        // The list is fixed when the batch starts, but the person can keep working: each slot is re-checked just before
        // its turn (and before its look-ahead download starts) so the batch never undoes or repeats what they did.
        var installedAtStart = work.Select(w => InstallManager.Shared.InstalledVersion(w.row.App.InstallKey(w.vid))).ToList();
        string? SkipReason(int i)
        {
            var (row, vid, tag) = work[i];
            var now = InstallManager.Shared.InstalledVersion(row.App.InstallKey(vid));
            // Held after the batch started: an update (latest, already installed) leaves it where it is.
            if (HeldNow(row, tag, now)) return "is held";
            if (installedAtStart[i] != null && now == null) return "was removed meanwhile";
            var target = tag ?? Versions.LatestFor(row.Releases, AssetFor(row.App, vid))?.TagName;
            if (now != null && target != null && VersionCompare.Equal(now, target)) return $"is already at {VersionCompare.Display(target)}";
            return null;
        }
        Task<Downloaded?>? next = null;
        try
        {
            for (int i = 0; i < work.Count; i++)
            {
                if (token.IsCancellationRequested) break;
                var (row, vid, tag) = work[i];
                var key = row.App.InstallKey(vid);
                if (SkipReason(i) is { } reason)
                {
                    if (next != null)
                    {
                        if (_rowCts.TryGetValue(row, out var lookAhead)) lookAhead.Cancel();   // its look-ahead: no point finishing it
                        var skipped = await next;
                        if (skipped != null) DiscardDownloaded(skipped);
                    }
                    next = null;
                    Log.Write($"{title}: {row.App.Id} {reason} — skipped");
                    continue;
                }
                var before = InstallManager.Shared.InstalledVersion(key);
                var current = next ?? DownloadSlotAsync(row, tag, vid, interactive: false, unattended, token);
                var d = await current;                                   // download N done (or failed + recorded)
                next = null;
                if (i + 1 < work.Count && !token.IsCancellationRequested && SkipReason(i + 1) == null)   // start download N+1 now…
                {
                    var (nrow, nvid, ntag) = work[i + 1];
                    next = DownloadSlotAsync(nrow, ntag, nvid, interactive: false, unattended, token);
                }
                Exception? err = null;
                if (d != null && SkipReason(i) is { } late)
                {
                    DiscardDownloaded(d);
                    Log.Write($"{title}: {row.App.Id} {late} — skipped");
                }
                else if (d != null) err = await InstallDownloadedAsync(d, interactive: false);   // …verify + extract N meanwhile
                else err = _lastFailure.TryGetValue(key, out var fe) ? fe : null;                  // a skip or cancel isn't a failure
                if (err != null && IsTransient(err) && !token.IsCancellationRequested && !Locked && SkipReason(i) == null)
                {
                    Log.Write($"install {row.App.Id}: retrying once after a transient error: {err.Message}");
                    await Task.Delay(2000);
                    var rd = await DownloadSlotAsync(row, tag, vid, interactive: false, unattended, token);
                    err = rd != null ? await InstallDownloadedAsync(rd, interactive: false)
                                     : (_lastFailure.TryGetValue(key, out var re) ? re : null);
                }
                if (err != null)
                    failures.Add((vid == null ? row.DisplayName : $"{row.DisplayName} ({row.App.VariantLabel(vid)})", err.Message));
                var after = InstallManager.Shared.InstalledVersion(key);
                if (after != null && (before == null || !VersionCompare.Equal(after, before)))
                    done.Add((row.App.Name + row.App.VariantSuffix(vid), after));
            }
        }
        finally
        {
            // A stopped batch may leave the look-ahead download running or finished: wait for it and drop it. It
            // records its own failures, but never let one escape here and leave the batch stuck "running".
            if (next != null)
            {
                try { var d = await next; if (d != null) DiscardDownloaded(d); }
                catch (Exception ex) { Log.Write($"{title}: look-ahead download ended with {ex.Message}"); }
            }
            bool stopped = token.IsCancellationRequested;
            _batchCts?.Dispose();
            _batchCts = null;
            _batchRunning = false;
            RefreshDownloadAllButton();
            Log.Write($"{title} {(stopped ? "stopped" : "complete")} ({done.Count} installed, {failures.Count} failed)");
        }
        if (failures.Count > 0 && !unattended)
            MessageBox.Show(this,
                $"{failures.Count} app{(failures.Count == 1 ? "" : "s")} could not be installed:\n\n" +
                string.Join("\n", failures.Select(f => $"• {f.name} — {f.msg}")),
                title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return done;
    }

    /// <summary>Default editions first, then Full editions — so consecutive slots are (almost always) different
    /// apps and the pipelined runner overlaps two apps rather than two slots of one row.</summary>
    private static List<(AppRowControl row, string? vid, string? tag)> OrderedSlots(IEnumerable<(AppRowControl row, string? vid, string? tag)> slots)
    {
        var list = slots.ToList();
        return list.Where(s => s.row.App.IsDefaultVariant(s.vid)).Concat(list.Where(s => !s.row.App.IsDefaultVariant(s.vid))).ToList();
    }

    private async void Uninstall(AppRowControl row)
    {
        if (BlockedByLock($"uninstall {row.App.Id}")) return;
        if (MessageBox.Show(this, $"Uninstall {row.DisplayName}?", "Uninstall",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        // Re-check after the dialog: an install (or show lock) may have started while it was open.
        if (row.IsBusy || BlockedByLock($"uninstall {row.App.Id}")) return;
        // Uninstalls the SELECTED variant's slot only (a sibling variant, if installed, stays). The delete runs
        // off the UI thread: a one-dir Full edition is thousands of files (300–450 MB), and Directory.Delete
        // of that froze the whole window for seconds. The row shows the same marquee it uses for Verifying.
        var key = InstallKey(row.App);
        // The slot is in flight while it's removed, like an install: Update All / Ctrl+U / a setup import can't start
        // downloading it meanwhile, and it can't be launched (tray included).
        if (!_slotsInFlight.Add(key)) { Log.Write($"uninstall {key}: an install of it is in progress — not started"); return; }
        _slotsInstalling.Add(key);
        var from = InstallManager.Shared.InstalledVersion(key);
        var name = row.DisplayName;
        row.SetBusy(true);
        row.SetPhase("Removing…", indeterminate: true);
        try
        {
            await Task.Run(() => InstallManager.Shared.Uninstall(key));
            History.Add(key, name, "uninstall", from);
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
            _slotsInFlight.Remove(key);
            _slotsInstalling.Remove(key);
            row.SetBusy(false);
            RefreshDownloadAllButton();
        }
    }

    private void Launch(AppRowControl row)
    {
        // The button is off while the slot is verified / installed / removed; a double-click must not get past that.
        if (_slotsInstalling.Contains(InstallKey(row.App))) return;
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
        bool prevLock = _settings.ShowLock;
        using var dlg = new SettingsDialog(_settings, _catalog.Self, CurrentVersion(), _catalog.DownloadServer, new SettingsExtras(
            Storage: () => Task.Run(() => (InstallManager.Shared.InstalledSize(), InstallManager.Shared.CacheSize())),
            CanClearCache: () => !Locked && !_batchRunning && !_rows.Any(r => r.IsBusy),
            ClearCache: () => Task.Run(() => InstallManager.Shared.ClearCache()),
            CopyDiagnostics: CopyDiagnostics));
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
        // Settings edits the live settings object even when the dialog is closed with its X: keep the show lock and
        // the tray icon truthful either way (both are cheap to re-apply) — and the lock saved, since the command line
        // reads it from disk.
        if (_settings.ShowLock != prevLock)
        {
            _settings.Save();
            Log.Write($"show lock {(_settings.ShowLock ? "on" : "off")} (settings)");
            if (_settings.ShowLock) StopEverything();
        }
        ApplyLock();
        UpdateTrayVisibility();
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

    // ── After a check: notifications, automatic updates, the scheduler ───────────────────────────

    /// <summary>The window is on screen and in use — a notification would only repeat what the list shows.</summary>
    private bool IsForeground() =>
        Visible && !_hiddenToTray && WindowState != FormWindowState.Minimized && Form.ActiveForm == this;

    /// <summary>Apps with an update on offer (installed, not held), each at the newest version any of its
    /// installed editions would move to.</summary>
    private List<UpdatePolicy.Pending> PendingUpdates()
    {
        var list = new List<UpdatePolicy.Pending>();
        foreach (var r in _rows)
        {
            string? best = null;
            foreach (var vid in SlotsToUpdate(r))
            {
                var tag = Versions.LatestFor(r.Releases, AssetFor(r.App, vid))?.TagName;
                if (tag != null && (best == null || Versions.IsNewer(tag, best))) best = tag;
            }
            if (best != null) list.Add(new UpdatePolicy.Pending(r.App.Id, r.App.Name, best));
        }
        return list;
    }

    /// <summary>After every successful check: announce new updates (once each), then update automatically when
    /// that's switched on. <paramref name="announce"/> = a check asked for from the tray, which always answers.</summary>
    private void AfterCheck(bool announce)
    {
        var pending = PendingUpdates();
        var (toNotify, pendingKeys) = UpdatePolicy.Notify(pending, _settings.NotifiedUpdates);
        // An app whose check failed this time keeps what it was announced at.
        var uncheckedIds = _rows.Where(r => r.Status == RowStatus.Error).Select(r => r.App.Id).ToHashSet();
        uncheckedIds.UnionWith(_uncheckedIds);
        var notified = UpdatePolicy.Remembered(pendingKeys, _settings.NotifiedUpdates, uncheckedIds);
        if (!notified.SequenceEqual(_settings.NotifiedUpdates)) { _settings.NotifiedUpdates = notified; _settings.Save(); }
        bool foreground = IsForeground();
        if (announce && !foreground)
            ShowBalloon(pending.Count == 0 ? "JB Theatre Tools" : UpdatePolicy.NotificationTitle(pending.Count),
                        pending.Count == 0 ? "Everything is up to date." : UpdatePolicy.NotificationBody(pending));
        else if (toNotify.Count > 0 && _settings.NotifyUpdates && !foreground && !Locked)
            ShowBalloon(UpdatePolicy.NotificationTitle(toNotify.Count), UpdatePolicy.NotificationBody(toNotify));
        _ = AutoUpdateAsync();
    }

    /// <summary>Automatic updates (opt-in): every non-held slot with an update whose app isn't open. Never runs
    /// under show lock or alongside other work, never prompts, and reports once when done.</summary>
    private async Task AutoUpdateAsync()
    {
        try
        {
            if (!_settings.AutoInstallUpdates || Locked || _batchRunning || _rows.Any(r => r.IsBusy)) return;
            if (!AuthClient.HasCredentials(_settings, _catalog.DownloadServer)) return;
            var candidates = UpdateWork();
            if (candidates.Count == 0) return;
            // Which apps are open: a walk of the process list per slot — off the UI thread.
            var keys = candidates.Select(w => w.row.App.InstallKey(w.vid)).ToList();
            var open = await Task.Run(() => keys.Select(k =>
            {
                var procs = InstallManager.Shared.RunningInstances(k);
                foreach (var p in procs) p.Dispose();
                return procs.Length > 0;
            }).ToList());
            var work = new List<(AppRowControl row, string? vid, string? tag)>();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (open[i]) { Log.Write($"automatic update: {candidates[i].row.App.Id} is open — left for later"); continue; }
                work.Add(candidates[i]);
            }
            // Anything may have started (or show lock come on) during that await.
            if (work.Count == 0 || Locked || _batchRunning || _rows.Any(r => r.IsBusy)) return;
            Log.Write($"automatic update: {work.Count} slot(s)");
            var done = await RunSlotsAsync(work, "Automatic update", unattended: true);
            if (done.Count > 0 && _settings.NotifyUpdates && !Locked && !IsForeground())
                ShowBalloon("JB Theatre Tools", UpdatePolicy.AutoUpdateSummary(done));
        }
        catch (Exception ex) { Log.Write($"automatic update failed: {ex.Message}"); }
    }

    /// <summary>Once a minute: run a check when "While open, check again" says one is due (only in Every-launch
    /// mode, and never on top of other work).</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    private async Task ScheduledTickAsync()
    {
        try
        {
            if (_settings.UpdateMode != "everyLaunch" || _refreshing || _batchRunning || _rows.Any(r => r.IsBusy)) return;
            // Show lock: nothing happens by itself during a show. No credentials: nothing to check.
            if (Locked || !AuthClient.HasCredentials(_settings, _catalog.DownloadServer)) return;
            // A dialog is open (Uninstall? / Roll back? / Settings / a message box): WinForms timers still tick under
            // it, and a check + automatic update could start underneath the user's decision.
            if (IsHandleCreated && !IsWindowEnabled(Handle)) return;
            if (!UpdatePolicy.IsDue(_lastCheck, DateTimeOffset.UtcNow, _settings.AutoCheckInterval)) return;
            Log.Write("scheduled update check");
            await RefreshAllAsync(quiet: true);
            await CheckLauncherUpdateAsync();
        }
        catch (Exception ex) { Log.Write($"scheduled check failed: {ex.Message}"); }
    }

    // ── Details, release notes, roll back ───────────────────────────────────────────────────────

    private void ShowReleaseNotes(AppRowControl row) => ShowReleaseNotes(row, this);

    private void ShowReleaseNotes(AppRowControl row, IWin32Window owner)
    {
        // Development builds only with the Development builds switch on (as in the ⋯ version list).
        using var dlg = new ReleaseNotesDialog($"{row.App.Name} — release notes", Versions.Offered(row.Releases), row.Installed,
                                               Theme.IsDark(_settings.Appearance));
        dlg.ShowDialog(owner);
    }

    private void ShowDetails(AppRowControl row)
    {
        var vid = SelectedVariant(row.App);
        var key = row.App.InstallKey(vid);
        var installed = InstallManager.Shared.InstalledVersion(key);
        var rec = installed != null ? InstallManager.Shared.Record(key) : null;
        var edition = row.App.HasVariants ? row.App.VariantLabel(vid) : null;
        string? installedAt = rec != null && RelativeAge.ParseIso(rec.InstalledAt) is { } at
            ? at.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) : null;
        var rel = row.Latest == null ? null : row.Releases.FirstOrDefault(r => r.TagName == row.Latest);
        long assetSize = row.LatestAssetId == null ? 0 : rel?.Assets.FirstOrDefault(a => a.Id == row.LatestAssetId)?.Size ?? 0;
        string? released = rel?.Published is { } p
            ? $"{p.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)} ({RelativeAge.Describe(p, DateTimeOffset.UtcNow)})" : null;
        var details = new AppDetails(
            row.DisplayName, row.App.Blurb, row.App.Category,
            installed == null ? null : VersionCompare.Display(installed) + (edition != null ? $" ({edition})" : ""),
            installedAt, rec?.Path,
            row.Latest == null ? null : VersionCompare.Display(row.Latest), released,
            assetSize > 0 ? ByteSize.Format(assetSize) : null,
            installed != null && IsHeld(row.App.Id),
            rec?.PreviousVersion == null ? null : VersionCompare.Display(rec.PreviousVersion),
            Platform.OsIsArm64 && installed != null ? (RunsEmulated(row) ? "x64, through emulation" : "ARM64") : null);
        AppDetailsDialog? dlg = null;
        dlg = new AppDetailsDialog(details, Theme.IsDark(_settings.Appearance),
            installed == null ? null : () => Task.Run(() => InstallManager.Shared.SizeOnDisk(key)),
            Versions.Offered(row.Releases).Count > 0 ? () => ShowReleaseNotes(row, (IWin32Window?)dlg ?? this) : null);
        using (dlg) dlg.ShowDialog(this);
    }

    /// <summary>The version this slot had before its last update, when that release still has this edition's
    /// build — the one-click roll back target; null when there's nothing to roll back to.</summary>
    private string? RollbackTag(AppRowControl row)
    {
        var vid = SelectedVariant(row.App);
        var key = row.App.InstallKey(vid);
        var installed = InstallManager.Shared.InstalledVersion(key);
        if (installed == null) return null;
        var prev = InstallManager.Shared.Record(key)?.PreviousVersion;
        // Only ever BACK: after a roll back (or an older install from the picker) "previous" is the newer one.
        if (prev == null || !VersionCompare.IsNewer(installed, prev)) return null;
        var asset = AssetFor(row.App, vid);
        // Roll back installs strictly, so only offer a release that can pass: signed checksums, and never a
        // development build on a PC without Development builds switched on.
        return Versions.Offered(row.Releases).FirstOrDefault(r => VersionCompare.Equal(r.TagName, prev)
            && r.Assets.Any(a => a.Name == asset) && Versions.HasSignedManifest(r))?.TagName;
    }

    /// <summary>Roll back to the previous version, then hold the app there so Update All leaves it alone.</summary>
    private async Task RollbackAsync(AppRowControl row, string tag)
    {
        if (BlockedByLock($"roll back {row.App.Id}")) return;
        var current = row.Installed;
        if (current == null) return;
        if (MessageBox.Show(this,
                $"Roll {row.DisplayName} back from {VersionCompare.Display(current)} to {VersionCompare.Display(tag)}?\n\n" +
                "It's then held at that version, so Update All and automatic updates leave it alone — release the hold " +
                "from its ⋯ menu when you're ready.",
                "Roll back", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (row.IsBusy || BlockedByLock($"roll back {row.App.Id}")) return;   // started / locked while the dialog was open
        await InstallVersionAsync(row, tag);
        if (VersionCompare.Equal(InstallManager.Shared.InstalledVersion(InstallKey(row.App)) ?? "", tag))
        {
            if (!IsHeld(row.App.Id)) { _settings.HeldApps.Add(row.App.Id); _settings.Save(); }
            row.RefreshHeld();
            RefreshDownloadAllButton();
            ReindexList();
            Log.Write($"rolled back {row.App.Id} to {tag} (held)");
        }
    }

    // ── More menu: show lock, activity, setup files, diagnostics ──────────────────────────────

    private void ShowMoreMenu()
    {
        var menu = new ContextMenuStrip();
        DialogKit.DisposeWhenClosed(menu, this);
        var lockItem = new ToolStripMenuItem("Show lock")
        {
            Checked = Locked, ShortcutKeyDisplayString = "Ctrl+L",
            ToolTipText = "Pause installs, updates and uninstalls — launching still works",
        };
        lockItem.Click += (_, _) => RequestShowLock(!Locked);
        menu.Items.Add(lockItem);
        menu.Items.Add(new ToolStripSeparator());
        var history = new ToolStripMenuItem("Activity…") { ShortcutKeyDisplayString = "Ctrl+H" };
        history.Click += async (_, _) => await ShowHistoryAsync();
        menu.Items.Add(history);
        var export = new ToolStripMenuItem("Export setup…") { ToolTipText = "Save which apps are installed, to set up another machine the same way" };
        export.Click += (_, _) => ExportSetup();
        menu.Items.Add(export);
        var import = new ToolStripMenuItem("Import setup…") { Enabled = !Locked && !_batchRunning };
        import.Click += async (_, _) => await ImportSetupAsync();
        menu.Items.Add(import);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Copy diagnostics", null, (_, _) => CopyDiagnostics(this));
        menu.Items.Add("Open log", null, (_, _) => Log.Open());
        menu.Show(_moreBtn, new Point(0, _moreBtn.Height));
    }

    /// <summary>Loading waits for queued history writes (up to 5 s), so it runs off the UI thread; the window opens
    /// once it's read.</summary>
    private async Task ShowHistoryAsync()
    {
        if (_historyOpening) return;   // Ctrl+H pressed again while it loads
        _historyOpening = true;
        try
        {
            var events = await Task.Run(History.Load);
            using var dlg = new HistoryDialog(events, Theme.IsDark(_settings.Appearance));
            dlg.ShowDialog(this);
        }
        catch (Exception ex) { Log.Write($"activity: {ex.Message}"); }
        finally { _historyOpening = false; }
    }

    private void ExportSetup()
    {
        var entries = new List<SetupProfile.Entry>();
        foreach (var r in _rows)
        {
            IEnumerable<string?> vids = r.App.HasVariants && r.App.Variants != null
                ? r.App.Variants.Select(v => (string?)v.Id) : new string?[] { null };
            foreach (var vid in vids)
            {
                var ver = InstallManager.Shared.InstalledVersion(r.App.InstallKey(vid));
                if (ver == null) continue;
                entries.Add(new SetupProfile.Entry(r.App.Id, r.App.IsDefaultVariant(vid) ? null : vid, ver, IsHeld(r.App.Id)));
            }
        }
        if (entries.Count == 0)
        {
            MessageBox.Show(this, "No apps are installed yet, so there's nothing to export.", "Export setup",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var profile = new SetupProfile
        {
            CreatedAt = SetupProfile.Timestamp(DateTimeOffset.UtcNow),
            CreatedBy = $"JB Theatre Tools {CurrentVersion()} (Windows)",
            Apps = entries,
            AppLayout = new SetupProfile.Layout(_settings.PinnedApps.ToList(), _settings.HiddenApps.ToList(),
                _rows.Select(r => r.App.Id).ToList(), _settings.CategoryOrder.ToList(), _settings.CollapsedCategories.ToList()),
        };
        using var dlg = new SaveFileDialog
        {
            Title = "Export setup", Filter = "JB Theatre Tools setup (*.json)|*.json",
            FileName = SetupProfile.SuggestedFileName(DateTime.Now), DefaultExt = "json", AddExtension = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, profile.Serialize());
            Log.Write($"exported setup: {entries.Count} slot(s)");
            MessageBox.Show(this, $"Saved {entries.Count} installed app{(entries.Count == 1 ? "" : "s")} to {Path.GetFileName(dlg.FileName)}.\n\n" +
                                  "On another machine: More ▾ → Import setup… installs the same apps.",
                            "Export setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ImportSetupAsync()
    {
        if (Locked)
        {
            MessageBox.Show(this, "Show lock is on — turn it off to import a setup.", "Import setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var ofd = new OpenFileDialog
        {
            Title = "Import setup", Filter = "JB Theatre Tools setup (*.json)|*.json|All files (*.*)|*.*",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        SetupProfile profile;
        try
        {
            if (new FileInfo(ofd.FileName).Length > SetupProfile.MaxBytes) throw new FormatException("This file is too large to be a setup file.");
            profile = SetupProfile.Parse(File.ReadAllText(ofd.FileName));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Import setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var catalog = _catalog.Apps.Select(a => new SetupPlanner.CatalogEntry(a.Id, a.Name,
            (a.Variants ?? new List<AppVariant>()).Select(v => (v.Id, v.Label)).ToList())).ToList();
        var installedKeys = new HashSet<string>(AllSlots().Where(x => InstallManager.Shared.InstalledVersion(x.key) != null).Select(x => x.key));
        // A development build named in the file installs only with Development builds on here: otherwise the preview
        // lists it under Skipped rather than promising it.
        var plan = SetupPlanner.Build(profile, catalog, installedKeys, allowDevTags: Versions.DevChannel);
        // An edition with no Windows build (a Mac-only Full edition) can't install here.
        foreach (var i in plan.ToInstall.ToList())
        {
            var app = _catalog.Apps.First(a => a.Id == i.AppId);
            if (AssetFor(app, i.VariantId ?? app.Variants?.FirstOrDefault()?.Id) != null) continue;
            plan.ToInstall.Remove(i);
            plan.Skipped.Add($"{i.Label} — no Windows build");
        }
        bool dark = Theme.IsDark(_settings.Appearance);
        using var preview = new ImportPreviewDialog(SetupPlanner.Summary(plan), profile.AppLayout != null,
                                                    plan.ToInstall.Count > 0, Path.GetFileName(ofd.FileName), dark);
        if (preview.ShowDialog(this) != DialogResult.OK) return;
        // Something may have started while the dialogs were open (an automatic update, show lock).
        if (Locked)
        {
            MessageBox.Show(this, "Show lock is on — turn it off to import a setup.", "Import setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_batchRunning && plan.ToInstall.Count > 0)
        {
            MessageBox.Show(this, "Other installs are running. Wait for them to finish, then import the setup again.", "Import setup",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        foreach (var id in plan.HoldIds) if (!_settings.HeldApps.Contains(id)) _settings.HeldApps.Add(id);
        if (preview.ApplyLayout && profile.AppLayout is { } layout)
        {
            var known = _catalog.Apps.Select(a => a.Id).ToHashSet();
            _settings.PinnedApps = layout.Pinned.Where(known.Contains).Distinct().ToList();
            _settings.HiddenApps = layout.Hidden.Where(known.Contains).Distinct().ToList();
            _settings.AppOrder = layout.Order.Where(known.Contains).Distinct().ToList();
            _settings.CategoryOrder = layout.CategoryOrder.Distinct().ToList();
            _settings.CollapsedCategories = layout.Collapsed.Distinct().ToList();
        }
        _settings.Save();
        foreach (var r in _rows) r.RefreshHeld();
        ApplyRowOrder();   // re-reads pins / hidden / order
        RefreshDownloadAllButton();
        Log.Write($"import setup: {plan.ToInstall.Count} to install, {plan.HoldIds.Count} held, layout {(preview.ApplyLayout ? "applied" : "kept")}");
        if (plan.ToInstall.Count == 0) return;
        if (!AuthClient.HasCredentials(_settings, _catalog.DownloadServer))
        {
            MessageBox.Show(this, NoCredsMsg, "Import setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // The default edition is passed by its real id: a null variant would mean "the row's selected edition".
        var work = plan.ToInstall.Select(i =>
        {
            var row = _rows.First(r => r.App.Id == i.AppId);
            return (row, vid: i.VariantId ?? (row.App.HasVariants ? row.App.Variants?.FirstOrDefault()?.Id : null), tag: i.Tag);
        }).ToList();
        await RunSlotsAsync(OrderedSlots(work), "Import setup");
        ApplyRowOrder();   // newly installed rows become eligible (visible) even without a refresh
    }

    // ── The launcher's own "what's new", once after it has been updated ────────────────────────

    private void PrepareLauncherWhatsNew()
    {
        var cur = CurrentVersion();
        var last = _settings.LastSeenLauncherVersion;
        // Nothing recorded, but the launcher has been used before (saved settings or installed apps): an update from a
        // version that predates the record.
        bool usedBefore = string.IsNullOrWhiteSpace(last)
            && (_settings.LoadedFromFile || InstallManager.Shared.Manifest().Count > 0);
        if (LauncherWhatsNew.ShouldShow(last, cur, usedBefore))
        {
            _whatsNewText.Text = $"Updated to JB Theatre Tools {VersionCompare.Display(cur)}.";
            _whatsNewBanner.Visible = true;
        }
        else if (!VersionCompare.Equal(last ?? "", cur))
        {
            _settings.LastSeenLauncherVersion = cur;   // a fresh install (or a downgrade): nothing to announce
            _settings.Save();
        }
    }

    private void DismissLauncherWhatsNew()
    {
        _settings.LastSeenLauncherVersion = CurrentVersion();
        _settings.Save();
        _whatsNewBanner.Visible = false;
    }

    /// <summary>The notes of every launcher release since the one last seen (the launcher's repo is public, so this
    /// never needs credentials), with the catalog's one-line what's-new as the offline fallback.</summary>
    private async Task ShowLauncherWhatsNewAsync()
    {
        var self = _catalog.Self;
        var cur = CurrentVersion();
        var last = _settings.LastSeenLauncherVersion;
        var releases = new List<ReleaseInfo>();
        if (self != null)
        {
            var label = _whatsNewBtn.Text;
            _whatsNewBtn.Enabled = false;
            _whatsNewBtn.Text = "Loading…";
            try
            {
                using var client = AuthClient.SelfUpdate(_settings, _catalog.DownloadServer);
                var all = await client.ReleasesAsync(self.Owner, self.Repo);
                releases = all.Where(r => !VersionCompare.IsNewer(r.TagName, cur)
                                          // Nothing seen before (an update from a version that didn't record it): just this version's notes.
                                          && (string.IsNullOrEmpty(last) ? VersionCompare.Equal(r.TagName, cur) : VersionCompare.IsNewer(r.TagName, last))
                                          && (Versions.DevChannel || !VersionCompare.IsDev(r.TagName))).ToList();
            }
            catch (Exception ex) { Log.Write($"launcher notes: {ex.Message}"); }
            finally { _whatsNewBtn.Text = label; _whatsNewBtn.Enabled = true; }
        }
        string? fallback = self?.WhatsNew is { Length: > 0 } w
            ? (self.WhatsNewVersion is { Length: > 0 } v ? $"New in {v}: {w}" : w) : null;
        using (var dlg = new ReleaseNotesDialog("JB Theatre Tools — what's new", releases, cur, Theme.IsDark(_settings.Appearance), fallback))
            dlg.ShowDialog(this);
        DismissLauncherWhatsNew();
    }

    // ── Keyboard shortcuts ──────────────────────────────────────────────────────────────────────

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F5:
            case Keys.Control | Keys.R:
                if (_refresh.Enabled) _ = RefreshAllAsync();
                return true;
            case Keys.Control | Keys.F:
                _search.Focus();
                _search.SelectAll();
                return true;
            case Keys.Control | Keys.Oemcomma:
                OpenSettings();
                return true;
            case Keys.Control | Keys.U:
                if (!Locked && !_batchRunning && UpdatesAvailable() > 0) _ = UpdateAllAsync();
                return true;
            case Keys.Control | Keys.L:
                RequestShowLock(!Locked);
                return true;
            case Keys.Control | Keys.D1:
                if (_settings.ViewMode == "grid") ToggleViewMode();
                return true;
            case Keys.Control | Keys.D2:
                if (_settings.ViewMode != "grid") ToggleViewMode();
                return true;
            case Keys.Control | Keys.H:
                _ = ShowHistoryAsync();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ── Diagnostics ─────────────────────────────────────────────────────────────────────────────

    private string BuildDiagnostics()
    {
        string? host = null;
        if (_settings.AuthMode == "server" && Uri.TryCreate(AuthClient.ResolveServerUrl(_settings, _catalog.DownloadServer),
                                                            UriKind.Absolute, out var relay)) host = relay.Host;
        var apps = _rows.Select(r => new Diagnostics.AppLine(r.DisplayName, r.Installed, r.Latest, StatusText(r.Status),
                                                             r.Installed != null && IsHeld(r.App.Id),
                                                             r.Installed != null && RunsEmulated(r))).ToList();
        return Diagnostics.Build(new Diagnostics.Info(
            CurrentVersion(), RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            _settings.AuthMode == "server" ? "Download server" : "GitHub token", host, _settings.DevChannel, Locked,
            _settings.InstallToApplications ? "Launcher, plus Start menu & Desktop shortcuts" : "Launcher only",
            apps, Log.Tail(Diagnostics.LogLines), DateTimeOffset.UtcNow));
    }

    private static string StatusText(RowStatus s) => s switch
    {
        RowStatus.UpToDate => "up to date",
        RowStatus.UpdateAvailable => "update available",
        RowStatus.NotInstalled => "not installed",
        RowStatus.Installed => "installed (not checked)",
        RowStatus.NoRelease => "no release",
        RowStatus.MissingAsset => "no Windows build",
        RowStatus.Error => "error",
        RowStatus.Checking => "checking",
        _ => "unknown",
    };

    /// <param name="owner">The window the confirmation sits on: the Settings dialog when it was asked for there (the
    /// main window is disabled under it), else this one.</param>
    private void CopyDiagnostics(IWin32Window owner)
    {
        try
        {
            Clipboard.SetText(BuildDiagnostics());
            MessageBox.Show(owner, "Diagnostics copied — paste them into a message when you ask for help. They contain no passwords or tokens.",
                            "Copy diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Copy diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
        _lockBanner.BackColor = Theme.BannerBack(Theme.Info, dark);
        _lockBannerText.ForeColor = Theme.Info;
        _whatsNewBanner.BackColor = Theme.BannerBack(Theme.Accent, dark);
        _whatsNewText.ForeColor = Theme.Accent;
        _filterBar.BackColor = Theme.Bg(dark);
        _search.BackColor = Theme.Card(dark);
        _search.ForeColor = Theme.Fg(dark);
        _statusFilter.BackColor = Theme.Card(dark);
        _statusFilter.ForeColor = Theme.Fg(dark);
        _filterCount.ForeColor = Theme.Muted(dark);
        _noMatches.ForeColor = Theme.Sub(dark);
        foreach (var row in _rows) row.ApplyTheme(dark);
        foreach (var header in _headers.Values) header.ApplyTheme(dark);
        if (IsHandleCreated) Theme.ApplyTitleBar(this, dark);
    }
}
