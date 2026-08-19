using System.Drawing.Drawing2D;
using System.Reflection;

namespace JBTheatreTools;

public enum RowStatus { Unknown, Checking, NoRelease, MissingAsset, NotInstalled, Installed, UpToDate, UpdateAvailable, Error }

/// <summary>A single catalog row: name, blurb, version line, status badge, and action buttons.</summary>
public sealed class AppRowControl : UserControl
{
    public CatalogApp App { get; }
    public string? Latest { get; private set; }
    public long? LatestAssetId { get; private set; }
    public string? Installed { get; private set; }
    public RowStatus Status { get; private set; } = RowStatus.Unknown;
    public List<ReleaseInfo> Releases { get; private set; } = new();
    /// <summary>The installed app's self-declared name (from its exe); overrides the catalog name.</summary>
    public string? ResolvedName { get; private set; }
    /// <summary>Name to show: the installed app's own name when available, else the catalog name.</summary>
    public string DisplayName => string.IsNullOrEmpty(ResolvedName) ? App.Name : ResolvedName!;

    private readonly PictureBox _icon = new();
    private readonly Label _name = new();
    private readonly Label _pin = new();
    private readonly Label _blurb = new();
    private readonly Label _version = new();
    private readonly Label _badge = new();
    private readonly Button _install = new();
    private readonly Button _launch = new();
    private readonly Button _more = new();
    private readonly ProgressBar _progress = new();
    private Point _mouseDownScreen;

    public event Func<AppRowControl, Task>? InstallRequested;
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

    public AppRowControl(CatalogApp app)
    {
        App = app;
        Height = 82;
        Margin = new Padding(0);

        _icon.Size = new Size(40, 40);
        _icon.Location = new Point(14, 21);
        _icon.SizeMode = PictureBoxSizeMode.Zoom;
        _icon.BackColor = Color.Transparent;

        // UseMnemonic=false on the catalog-text labels so a literal "&" renders (e.g. blurb
        // "Back up & manage") instead of being eaten as an Alt-mnemonic prefix.
        _name.Text = app.Name;
        _name.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold);
        _name.AutoSize = true;
        _name.Location = new Point(64, 10);
        _name.UseMnemonic = false;

        _pin.Text = "📌";
        _pin.Font = new Font("Segoe UI Emoji", 8f);
        _pin.AutoSize = true;
        _pin.Location = new Point(_name.Right + 4, 12);
        _pin.Visible = false;

        _blurb.Text = app.Blurb;
        _blurb.AutoSize = true;
        _blurb.Location = new Point(64, 31);
        _blurb.UseMnemonic = false;

        _version.AutoSize = true;
        _version.Location = new Point(64, 52);

        _badge.AutoSize = true;
        _badge.Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold);

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

        Controls.AddRange(new Control[] { _icon, _name, _pin, _blurb, _version, _badge, _install, _launch, _more, _progress });
        Resize += (_, _) => LayoutControls();
        // Drag-to-reorder: a press-and-drag anywhere on the row body (not on the buttons) starts a move.
        foreach (Control c in new Control[] { this, _icon, _name, _blurb, _version })
        {
            c.MouseDown += Row_MouseDown;
            c.MouseMove += Row_MouseMove;
        }
        LayoutControls();
        UpdateVisual();
        UpdateIcon();
    }

    /// <summary>Reflects the pinned state: shows the 📌 badge and repositions it after the name.</summary>
    public void SetPinned(bool pinned)
    {
        _pin.Visible = pinned;
        _pin.Location = new Point(_name.Right + 4, 12);
    }

    private void Row_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) _mouseDownScreen = Cursor.Position;
    }

    private void Row_MouseMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var d = SystemInformation.DragSize;
        if (Math.Abs(Cursor.Position.X - _mouseDownScreen.X) < d.Width &&
            Math.Abs(Cursor.Position.Y - _mouseDownScreen.Y) < d.Height) return;
        DoDragDrop(this, DragDropEffects.Move);   // MainForm's list handles the drop + reorder
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
        if (menu.Items.Count > 0) menu.Show(_more, new Point(0, _more.Height));
    }

    private void LayoutControls()
    {
        _pin.Location = new Point(_name.Right + 4, 12);
        int x = Width - 14;
        _more.Location = new Point(x - _more.Width, 28); x = _more.Left - 8;
        if (_launch.Visible) { _launch.Location = new Point(x - _launch.Width, 28); x = _launch.Left - 8; }
        if (_install.Visible) { _install.Location = new Point(x - _install.Width, 28); x = _install.Left - 8; }
        _badge.Location = new Point(x - _badge.Width - 4, 32);
        _progress.Location = new Point(14, 70);
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
        using (var path = RoundedRect(new Rectangle(0, 0, 39, 39), 9))
            g.FillPath(fill, path);
        var letter = string.IsNullOrWhiteSpace(name) ? "•" : name.Substring(0, 1).ToUpperInvariant();
        using var font = new Font("Segoe UI", 16f, FontStyle.Bold);
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
        _version.Text = $"Installed: {Installed ?? "—"}    ·    Latest: {Latest ?? "—"}";

        (string text, Color color) = Status switch
        {
            RowStatus.UpToDate => ("Up to date", Color.SeaGreen),
            RowStatus.UpdateAvailable => ("Update", Theme.Accent),
            RowStatus.NotInstalled => ("Not installed", Color.Gray),
            RowStatus.Installed => ("Installed", Color.Gray),
            RowStatus.NoRelease => ("No release", Color.Gray),
            RowStatus.MissingAsset => ("No Windows build", Color.DarkOrange),
            RowStatus.Error => ("Error", Color.Firebrick),
            RowStatus.Checking => ("Checking…", Color.Gray),
            _ => ("", Color.Gray),
        };
        _badge.Text = text;
        _badge.ForeColor = color;

        bool installed = Installed != null;
        _install.Visible = Status is RowStatus.NotInstalled or RowStatus.UpdateAvailable or RowStatus.Error;
        _install.Text = Status == RowStatus.UpdateAvailable ? "Update" : (installed ? "Retry" : "Install");
        _install.Enabled = LatestAssetId != null;
        // Launch is available whenever something is installed, even before a refresh has run.
        _launch.Visible = installed;
        _more.Visible = true;   // reordering lives here, so every row keeps its ⋯ menu

        LayoutControls();
    }

    public void ApplyTheme(bool dark)
    {
        BackColor = Theme.Card(dark);
        _name.ForeColor = Theme.Fg(dark);
        _blurb.ForeColor = Theme.Sub(dark);
        _version.ForeColor = Theme.Sub(dark);
    }
}
