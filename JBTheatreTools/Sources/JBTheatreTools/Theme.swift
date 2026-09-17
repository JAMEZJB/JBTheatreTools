import SwiftUI
import AppKit

/// House Style v2 design tokens (kit v2.0.0) for the native macOS launcher.
///
/// The suite's shared kit ships as CSS custom properties; the native apps carry the same values by hand
/// (there is no stylesheet to load). Every colour is a *dynamic* `NSColor`, so it resolves against the
/// window's effective appearance — which the "Appearance" setting (System / Light / Dark) drives through
/// `.preferredColorScheme`. Nothing here decides layout: this is a token layer only.
///
/// Parity note: `JBTheatreToolsWin/Theme.cs` carries the identical numbers for the WinForms build.
enum JBTokens {
    /// Builds a colour that resolves light/dark from the drawing appearance.
    static func dynamic(light: UInt32, dark: UInt32) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? ns(dark) : ns(light)
        })
    }

    /// A colour that is the same in both themes (the shared slate selector).
    static func fixed(_ hex: UInt32) -> Color { Color(nsColor: ns(hex)) }

    private static func ns(_ hex: UInt32) -> NSColor {
        NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255,
                green: CGFloat((hex >> 8) & 0xFF) / 255,
                blue: CGFloat(hex & 0xFF) / 255,
                alpha: 1)
    }
}

extension Color {
    // ── neutrals (graphite) ────────────────────────────────────────────────────────────────────────
    /// Window background.
    static let jbGround = JBTokens.dynamic(light: 0xF3F4F6, dark: 0x141619)
    /// Panel / card / row surface.
    static let jbSurface = JBTokens.dynamic(light: 0xFFFFFF, dark: 0x1B1E23)
    /// A step up from the surface — secondary buttons, grid tiles.
    static let jbRaised = JBTokens.dynamic(light: 0xF8F9FB, dark: 0x22262C)
    /// A step down — wells, segmented-control troughs.
    static let jbSunken = JBTokens.dynamic(light: 0xECEEF1, dark: 0x101215)
    /// Hairline separation (rule: hairlines, not card shadows).
    static let jbLine = JBTokens.dynamic(light: 0xDCDFE4, dark: 0x2C3138)
    /// A stronger hairline — control borders, hovered tiles.
    static let jbLineStrong = JBTokens.dynamic(light: 0xC6CAD2, dark: 0x3A4048)

    // ── text ───────────────────────────────────────────────────────────────────────────────────────
    static let jbText = JBTokens.dynamic(light: 0x16181C, dark: 0xE8EAEE)
    static let jbText2 = JBTokens.dynamic(light: 0x5F6670, dark: 0x9AA1AB)
    static let jbText3 = JBTokens.dynamic(light: 0x8B929C, dark: 0x6C737D)

    // ── identity + semantics ──────────────────────────────────────────────────────────────────────
    /// Suite accent: purple #AF52DE light / #C77BF0 dark. Reserved for identity, the primary action,
    /// active state and focus (house rule 21).
    static let jbAccent = JBTokens.dynamic(light: 0xAF52DE, dark: 0xC77BF0)
    /// Foreground on a filled accent surface.
    static let jbOnAccent = JBTokens.dynamic(light: 0xFFFFFF, dark: 0x0B0F1E)
    /// Shared house "selector" slate #6E8299 — dropdowns, chevrons, overflow menus, grips (rule 21),
    /// so the accent is never spent on a selector. Identical in both themes.
    static let selectorBlue = JBTokens.fixed(0x6E8299)
    /// Semantic set (rule 25) — orange warn, never yellow.
    static let jbOk = JBTokens.dynamic(light: 0x1F9D4C, dark: 0x3FB950)
    static let jbWarn = JBTokens.dynamic(light: 0xC2610B, dark: 0xF0883E)
    static let jbDanger = JBTokens.dynamic(light: 0xD3312B, dark: 0xF85149)
    static let jbInfo = JBTokens.dynamic(light: 0x1F6FD6, dark: 0x58A6FF)

    // ── derived ────────────────────────────────────────────────────────────────────────────────────
    /// Fill behind a hovered row: a sunken step in light, a raised step in dark.
    static let jbRowHover = JBTokens.dynamic(light: 0xECEEF1, dark: 0x22262C)
    /// Hairline separator drawn between rows (alias of `jbLine`, kept for call-site readability).
    static var jbHairline: Color { .jbLine }
}

/// Radius by role (6 controls · 9 panels · 12 windows). The window's own corner radius is owned by
/// macOS, so `win` applies to app-drawn window-scale surfaces only.
enum JBRadius {
    static let ctl: CGFloat = 6
    static let panel: CGFloat = 9
    static let win: CGFloat = 12
}

extension View {
    /// The kit's `.banner` treatment for a full-bleed notice band: a 10% wash of the semantic colour over
    /// the panel surface, closed with a 40% hairline instead of a shadow.
    func bannerTint(_ tint: Color) -> some View {
        self
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(tint.opacity(0.10).background(Color.jbSurface))
            .overlay(alignment: .bottom) { Rectangle().fill(tint.opacity(0.40)).frame(height: 1) }
    }
}

/// The six-step house type scale, in the system font (Inter is a web-kit asset and is NOT bundled
/// natively). Weights are limited to 400 / 500 / 600 — never 700.
enum JBFont {
    /// 40 · mono · tabular — big single readouts. (No readout in the launcher; kept for scale parity.)
    static let hero = Font.system(size: 40, weight: .medium, design: .monospaced)
    /// 15/600 — the status line.
    static let status = Font.system(size: 15, weight: .semibold)
    /// 15/600 — view + dialog titles.
    static let title = Font.system(size: 15, weight: .semibold)
    /// 13/400 — body.
    static let body = Font.system(size: 13, weight: .regular)
    /// 13/500 — body, emphasised.
    static let bodyMedium = Font.system(size: 13, weight: .medium)
    /// 13/600 — a list row's primary name.
    static let bodyStrong = Font.system(size: 13, weight: .semibold)
    /// 12/400 — secondary prose.
    static let small = Font.system(size: 12, weight: .regular)
    /// 12/600 — secondary prose, emphasised.
    static let smallStrong = Font.system(size: 12, weight: .semibold)
    /// 10.5/600 — the caps micro-label (section headings, meta lines, badges).
    static let label = Font.system(size: 10.5, weight: .semibold)
    /// 10.5/400 — the same step, unemphasised (version meta).
    static let labelRegular = Font.system(size: 10.5, weight: .regular)
    /// Letter-spacing that goes with `label` when the text is uppercased.
    static let labelTracking: CGFloat = 0.8
}
