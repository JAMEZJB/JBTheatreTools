import SwiftUI
import AppKit

/// House convention (2026-06-20): the window's close (X) button QUITS the app by default on both
/// platforms. The user can opt into "keep running" in Settings — on macOS that means staying in the
/// Dock (handled by `WindowCloseProxy`, which intercepts the close and hides instead). In the default
/// quit mode the window closes normally and, with no windows left, this terminates the app (macOS
/// otherwise keeps the process alive window-less).
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
