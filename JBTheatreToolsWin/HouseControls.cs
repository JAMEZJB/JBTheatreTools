using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace JBTheatreTools;

// House Style v2 controls drawn by the launcher itself, at parity with the macOS build's Theme.swift (JBButtonStyle,
// JBSegmented, JBMenuPill). The stock WinForms controls fall back to Windows' own chrome — square corners and white
// 1 px borders in dark mode, no primary/secondary hierarchy — so the main window's buttons, the Light/Full picker, the
// status filter, the search field and the Settings dropdowns are painted here from the house tokens instead. Every
// pixel number is a 96-DPI design value scaled at the control's DeviceDpi; fonts are built in device pixels.

/// <summary>The three house button roles (macOS JBButtonStyle.Role): PRIMARY = accent fill, OnAccent text, no border;
/// SECONDARY = raised fill with a strong hairline; ICON = no fill, a slate glyph, a hover wash only (rule 21).</summary>
public enum HouseRole { Primary, Secondary, Icon }

/// <summary>A vector glyph a <see cref="HouseButton"/> draws in place of its text (the text stays its accessible name).</summary>
public enum HouseGlyph { None, More, Up, Down }

/// <summary>Shared drawing helpers for the house controls.</summary>
internal static class HouseDraw
{
    /// <summary>A rounded rectangle (radius clamped to the rectangle), for fills and 1 px strokes.</summary>
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>The first opaque background up the parent chain — what an owner-drawn control's corners sit on. A
    /// transparent parent (the section header) passes through to the list behind it.</summary>
    public static Color EffectiveBack(Control c)
    {
        for (var p = c.Parent; p != null; p = p.Parent)
            if (p.BackColor.A == 255) return p.BackColor;
        return Theme.Bg(Theme.CurrentDark);
    }

    /// <summary>Disabled controls render at 45% (the kit's disabled opacity), pre-blended over what's behind them.</summary>
    public static Color Fade(Color c, Color back, bool enabled) => enabled ? c : Theme.Blend(c, back, 0.45);

    /// <summary>The keyboard-focus ring: 2 px in the accent (or <paramref name="color"/>), inset so it stays inside
    /// the control's bounds.</summary>
    public static void FocusRing(Graphics g, Size size, int dpi, Color color)
    {
        float w = Theme.Pxf(2f, dpi);
        var r = new RectangleF(w / 2, w / 2, size.Width - w - 1, size.Height - w - 1);
        using var pen = new Pen(color, w);
        using var path = Rounded(r, Theme.Pxf(Theme.RCtl, dpi) - w / 2);
        g.DrawPath(pen, path);
    }

    /// <summary>A down chevron centred on (cx, cy), <paramref name="w"/> wide — the menu / dropdown indicator.</summary>
    public static void Chevron(Graphics g, float cx, float cy, float w, Color color, float stroke)
    {
        using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float h = w / 2;
        g.DrawLines(pen, new[] { new PointF(cx - w / 2, cy - h / 2), new PointF(cx, cy + h / 2), new PointF(cx + w / 2, cy - h / 2) });
    }

    // Windows' own symbol font (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10) for the header
    // buttons' leading symbols; without either, the buttons are text-only.
    private static readonly FontFamily? IconFamily = ResolveFamily("Segoe Fluent Icons") ?? ResolveFamily("Segoe MDL2 Assets");

    private static FontFamily? ResolveFamily(string name)
    {
        try { return new FontFamily(name); }
        catch { return null; }   // not installed (or a non-Windows build host)
    }

    public static Font? IconFont(float px) => IconFamily == null ? null : new Font(IconFamily, px, FontStyle.Regular, GraphicsUnit.Pixel);

    public const TextFormatFlags TextFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

    // ── native theming (the parts of a control Windows still draws: scrollbars, drop-down lists) ──────────────────
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    /// <summary>Dark scrollbars (and borders) on a native-scrolling control in dark mode, the standard ones in light.
    /// Needs the handle; callers re-apply it from ApplyTheme and on HandleCreated.</summary>
    public static void NativeTheme(Control c, bool dark, string darkClass = "DarkMode_Explorer")
    {
        if (!c.IsHandleCreated) return;
        try { SetWindowTheme(c.Handle, dark ? darkClass : "Explorer", null); }
        catch { /* uxtheme missing / older Windows: the light system scrollbar stays */ }
    }

    // ── letter-spaced caps (the kit's micro-label: 10.5px/600 uppercase, 0.8 tracking) ─────────────────────────
    // GDI (TextRenderer) has no letter-spacing, so the caps labels are laid out glyph by glyph in GDI+ with typographic
    // (unpadded) advances, then the tracking added between them.

    // Typographic advances, counting a space's width (GenericTypographic measures a lone space as 0 px).
    private static readonly StringFormat Typographic = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormat.GenericTypographic.FormatFlags | StringFormatFlags.MeasureTrailingSpaces,
    };

    /// <summary>The width of <paramref name="text"/> in <paramref name="font"/> with <paramref name="tracking"/> px
    /// between glyphs.</summary>
    public static float TrackedWidth(Graphics g, string text, Font font, float tracking)
    {
        float w = 0;
        foreach (var ch in text) w += g.MeasureString(ch.ToString(), font, PointF.Empty, Typographic).Width;
        return w + tracking * Math.Max(0, text.Length - 1);
    }

    /// <summary>Draws <paramref name="text"/> with <paramref name="tracking"/> px between glyphs, its top-left at
    /// <paramref name="origin"/>. Returns the x after the last glyph.</summary>
    public static float DrawTracked(Graphics g, string text, Font font, PointF origin, Color color, float tracking)
    {
        var hint = g.TextRenderingHint;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var brush = new SolidBrush(color);
        float x = origin.X;
        foreach (var ch in text)
        {
            var glyph = ch.ToString();
            g.DrawString(glyph, font, brush, x, origin.Y, Typographic);
            x += g.MeasureString(glyph, font, PointF.Empty, Typographic).Width + tracking;
        }
        g.TextRenderingHint = hint;
        return x - tracking;
    }

    // ── Windows 11 window corners (pop-up menus, tooltips) ─────────────────────────────────────────────────────
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Small rounded corners (DWMWA_WINDOW_CORNER_PREFERENCE = ROUNDSMALL) and a 1 px DWM border in
    /// <paramref name="border"/> (DWMWA_BORDER_COLOR) on a pop-up window. Windows 10 ignores both: the window keeps
    /// square corners and the border its renderer draws.</summary>
    public static void RoundPopup(IntPtr hwnd, Color border)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int pref = 3;   // DWMWCP_ROUNDSMALL
            DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
            int colorRef = border.R | border.G << 8 | border.B << 16;
            DwmSetWindowAttribute(hwnd, 34, ref colorRef, sizeof(int));
        }
        catch { /* dwmapi missing: square corners */ }
    }

    // ── the suite glyph (Tabler "masks-theater", the same 24-unit paths as the Android identity tile) ─────────────
    /// <summary>The launcher's identity tile: an accent-filled rounded square with the theatre-masks glyph in
    /// OnAccent at 62% of the tile (parity with Android's IdentityTile and the macOS header's masks in the accent).</summary>
    public static void IdentityTile(Graphics g, RectangleF tile, int dpi)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(Theme.Accent))
        using (var path = Rounded(tile, Theme.Pxf(Theme.RPanel, dpi)))
            g.FillPath(fill, path);

        float k = tile.Width * 0.62f / 24f;
        float ox = tile.X + tile.Width * 0.19f, oy = tile.Y + tile.Height * 0.19f;
        PointF P(float x, float y) => new(ox + x * k, oy + y * k);
        RectangleF Box(float cx, float cy, float r) => new(ox + (cx - r) * k, oy + (cy - r) * k, 2 * r * k, 2 * r * k);

        using var pen = new Pen(Theme.OnAccent, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var dot = new SolidBrush(Theme.OnAccent);

        // Front mask (right): 2-unit top corners, 4-unit bottom corners (the SVG arcs, as centre + angles).
        using (var front = new GraphicsPath())
        {
            front.AddLine(P(13.192f, 9f), P(19.808f, 9f));
            front.AddArc(Box(19.808f, 11f, 2f), -90f, 95.25f);
            front.AddLine(P(21.8f, 11.183f), P(21.233f, 17.365f));
            front.AddArc(Box(17.25f, 17f, 4f), 5.24f, 84.76f);
            front.AddLine(P(17.25f, 21f), P(15.75f, 21f));
            front.AddArc(Box(15.75f, 17f, 4f), 90f, 84.76f);
            front.AddLine(P(11.767f, 17.365f), P(11.2f, 11.183f));
            front.AddArc(Box(13.192f, 11f, 2f), 174.75f, 95.25f);
            front.CloseFigure();
            g.DrawPath(pen, front);
        }
        // Back mask (left): an open outline that stops where the front mask overlaps it.
        using (var back = new GraphicsPath())
        {
            back.AddLine(P(8.632f, 15.982f), P(8.25f, 16f));
            back.AddLine(P(8.25f, 16f), P(6.75f, 16f));
            back.AddArc(Box(6.75f, 12f, 4f), 90f, 84.76f);
            back.AddLine(P(2.767f, 12.365f), P(2.2f, 6.183f));
            back.AddArc(Box(4.192f, 6f, 2f), 174.75f, 95.25f);
            back.AddLine(P(4.192f, 4f), P(10.808f, 4f));
            back.AddArc(Box(10.808f, 6f, 2f), 270f, 90f);
            g.DrawPath(pen, back);
        }
        foreach (var (x, y) in new[] { (15f, 13f), (18f, 13f), (6f, 8f), (9f, 8f) })
            g.FillEllipse(dot, Box(x, y, 1f));
        g.DrawBezier(pen, P(15f, 16.5f), P(16f, 17.167f), P(17f, 17.167f), P(18f, 16.5f));        // smile
        g.DrawBezier(pen, P(6f, 12f), P(6.764f, 11.49f), P(7.528f, 11.37f), P(8.291f, 11.64f));   // frown
    }
}

/// <summary>The house button (macOS JBButtonStyle): owner-drawn, anti-aliased, radius 6 (JBRadius.ctl), 13 px Inter Medium
/// text with 12×5 padding (compact 11 px, 9×3, for list rows). Hover = 6% of the text colour over the fill, pressed =
/// the sunken step, disabled = 45%. Keyboard focus shows a 2 px accent ring only when Windows is showing focus cues,
/// so the window never opens with a highlighted "default" button. Still a <see cref="Button"/>: Click, DialogResult,
/// AcceptButton/CancelButton, AutoSize, tooltips and accessibility all behave as before.</summary>
public sealed class HouseButton : Button
{
    private HouseRole _role;
    private bool _compact, _chevron, _hover, _pressed, _showSymbol = true;
    private HouseGlyph _glyph;
    private string? _symbol;
    private float _textPt;
    private int _dpi;
    private Font? _font, _symbolFont;
    private Size? _pref;   // measured size, until the text / font / role changes

    private int S(int v) => Theme.Px(v, DeviceDpi);
    private float Sf(float v) => Theme.Pxf(v, DeviceDpi);

    public HouseButton() : this(HouseRole.Secondary) { }

    public HouseButton(HouseRole role, bool compact = false)
    {
        _role = role;
        _compact = compact;
        FlatStyle = FlatStyle.Flat;          // never FlatStyle.System: that hands painting back to Windows
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        UseMnemonic = false;                 // a literal "&" renders; no Alt-underline games
        AutoSizeMode = AutoSizeMode.GrowAndShrink;   // hug the text like the mac buttons (the stock minimum is 75 px)
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BuildFonts();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HouseRole Role { get => _role; set { if (_role == value) return; _role = value; Remeasure(); } }

    /// <summary>The list-row size: 11 px text, 9×3 padding, 20 px minimum height.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Compact { get => _compact; set { if (_compact == value) return; _compact = value; BuildFonts(); } }

    /// <summary>A trailing slate chevron — the button opens a menu (the macOS menu pill's indicator).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Chevron { get => _chevron; set { if (_chevron == value) return; _chevron = value; Remeasure(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HouseGlyph Glyph { get => _glyph; set { if (_glyph == value) return; _glyph = value; Remeasure(); } }

    /// <summary>A leading symbol from Windows' icon font (e.g. "" refresh), drawn before the text.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Symbol { get => _symbol; set { if (_symbol == value) return; _symbol = value; BuildFonts(); } }

    /// <summary>False drops the <see cref="Symbol"/> (a narrow window's header makes room for the title this way).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSymbol { get => _showSymbol; set { if (_showSymbol == value) return; _showSymbol = value; Remeasure(); } }

    private Font? ShownSymbolFont => _showSymbol ? _symbolFont : null;

    /// <summary>Overrides the text size (points), e.g. the section header's small arrows. 0 = by <see cref="Compact"/>.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float TextPt { get => _textPt; set { if (_textPt == value) return; _textPt = value; BuildFonts(); } }

    /// <summary>(Re)builds the text + symbol fonts in device pixels for the current DPI, then re-measures. A font is
    /// only replaced when its size changes: Control.Font ignores a new font that Equals the current one (same family,
    /// size and style) and keeps the old instance — disposing that would leave the button measuring a dead font.</summary>
    private void BuildFonts()
    {
        _dpi = DeviceDpi;
        float px = (_textPt > 0 ? _textPt : (_compact ? 8.25f : Theme.PtBody)) * _dpi / 72f;
        if (_font == null || Math.Abs(_font.Size - px) > 0.01f)
        {
            var old = _font;
            _font = Theme.Ui(_textPt > 0 ? _textPt : (_compact ? 8.25f : Theme.PtBody), HouseWeight.Medium, _dpi);   // mac .medium
            Font = _font;
            if (old != null && !ReferenceEquals(Font, old)) old.Dispose();
        }
        float symbolPx = Sf(_compact ? 11f : 13f);
        if (_symbol == null) { _symbolFont?.Dispose(); _symbolFont = null; }
        else if (_symbolFont == null || Math.Abs(_symbolFont.Size - symbolPx) > 0.01f)
        {
            _symbolFont?.Dispose();   // private to this button (never handed to Control.Font)
            _symbolFont = HouseDraw.IconFont(symbolPx);
        }
        Remeasure();
    }

    /// <summary>Rebuilds the fonts if the DPI changed — for an owner re-laying-out after a DPI change, which may run
    /// before this button has heard WM_DPICHANGED_AFTERPARENT itself (idempotent).</summary>
    public void Rescale() { if (DeviceDpi != _dpi) BuildFonts(); }

    private void Remeasure()
    {
        _pref = null;
        if (AutoSize) Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e) { _pref = null; base.OnTextChanged(e); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { _pref = null; base.OnFontChanged(e); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (DeviceDpi != _dpi) BuildFonts();   // Per-Monitor V2: the handle may land on another monitor's DPI
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        BuildFonts();
    }

    private int SymbolWidth => ShownSymbolFont is { } f ? TextRenderer.MeasureText(_symbol, f, Size.Empty, HouseDraw.TextFlags).Width : 0;

    /// <summary>The house control height (the stock button is 23 px tall), for buttons given only a width.</summary>
    protected override Size DefaultSize => new(75, 28);

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (_pref is { } cached) return cached;
        int padX = _role == HouseRole.Icon ? S(4) : S(_compact ? 9 : 12);
        int padY = S(_compact ? 3 : 5);
        int minH = S(_compact ? 20 : 26);
        Size content;
        if (_glyph != HouseGlyph.None) content = new Size(S(12), S(12));
        else
        {
            content = TextRenderer.MeasureText(Text, Font, Size.Empty, HouseDraw.TextFlags);
            if (ShownSymbolFont != null) content.Width += SymbolWidth + S(6);
            if (_chevron) content.Width += S(14);
        }
        var size = new Size(content.Width + 2 * padX, Math.Max(minH, content.Height + 2 * padY));
        _pref = size;
        return size;
    }

    // ── interaction state ───────────────────────────────────────────────────────────────────────────────────────
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        base.OnMouseDown(mevent);
        if (mevent.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
    }
    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        base.OnMouseUp(mevent);
        if (_pressed) { _pressed = false; Invalidate(); }
    }
    protected override void OnKeyDown(KeyEventArgs kevent)
    {
        base.OnKeyDown(kevent);
        if (kevent.KeyCode == Keys.Space) { _pressed = true; Invalidate(); }
    }
    protected override void OnKeyUp(KeyEventArgs kevent)
    {
        if (_pressed) { _pressed = false; Invalidate(); }
        base.OnKeyUp(kevent);
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); _hover = _pressed = false; Invalidate(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); _hover = _pressed = false; }
    /// <summary>The row / banner behind changed colour (hover, theme): repaint the corners that sit on it.</summary>
    protected override void OnParentBackColorChanged(EventArgs e) { base.OnParentBackColorChanged(e); Invalidate(); }

    protected override void OnPaintBackground(PaintEventArgs pevent) { /* OnPaint covers every pixel */ }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        var back = HouseDraw.EffectiveBack(this);
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool dark = Theme.CurrentDark, on = Enabled;
        Color fill, fg;
        Color? border = null;
        switch (_role)
        {
            case HouseRole.Primary:
                fill = _pressed ? Theme.Blend(Theme.Accent, back, 0.8) : _hover ? Theme.Blend(Theme.Fg(dark), Theme.Accent, 0.06) : Theme.Accent;
                fg = Theme.OnAccent;
                break;
            case HouseRole.Secondary:
                fill = _pressed ? Theme.Sunken(dark) : _hover ? Theme.Blend(Theme.Fg(dark), Theme.Raised(dark), 0.06) : Theme.Raised(dark);
                fg = Theme.Fg(dark);
                border = Theme.LineStrong(dark);
                break;
            default:
                fill = _pressed ? Theme.Blend(Theme.Selector, back, 0.18) : _hover ? Theme.Blend(Theme.Selector, back, 0.10) : back;
                fg = Theme.Selector;
                break;
        }
        fill = HouseDraw.Fade(fill, back, on);
        fg = HouseDraw.Fade(fg, fill, on);

        float radius = Sf(Theme.RCtl);
        if (fill != back)
        {
            using var path = HouseDraw.Rounded(new RectangleF(0, 0, Width, Height), radius);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }
        if (border is { } b)
        {
            using var path = HouseDraw.Rounded(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), radius);
            using var pen = new Pen(HouseDraw.Fade(b, back, on));   // a hairline stays one device pixel
            g.DrawPath(pen, path);
        }

        if (_glyph != HouseGlyph.None) DrawGlyph(g, fg);
        else DrawContent(g, fg, fill);

        if (Focused && ShowFocusCues)
            HouseDraw.FocusRing(g, Size, DeviceDpi, _role == HouseRole.Primary ? Theme.OnAccent : Theme.Accent);
    }

    private void DrawContent(Graphics g, Color fg, Color fill)
    {
        var textSize = TextRenderer.MeasureText(Text, Font, Size.Empty, HouseDraw.TextFlags);
        int symW = SymbolWidth;
        int total = textSize.Width + (symW > 0 ? symW + S(6) : 0) + (_chevron ? S(14) : 0);
        int padX = _role == HouseRole.Icon ? S(4) : S(_compact ? 9 : 12);
        // Centred when the button is wider than its content (a fixed-width button), else left at the padding.
        int x = Math.Max(padX, (Width - total) / 2);
        if (symW > 0)
        {
            TextRenderer.DrawText(g, _symbol, ShownSymbolFont, new Rectangle(x, 0, symW, Height), fg, fill,
                                  HouseDraw.TextFlags | TextFormatFlags.VerticalCenter);
            x += symW + S(6);
        }
        int textW = Math.Min(textSize.Width, Math.Max(0, Width - x - padX - (_chevron ? S(14) : 0)));
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, textW, Height), fg, fill,
                              HouseDraw.TextFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (_chevron)
        {
            // Slate on a secondary pill (rule 21: selectors are slate, not the accent); the label colour on a filled one.
            var chevron = _role == HouseRole.Secondary ? HouseDraw.Fade(Theme.Selector, fill, Enabled) : fg;
            HouseDraw.Chevron(g, x + textW + S(6) + Sf(4f), Height / 2f, Sf(8f), chevron, Sf(1.6f));
        }
    }

    private void DrawGlyph(Graphics g, Color fg)
    {
        using var brush = new SolidBrush(fg);
        float cx = Width / 2f, cy = Height / 2f;
        switch (_glyph)
        {
            case HouseGlyph.More:   // ⋯ — three dots
                float d = Sf(3.2f), gap = Sf(3.4f);
                for (int i = -1; i <= 1; i++)
                    g.FillEllipse(brush, cx + i * (d + gap) - d / 2, cy - d / 2, d, d);
                break;
            case HouseGlyph.Up:
            case HouseGlyph.Down:
                float w = Sf(8f), h = Sf(5f), s = _glyph == HouseGlyph.Up ? -1 : 1;
                g.FillPolygon(brush, new[] { new PointF(cx - w / 2, cy - s * h / 2), new PointF(cx + w / 2, cy - s * h / 2), new PointF(cx, cy + s * h / 2) });
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _font?.Dispose(); _symbolFont?.Dispose(); _font = _symbolFont = null; }
    }
}

/// <summary>A "pick one" segmented control (macOS JBSegmented): a sunken trough with the selected segment lifted to
/// the surface. Used for the list rows' Light/Full picker and the main window's status filter. The API mirrors the
/// ComboBox it replaces (SelectedIndex + SelectedIndexChanged, raised for programmatic changes too). The control sizes
/// itself to its segments; arrow keys move the selection.</summary>
public sealed class HouseSegmented : Control
{
    private readonly List<string> _items = new();
    private readonly List<Rectangle> _segments = new();
    private int _selected = -1, _hot = -1;
    private bool _compact, _large;
    private int _dpi;
    private Font? _font;
    private int S(int v) => Theme.Px(v, DeviceDpi);

    public event EventHandler? SelectedIndexChanged;

    /// <param name="compact">The list rows' Light/Full size (10 px text).</param>
    /// <param name="large">The settings size (13 px text, the macOS Settings sheet's segmented Appearance switch).</param>
    public HouseSegmented(IEnumerable<string> items, bool compact = false, bool large = false)
    {
        _items.AddRange(items);
        _compact = compact;
        _large = large && !compact;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Grouping;
        Rescale();
    }

    public IReadOnlyList<string> Items => _items;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < -1 || value >= _items.Count || value == _selected) return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        }
    }

    public string? SelectedText => _selected >= 0 ? _items[_selected] : null;

    /// <summary>Re-derives the font and the control's size from the current DPI (and on a DPI change).</summary>
    public void Rescale()
    {
        if (DeviceDpi != _dpi || _font == null)
        {
            _dpi = DeviceDpi;
            var old = _font;
            _font = Theme.Ui(_compact ? 7.5f : _large ? Theme.PtBody : 8.25f, HouseWeight.Medium, _dpi);   // 10 / 11 / 13 px medium
            Font = _font;
            // Control.Font keeps the old instance when the new one Equals it: never dispose the font still in use.
            if (old != null && !ReferenceEquals(Font, old)) old.Dispose();
        }
        // Mac metrics: 2 px trough padding, 2 px between segments, segment padding 7×2 (compact) / 9×3.
        int pad = S(2), gap = S(2), padX = S(_compact ? 7 : _large ? 14 : 9), padY = S(_compact ? 2 : 3);
        int textH = TextRenderer.MeasureText("Ag", _font, Size.Empty, HouseDraw.TextFlags).Height;
        int segH = textH + 2 * padY;
        _segments.Clear();
        int x = pad;
        foreach (var item in _items)
        {
            int w = TextRenderer.MeasureText(item, _font, Size.Empty, HouseDraw.TextFlags).Width + 2 * padX;
            _segments.Add(new Rectangle(x, pad, w, segH));
            x += w + gap;
        }
        Size = new Size(x - gap + pad, segH + 2 * pad);
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize) => Size;

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

    private int HitTest(Point p) => _segments.FindIndex(r => r.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hot = HitTest(e.Location);
        if (hot != _hot) { _hot = hot; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hot != -1) { _hot = -1; Invalidate(); } }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        int i = HitTest(e.Location);
        if (i >= 0) SelectedIndex = i;
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & ~Keys.Shift) is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;
        int i = e.KeyCode switch
        {
            Keys.Left => Math.Max(0, _selected - 1),
            Keys.Right => Math.Min(_items.Count - 1, _selected + 1),
            Keys.Home => 0,
            Keys.End => _items.Count - 1,
            _ => _selected,
        };
        if (i != _selected) { SelectedIndex = i; e.Handled = true; }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); _hot = -1; Invalidate(); }
    protected override void OnParentBackColorChanged(EventArgs e) { base.OnParentBackColorChanged(e); Invalidate(); }
    protected override void OnPaintBackground(PaintEventArgs pevent) { /* OnPaint covers every pixel */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = HouseDraw.EffectiveBack(this);
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool dark = Theme.CurrentDark, on = Enabled;
        float radius = Theme.Pxf(Theme.RCtl, DeviceDpi);
        var trough = HouseDraw.Fade(Theme.Sunken(dark), back, on);
        using (var path = HouseDraw.Rounded(new RectangleF(0, 0, Width, Height), radius))
        using (var brush = new SolidBrush(trough))
            g.FillPath(brush, path);
        using (var path = HouseDraw.Rounded(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), radius))
        using (var pen = new Pen(HouseDraw.Fade(Theme.Line(dark), back, on)))
            g.DrawPath(pen, path);

        for (int i = 0; i < _items.Count && i < _segments.Count; i++)
        {
            var r = _segments[i];
            bool selected = i == _selected;
            var segBack = trough;
            if (selected)
            {
                segBack = HouseDraw.Fade(Theme.Surface(dark), back, on);
                var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);
                using var path = HouseDraw.Rounded(rf, radius - Theme.Pxf(2, DeviceDpi));
                using var brush = new SolidBrush(segBack);
                using var pen = new Pen(HouseDraw.Fade(Theme.LineStrong(dark), back, on));
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
            var fg = selected || (i == _hot && on) ? Theme.Fg(dark) : Theme.Sub(dark);
            TextRenderer.DrawText(g, _items[i], Font, r, HouseDraw.Fade(fg, segBack, on), segBack,
                                  HouseDraw.TextFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        if (Focused && ShowFocusCues) HouseDraw.FocusRing(g, Size, DeviceDpi, Theme.Accent);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _font?.Dispose(); _font = null; }
    }

    // Screen readers see a group of radio buttons, one per segment, with the selected one checked.
    protected override AccessibleObject CreateAccessibilityInstance() => new SegmentedAccessible(this);

    private sealed class SegmentedAccessible(HouseSegmented owner) : ControlAccessibleObject(owner)
    {
        public override string? Value { get => owner.SelectedText; set { } }
        public override int GetChildCount() => owner._items.Count;
        public override AccessibleObject? GetChild(int index) =>
            index >= 0 && index < owner._items.Count ? new SegmentAccessible(owner, this, index) : null;
    }

    private sealed class SegmentAccessible(HouseSegmented owner, AccessibleObject parent, int index) : AccessibleObject
    {
        public override string? Name { get => owner._items[index]; set { } }
        public override AccessibleRole Role => AccessibleRole.RadioButton;
        public override AccessibleObject Parent => parent;
        public override AccessibleStates State =>
            AccessibleStates.Selectable | (index == owner._selected ? AccessibleStates.Checked | AccessibleStates.Selected : 0)
            | (owner.Enabled ? 0 : AccessibleStates.Unavailable);
        public override Rectangle Bounds =>
            index < owner._segments.Count && owner.IsHandleCreated ? owner.RectangleToScreen(owner._segments[index]) : Rectangle.Empty;
        public override string DefaultAction => "Select";
        public override void DoDefaultAction() { if (owner.Enabled) owner.SelectedIndex = index; }
    }
}

/// <summary>A TextBox that draws its placeholder itself, in a colour the caller picks (the stock cue banner is
/// always the system grey). Shown whenever the box is empty, focused or not — as on the mac find field.</summary>
internal sealed class CueTextBox : TextBox
{
    private string _cue = "";
    private Color _cueColor = SystemColors.GrayText;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Cue { get => _cue; set { _cue = value ?? ""; Invalidate(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color CueColor { get => _cueColor; set { _cueColor = value; Invalidate(); } }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x000F /* WM_PAINT */ && TextLength == 0 && _cue.Length > 0 && IsHandleCreated)
        {
            // The edit control's own left margin, so the cue starts where typed text will.
            int left = (int)SendMessage(Handle, 0x00D4 /* EM_GETMARGINS */, IntPtr.Zero, IntPtr.Zero) & 0xFFFF;
            using var g = Graphics.FromHwnd(Handle);
            var r = new Rectangle(left, 0, Math.Max(0, ClientSize.Width - left), ClientSize.Height);
            TextRenderer.DrawText(g, _cue, Font, r, _cueColor, BackColor,
                                  HouseDraw.TextFlags | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

/// <summary>A house text field (the macOS find field): a borderless TextBox inside a sunken, radius-6 well with a 1 px
/// hairline (an accent hairline while typing), the placeholder in the tertiary tone, and — for the find field — a
/// magnifier glyph. <see cref="Box"/> is the TextBox itself, so Ctrl+F / Esc / TextChanged / password masking work on
/// it unchanged.</summary>
public sealed class HouseTextField : Panel
{
    private readonly CueTextBox _box = new() { BorderStyle = BorderStyle.None };
    private readonly bool _searchGlyph;
    private int S(int v) => Theme.Px(v, DeviceDpi);

    public TextBox Box => _box;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Placeholder { get => _box.Cue; set => _box.Cue = value; }

    public HouseTextField(bool searchGlyph = false)
    {
        _searchGlyph = searchGlyph;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.IBeam;
        Controls.Add(_box);
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => Invalidate();
        Click += (_, _) => _box.Focus();   // the well around the text is part of the field
    }

    /// <summary>Re-reads the theme tokens (the TextBox is a native control: its colours are set, not painted).</summary>
    public void ApplyTheme(bool dark)
    {
        _box.BackColor = Theme.Sunken(dark);
        _box.ForeColor = Theme.Fg(dark);
        _box.CueColor = Theme.Muted(dark);
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int left = _searchGlyph ? S(26) : S(8);
        _box.Location = new Point(left, (Height - _box.Height) / 2);
        _box.Width = Math.Max(S(20), Width - left - S(8));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(HouseDraw.EffectiveBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool dark = Theme.CurrentDark;
        float radius = Theme.Pxf(Theme.RCtl, DeviceDpi);
        using (var path = HouseDraw.Rounded(new RectangleF(0, 0, Width, Height), radius))
        using (var brush = new SolidBrush(Theme.Sunken(dark)))
            g.FillPath(brush, path);
        var edge = _box.Focused ? Theme.Blend(Theme.Accent, Theme.Sunken(dark), 0.6) : Theme.Line(dark);
        using (var path = HouseDraw.Rounded(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), radius))
        using (var pen = new Pen(edge))
            g.DrawPath(pen, path);

        if (!_searchGlyph) return;
        // Magnifier: a 7 px lens and a short handle, in the tertiary tone.
        float k = DeviceDpi / 96f, cx = S(9) + 3.5f * k, cy = Height / 2f - 1f * k;
        using var lens = new Pen(Theme.Muted(dark), 1.4f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(lens, cx - 3.5f * k, cy - 3.5f * k, 7f * k, 7f * k);
        g.DrawLine(lens, cx + 2.6f * k, cy + 2.6f * k, cx + 5.2f * k, cy + 5.2f * k);
    }
}

/// <summary>A house dropdown (the Settings pickers): the closed box is painted here — raised fill, strong hairline,
/// radius 6, a slate chevron (rule 21) — and the open list is owner-drawn in the panel colours, with a dark native
/// frame and scrollbar in dark mode. The native control still owns the list, keyboard and accessibility.</summary>
public sealed class HouseComboBox : ComboBox
{
    private bool _hover;

    public HouseComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        ItemHeight = Theme.Px(20, DeviceDpi);   // closed box ≈ 26 px (the house control height) at 96 DPI
    }

    /// <summary>Re-reads the theme tokens: the open list's fill + the native frame's dark/light theme.</summary>
    public void ApplyTheme(bool dark)
    {
        BackColor = Theme.Surface(dark);
        ForeColor = Theme.Fg(dark);
        HouseDraw.NativeTheme(this, dark, "DarkMode_CFD");
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        HouseDraw.NativeTheme(this, Theme.CurrentDark, "DarkMode_CFD");
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }

    /// <summary>The open list's rows (the closed box is painted in WndProc below): the house menus' look — the Surface
    /// fill and, under the pointer / keyboard selection, the same rounded 6 px wash of the text colour (not the
    /// accent: a dropdown is a selector, rule 21).</summary>
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        bool dark = Theme.CurrentDark;
        bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        var g = e.Graphics;
        var surface = Theme.Surface(dark);
        using (var brush = new SolidBrush(surface)) g.FillRectangle(brush, e.Bounds);
        if (selected)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float inset = Theme.Pxf(3f, DeviceDpi);
            var wash = new RectangleF(e.Bounds.X + inset, e.Bounds.Y + Theme.Pxf(1f, DeviceDpi),
                                      e.Bounds.Width - 2 * inset, e.Bounds.Height - Theme.Pxf(2f, DeviceDpi));
            using var path = HouseDraw.Rounded(wash, Theme.Pxf(Theme.RCtl, DeviceDpi));
            using var brush = new SolidBrush(Theme.Blend(Theme.Fg(dark), surface, 0.08));
            g.FillPath(brush, path);
        }
        var r = new Rectangle(e.Bounds.X + Theme.Px(9, DeviceDpi), e.Bounds.Y, Math.Max(0, e.Bounds.Width - Theme.Px(12, DeviceDpi)), e.Bounds.Height);
        TextRenderer.DrawText(g, GetItemText(Items[e.Index]), Font, r, Theme.Fg(dark),
                              HouseDraw.TextFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    // ── the open list's frame ──────────────────────────────────────────────────────────────────────────────────
    // Windows draws the drop-down list's 1 px border itself, in the system accent under the dark theme (and a stock
    // grey in light). The list window is subclassed once, on the first drop-down, to paint that border in the house
    // LineStrong hairline instead — the same edge as the closed box.

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMBOBOXINFO
    {
        public int cbSize;
        public RECT rcItem, rcButton;
        public int stateButton;
        public IntPtr hwndCombo, hwndItem, hwndList;
    }

    [DllImport("user32.dll")] private static extern bool GetComboBoxInfo(IntPtr hwnd, ref COMBOBOXINFO info);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    private ListFrame? _listFrame;

    protected override void OnDropDown(EventArgs e)
    {
        base.OnDropDown(e);
        if (_listFrame != null) return;
        try
        {
            var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf<COMBOBOXINFO>() };
            if (GetComboBoxInfo(Handle, ref info) && info.hwndList != IntPtr.Zero)
            {
                _listFrame = new ListFrame();
                _listFrame.AssignHandle(info.hwndList);
            }
        }
        catch { _listFrame = null; /* the system border stays */ }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _listFrame?.ReleaseHandle();
        _listFrame = null;
        base.OnHandleDestroyed(e);
    }

    private sealed class ListFrame : NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg is 0x0085 /* WM_NCPAINT */ or 0x0086 /* WM_NCACTIVATE */) PaintFrame();
        }

        private void PaintFrame()
        {
            IntPtr dc = GetWindowDC(Handle);
            if (dc == IntPtr.Zero) return;
            try
            {
                if (!GetWindowRect(Handle, out var r)) return;
                using var g = Graphics.FromHdc(dc);
                using var pen = new Pen(Theme.LineStrong(Theme.CurrentDark));
                g.DrawRectangle(pen, 0, 0, r.Right - r.Left - 1, r.Bottom - r.Top - 1);
            }
            catch { /* a failed frame paint leaves the system border */ }
            finally { ReleaseDC(Handle, dc); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public int left, top, right, bottom;
        public bool fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case 0x0014:   // WM_ERASEBKGND — the paint below covers everything; no white flash first
                m.Result = (IntPtr)1;
                return;
            case 0x000F:   // WM_PAINT — the closed box, drawn entirely here (double-buffered)
                var hdc = BeginPaint(m.HWnd, out var ps);
                try
                {
                    using var buffer = BufferedGraphicsManager.Current.Allocate(hdc, ClientRectangle);
                    PaintClosed(buffer.Graphics);
                    buffer.Render();
                }
                catch { /* a failed paint leaves the old pixels; never take the dialog down */ }
                finally { EndPaint(m.HWnd, ref ps); }
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    private void PaintClosed(Graphics g)
    {
        var back = HouseDraw.EffectiveBack(this);
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool dark = Theme.CurrentDark, on = Enabled;
        int dpi = DeviceDpi;
        float radius = Theme.Pxf(Theme.RCtl, dpi);
        var fill = HouseDraw.Fade(_hover && on ? Theme.Blend(Theme.Fg(dark), Theme.Raised(dark), 0.06) : Theme.Raised(dark), back, on);
        using (var path = HouseDraw.Rounded(new RectangleF(0, 0, Width, Height), radius))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        using (var path = HouseDraw.Rounded(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), radius))
        using (var pen = new Pen(HouseDraw.Fade(Theme.LineStrong(dark), back, on)))
            g.DrawPath(pen, path);

        int chevronZone = Theme.Px(24, dpi);
        var text = SelectedIndex >= 0 ? GetItemText(SelectedItem) : "";
        var r = new Rectangle(Theme.Px(9, dpi), 0, Math.Max(0, Width - Theme.Px(9, dpi) - chevronZone), Height);
        TextRenderer.DrawText(g, text, Font, r, HouseDraw.Fade(Theme.Fg(dark), fill, on), fill,
                              HouseDraw.TextFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        HouseDraw.Chevron(g, Width - chevronZone / 2f, Height / 2f, Theme.Pxf(8f, dpi), HouseDraw.Fade(Theme.Selector, fill, on), Theme.Pxf(1.6f, dpi));
        if (Focused && ShowFocusCues) HouseDraw.FocusRing(g, Size, dpi, Theme.Accent);
    }
}

/// <summary>The header's identity tile (see <see cref="HouseDraw.IdentityTile"/>), sized by its owner.</summary>
public sealed class IdentityTileControl : Control
{
    public IdentityTileControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "JB Theatre Tools";
    }

    protected override void OnParentBackColorChanged(EventArgs e) { base.OnParentBackColorChanged(e); Invalidate(); }
    protected override void OnPaintBackground(PaintEventArgs pevent) { /* OnPaint covers every pixel */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(HouseDraw.EffectiveBack(this));
        HouseDraw.IdentityTile(e.Graphics, new RectangleF(0, 0, Width, Height), DeviceDpi);
    }
}

/// <summary>A settings panel (the macOS GroupBox): a rounded, radius-8 surface in <see cref="Panel.BackColor"/> (the
/// Raised token in Settings, the Surface token behind a reader) closed by a 1 px Line hairline. Children inherit the
/// fill as their BackColor, so labels and the house controls paint onto it; the corners outside the curve are painted
/// in whatever the panel sits on.</summary>
public sealed class HousePanel : Panel
{
    public HousePanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Corner radius in 96-DPI pixels.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 8;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(HouseDraw.EffectiveBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float radius = Theme.Pxf(Radius, DeviceDpi);
        using (var path = HouseDraw.Rounded(new RectangleF(0, 0, Width, Height), radius))
        using (var brush = new SolidBrush(BackColor))
            g.FillPath(brush, path);
        using (var path = HouseDraw.Rounded(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), radius))
        using (var pen = new Pen(Theme.Line(Theme.CurrentDark)))
            g.DrawPath(pen, path);
    }

    protected override void OnParentBackColorChanged(EventArgs e) { base.OnParentBackColorChanged(e); Invalidate(); }
}

/// <summary>The kit's panel heading (macOS SettingsView.panelLabel): a 10.5px/600 caps micro-label, letter-spaced
/// 0.8 px, in the tertiary tone. Sizes its own height from the DPI; the owner sets the width.</summary>
public sealed class HouseHeading : Control
{
    private Font? _font;
    private int _dpi;

    public HouseHeading(string text)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        Text = text.ToUpperInvariant();
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = text;
        Rescale();
    }

    /// <summary>Re-derives the font and the height from the current DPI.</summary>
    public void Rescale()
    {
        if (_font != null && DeviceDpi == _dpi) return;
        _dpi = DeviceDpi;
        var old = _font;
        _font = Theme.Ui(Theme.PtLabel, HouseWeight.SemiBold, _dpi);   // private: never handed to Control.Font
        old?.Dispose();
        Height = _font.Height + Theme.Px(1, _dpi);
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); if (DeviceDpi != _dpi) Rescale(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Rescale(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnParentBackColorChanged(EventArgs e) { base.OnParentBackColorChanged(e); Invalidate(); }
    protected override void OnPaintBackground(PaintEventArgs pevent) { /* OnPaint covers every pixel */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(HouseDraw.EffectiveBack(this));
        if (_font == null) return;
        HouseDraw.DrawTracked(e.Graphics, Text, _font, PointF.Empty, Theme.Muted(Theme.CurrentDark),
                              Theme.Pxf(Theme.LabelTracking, DeviceDpi));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _font?.Dispose(); _font = null; }
    }
}

/// <summary>The house check box (the macOS Toggle in a settings form): a 16 px, radius-4 box — a sunken well with a
/// strong hairline when off, the accent with an OnAccent tick when on — and the label beside it, wrapping onto more
/// lines when the owner gives it less width than the text needs (<see cref="GetPreferredSize"/> honours a proposed
/// width). Still a <see cref="CheckBox"/>: Checked / CheckedChanged, Space, tab order and accessibility are unchanged.
/// A literal "&amp;" renders (no mnemonics).</summary>
public sealed class HouseCheckBox : CheckBox
{
    private bool _hover, _pressed;
    private int S(int v) => Theme.Px(v, DeviceDpi);
    private const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;

    public HouseCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        UseMnemonic = false;
        FlatStyle = FlatStyle.Flat;   // never System: that hands painting back to Windows
        AutoSize = false;             // the owner sizes it (GetPreferredSize wraps to a proposed width)
    }

    private int BoxSize => S(16);
    private int TextLeft => Padding.Left + BoxSize + S(8);

    /// <summary>Box + gap + text. A proposed width wraps the text to fit it (the Settings layout asks this way).</summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        int maxText = proposedSize.Width > 0 ? Math.Max(S(40), proposedSize.Width - TextLeft - Padding.Right) : int.MaxValue;
        var text = TextRenderer.MeasureText(Text, Font, new Size(maxText, int.MaxValue), Flags);
        return new Size(TextLeft + text.Width + Padding.Right,
                        Padding.Vertical + Math.Max(BoxSize + S(2), text.Height));
    }

    protected override void OnMouseEnter(EventArgs eventargs) { base.OnMouseEnter(eventargs); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs eventargs) { base.OnMouseLeave(eventargs); _hover = _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs mevent) { base.OnMouseDown(mevent); if (mevent.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs mevent) { base.OnMouseUp(mevent); if (_pressed) { _pressed = false; Invalidate(); } }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); _hover = _pressed = false; Invalidate(); }
    protected override void OnParentBackColorChanged(EventArgs e) { base.OnParentBackColorChanged(e); Invalidate(); }
    protected override void OnPaintBackground(PaintEventArgs pevent) { /* OnPaint covers every pixel */ }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        var back = HouseDraw.EffectiveBack(this);
        if (BackColor.A == 255) back = BackColor;
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool dark = Theme.CurrentDark, on = Enabled;

        // The box sits on the first text line's centre.
        int lineH = TextRenderer.MeasureText("Ag", Font, Size.Empty, Flags).Height;
        int box = BoxSize;
        var r = new RectangleF(Padding.Left + 0.5f, Padding.Top + Math.Max(0, (lineH - box) / 2) + 0.5f, box - 1f, box - 1f);
        float radius = Theme.Pxf(4f, DeviceDpi);
        Color fill, edge;
        if (Checked)
        {
            fill = _pressed ? Theme.Blend(Theme.Accent, back, 0.8) : _hover ? Theme.Blend(Theme.Fg(dark), Theme.Accent, 0.08) : Theme.Accent;
            edge = fill;
        }
        else
        {
            fill = _pressed ? Theme.Line(dark) : Theme.Sunken(dark);
            edge = _hover ? Theme.Blend(Theme.Fg(dark), Theme.LineStrong(dark), 0.25) : Theme.LineStrong(dark);
        }
        using (var path = HouseDraw.Rounded(r, radius))
        {
            using (var brush = new SolidBrush(HouseDraw.Fade(fill, back, on))) g.FillPath(brush, path);
            using (var pen = new Pen(HouseDraw.Fade(edge, back, on))) g.DrawPath(pen, path);
        }
        if (Checked)
        {
            using var tick = new Pen(HouseDraw.Fade(Theme.OnAccent, fill, on), Theme.Pxf(1.8f, DeviceDpi))
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(tick, new[]
            {
                new PointF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.52f),
                new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.70f),
                new PointF(r.X + r.Width * 0.76f, r.Y + r.Height * 0.32f),
            });
        }
        if (Focused && ShowFocusCues)
        {
            float w = Theme.Pxf(2f, DeviceDpi);
            var ring = RectangleF.Inflate(r, w * 1.5f, w * 1.5f);
            using var pen = new Pen(Theme.Accent, w);
            using var path = HouseDraw.Rounded(ring, radius + w * 1.5f);
            g.DrawPath(pen, path);
        }

        var textRect = new Rectangle(TextLeft, Padding.Top, Math.Max(0, Width - TextLeft - Padding.Right), Math.Max(0, Height - Padding.Vertical));
        TextRenderer.DrawText(g, Text, Font, textRect, HouseDraw.Fade(Theme.Fg(dark), back, on), back, Flags);
    }
}

/// <summary>The row / tile progress bar (macOS RowProgressBar): a 3 px capsule — the accent filling a Line-coloured
/// track from the leading edge — drawn here instead of the system ProgressBar (square, green, a different height per
/// Windows version). <see cref="Indeterminate"/> slides an accent segment along the track (verify / install phases);
/// its timer only runs while the bar is visible.</summary>
public sealed class HouseProgressBar : Control
{
    private double _value;
    private bool _indeterminate;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };

    public HouseProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
        _timer.Tick += (_, _) => Invalidate();
    }

    /// <summary>0–1 (clamped).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (Math.Abs(value - _value) < 0.0005) return;
            _value = value;
            AccessibleDescription = $"{Math.Round(_value * 100)}%";
            if (!_indeterminate) Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Indeterminate
    {
        get => _indeterminate;
        set { if (_indeterminate == value) return; _indeterminate = value; UpdateTimer(); Invalidate(); }
    }

    private void UpdateTimer() => _timer.Enabled = _indeterminate && Visible && IsHandleCreated;

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateTimer(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateTimer(); }
    protected override void OnHandleDestroyed(EventArgs e) { _timer.Enabled = false; base.OnHandleDestroyed(e); }
    protected override void OnParentBackColorChanged(EventArgs e) { base.OnParentBackColorChanged(e); Invalidate(); }
    protected override void OnPaintBackground(PaintEventArgs pevent) { /* OnPaint covers every pixel */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(HouseDraw.EffectiveBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool dark = Theme.CurrentDark;
        float h = Height, r = h / 2f;
        var track = new RectangleF(0, 0, Width, h);
        using var trackPath = HouseDraw.Rounded(track, r);
        using (var brush = new SolidBrush(Theme.Line(dark))) g.FillPath(brush, trackPath);

        RectangleF fill;
        if (_indeterminate)
        {
            // A segment a third of the track wide, crossing it every 1.4 s (clock-based, so a late tick doesn't stutter).
            float seg = Math.Max(h * 4, Width / 3f);
            float t = (Environment.TickCount64 % 1400) / 1400f;
            fill = new RectangleF(-seg + t * (Width + seg), 0, seg, h);
        }
        else
        {
            if (_value <= 0) return;
            fill = new RectangleF(0, 0, Math.Max(h, (float)(Width * _value)), h);
        }
        var saved = g.Save();   // (reading g.Clip would allocate a Region per paint — ~30/s while indeterminate)
        g.SetClip(trackPath, CombineMode.Intersect);
        using (var path = HouseDraw.Rounded(fill, r))
        using (var brush = new SolidBrush(Theme.Accent))
            g.FillPath(brush, path);
        g.Restore(saved);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>House pop-up menus: every ContextMenuStrip the launcher shows (the header's More and Download All menus,
/// a row's ⋯ / right-click menu, the notification-area menu) and their submenus are painted by
/// <see cref="HouseMenuRenderer"/> in the current theme, in Inter, with the macOS menus' calm spacing, small rounded
/// corners on Windows 11, and house tooltips for items that carry one. Menus are built fresh for each showing (the tray
/// menu on each Opening), so styling at build time always matches the theme in force.</summary>
internal static class HouseMenu
{
    private static readonly HouseMenuRenderer Renderer = new();
    // One menu font per DPI, shared by every menu for the life of the process (never disposed: menus built from it
    // may still be closing when another opens).
    private static readonly Dictionary<int, Font> Fonts = new();
    private static readonly ToolTip Tip = HouseTip.Create();

    public static Font FontFor(int dpi)
    {
        lock (Fonts)
        {
            if (!Fonts.TryGetValue(dpi, out var f)) Fonts[dpi] = f = Theme.Ui(Theme.PtBody, HouseWeight.Regular, dpi);
            return f;
        }
    }

    /// <summary>Styles <paramref name="menu"/> and every submenu in it for <paramref name="dpi"/> (the DPI of the
    /// control it opens from). Call after the items are added, before Show.</summary>
    public static void Apply(ToolStripDropDown menu, int dpi)
    {
        menu.Renderer = Renderer;
        menu.Font = FontFor(dpi);
        menu.BackColor = Theme.Surface(Theme.CurrentDark);
        menu.ForeColor = Theme.Fg(Theme.CurrentDark);
        menu.Padding = new Padding(0, Theme.Px(4, dpi), 0, Theme.Px(4, dpi));
        menu.ShowItemToolTips = false;   // the house tooltip instead (see ApplyItem)
        if (menu is ToolStripDropDownMenu m) { m.ShowImageMargin = true; m.ShowCheckMargin = false; }
        if (menu.Tag as string != "house-menu")
        {
            menu.Tag = "house-menu";
            menu.HandleCreated += (_, _) => HouseDraw.RoundPopup(menu.Handle, Theme.Line(Theme.CurrentDark));
            menu.Opened += (_, _) => HouseDraw.RoundPopup(menu.Handle, Theme.Line(Theme.CurrentDark));
            menu.Closed += (_, _) => Tip.Hide(menu);
        }
        foreach (ToolStripItem item in menu.Items) ApplyItem(item, dpi);
    }

    private static void ApplyItem(ToolStripItem item, int dpi)
    {
        if (item is ToolStripSeparator sep)
        {
            sep.AutoSize = false;
            sep.Height = Theme.Px(9, dpi);
            return;
        }
        item.Padding = new Padding(Theme.Px(2, dpi), Theme.Px(3, dpi), Theme.Px(10, dpi), Theme.Px(3, dpi));
        if (!string.IsNullOrEmpty(item.ToolTipText) && item.Tag as string != "house-tip")
        {
            item.Tag = "house-tip";
            item.MouseHover += (_, _) =>
            {
                if (item.Owner is not { } owner || string.IsNullOrEmpty(item.ToolTipText)) return;
                var at = owner.PointToClient(Cursor.Position);
                HouseTip.Show(Tip, item.ToolTipText, owner, new Point(at.X, at.Y + Theme.Px(20, dpi)));
            };
            item.MouseLeave += (_, _) => { if (item.Owner is { } owner) Tip.Hide(owner); };
        }
        if (item is ToolStripMenuItem mi && mi.HasDropDownItems) Apply(mi.DropDown, dpi);
    }
}

/// <summary>Paints a house pop-up menu: the Surface fill, a 1 px Line border, a rounded 6 px hover wash (a blend of the
/// text colour, like the house buttons), text in Fg (shortcuts in Sub, disabled items in Muted), an accent tick for a
/// checked item, Line hairline separators and a slate chevron for a submenu (rule 21). Reads the theme as it paints.</summary>
internal sealed class HouseMenuRenderer : ToolStripProfessionalRenderer
{
    private static bool Dark => Theme.CurrentDark;
    private static int Dpi(ToolStripItemRenderEventArgs e) => e.ToolStrip?.DeviceDpi ?? e.Item.Owner?.DeviceDpi ?? 96;

    public HouseMenuRenderer() { RoundedEdges = false; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Theme.Surface(Dark));

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Line(Dark));
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { /* no gutter stripe */ }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        if (!item.Enabled || !(item.Selected || item.Pressed)) return;
        int dpi = Dpi(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Inset from the menu's visible content edges (an item can run under the border on the right).
        float inset = Theme.Pxf(4f, dpi);
        var shown = e.ToolStrip?.DisplayRectangle ?? new Rectangle(item.Bounds.Left, 0, item.Width, item.Height);
        float left = shown.Left - item.Bounds.Left + inset, right = Math.Min(item.Width, shown.Right - item.Bounds.Left) - inset;
        var r = new RectangleF(left, Theme.Pxf(1f, dpi), right - left, item.Height - Theme.Pxf(2f, dpi));
        using var path = HouseDraw.Rounded(r, Theme.Pxf(Theme.RCtl, dpi));
        using var brush = new SolidBrush(Theme.Blend(Theme.Fg(Dark), Theme.Surface(Dark), 0.08));
        g.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        bool dark = Dark;
        bool shortcut = e.Item is ToolStripMenuItem mi && !string.IsNullOrEmpty(e.Text) && e.Text != mi.Text;
        var color = !e.Item.Enabled ? Theme.Muted(dark) : shortcut ? Theme.Sub(dark) : Theme.Fg(dark);
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, e.TextFormat | TextFormatFlags.NoPrefix);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        int dpi = Dpi(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        float s = Theme.Pxf(12f, dpi), cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        var color = e.Item.Enabled ? Theme.Accent : Theme.Muted(Dark);
        using var pen = new Pen(color, Theme.Pxf(1.8f, dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(cx - s * 0.40f, cy + s * 0.02f),
            new PointF(cx - s * 0.12f, cy + s * 0.30f),
            new PointF(cx + s * 0.42f, cy - s * 0.30f),
        });
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        int dpi = e.Item?.Owner?.DeviceDpi ?? 96;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ArrowRectangle;
        float w = Theme.Pxf(8f, dpi), cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        var color = e.Item?.Enabled == false ? Theme.Muted(Dark) : Theme.Selector;
        using var pen = new Pen(color, Theme.Pxf(1.6f, dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(cx - w / 4, cy - w / 2), new PointF(cx + w / 4, cy), new PointF(cx - w / 4, cy + w / 2) });
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int dpi = e.ToolStrip?.DeviceDpi ?? 96;
        int y = e.Item.Height / 2, inset = Theme.Px(10, dpi);
        using var pen = new Pen(Theme.Line(Dark));
        e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
    }
}

/// <summary>House tooltips: owner-drawn in the Raised token with a 1 px Line border, Fg text in Inter 12 px and a small
/// padding, small rounded corners on Windows 11 — dark in dark mode (the stock tooltip is always the light system one).
/// Theme tokens are read as each tooltip paints.</summary>
internal static class HouseTip
{
    private const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
    private static readonly Dictionary<int, Font> Fonts = new();
    // Text handed to Show(): the Popup event (which sizes the window) can't see it, only GetToolTip's text.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolTip, string> Pending = new();

    private static Font FontFor(int dpi)
    {
        lock (Fonts)
        {
            if (!Fonts.TryGetValue(dpi, out var f)) Fonts[dpi] = f = Theme.Ui(Theme.PtSmall, HouseWeight.Regular, dpi);
            return f;
        }
    }

    private static int DpiOf(Control? c, IWin32Window? w) => c?.DeviceDpi ?? (w as Control)?.DeviceDpi ?? 96;

    public static ToolTip Create()
    {
        var tip = new ToolTip();
        Style(tip);
        return tip;
    }

    /// <summary>Shows <paramref name="text"/> at <paramref name="point"/> (client coordinates of
    /// <paramref name="window"/>), for tooltips the launcher raises itself (menu items).</summary>
    public static void Show(ToolTip tip, string text, IWin32Window window, Point point)
    {
        Pending.AddOrUpdate(tip, text);
        tip.Show(text, window, point, 8000);
    }

    public static void Style(ToolTip tip)
    {
        tip.OwnerDraw = true;
        tip.Popup += (_, e) =>
        {
            var text = e.AssociatedControl is { } c ? tip.GetToolTip(c) : null;
            if (string.IsNullOrEmpty(text)) Pending.TryGetValue(tip, out text);
            if (string.IsNullOrEmpty(text)) return;
            int dpi = DpiOf(e.AssociatedControl, e.AssociatedWindow);
            var size = TextRenderer.MeasureText(text, FontFor(dpi), new Size(Theme.Px(360, dpi), int.MaxValue), Flags);
            e.ToolTipSize = new Size(size.Width + 2 * Theme.Px(8, dpi), size.Height + 2 * Theme.Px(5, dpi));
        };
        tip.Draw += (_, e) =>
        {
            bool dark = Theme.CurrentDark;
            int dpi = DpiOf(e.AssociatedControl, e.AssociatedWindow);
            var g = e.Graphics;
            using (var fill = new SolidBrush(Theme.Raised(dark))) g.FillRectangle(fill, e.Bounds);
            using (var pen = new Pen(Theme.Line(dark))) g.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            var text = Rectangle.Inflate(e.Bounds, -Theme.Px(8, dpi), -Theme.Px(5, dpi));
            TextRenderer.DrawText(g, e.ToolTipText, FontFor(dpi), text, Theme.Fg(dark), Theme.Raised(dark), Flags);
            RoundOnce(g, dark);
        };
    }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromDC(IntPtr hdc);

    /// <summary>Windows 11's small rounded corners + a Line-coloured DWM border on the tooltip window (found from the
    /// DC it paints into). Cheap to repeat; the border colour follows the theme.</summary>
    private static void RoundOnce(Graphics g, bool dark)
    {
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            var hdc = g.GetHdc();
            try { hwnd = WindowFromDC(hdc); } finally { g.ReleaseHdc(hdc); }
        }
        catch { return; }
        HouseDraw.RoundPopup(hwnd, Theme.Line(dark));
    }
}
