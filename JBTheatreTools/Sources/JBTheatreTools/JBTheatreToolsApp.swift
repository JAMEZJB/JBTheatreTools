import SwiftUI
import AppKit

/// House convention (2026-06-20): the window's close (X) button QUITS the app on both platforms by
/// default. WinForms already exits when its main form closes; macOS otherwise keeps the process alive
/// with no window, so we opt into terminate-on-last-window-closed here. JBTheatreTools is a one-shot
/// launcher (open → install/update/launch → close), so there's no "keep running in the tray" option —
/// per the convention, when keep-running adds nothing you just quit-on-close and skip the toggle.
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

struct JBTheatreToolsApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var state = AppState()

    var body: some Scene {
        WindowGroup("JB Theatre Tools") {
            ContentView()
                .environmentObject(state)
        }
        .windowResizability(.contentSize)
    }
}
