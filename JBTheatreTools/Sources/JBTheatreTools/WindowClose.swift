import SwiftUI
import AppKit

/// Lets the user keep the app running when they click the window's close (X) button.
///
/// House convention: the X quits by default (handled by `AppDelegate`'s
/// `applicationShouldTerminateAfterLastWindowClosed` returning true — the window closes, and with no
/// windows left the app terminates). When the user opts into "keep running", we instead intercept the
/// close and HIDE the app, so it stays in the Dock and a Dock-icon click brings the window straight
/// back (the window is never destroyed). JBTheatreTools is single-window, so this is all we need.
///
/// We attach a delegate to the SwiftUI window but **forward every call we don't handle** to SwiftUI's
/// own window delegate, so none of SwiftUI's behaviour is lost — we only override `windowShouldClose`.
final class WindowCloseProxy: NSObject, NSWindowDelegate {
    private weak var original: NSWindowDelegate?
    private let shouldKeepRunning: () -> Bool

    init(original: NSWindowDelegate?, shouldKeepRunning: @escaping () -> Bool) {
        self.original = original
        self.shouldKeepRunning = shouldKeepRunning
        super.init()
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if shouldKeepRunning() {
            NSApp.hide(nil)   // stay alive in the Dock; clicking the Dock icon re-shows the window
            return false
        }
        return true            // quit mode: allow the close → app terminates (see AppDelegate)
    }

    // Transparently forward every other delegate / NSObject message to SwiftUI's own delegate.
    override func responds(to aSelector: Selector!) -> Bool {
        super.responds(to: aSelector) || (original?.responds(to: aSelector) ?? false)
    }
    override func forwardingTarget(for aSelector: Selector!) -> Any? {
        original
    }
}

/// Invisible helper view that, once added to the window, installs `WindowCloseProxy` as the window's
/// delegate. Drop it in a `.background(...)` of the root view.
struct WindowCloseConfigurator: NSViewRepresentable {
    let shouldKeepRunning: () -> Bool

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async {
            guard let window = view.window, !(window.delegate is WindowCloseProxy) else { return }
            let proxy = WindowCloseProxy(original: window.delegate, shouldKeepRunning: shouldKeepRunning)
            context.coordinator.proxy = proxy   // retain — NSWindow.delegate is a weak reference
            window.delegate = proxy
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {}

    func makeCoordinator() -> Coordinator { Coordinator() }

    final class Coordinator { var proxy: WindowCloseProxy? }
}
