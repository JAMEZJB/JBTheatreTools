using System.Drawing.Drawing2D;

namespace JBTheatreTools;

/// <summary>
/// A category section heading in the list/grid: a disclosure chevron + title + app count, and (for real
/// categories, not the Pinned group) small up/down buttons to move the whole section. Click the header to
/// collapse/expand the section. Mirrors the macOS section header; on Windows the reorder is buttons rather
/// than a live drag (robust on a platform this build isn't runtime-tested on).
/// </summary>
public sealed class SectionHeaderControl : UserControl
{
    public string Key { get; private set; } = "";
    private string _title = "";
    private int _count;
    private bool _collapsed;
    private bool _pinnedGroup;
    private bool _dark;

    private readonly Button _up = new();
    private readonly Button _down = new();

    /// <summary>Raised when the header is clicked (toggle collapse). Argument = section key.</summary>
    public event Action<string>? CollapseToggleRequested;
    /// <summary>Raised by the up/down buttons. Arguments = section key, moveUp.</summary>
    public event Action<string, bool>? MoveSectionRequested;

    public SectionHeaderControl()
    {
        Height = 30;
        Margin = new Padding(0, 6, 0, 1);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;

        foreach (var b in new[] { _up, _down })
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.Size = new Size(26, 22);
            b.TabStop = false;
            b.BackColor = Color.Transparent;
            b.Font = Theme.Ui(7f, semibold: true);
            b.Cursor = Cursors.Hand;
        }
        _up.Text = "▲";     // ▲
        _down.Text = "▼";   // ▼
        _up.Click += (_, _) => MoveSectionRequested?.Invoke(Key, true);
        _down.Click += (_, _) => MoveSectionRequested?.Invoke(Key, false);
        Controls.Add(_up);
        Controls.Add(_down);

        Click += (_, _) => CollapseToggleRequested?.Invoke(Key);
    }

    public void Configure(string key, string title, int count, bool collapsed, bool pinnedGroup, bool dark)
    {
        Key = key; _title = title; _count = count; _collapsed = collapsed; _pinnedGroup = pinnedGroup; _dark = dark;
        _up.Visible = _down.Visible = !pinnedGroup;
        _up.ForeColor = _down.ForeColor = Theme.Selector;
        LayoutButtons();
        Invalidate();
    }

    /// <summary>Re-reads the theme tokens (called by MainForm when the appearance changes).</summary>
    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        _up.ForeColor = _down.ForeColor = Theme.Selector;
        Invalidate();
    }

    public void SetMoveEnabled(bool canUp, bool canDown)
    {
        _up.Enabled = canUp;
        _down.Enabled = canDown;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutButtons(); }

    private void LayoutButtons()
    {
        _down.Location = new Point(Width - _down.Width - 8, (Height - _down.Height) / 2);
        _up.Location = new Point(_down.Left - _up.Width - 2, (Height - _up.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var sel = new SolidBrush(Theme.Selector);

        using var chevFont = Theme.Ui(7f, semibold: true);
        var chevron = _collapsed ? "▶" : "▼";   // ▶ collapsed / ▼ expanded
        g.DrawString(chevron, chevFont, sel, 6, (Height - 14) / 2f);

        // t-label: 10.5px/600 uppercase in the shared slate selector (never the accent — rule 21).
        using var titleFont = Theme.Ui(Theme.PtLabel, semibold: true);
        var title = _title.ToUpperInvariant();
        float x = 24;
        g.DrawString(title, titleFont, sel, x, (Height - titleFont.Height) / 2f);
        x += g.MeasureString(title, titleFont).Width + 4;

        using var countBrush = new SolidBrush(Color.FromArgb(178, Theme.Selector));
        g.DrawString(_count.ToString(), titleFont, countBrush, x, (Height - titleFont.Height) / 2f);
    }
}
