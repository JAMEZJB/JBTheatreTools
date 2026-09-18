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

// MARK: - Controls drawn in SwiftUI (no AppKit cells)
//
// Every AppKit-backed control hosted inside a list row (NSButton, NSPopUpButton, NSSegmentedControl) is
// re-measured through CoreUI on EVERY list-level layout pass — about a millisecond per cell, times ~3 cells
// per row, times 21 rows: 50–90 ms of main-thread stall whenever a row appears, an install finishes, a
// section collapses or a drag moves a row. These controls draw with the kit's own tokens instead (flat
// accent primary · raised secondary with a hairline · bare selector-blue icon · sunken segmented trough),
// so SwiftUI lays them out like any Text and a full-list pass costs a few milliseconds.

/// House-kit button style. `compact` follows `.controlSize(.small/.mini)` automatically (row buttons).
struct JBButtonStyle: ButtonStyle {
    enum Role { case primary, secondary, icon }
    var role: Role

    @Environment(\.isEnabled) private var enabled
    @Environment(\.controlSize) private var controlSize

    func makeBody(configuration: Configuration) -> some View {
        JBButtonBody(label: configuration.label, pressed: configuration.isPressed, role: role,
                     enabled: enabled, compact: controlSize == .small || controlSize == .mini)
    }
}

private struct JBButtonBody: View {
    let label: ButtonStyleConfiguration.Label
    let pressed: Bool
    let role: JBButtonStyle.Role
    let enabled: Bool
    let compact: Bool
    @State private var hovering = false

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: JBRadius.ctl, style: .continuous)
        label
            .font(.system(size: compact ? 11 : 13, weight: .medium))
            .lineLimit(1)
            .foregroundStyle(foreground)
            .padding(.horizontal, role == .icon ? 2 : (compact ? 9 : 12))
            .padding(.vertical, role == .icon ? 2 : (compact ? 3 : 5))
            .frame(minHeight: role == .icon ? 0 : (compact ? 20 : 26))
            .background(shape.fill(fill))   // the fill is also the hit surface — no separate contentShape view
            .overlay { if role == .secondary { shape.strokeBorder(border) } }
            .opacity(enabled ? 1 : 0.45)
            .onHover { hovering = $0 }
    }

    private var foreground: Color {
        switch role {
        case .primary:   return .jbOnAccent
        case .secondary: return .jbText
        case .icon:      return .selectorBlue
        }
    }
    private var fill: Color {
        switch role {
        case .primary:   return pressed ? Color.jbAccent.opacity(0.8) : .jbAccent
        case .secondary: return pressed ? .jbSunken : (hovering ? .jbSurface : .jbRaised)
        case .icon:      return pressed ? Color.selectorBlue.opacity(0.18) : (hovering ? Color.selectorBlue.opacity(0.10) : .clear)
        }
    }
    private var border: Color { hovering ? .jbLineStrong : .jbLine }
}

extension ButtonStyle where Self == JBButtonStyle {
    /// The accent-filled primary action (Install / Update / Download All).
    static var jbPrimary: JBButtonStyle { JBButtonStyle(role: .primary) }
    /// A raised-surface secondary action with a hairline (Launch / Refresh / Settings).
    static var jbSecondary: JBButtonStyle { JBButtonStyle(role: .secondary) }
    /// A bare selector-blue icon (the row's ⋯ menu, per house rule 21).
    static var jbIcon: JBButtonStyle { JBButtonStyle(role: .icon) }
}

/// A segmented "pick one" control drawn in SwiftUI: a sunken trough with the selected segment lifted to the
/// surface. Segments carry a text label, a symbol, or both.
struct JBSegmented<ID: Hashable>: View {
    struct Segment: Identifiable {
        let id: ID
        var label: String? = nil
        var symbol: String? = nil
        var help: String? = nil
    }
    let segments: [Segment]
    @Binding var selection: ID
    var compact = false

    @Environment(\.isEnabled) private var enabled

    var body: some View {
        HStack(spacing: 2) {
            ForEach(segments) { seg in
                let selected = seg.id == selection
                HStack(spacing: 3) {
                    if let s = seg.symbol { Image(systemName: s).font(.system(size: compact ? 9 : 11, weight: .medium)) }
                    if let l = seg.label { Text(l).font(.system(size: compact ? 10 : 11, weight: .medium)) }
                }
                .foregroundStyle(selected ? Color.jbText : Color.jbText2)
                .padding(.horizontal, compact ? 7 : 9)
                .padding(.vertical, compact ? 2 : 3)
                .background(
                    RoundedRectangle(cornerRadius: JBRadius.ctl - 2, style: .continuous)
                        .fill(selected ? Color.jbSurface : Color.clear)
                )
                .overlay(
                    RoundedRectangle(cornerRadius: JBRadius.ctl - 2, style: .continuous)
                        .strokeBorder(selected ? Color.jbLineStrong : Color.clear)
                )
                .contentShape(Rectangle())
                .onTapGesture { if !selected { selection = seg.id } }
                .help(seg.help ?? "")
            }
        }
        .padding(2)
        .background(RoundedRectangle(cornerRadius: JBRadius.ctl, style: .continuous).fill(Color.jbSunken))
        .overlay(RoundedRectangle(cornerRadius: JBRadius.ctl, style: .continuous).strokeBorder(Color.jbLine))
        .opacity(enabled ? 1 : 0.45)
        .allowsHitTesting(enabled)
    }
}

/// House-kit chrome for a `Menu`: a `ButtonStyle` never reaches a menu's popup button on macOS, so the
/// menu is drawn borderless (label only, no indicator) and the pill is applied around it here.
struct JBMenuPill: ViewModifier {
    let role: JBButtonStyle.Role
    @Environment(\.isEnabled) private var enabled
    @Environment(\.controlSize) private var controlSize
    @State private var hovering = false

    func body(content: Content) -> some View {
        let compact = controlSize == .small || controlSize == .mini
        let shape = RoundedRectangle(cornerRadius: JBRadius.ctl, style: .continuous)
        content
            .menuStyle(.borderlessButton)
            .menuIndicator(role == .icon ? .hidden : .visible)
            .font(.system(size: compact ? 11 : 13, weight: .medium))
            // The borderless popup draws its label (and chevron) in the TINT, not the foreground style — and
            // the window's tint is the accent, which would vanish on the primary pill's accent fill.
            .tint(labelColor)
            .foregroundStyle(labelColor)
            .padding(.horizontal, role == .icon ? 2 : (compact ? 9 : 12))
            .padding(.vertical, role == .icon ? 2 : (compact ? 3 : 5))
            .frame(minHeight: role == .icon ? 0 : (compact ? 20 : 26))
            .background(shape.fill(fill))
            .overlay { if role == .secondary { shape.strokeBorder(hovering ? Color.jbLineStrong : Color.jbLine) } }
            .opacity(enabled ? 1 : 0.45)
            .onHover { hovering = $0 }
    }

    private var labelColor: Color {
        switch role {
        case .primary:   return .jbOnAccent
        case .secondary: return .jbText
        case .icon:      return .selectorBlue
        }
    }
    private var fill: Color {
        switch role {
        case .primary:   return .jbAccent
        case .secondary: return hovering ? .jbSurface : .jbRaised
        case .icon:      return hovering ? Color.selectorBlue.opacity(0.10) : .clear
        }
    }
}

extension View {
    func jbMenuPill(_ role: JBButtonStyle.Role) -> some View { modifier(JBMenuPill(role: role)) }
}
