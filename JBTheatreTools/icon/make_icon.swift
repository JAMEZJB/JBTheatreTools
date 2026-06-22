// Generates icon_1024.png — the master art for JB Theatre Tools' app icon.
// Family style (dark slate "squircle", glossy centre, heavy wordmark) themed for the launcher:
// a theatre spotlight shining down onto a glossy 2×2 grid of app "tiles" (the suite it installs),
// over a "JB TOOLS" wordmark.
// Run:  swift make_icon.swift   (then sips/iconutil -> AppIcon.icns; PIL -> app.ico — see make.sh)
import AppKit

let SIZE = 1024
let W = CGFloat(SIZE)

let rep = NSBitmapImageRep(
    bitmapDataPlanes: nil, pixelsWide: SIZE, pixelsHigh: SIZE,
    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
    colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
let ctx = NSGraphicsContext.current!.cgContext

let inset: CGFloat = 100
let r = NSRect(x: inset, y: inset, width: W - inset * 2, height: W - inset * 2)
let radius = r.width * 0.2237

func clipSquircle() { NSBezierPath(roundedRect: r, xRadius: radius, yRadius: radius).addClip() }

// --- squircle background (identical family slate gradient) ---
let bgPath = NSBezierPath(roundedRect: r, xRadius: radius, yRadius: radius)
bgPath.addClip()
NSGradient(colors: [NSColor(red: 0.17, green: 0.19, blue: 0.23, alpha: 1),
                    NSColor(red: 0.07, green: 0.08, blue: 0.10, alpha: 1)])!.draw(in: r, angle: -90)
ctx.resetClip(); clipSquircle()

// --- spotlight beam from a lamp at top centre ---
let apex = NSPoint(x: r.midX, y: r.maxY - 30)
let beam = NSBezierPath()
beam.move(to: apex)
beam.line(to: NSPoint(x: r.midX - 340, y: r.minY + 250))
beam.line(to: NSPoint(x: r.midX + 340, y: r.minY + 250))
beam.close()
NSGraphicsContext.saveGraphicsState()
beam.addClip()
NSGradient(colors: [NSColor(red: 1, green: 0.96, blue: 0.80, alpha: 0.42),
                    NSColor(red: 1, green: 0.96, blue: 0.80, alpha: 0.0)])!
    .draw(in: NSRect(x: r.minX, y: r.minY + 230, width: r.width, height: r.height), angle: -90)
NSGraphicsContext.restoreGraphicsState()

// --- soft warm glow behind the grid (tiles read as lit) ---
NSGradient(colors: [NSColor(red: 1, green: 0.95, blue: 0.82, alpha: 0.22),
                    NSColor(red: 1, green: 0.95, blue: 0.82, alpha: 0)])!
    .draw(in: NSBezierPath(ovalIn: NSRect(x: r.midX - 300, y: 430, width: 600, height: 480)),
          relativeCenterPosition: .zero)

// --- stage-floor light pool under the grid ---
NSGradient(colors: [NSColor(red: 1, green: 0.96, blue: 0.82, alpha: 0.5),
                    NSColor(red: 1, green: 0.96, blue: 0.82, alpha: 0)])!
    .draw(in: NSBezierPath(ovalIn: NSRect(x: r.midX - 280, y: 320, width: 560, height: 140)),
          relativeCenterPosition: .zero)

// --- glossy app tiles (the modern motif) ---
func drawTile(center: NSPoint, color: NSColor, s: CGFloat) {
    let rect = NSRect(x: center.x - s / 2, y: center.y - s / 2, width: s, height: s)
    let rad = s * 0.24
    let path = NSBezierPath(roundedRect: rect, xRadius: rad, yRadius: rad)

    NSGraphicsContext.saveGraphicsState()
    ctx.setShadow(offset: CGSize(width: 0, height: -8), blur: 22, color: NSColor(white: 0, alpha: 0.45).cgColor)
    color.setFill(); path.fill()
    NSGraphicsContext.restoreGraphicsState()

    NSGraphicsContext.saveGraphicsState()
    path.addClip()
    NSGradient(colors: [NSColor(white: 1, alpha: 0.32), NSColor(white: 1, alpha: 0)])!
        .draw(in: NSRect(x: rect.minX, y: rect.midY, width: rect.width, height: rect.height / 2), angle: -90)
    NSGradient(colors: [NSColor(white: 0, alpha: 0), NSColor(white: 0, alpha: 0.12)])!
        .draw(in: NSRect(x: rect.minX, y: rect.minY, width: rect.width, height: rect.height / 2), angle: -90)
    NSGraphicsContext.restoreGraphicsState()

    NSColor(white: 1, alpha: 0.18).setStroke()
    let o = NSBezierPath(roundedRect: rect.insetBy(dx: 1.5, dy: 1.5), xRadius: rad, yRadius: rad)
    o.lineWidth = 2.5; o.stroke()
}

let green = NSColor(red: 0.27, green: 0.85, blue: 0.42, alpha: 1)
let blue = NSColor(red: 0.30, green: 0.62, blue: 0.95, alpha: 1)
let amber = NSColor(red: 0.98, green: 0.72, blue: 0.20, alpha: 1)
let violet = NSColor(red: 0.686, green: 0.322, blue: 0.871, alpha: 1)   // suite accent #AF52DE

let gc = NSPoint(x: r.midX, y: 560)
let off: CGFloat = 96
let ts: CGFloat = 158
drawTile(center: NSPoint(x: gc.x - off, y: gc.y + off), color: green, s: ts)
drawTile(center: NSPoint(x: gc.x + off, y: gc.y + off), color: blue, s: ts)
drawTile(center: NSPoint(x: gc.x - off, y: gc.y - off), color: amber, s: ts)
drawTile(center: NSPoint(x: gc.x + off, y: gc.y - off), color: violet, s: ts)

// --- lamp glow at the apex ---
NSGradient(colors: [NSColor(white: 1, alpha: 0.9), NSColor(red: 1, green: 0.9, blue: 0.65, alpha: 0)])!
    .draw(in: NSBezierPath(ovalIn: NSRect(x: apex.x - 40, y: apex.y - 40, width: 80, height: 80)),
          relativeCenterPosition: .zero)

// --- "JB TOOLS" wordmark ---
let para = NSMutableParagraphStyle(); para.alignment = .center
let attr = NSAttributedString(string: "JB TOOLS", attributes: [
    .font: NSFont.systemFont(ofSize: 118, weight: .heavy),
    .foregroundColor: NSColor.white, .paragraphStyle: para, .kern: 4,
])
let sz = attr.size()
attr.draw(in: NSRect(x: r.minX, y: 232 - sz.height / 2, width: r.width, height: sz.height))

// --- crisp edge ---
ctx.resetClip()
NSColor(white: 1, alpha: 0.06).setStroke()
let bb = NSBezierPath(roundedRect: r.insetBy(dx: 2, dy: 2), xRadius: radius, yRadius: radius)
bb.lineWidth = 3; bb.stroke()

NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "icon_1024.png"))
print("Wrote icon_1024.png")
