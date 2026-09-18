using System.Runtime.InteropServices;

namespace JBTheatreTools;

/// <summary>
/// House Style v2 design tokens (kit v2.0.0) + light/dark theming helpers, at parity with the macOS
/// build's <c>Theme.swift</c>. The suite's shared kit ships as CSS custom properties; the native apps
/// carry the same numbers by hand. This is a token layer only — it never decides layout.
/// </summary>
public static class Theme
{
    public static bool IsDark(string appearance)
    {
        if (appearance == "dark") return true;
        if (appearance == "light") return false;
        return SystemPrefersDark();
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { /* default to light */ }
        return false;
    }

    /// <summary>The theme in force, published by <c>MainForm.ApplyTheme</c> so the token accessors that
    /// can't take a parameter (static painters, per-control paint handlers) resolve the right set.</summary>
    public static bool CurrentDark { get; private set; }

    public static void SetCurrent(bool dark) => CurrentDark = dark;

    private static Color C(int rgb) => Color.FromArgb(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF);

    // ── neutrals (graphite) ───────────────────────────────────────────────────────────────────────
    /// <summary>Window background.</summary>
    public static Color Ground(bool dark) => dark ? C(0x141619) : C(0xF3F4F6);
    /// <summary>Panel / card / row surface.</summary>
    public static Color Surface(bool dark) => dark ? C(0x1B1E23) : C(0xFFFFFF);
    /// <summary>A step up from the surface — hovered rows, grid tiles.</summary>
    public static Color Raised(bool dark) => dark ? C(0x22262C) : C(0xF8F9FB);
    /// <summary>A step down — wells and troughs.</summary>
    public static Color Sunken(bool dark) => dark ? C(0x101215) : C(0xECEEF1);
    /// <summary>Hairline separation (v2 separates with hairlines, not card shadows).</summary>
    public static Color Line(bool dark) => dark ? C(0x2C3138) : C(0xDCDFE4);
    /// <summary>A stronger hairline — control borders.</summary>
    public static Color LineStrong(bool dark) => dark ? C(0x3A4048) : C(0xC6CAD2);

    // ── text ──────────────────────────────────────────────────────────────────────────────────────
    public static Color Fg(bool dark) => dark ? C(0xE8EAEE) : C(0x16181C);
    public static Color Sub(bool dark) => dark ? C(0x9AA1AB) : C(0x5F6670);
    /// <summary>Tertiary text — meta lines, panel headings, the credit line.</summary>
    public static Color Muted(bool dark) => dark ? C(0x6C737D) : C(0x8B929C);

    // ── identity + semantics ─────────────────────────────────────────────────────────────────────
    /// <summary>Suite accent: purple #AF52DE light / #C77BF0 dark. Reserved for identity, the primary
    /// action, active state and focus (house rule 21).</summary>
    public static Color Accent => CurrentDark ? C(0xC77BF0) : C(0xAF52DE);
    /// <summary>Foreground on a filled accent surface.</summary>
    public static Color OnAccent => CurrentDark ? C(0x0B0F1E) : C(0xFFFFFF);
    /// <summary>House "selector" slate #6E8299 — dropdowns, chevrons, overflow menus, grips (rule 21),
    /// so the accent is never spent on a selector. Identical in both themes.</summary>
    public static Color Selector => C(0x6E8299);

    // Semantic set (rule 25) — orange warn, never yellow.
    public static Color Ok => CurrentDark ? C(0x3FB950) : C(0x1F9D4C);
    public static Color Warn => CurrentDark ? C(0xF0883E) : C(0xC2610B);
    public static Color Danger => CurrentDark ? C(0xF85149) : C(0xD3312B);
    public static Color Info => CurrentDark ? C(0x58A6FF) : C(0x1F6FD6);

    // ── derived ───────────────────────────────────────────────────────────────────────────────────
    /// <summary>Window background (alias kept for call-site readability).</summary>
    public static Color Bg(bool dark) => Ground(dark);
    /// <summary>A row/tile surface.</summary>
    public static Color Card(bool dark) => Surface(dark);
    /// <summary>Fill behind a hovered / lifted row: a sunken step in light, a raised step in dark.</summary>
    public static Color CardHover(bool dark) => dark ? Raised(true) : Sunken(false);

    /// <summary>Composites <paramref name="fg"/> over <paramref name="bg"/> at <paramref name="amount"/>.
    /// WinForms panels don't alpha-composite reliably against a parent, so tints are pre-blended.</summary>
    public static Color Blend(Color fg, Color bg, double amount) => Color.FromArgb(
        (int)Math.Round(fg.R * amount + bg.R * (1 - amount)),
        (int)Math.Round(fg.G * amount + bg.G * (1 - amount)),
        (int)Math.Round(fg.B * amount + bg.B * (1 - amount)));

    /// <summary>The kit's `.banner` wash: 10% of the semantic colour over the panel surface.</summary>
    public static Color BannerBack(Color tint, bool dark) => Blend(tint, Surface(dark), 0.10);

    // ── radius by role ────────────────────────────────────────────────────────────────────────────
    /// <summary>Controls.</summary>
    public const int RCtl = 6;
    /// <summary>Panels, cards, tiles, drag cards.</summary>
    public const int RPanel = 9;
    /// <summary>Window-scale surfaces (the OS owns the real window corner).</summary>
    public const int RWin = 12;

    // ── the six-step type scale ──────────────────────────────────────────────────────────────────
    // The kit's steps are in CSS pixels; WinForms takes points (pt = px × 0.75). The system font stays
    // (Inter is a web-kit asset, not bundled natively). Weights are 400 / 500 / 600 — never 700, so a
    // "semibold" step uses the real Segoe UI Semibold family where present rather than faux-bold.
    /// <summary>t-title / t-status — 15px.</summary>
    public const float PtTitle = 11.25f;
    /// <summary>body — 13px.</summary>
    public const float PtBody = 9.75f;
    /// <summary>t-small — 12px.</summary>
    public const float PtSmall = 9f;
    /// <summary>t-label — 10.5px (uppercase, tracked).</summary>
    public const float PtLabel = 7.9f;

    private static readonly FontFamily? SemiBold = ResolveSemiBold();

    private static FontFamily? ResolveSemiBold()
    {
        try { return new FontFamily("Segoe UI Semibold"); }
        catch { return null; }   // not installed (or a non-Windows build host) — fall back below
    }

    /// <summary>A font from the house scale. <paramref name="semibold"/> picks weight 600 via the real
    /// Segoe UI Semibold family, falling back to synthetic bold only where that family is absent.
    /// With <paramref name="dpi"/> the font is built in DEVICE PIXELS for that DPI (pt × dpi / 72), so it
    /// renders at the right size on whichever monitor its control is on and can be rebuilt idempotently
    /// on a DPI change. Without it the font is in points, which GDI sizes at the SYSTEM DPI — right on the
    /// launch monitor only (fine for the one-shot Settings dialog).</summary>
    public static Font Ui(float pt, bool semibold = false, int dpi = 0)
    {
        float size = dpi > 0 ? pt * dpi / 72f : pt;
        var unit = dpi > 0 ? GraphicsUnit.Pixel : GraphicsUnit.Point;
        if (semibold && SemiBold != null)
        {
            try { return new Font(SemiBold, size, FontStyle.Regular, unit); } catch { /* fall through */ }
        }
        return new Font("Segoe UI", size, semibold ? FontStyle.Bold : FontStyle.Regular, unit);
    }

    // ── DPI ───────────────────────────────────────────────────────────────────────────────────────
    // Every hard-coded layout number in the WinForms UI is a 96-DPI design value — the same number the
    // macOS build uses in points — passed through Px() at the owning control's DeviceDpi before use. The
    // process is Per-Monitor V2 aware (csproj <ApplicationHighDpiMode>), so DeviceDpi is the DPI of the
    // monitor the window is on and can change at runtime; the custom controls re-derive geometry + fonts
    // from it (their Rescale) instead of leaning on the framework's one-shot AutoScale pass, which never
    // reaches controls created later (rows, section headers) or the layout code that runs on resize.

    /// <summary>Scales a 96-DPI design value to device pixels at <paramref name="dpi"/>.</summary>
    public static int Px(int v, int dpi) => (int)Math.Round(v * dpi / 96.0);
    /// <summary>Float variant, for pen widths and sub-pixel glyph geometry.</summary>
    public static float Pxf(float v, int dpi) => v * dpi / 96f;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark-mode title bar via DWMWA_USE_IMMERSIVE_DARK_MODE (attr 20).</summary>
    public static void ApplyTitleBar(Form form, bool dark)
    {
        try
        {
            int v = dark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, 20, ref v, sizeof(int));
        }
        catch { /* non-fatal on older Windows */ }
    }
}
