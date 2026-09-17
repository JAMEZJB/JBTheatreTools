using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace JBTheatreTools;

public enum RowStatus { Unknown, Checking, NoRelease, MissingAsset, NotInstalled, Installed, UpToDate, UpdateAvailable, Error }

/// <summary>A status badge drawn as a rounded "pill": a tinted rounded background with the label on top,
/// hugging the text and centred within the control's bounds. Parity with the macOS capsule badge.</summary>
public sealed class PillLabel : Label
{
    private Color _pill = Color.Transparent;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color PillBack { get => _pill; set { _pill = value; Invalidate(); } }

    public PillLabel()
    {
        BackColor = Color.Transparent;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    public override Size GetPreferredSize(Size proposed)
    {
        var s = TextRenderer.MeasureText(Text, Font);
        return new Size(s.Width + 18, s.Height + 6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (string.IsNullOrEmpty(Text)) return;
        var sz = TextRenderer.MeasureText(Text, Font);
        int pw = sz.Width + 16, ph = sz.Height + 4;
        int px = Math.Max(0, (Width - pw) / 2), py = Math.Max(0, (Height - ph) / 2);
        var rect = new Rectangle(px, py, pw, ph);
        if (_pill.A > 0)
        {
            using var b = new SolidBrush(_pill);
            using var path = PillPath(rect, ph / 2);
            g.FillPath(b, path);
        }
        TextRenderer.DrawText(g, Text, Font, rect, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static GraphicsPath PillPath(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }
}

/// <summary>The "pinned" marker: a drawn pushpin in the house slate. House Style v2 retires emoji from
/// labels, so this is a vector glyph rather than 📌 — the WinForms counterpart of the macOS `pin.fill`
/// SF Symbol. Slate is identical in both themes, so it needs no theme plumbing.</summary>
public sealed class PinGlyph : Label
{
    public PinGlyph()
    {
        AutoSize = false;
        Size = new Size(12, 12);
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Theme.Selector);
        using var pen = new Pen(Theme.Selector, 1.4f);
        g.FillEllipse(brush, 1.5f, 1f, 7f, 7f);       // head
        g.DrawLine(pen, 5f, 8f, 5f, 11f);             // needle
    }
}

/// <summary>A single catalog row: name, blurb, version line, status badge, and action buttons.</summary>
public sealed class AppRowControl : UserControl
{
    public CatalogApp App { get; }
    public string? Latest { get; private set; }
    public long? LatestAssetId { get; private set; }
    public string? Installed { get; private set; }
    public RowStatus Status { get; private set; } = RowStatus.Unknown;
    public List<ReleaseInfo> Releases { get; private set; } = new();
    /// <summary>The installed app's self-declared exe name (kept for diagnostics only — NOT shown; see DisplayName).</summary>
    public string? ResolvedName { get; private set; }
    /// <summary>The selected variant id (from the row's combobox), or null for a non-variant app.</summary>
    private string? CurrentVariantId =>
        App.HasVariants && App.Variants != null && _variant.SelectedIndex >= 0 && _variant.SelectedIndex < App.Variants.Count
            ? App.Variants[_variant.SelectedIndex].Id : null;
    /// <summary>Name to show: the launcher's CURATED catalog name (James's naming) + the selected variant's
    /// suffix. We do NOT use the installed exe's self-name — several diverge from the curated name (e.g. the
    /// Convert app calls itself "Convert to it!", Network Port Map's exe is "Build Port Map").</summary>
    public string DisplayName => App.Name + App.VariantSuffix(CurrentVariantId);
    /// <summary>The row's current icon image (installed app icon / bundled / monogram) — used by the
    /// floating drag card so it shows the same icon as the row.</summary>
    public Image? CurrentIcon => _icon.Image;

    private readonly PictureBox _icon = new();
    private readonly Label _name = new();
    private readonly PinGlyph _pin = new();
    private readonly Label _blurb = new();
    private readonly Label _version = new();
    private readonly Label _whatsNew = new();
    private readonly ComboBox _variant = new();
    private readonly PillLabel _badge = new();
    private bool _compact;
    private bool _suppressVariantEvent;
    private readonly Button _install = new();
    private readonly Button _launch = new();
    private readonly Button _more = new();
    private readonly ProgressBar _progress = new();
    // Custom grip drag (web-style reorder): a floating card follows the cursor, rows rearrange live, commit
    // on mouse-up. Started only from the grip zone (left edge), driven by mouse capture on this row.
    private Point _downScreen;
    private bool _gripDown;
    private bool _dragging;
    private readonly Panel _placeholder = new() { Visible = false, Dock = DockStyle.Fill };
    private bool _hover;
    private bool _dark;

    public event Func<AppRowControl, Task>? InstallRequested;
    /// <summary>Grip drag lifecycle for reorder (MainForm drives the floating card + live reorder).</summary>
    public event Action<AppRowControl>? ReorderStart;
    public event Action<Point>? ReorderMove;   // carries the current screen point
    public event Action? ReorderEnd;
    public event Func<AppRowControl, string, Task>? InstallVersionRequested;
    public event Action<AppRowControl>? UninstallRequested;
    public event Action<AppRowControl>? LaunchRequested;
    /// <summary>Reorder request from the ⋯ menu: true = move up, false = move down.</summary>
    public event Action<AppRowControl, bool>? MoveRequested;
    /// <summary>Set by MainForm before the menu opens so Move Up/Down grey out at the list edges.</summary>
    public Func<AppRowControl, bool, bool>? CanMove;
    /// <summary>Per-app shortcut toggle: (row, desktop: true=Desktop/false=Start Menu, add).</summary>
    public event Action<AppRowControl, bool, bool>? ShortcutToggleRequested;
    /// <summary>Queries set by MainForm so the menu shows Add vs Remove correctly.</summary>
    public Func<AppRowControl, bool>? HasDesktopShortcut;
    public Func<AppRowControl, bool>? HasStartMenuShortcut;
    /// <summary>Pin-to-top toggle from the ⋯ menu, and a query so the menu label + badge are correct.</summary>
    public event Action<AppRowControl>? PinToggleRequested;
    public Func<AppRowControl, bool>? IsPinnedQuery;
    /// <summary>Hide-from-list request from the ⋯ menu.</summary>
    public event Action<AppRowControl>? HideRequested;
    /// <summary>User picked a different variant (Standard/Full) — carries the chosen variant id.</summary>
    public event Action<AppRowControl, string>? VariantChangeRequested;
    /// <summary>Set by MainForm: the currently-selected variant id for this app.</summary>
    public Func<AppRowControl, string?>? SelectedVariantQuery;

    public AppRowControl(CatalogApp app)
    {
        App = app;
        bool hasWhatsNew = !string.IsNullOrEmpty(app.WhatsNew);
        int variantY = hasWhatsNew ? 86 : 68;   // y of the variant toggle, below the version / what's-new lines
        Height = app.HasVariants ? variantY + 28 : (hasWhatsNew ? 100 : 82);
        Margin = new Padding(0);

        _icon.Size = new Size(40, 40);
        _icon.Location = new Point(14, 21);
        _icon.SizeMode = PictureBoxSizeMode.Zoom;
        _icon.BackColor = Color.Transparent;

        // UseMnemonic=false on the catalog-text labels so a literal "&" renders (e.g. blurb
        // "Back up & manage") instead of being eaten as an Alt-mnemonic prefix.
        _name.Text = app.Name;
        _name.Font = Theme.Ui(Theme.PtBody, semibold: true);   // body 13px/600
        _name.AutoSize = true;
        _name.Location = new Point(64, 10);
        _name.UseMnemonic = false;

        _pin.Location = new Point(_name.Right + 4, 12);
        _pin.Visible = false;

        _blurb.Text = app.Blurb;
        _blurb.Font = Theme.Ui(Theme.PtSmall);   // t-small 12px
        _blurb.AutoSize = true;
        _blurb.Location = new Point(64, 31);
        _blurb.UseMnemonic = false;

        _version.Font = Theme.Ui(Theme.PtLabel);   // t-label step, sentence case (a meta line)
        _version.AutoSize = true;
        _version.Location = new Point(64, 52);

        // One-line "what's new" for the app's current release; only present when the catalog carries it.
        _whatsNew.AutoSize = true;
        _whatsNew.Location = new Point(64, 68);
        _whatsNew.UseMnemonic = false;
        _whatsNew.Font = Theme.Ui(Theme.PtLabel);
        _whatsNew.Visible = hasWhatsNew;
        if (hasWhatsNew)
        {
            string label = string.IsNullOrEmpty(app.WhatsNewVersion) ? "What's new:" : $"New in {app.WhatsNewVersion}:";
            _whatsNew.Text = $"{label} {app.WhatsNew}";
        }

        // Variant toggle (Standard/Full) — only for apps that ship more than one download.
        _variant.DropDownStyle = ComboBoxStyle.DropDownList;
        _variant.Font = Theme.Ui(Theme.PtSmall);
        _variant.Width = 120;
        _variant.Location = new Point(64, variantY);
        _variant.Visible = app.HasVariants;
        if (app.HasVariants && app.Variants != null)
        {
            foreach (var v in app.Variants) _variant.Items.Add(v.Label);
            if (_variant.Items.Count > 0) { _suppressVariantEvent = true; _variant.SelectedIndex = 0; _suppressVariantEvent = false; }
            _variant.SelectedIndexChanged += (_, _) =>
            {
                if (_suppressVariantEvent) return;
                int idx = _variant.SelectedIndex;
                _name.Text = DisplayName;   // reflect the new variant suffix immediately
                if (idx >= 0 && app.Variants != null && idx < app.Variants.Count)
                    VariantChangeRequested?.Invoke(this, app.Variants[idx].Id);
            };
        }

        _badge.AutoSize = true;
        _badge.Font = Theme.Ui(Theme.PtLabel, semibold: true);   // t-label 10.5px/600

        _install.AutoSize = true;
        _install.Click += async (_, _) =>
        {
            if (InstallRequested != null) await InstallRequested(this);
        };

        _launch.Text = "Launch";
        _launch.AutoSize = true;
        _launch.Click += (_, _) => LaunchRequested?.Invoke(this);

        _more.Text = "⋯";
        _more.Size = new Size(30, 24);
        _more.Click += (_, _) => ShowMoreMenu();

        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Maximum = 100;
        _progress.Visible = false;
        _progress.Size = new Size(220, 6);

        DoubleBuffered = true;
        ResizeRedraw = true;   // repaint the grip / hairline / drop line when the row resizes

        // Placeholder overlay shown while this row is the one being dragged: an opaque card-coloured panel
        // (hides the row content) with a dashed accent outline — the "slot" where the item will land.
        _placeholder.Paint += PaintPlaceholder;
        Controls.Add(_placeholder);   // added last → top of the z-order, so it covers the row content

        Controls.AddRange(new Control[] { _icon, _name, _pin, _blurb, _version, _whatsNew, _variant, _badge, _install, _launch, _more, _progress });
        Resize += (_, _) => LayoutControls();
        // Drag-to-reorder: press-and-drag on the GRIP zone (left edge) starts a custom reorder (MainForm
        // drives a floating card + live reorder). Right-click opens the action menu; double-click launches.
        foreach (Control c in new Control[] { this, _icon, _name, _blurb, _version, _whatsNew })
        {
            c.MouseDown += Row_MouseDown;
            c.MouseMove += Row_MouseMove;
            c.MouseUp += Row_MouseUp;
            c.DoubleClick += (_, _) => PrimaryAction();
        }
        // Hover highlight (parity with the macOS row hover): recompute from the real cursor position on
        // every enter/leave of the row or any child, so moving across children doesn't flicker it off.
        foreach (Control c in new Control[] { this, _icon, _name, _pin, _blurb, _version, _whatsNew, _variant, _badge, _install, _launch, _more, _progress })
        {
            c.MouseEnter += (_, _) => RecomputeHover();
            c.MouseLeave += (_, _) => RecomputeHover();
        }
        LayoutControls();
        UpdateVisual();
        UpdateIcon();
    }

    /// <summary>Reflects the pinned state: shows the pin marker and repositions it after the name.</summary>
    public void SetPinned(bool pinned)
    {
        _pin.Visible = pinned;
        _pin.Location = new Point(_name.Right + 4, 12);
    }

    /// <summary>Recomputes the hover state from the actual cursor position (robust across child controls)
    /// and repaints the soft highlight + grip.</summary>
    private void RecomputeHover()
    {
        bool h = ClientRectangle.Contains(PointToClient(Cursor.Position));
        if (h == _hover) return;
        _hover = h;
        BackColor = RowBack();
        Invalidate();
    }

    /// <summary>Shows/hides the dashed placeholder over this row while it's the one being dragged (the row
    /// content is hidden behind it). MainForm toggles this at drag start/end; the row keeps it as it moves.</summary>
    public void SetDragPlaceholder(bool on)
    {
        if (_placeholder.Visible == on) return;
        _placeholder.BackColor = Theme.Card(_dark);   // opaque → hides the row content beneath the slot
        _placeholder.Visible = on;
        if (on) _placeholder.BringToFront();
    }

    private void PaintPlaceholder(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = _placeholder.ClientRectangle;
        r.Inflate(-4, -3);
        r.Width -= 1; r.Height -= 1;
        using var fill = new SolidBrush(Color.FromArgb(24, Theme.Accent));
        using var path = RoundedRect(r, Theme.RPanel);
        g.FillPath(fill, path);
        using var pen = new Pen(Theme.Accent, 2) { DashStyle = DashStyle.Dash };
        g.DrawPath(pen, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_compact)
        {
            // Grid tile: a flat panel closed by a hairline — v2 separates with hairlines, never a card
            // shadow. Hover firms the hairline instead of lifting the tile (parity with the macOS tile).
            // The control's BackColor IS the tile fill, so the icon / name / badge children inherit it;
            // only the four corners outside the rounded path are painted back to the window ground.
            var tile = new Rectangle(0, 0, Width - 1, Height - 1);
            using var tilePath = RoundedRect(tile, Theme.RPanel);
            using (var corners = new Region(ClientRectangle))
            {
                corners.Exclude(tilePath);
                using var ground = new SolidBrush(Theme.Bg(_dark));
                g.FillRegion(ground, corners);
            }
            using (var border = new Pen(_hover ? Theme.LineStrong(_dark) : Theme.Line(_dark)))
                g.DrawPath(border, tilePath);
            return;
        }

        // Grip handle (2×3 dots) at the left margin — the drag affordance (grab here to reorder).
        int gx = 6, gy = Height / 2 - 8;
        using var dot = new SolidBrush(Color.FromArgb(_hover ? 210 : 150, Theme.Selector));
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 2; c++)
                g.FillEllipse(dot, gx + c * 5, gy + r * 6, 3, 3);

        // Hairline separator along the bottom (inset past the icon), hidden while hovered.
        if (!_hover)
        {
            using var pen = new Pen(Theme.Line(_dark));
            g.DrawLine(pen, 60, Height - 1, Width - 12, Height - 1);
        }
    }

    // ── Custom grip drag: press-and-drag the grip zone (whole tile in grid mode) to reorder. Mouse capture
    // routes the move/up here even over other rows; MainForm draws the floating card and reorders live. ──
    private void Row_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        // List mode: only the left-edge grip zone starts a drag (the icon starts at x=14). Grid tiles are
        // draggable anywhere. Don't capture yet — a plain click/double-click must be unaffected; capture is
        // taken only once a real drag begins (below), so clicks that never cross the threshold are normal.
        if (!_compact && PointToClient(Cursor.Position).X >= 14) return;
        _gripDown = true;
        _dragging = false;
        _downScreen = Cursor.Position;
    }

    private void Row_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_gripDown || (e.Button & MouseButtons.Left) == 0) return;
        if (!_dragging)
        {
            var d = SystemInformation.DragSize;
            if (Math.Abs(Cursor.Position.X - _downScreen.X) < d.Width &&
                Math.Abs(Cursor.Position.Y - _downScreen.Y) < d.Height) return;
            _dragging = true;
            Capture = true;                 // now route moves here even over other rows
            ReorderStart?.Invoke(this);
        }
        ReorderMove?.Invoke(Cursor.Position);
    }

    private void Row_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { ShowMoreMenu(); return; }
        if (!_gripDown) return;
        bool wasDragging = _dragging;
        _gripDown = false;
        _dragging = false;
        if (wasDragging) { Capture = false; ReorderEnd?.Invoke(); }
    }

    /// <summary>Double-click / grid-tile click: install the selected variant when it differs from the
    /// installed one; else launch when installed; else install (when the status allows it).</summary>
    private void PrimaryAction()
    {
        if (!Enabled) return;
        if (Installed != null) { LaunchRequested?.Invoke(this); return; }
        if (Status is RowStatus.NotInstalled or RowStatus.UpdateAvailable or RowStatus.Error && InstallRequested != null)
            _ = InstallRequested(this);
    }

    /// <summary>Sets the variant dropdown's selection without firing the change event (used by MainForm
    /// to reflect the persisted choice).</summary>
    public void SetSelectedVariant(string? variantId)
    {
        if (!App.HasVariants || App.Variants == null) return;
        int idx = App.Variants.FindIndex(v => v.Id == variantId);
        if (idx < 0) idx = 0;
        if (_variant.SelectedIndex == idx) return;
        _suppressVariantEvent = true;
        _variant.SelectedIndex = idx;
        _suppressVariantEvent = false;
        _name.Text = DisplayName;   // reflect the new variant suffix
    }

    private void ShowMoreMenu()
    {
        var menu = new ContextMenuStrip();
        bool pinned = IsPinnedQuery?.Invoke(this) ?? false;
        var pin = new ToolStripMenuItem(pinned ? "Unpin from top" : "Pin to top");
        pin.Click += (_, _) => PinToggleRequested?.Invoke(this);
        menu.Items.Add(pin);
        var moveUp = new ToolStripMenuItem("Move up") { Enabled = CanMove?.Invoke(this, true) ?? false };
        moveUp.Click += (_, _) => MoveRequested?.Invoke(this, true);
        var moveDown = new ToolStripMenuItem("Move down") { Enabled = CanMove?.Invoke(this, false) ?? false };
        moveDown.Click += (_, _) => MoveRequested?.Invoke(this, false);
        menu.Items.Add(moveUp);
        menu.Items.Add(moveDown);
        var hide = new ToolStripMenuItem("Hide from list");
        hide.Click += (_, _) => HideRequested?.Invoke(this);
        menu.Items.Add(hide);
        if (App.HasVariants && App.Variants != null)
        {
            menu.Items.Add(new ToolStripSeparator());
            var sel = SelectedVariantQuery?.Invoke(this);
            var variant = new ToolStripMenuItem("Variant");
            foreach (var v in App.Variants)
            {
                var vid = v.Id;
                var item = new ToolStripMenuItem(v.Label) { Checked = v.Id == sel };
                item.Click += (_, _) => VariantChangeRequested?.Invoke(this, vid);
                variant.DropDownItems.Add(item);
            }
            menu.Items.Add(variant);
        }
        if (Releases.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            var versions = new ToolStripMenuItem("Install version");
            foreach (var rel in Releases)
            {
                var label = rel.TagName
                    + (rel.Prerelease ? " (pre-release)" : "")
                    + (rel.TagName == Installed ? "  ✓ installed" : "");
                var tag = rel.TagName;
                var item = new ToolStripMenuItem(label);
                item.Click += async (_, _) =>
                {
                    if (InstallVersionRequested != null) await InstallVersionRequested(this, tag);
                };
                versions.DropDownItems.Add(item);
            }
            menu.Items.Add(versions);
        }
        if (Installed != null)
        {
            menu.Items.Add(new ToolStripSeparator());
            bool hasDesk = HasDesktopShortcut?.Invoke(this) ?? false;
            var desk = new ToolStripMenuItem(hasDesk ? "Remove desktop shortcut" : "Add desktop shortcut");
            desk.Click += (_, _) => ShortcutToggleRequested?.Invoke(this, true, !hasDesk);
            menu.Items.Add(desk);
            bool hasStart = HasStartMenuShortcut?.Invoke(this) ?? false;
            var start = new ToolStripMenuItem(hasStart ? "Remove Start Menu shortcut" : "Add Start Menu shortcut");
            start.Click += (_, _) => ShortcutToggleRequested?.Invoke(this, false, !hasStart);
            menu.Items.Add(start);

            menu.Items.Add(new ToolStripSeparator());
            var uninstall = new ToolStripMenuItem($"Uninstall {DisplayName}");
            uninstall.Click += (_, _) => UninstallRequested?.Invoke(this);
            menu.Items.Add(uninstall);
        }
        if (menu.Items.Count > 0) menu.Show(Cursor.Position);
    }

    private void LayoutControls()
    {
        if (_compact) { LayoutCompact(); return; }
        _pin.Location = new Point(_name.Right + 4, 12);
        int x = Width - 14;
        _more.Location = new Point(x - _more.Width, 28); x = _more.Left - 8;
        if (_launch.Visible) { _launch.Location = new Point(x - _launch.Width, 28); x = _launch.Left - 8; }
        if (_install.Visible) { _install.Location = new Point(x - _install.Width, 28); x = _install.Left - 8; }
        _badge.Location = new Point(x - _badge.Width - 4, 32);
        _progress.Location = new Point(14, Height - 12);   // pinned to the bottom (row height varies with the what's-new line)
    }

    /// <summary>Grid-tile layout: a large centred icon, the name below it, and a compact status line.
    /// Per-app actions live in the right-click menu; a double-click launches or installs.</summary>
    private void LayoutCompact()
    {
        int w = Width;
        _icon.Size = new Size(48, 48);
        _icon.Location = new Point((w - 48) / 2, 12);
        _name.Location = new Point(6, 64);
        _name.Size = new Size(w - 12, 32);
        _badge.Location = new Point(6, 100);
        _badge.Size = new Size(w - 12, 16);
        _pin.Location = new Point(w - _pin.Width - 6, 6);
        _progress.Location = new Point(10, Height - 12);
        _progress.Width = w - 20;
        _blurb.Visible = _version.Visible = _whatsNew.Visible = false;
        _install.Visible = _launch.Visible = _more.Visible = _variant.Visible = false;
    }

    /// <summary>Switches the row between the detailed list layout and a compact grid tile.</summary>
    public void SetCompact(bool compact)
    {
        _compact = compact;
        BackColor = RowBack();
        if (compact)
        {
            Margin = new Padding(6);
            Size = new Size(132, 140);
            _name.AutoSize = false;
            _name.TextAlign = ContentAlignment.TopCenter;
            _badge.AutoSize = false;
            _badge.TextAlign = ContentAlignment.MiddleCenter;
        }
        else
        {
            Margin = new Padding(0);
            _icon.Size = new Size(40, 40);
            _icon.Location = new Point(14, 21);
            _name.AutoSize = true;
            _name.TextAlign = ContentAlignment.TopLeft;
            _name.Location = new Point(64, 10);
            _badge.AutoSize = true;
            _badge.TextAlign = ContentAlignment.TopLeft;
            _blurb.Visible = _version.Visible = true;
            _whatsNew.Visible = !string.IsNullOrEmpty(App.WhatsNew);
            _variant.Visible = App.HasVariants;
            bool hasWhatsNew = !string.IsNullOrEmpty(App.WhatsNew);
            int variantY = hasWhatsNew ? 86 : 68;
            Height = App.HasVariants ? variantY + 28 : (hasWhatsNew ? 100 : 82);
        }
        UpdateVisual();
    }

    private string? SelectedVariantLabel()
    {
        var sel = SelectedVariantQuery?.Invoke(this);
        return App.Variants?.FirstOrDefault(v => v.Id == sel)?.Label;
    }

    public void SetChecking()
    {
        Status = RowStatus.Checking;
        UpdateVisual();
    }

    public void SetReleases(List<ReleaseInfo> releases)
    {
        Releases = releases;
        UpdateVisual();
    }

    public void SetResolvedName(string? name)
    {
        ResolvedName = name;
        _name.Text = DisplayName;
        LayoutControls();
        UpdateIcon();
    }

    public void SetState(string? installed, string? latest, long? assetId, RowStatus status)
    {
        Installed = installed;
        Latest = latest;
        LatestAssetId = assetId;
        Status = status;
        UpdateVisual();
        UpdateIcon();
    }

    /// <summary>Leading icon: the installed app's REAL icon (extracted from its exe) once installed;
    /// before install, a per-app icon shipped inside the launcher (so the row shows the actual app
    /// icon, not just a letter); and only if neither is available, a tinted monogram tile.</summary>
    private void UpdateIcon()
    {
        Image? img = null;
        try
        {
            var path = InstallManager.Shared.InstalledPath(App.Id);
            if (path != null && File.Exists(path))
            {
                using var ico = Icon.ExtractAssociatedIcon(path);
                img = ico?.ToBitmap();
            }
        }
        catch { /* fall through to the bundled icon / monogram */ }
        img ??= BundledIcon(App.Id);
        img ??= MonogramIcon(DisplayName);
        var old = _icon.Image;
        _icon.Image = img;
        old?.Dispose();
    }

    /// <summary>A per-app icon embedded in the launcher (rowicon-&lt;id&gt;.png), shown before the app is
    /// installed. Returns null if this app has no bundled icon (→ monogram fallback).</summary>
    private static Image? BundledIcon(string id)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"rowicon-{id}.png");
            if (s == null) return null;
            using var tmp = Image.FromStream(s);
            return new Bitmap(tmp);   // independent copy so the icon survives the stream being closed
        }
        catch { return null; }
    }

    private static Image MonogramIcon(string name)
    {
        var bmp = new Bitmap(40, 40);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var accent = Theme.Accent;
        using (var fill = new SolidBrush(Color.FromArgb(38, accent)))   // ~15% tint of the suite accent
        using (var path = RoundedRect(new Rectangle(0, 0, 39, 39), Theme.RPanel))
            g.FillPath(fill, path);
        var letter = string.IsNullOrWhiteSpace(name) ? "•" : name.Substring(0, 1).ToUpperInvariant();
        using var font = Theme.Ui(16f, semibold: true);
        using var txt = new SolidBrush(accent);
        var sz = g.MeasureString(letter, font);
        g.DrawString(letter, font, txt, (40 - sz.Width) / 2f, (40 - sz.Height) / 2f);
        return bmp;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void SetProgress(double p)
    {
        _progress.Visible = p > 0 && p < 1;
        _progress.Value = Math.Clamp((int)(p * 100), 0, 100);
    }

    public void SetBusy(bool busy)
    {
        _install.Enabled = !busy;
        _launch.Enabled = !busy;
        _more.Enabled = !busy;
        _progress.Visible = busy;
        if (!busy) _progress.Value = 0;
    }

    private void UpdateVisual()
    {
        if (App.HasVariants) SetSelectedVariant(SelectedVariantQuery?.Invoke(this));
        // The row shows the SELECTED variant's slot, so the version line is annotated with that label.
        var selVar = App.HasVariants ? SelectedVariantLabel() : null;
        string instText = Installed == null ? "—" : (selVar != null ? $"{Installed} ({selVar})" : Installed);
        _version.Text = $"Installed: {instText}    ·    Latest: {Latest ?? "—"}";

        (string text, Color color) = Status switch
        {
            RowStatus.UpToDate => ("Up to date", Theme.Ok),
            RowStatus.UpdateAvailable => ("Update", Theme.Accent),
            RowStatus.NotInstalled => ("Not installed", Theme.Sub(_dark)),
            RowStatus.Installed => ("Installed", Theme.Sub(_dark)),
            RowStatus.NoRelease => ("No release", Theme.Sub(_dark)),
            RowStatus.MissingAsset => ("No Windows build", Theme.Warn),
            RowStatus.Error => ("Error", Theme.Danger),
            RowStatus.Checking => ("Checking…", Theme.Sub(_dark)),
            _ => ("", Theme.Sub(_dark)),
        };
        _badge.Text = text;
        _badge.ForeColor = color;
        _badge.PillBack = string.IsNullOrEmpty(text) ? Color.Transparent : Color.FromArgb(38, color);   // ~15% tint

        bool installed = Installed != null;
        _install.Visible = Status is RowStatus.NotInstalled or RowStatus.UpdateAvailable or RowStatus.Error;
        _install.Text = Status == RowStatus.UpdateAvailable ? "Update" : (installed ? "Retry" : "Install");
        _install.Enabled = LatestAssetId != null;
        // Launch is available whenever something is installed, even before a refresh has run.
        _launch.Visible = installed;
        _more.Visible = true;   // reordering lives here, so every row keeps its ⋯ menu

        LayoutControls();
    }

    /// <summary>The control's own background — which its child labels inherit. A list row is a surface
    /// that lifts on hover; a GRID TILE is the panel fill (raised on hover), with OnPaint rounding the
    /// corners back to the window ground.</summary>
    private Color RowBack() => _compact
        ? (_hover ? Theme.Raised(_dark) : Theme.Surface(_dark))
        : (_hover ? Theme.CardHover(_dark) : Theme.Card(_dark));

    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        BackColor = RowBack();
        _name.ForeColor = Theme.Fg(dark);
        _blurb.ForeColor = Theme.Sub(dark);
        _version.ForeColor = Theme.Muted(dark);
        _whatsNew.ForeColor = Theme.Selector;   // rule 21 — meta prose is slate, not the accent
        _variant.BackColor = Theme.Card(dark);
        _variant.ForeColor = Theme.Fg(dark);
        UpdateVisual();   // the badge's semantic colours are theme-dependent
        UpdateIcon();     // ditto the monogram fallback, which is tinted with the accent
    }
}
