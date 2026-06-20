namespace JBTheatreTools;

public enum RowStatus { Unknown, Checking, NoRelease, MissingAsset, NotInstalled, UpToDate, UpdateAvailable, Error }

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

    private readonly Label _name = new();
    private readonly Label _blurb = new();
    private readonly Label _version = new();
    private readonly Label _badge = new();
    private readonly Button _install = new();
    private readonly Button _launch = new();
    private readonly Button _more = new();
    private readonly ProgressBar _progress = new();

    public event Func<AppRowControl, Task>? InstallRequested;
    public event Func<AppRowControl, string, Task>? InstallVersionRequested;
    public event Action<AppRowControl>? UninstallRequested;
    public event Action<AppRowControl>? LaunchRequested;

    public AppRowControl(CatalogApp app)
    {
        App = app;
        Height = 82;
        Margin = new Padding(0);

        _name.Text = app.Name;
        _name.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold);
        _name.AutoSize = true;
        _name.Location = new Point(14, 10);

        _blurb.Text = app.Blurb;
        _blurb.AutoSize = true;
        _blurb.Location = new Point(14, 31);

        _version.AutoSize = true;
        _version.Location = new Point(14, 52);

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

        Controls.AddRange(new Control[] { _name, _blurb, _version, _badge, _install, _launch, _more, _progress });
        Resize += (_, _) => LayoutControls();
        LayoutControls();
        UpdateVisual();
    }

    private void ShowMoreMenu()
    {
        var menu = new ContextMenuStrip();
        if (Releases.Count > 0)
        {
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
            if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
            var uninstall = new ToolStripMenuItem($"Uninstall {DisplayName}");
            uninstall.Click += (_, _) => UninstallRequested?.Invoke(this);
            menu.Items.Add(uninstall);
        }
        if (menu.Items.Count > 0) menu.Show(_more, new Point(0, _more.Height));
    }

    private void LayoutControls()
    {
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
    }

    public void SetState(string? installed, string? latest, long? assetId, RowStatus status)
    {
        Installed = installed;
        Latest = latest;
        LatestAssetId = assetId;
        Status = status;
        UpdateVisual();
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
            RowStatus.UpdateAvailable => ("Update", Color.RoyalBlue),
            RowStatus.NotInstalled => ("Not installed", Color.Gray),
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
        _launch.Visible = installed && Status is RowStatus.UpToDate or RowStatus.UpdateAvailable or RowStatus.Error;
        _more.Visible = Releases.Count > 0 || installed;

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
