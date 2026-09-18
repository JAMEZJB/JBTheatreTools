using System.Drawing.Drawing2D;

namespace JBTheatreTools;

/// <summary>The lifted "card" that follows the cursor during a grip drag-reorder — a small borderless,
/// non-activating top-level window showing the dragged app's icon and name. It's clipped to a rounded rect
/// and never takes focus, so it floats over the list glued to the pointer (parity with the macOS build).
/// Mouse events still go to the row that holds the capture, so the card being under the cursor is harmless.</summary>
public sealed class DragCardForm : Form
{
    private readonly Image? _icon;
    private readonly string _name;
    private readonly bool _dark;
    // The card is sized for the DPI of the row it was lifted from (a fresh top-level form only learns its
    // own monitor's DPI once shown); every pixel number is a 96-DPI design value through S().
    private readonly int _dpi;
    private int S(int v) => Theme.Px(v, _dpi);

    public DragCardForm(Image? icon, string name, bool dark, int dpi)
    {
        _icon = icon;
        _name = name;
        _dark = dark;
        _dpi = dpi;

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        using (var measure = Theme.Ui(Theme.PtBody, semibold: true, _dpi))
            Size = new Size(S(40) + TextRenderer.MeasureText(name, measure).Width + S(16), S(40));
        Region = new Region(Rounded(new Rectangle(0, 0, Width, Height), S(Theme.RPanel)));
    }

    /// <summary>Don't steal focus when shown.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */;
            return cp;
        }
    }

    /// <summary>Places the card so the cursor sits at its grab point (near the top-left), glued to the pointer.</summary>
    public void MoveTo(Point screenPoint) => Location = new Point(screenPoint.X - S(16), screenPoint.Y - S(20));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var card = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var bg = new SolidBrush(Theme.Card(_dark)))
        using (var path = Rounded(card, S(Theme.RPanel)))
            g.FillPath(bg, path);
        using (var pen = new Pen(Theme.LineStrong(_dark)))
        using (var path = Rounded(card, S(Theme.RPanel)))
            g.DrawPath(pen, path);

        if (_icon != null)
            g.DrawImage(_icon, new Rectangle(S(9), (Height - S(24)) / 2, S(24), S(24)));
        using var font = Theme.Ui(Theme.PtBody, semibold: true, _dpi);   // body 13px/600
        TextRenderer.DrawText(g, _name, font, new Rectangle(S(40), 0, Width - S(46), Height),
            Theme.Fg(_dark), TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
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
}
