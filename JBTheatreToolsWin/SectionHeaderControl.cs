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
    /// <summary>One StringFormat for the count capsule (StringFormat.GenericTypographic builds a new one per call).</summary>
    private static readonly StringFormat CountFormat = new(StringFormat.GenericTypographic);

    public string Key { get; private set; } = "";
    private string _title = "";
    private int _count;
    private bool _collapsed;
    private bool _pinnedGroup;
    private bool _dark;

    // House icon buttons (no fill, slate glyph, hover wash only — rule 21), drawn as vector triangles.
    private readonly ToolTip _tip = HouseTip.Create();
    private readonly HouseButton _up = new(HouseRole.Icon) { Glyph = HouseGlyph.Up, AccessibleName = "Move section up" };
    private readonly HouseButton _down = new(HouseRole.Icon) { Glyph = HouseGlyph.Down, AccessibleName = "Move section down" };
    // DPI: every pixel number is a 96-DPI design value scaled through S(); _dpi = what the geometry was built for.
    private int _dpi;
    private int S(int v) => Theme.Px(v, DeviceDpi);

    /// <summary>Raised when the header is clicked (toggle collapse). Argument = section key.</summary>
    public event Action<string>? CollapseToggleRequested;
    /// <summary>Raised by the up/down buttons. Arguments = section key, moveUp.</summary>
    public event Action<string, bool>? MoveSectionRequested;

    public SectionHeaderControl()
    {
        AutoScaleMode = AutoScaleMode.None;   // Rescale() owns the geometry, absolutely, from DeviceDpi
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;

        foreach (var b in new[] { _up, _down })
        {
            b.TabStop = false;
            b.AutoSize = false;   // a fixed 26×22 hit target (Rescale)
        }
        _up.Text = "▲";     // ▲
        _down.Text = "▼";   // ▼
        _up.Click += (_, _) => MoveSectionRequested?.Invoke(Key, true);
        _down.Click += (_, _) => MoveSectionRequested?.Invoke(Key, false);
        Controls.Add(_up);
        Controls.Add(_down);

        Click += (_, _) => CollapseToggleRequested?.Invoke(Key);
        Rescale();
    }

    /// <summary>Re-derives the height, margin, button size and fonts from the current DPI (constructor,
    /// handle creation on a different-DPI monitor, and every Per-Monitor V2 DPI change).</summary>
    public void Rescale()
    {
        _dpi = DeviceDpi;
        Height = S(30);
        Margin = new Padding(0, S(6), 0, S(1));
        _up.Size = _down.Size = new Size(S(26), S(22));
        LayoutButtons();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (DeviceDpi != _dpi) Rescale();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Rescale();
    }

    public void Configure(string key, string title, int count, bool collapsed, bool pinnedGroup, bool dark)
    {
        Key = key; _title = title; _count = count; _collapsed = collapsed; _pinnedGroup = pinnedGroup; _dark = dark;
        _up.Visible = _down.Visible = !pinnedGroup;
        // Named for the section (screen readers) and explained on hover.
        _up.AccessibleName = $"Move {title} up";
        _down.AccessibleName = $"Move {title} down";
        _tip.SetToolTip(_up, $"Move the {title} section up");
        _tip.SetToolTip(_down, $"Move the {title} section down");
        LayoutButtons();
        Invalidate();
    }

    /// <summary>Re-reads the theme tokens (called by MainForm when the appearance changes).</summary>
    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        Invalidate();
        _up.Invalidate();
        _down.Invalidate();
    }

    public void SetMoveEnabled(bool canUp, bool canDown)
    {
        _up.Enabled = canUp;
        _down.Enabled = canDown;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutButtons(); }

    private void LayoutButtons()
    {
        _down.Location = new Point(Width - _down.Width - S(8), (Height - _down.Height) / 2);
        _up.Location = new Point(_down.Left - _up.Width - S(2), (Height - _up.Height) / 2);
    }

    /// <summary>The title font (t-label 10.5px/600), built for the DPI in Rescale — not per paint.</summary>
    private Font? _titleFont;
    private int _fontDpi;

    private Font TitleFont()
    {
        if (_titleFont == null || _fontDpi != DeviceDpi)
        {
            _titleFont?.Dispose();   // private to this header (never handed to Control.Font)
            _fontDpi = DeviceDpi;
            _titleFont = Theme.Ui(Theme.PtLabel, HouseWeight.SemiBold, _fontDpi);
        }
        return _titleFont;
    }

    /// <summary>Parity with the macOS SectionCollapseLabel: a slate disclosure chevron (right when collapsed, down when
    /// open), the title as a letter-spaced caps micro-label in slate (never the accent — rule 21), and the count in a
    /// small slate capsule.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var sel = Theme.Selector;
        var back = HouseDraw.EffectiveBack(this);
        float k = DeviceDpi / 96f, cy = Height / 2f, cx = S(10);

        // Chevron: the same stroke as the dropdowns' (HouseDraw.Chevron), turned to point right when collapsed.
        if (_collapsed)
        {
            float w = 8f * k;
            using var pen = new Pen(sel, 1.6f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[] { new PointF(cx - w / 4, cy - w / 2), new PointF(cx + w / 4, cy), new PointF(cx - w / 4, cy + w / 2) });
        }
        else HouseDraw.Chevron(g, cx, cy, 8f * k, sel, 1.6f * k);

        var font = TitleFont();
        float ty = cy - font.Height / 2f;
        float x = HouseDraw.DrawTracked(g, _title.ToUpperInvariant(), font, new PointF(S(22), ty), sel,
                                        Theme.Pxf(Theme.LabelTracking, DeviceDpi)) + S(6);

        // Count capsule: 12% slate wash, 70% slate figure, 5 × 0.5 px padding (the mac badge).
        var count = _count.ToString();
        var size = g.MeasureString(count, font, PointF.Empty, CountFormat);
        var pill = new RectangleF(x, cy - (font.Height + k) / 2f, size.Width + 10f * k, font.Height + k);
        using (var path = HouseDraw.Rounded(pill, pill.Height / 2f))
        using (var fill = new SolidBrush(Theme.Blend(sel, back, 0.12)))
            g.FillPath(fill, path);
        HouseDraw.DrawTracked(g, count, font, new PointF(pill.X + 5f * k, ty + k / 2f), Theme.Blend(sel, back, 0.7), 0f);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _titleFont?.Dispose(); _titleFont = null; _tip.Dispose(); }
    }
}
