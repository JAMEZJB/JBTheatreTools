import Foundation
import AppKit

// Custom entry point: if a recognised `--…` command appears ANYWHERE in the args, run the headless
// CLI and exit; otherwise the SwiftUI app launches. (Checking only args[0] missed invocations like
// `--token X --install helo`, which then wrongly opened the GUI — see THEATRE-01.) We match on a real
// verb rather than "any dash-arg" so OS-supplied launch flags don't suppress the GUI.
let arguments = Array(CommandLine.arguments.dropFirst())

if arguments.contains(where: { CLI.commands.contains($0) }) {
    CLI.run(args: arguments)
    exit(0)
}

LoopWatch.start()
JBTheatreToolsApp.main()

/// Dev harness only (`JBTT_LOOP_WATCH=1`): reports every main run-loop turn longer than 8 ms to stderr.
/// A turn is one wake → sleep cycle; anything over ~16 ms drops a frame, so this is the direct measure of
/// "does it feel laggy" that an aggregate CPU sample can't give. Inert unless the variable is set.
enum LoopWatch {
    private static var benchActive = false
    private static var benchTurns: [Double] = []
    private static var enabled = false
    private static var launchTime: CFAbsoluteTime = 0
    /// Dev marker: prints `[mark] +t label` to stderr when the watchdog is on, else nothing.
    static func mark(_ label: @autoclosure () -> String) {
        guard enabled else { return }
        fputs(String(format: "[mark] +%5.3fs %@\n", CFAbsoluteTimeGetCurrent() - launchTime, label()), stderr)
    }
    static func start() {
        guard ProcessInfo.processInfo.environment["JBTT_LOOP_WATCH"] == "1" else { return }
        let launch = CFAbsoluteTimeGetCurrent()
        enabled = true; launchTime = launch
        setvbuf(stdout, nil, _IONBF, 0)   // `_printChanges()` prints via stdout; unbuffer so a kill doesn't lose it
        var turnStart = launch
        let activities = CFRunLoopActivity.afterWaiting.rawValue | CFRunLoopActivity.beforeWaiting.rawValue
        let observer = CFRunLoopObserverCreateWithHandler(nil, activities, true, 0) { _, activity in
            let now = CFAbsoluteTimeGetCurrent()
            if activity == .afterWaiting { turnStart = now; return }
            let ms = (now - turnStart) * 1000
            if benchActive { benchTurns.append(ms); return }
            if ms > 8 { fputs(String(format: "[loop] +%5.3fs %6.1f ms\n", turnStart - launch, ms), stderr) }
        }
        CFRunLoopAddObserver(CFRunLoopGetMain(), observer, .commonModes)
        // Six seconds in (rows rendered), print a class histogram of every NSView in the main window — the
        // AppKit view tree is what Auto Layout walks on each pass, so this shows what SwiftUI is still hosting.
        DispatchQueue.main.asyncAfter(deadline: .now() + 6) {
            fputs("[views] windows \(NSApp.windows.count)\n", stderr)
            let root = NSApp.windows.max(by: { $0.frame.width < $1.frame.width })?.contentView?.superview
            guard let root else { return }
            var counts: [String: Int] = [:]; var total = 0
            func walk(_ v: NSView) { total += 1; counts[String(describing: type(of: v)), default: 0] += 1; v.subviews.forEach(walk) }
            walk(root)
            fputs("[views] total \(total)\n", stderr)
            for (k, v) in counts.sorted(by: { $0.value > $1.value }) { fputs("[views] \(v)\t\(k)\n", stderr) }
        }
        // `JBTT_SNAPSHOT=/path/base`: writes PNGs of the main window at +4 s and +9 s (own-window capture
        // needs no screen-recording permission) — a look at the real rendering without a human present.
        if let base = ProcessInfo.processInfo.environment["JBTT_SNAPSHOT"] {
            for delay in [4.0, 12.0] {
                DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                    guard let win = NSApp.windows.max(by: { $0.frame.width < $1.frame.width }) else {
                        fputs("[snapshot] failed: no window\n", stderr); return
                    }
                    // Window-server capture first; when that's refused (screen locked / display asleep), draw the
                    // view hierarchy into a bitmap instead (Metal-backed layers may render blank that way).
                    let rep: NSBitmapImageRep
                    if let cg = CGWindowListCreateImage(.null, .optionIncludingWindow, CGWindowID(win.windowNumber),
                                                        [.boundsIgnoreFraming, .bestResolution]) {
                        rep = NSBitmapImageRep(cgImage: cg)
                    } else if let view = win.contentView?.superview ?? win.contentView,
                              let r = view.bitmapImageRepForCachingDisplay(in: view.bounds) {
                        view.cacheDisplay(in: view.bounds, to: r)
                        rep = r
                        fputs("[snapshot] (view-cache fallback)\n", stderr)
                    } else { fputs("[snapshot] failed\n", stderr); return }
                    let url = URL(fileURLWithPath: "\(base)-\(Int(delay)).png")
                    try? rep.representation(using: .png, properties: [:])?.write(to: url)
                    fputs("[snapshot] wrote \(url.path) \(rep.pixelsWide)x\(rep.pixelsHigh)\n", stderr)
                }
            }
        }
        // `JBTT_SCROLL_BENCH=1`: twelve seconds in (Download All done), scroll the list down and back up at
        // 60 Hz for two seconds — the run-loop report above then shows what a scroll step costs.
        guard ProcessInfo.processInfo.environment["JBTT_SCROLL_BENCH"] == "1" else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 12) {
            func findScroll(_ v: NSView) -> NSScrollView? {
                if let s = v as? NSScrollView { return s }
                for sub in v.subviews { if let s = findScroll(sub) { return s } }
                return nil
            }
            guard let win = NSApp.windows.max(by: { $0.frame.width < $1.frame.width }),
                  let root = win.contentView, let scroll = findScroll(root) else { fputs("[scroll] no scroll view\n", stderr); return }
            let clip = scroll.contentView
            let maxY = max(0, (scroll.documentView?.frame.height ?? 0) - clip.bounds.height)
            var step = 0
            fputs("[scroll] begin (range \(Int(maxY)) px)\n", stderr)
            benchActive = true; benchTurns = []
            Timer.scheduledTimer(withTimeInterval: 1.0 / 60, repeats: true) { t in
                step += 1
                let phase = Double(step % 120) / 60.0                 // 0→2 : down then up
                let y = maxY * (phase <= 1 ? phase : 2 - phase)
                clip.scroll(to: NSPoint(x: 0, y: y)); scroll.reflectScrolledClipView(clip)
                if step >= 120 {
                    t.invalidate(); benchActive = false
                    let turns = benchTurns.filter { $0 > 0.5 }.sorted()
                    let sum = turns.reduce(0, +)
                    fputs(String(format: "[scroll] end: %d turns, busy %.0f ms, avg %.1f, p90 %.1f, max %.1f ms\n",
                                 turns.count, sum, sum / Double(max(1, turns.count)),
                                 turns[max(0, Int(Double(turns.count) * 0.9) - 1)], turns.last ?? 0), stderr)
                }
            }
        }
    }
}
