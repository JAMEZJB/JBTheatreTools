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

    private readonly Label _name = new();
    private readonly Label _blurb = new();
    private readonly Label _version = new();
    private readonly Label _badge = new();
    private readonly Button _install = new();
    private readonly Button _launch = new();
    private readonly ProgressBar _progress = new();

    public event Func<AppRowControl, Task>? InstallRequested;
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

        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Maximum = 100;
        _progress.Visible = false;
        _progress.Size = new Size(220, 6);

        Controls.AddRange(new Control[] { _name, _blurb, _version, _badge, _install, _launch, _progress });
        Resize += (_, _) => LayoutControls();
        LayoutControls();
        UpdateVisual();
    }

    private void LayoutControls()
    {
        int right = Width - 14;
        _launch.Location = new Point(right - _launch.Width, 28);
        _install.Location = new Point(_launch.Left - _install.Width - 8, 28);
        _badge.Location = new Point((_install.Visible ? _install.Left : _launch.Left) - _badge.Width - 12, 32);
        _progress.Location = new Point(14, 70);
    }

    public void SetChecking()
    {
        Status = RowStatus.Checking;
        UpdateVisual();
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
