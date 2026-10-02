import SwiftUI
import AppKit

/// House convention (2026-06-20): the window's close (X) button QUITS the app by default on both
/// platforms. The user can opt into "keep running" in Settings — on macOS that means staying in the
/// Dock (handled by `WindowCloseProxy`, which intercepts the close and hides instead). In the default
/// quit mode the window closes normally and, with no windows left, this terminates the app (macOS
/// otherwise keeps the process alive window-less).
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    /// Set by the app once the model exists: the Dock icon's menu lists what it can open.
    @MainActor weak var state: AppState?

    /// Quick launch from the Dock icon (right-click / long-press): every installed app, the same list as the menu-bar
    /// menu and the Windows tray's Launch submenu.
    @MainActor func applicationDockMenu(_ sender: NSApplication) -> NSMenu? {
        guard let state else { return nil }
        let slots = state.chrome.launchSlots
        guard !slots.isEmpty else { return nil }
        let menu = NSMenu()
        let header = NSMenuItem(title: "Open", action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        for slot in slots {
            let item = NSMenuItem(title: slot.name, action: #selector(openSlot(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = slot.key
            menu.addItem(item)
        }
        return menu
    }

    @MainActor @objc private func openSlot(_ sender: NSMenuItem) {
        guard let key = sender.representedObject as? String else { return }
        state?.launchSlot(key)
    }
}

struct JBTheatreToolsApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    /// A plain stored property, NOT `@StateObject`: the App struct lives for the whole process, so the model
    /// needs no SwiftUI ownership — and `@StateObject` would subscribe the App's body to every AppState
    /// publish, re-creating `ContentView` (an `@self` change) and rendering the list a second time per event.
    private let state = AppState()
    /// Settings → Quick launch → "Show in the menu bar".
    @AppStorage(AppState.menuBarKey) private var showMenuBar = false

    var body: some Scene {
        WindowGroup("JB Theatre Tools") {
            ContentView()
                .environmentObject(state)
                .environmentObject(state.progressHub)
                .environment(\.appState, state)
                .onAppear { appDelegate.state = state }
        }
        .windowResizability(.contentSize)
        .commands { LauncherCommands(state: state, chrome: state.chrome) }

        // The binding writes ONLY on a real change: MenuBarExtra pushes `isInserted` back on every scene update,
        // and a UserDefaults write re-fires every @AppStorage in the window → re-render → scene update → write…
        // (the launcher spun at 100% CPU and never finished starting).
        MenuBarExtra("JB Theatre Tools", systemImage: "theatermasks", isInserted: Binding(
            get: { showMenuBar },
            set: { if $0 != showMenuBar { showMenuBar = $0 } })) {
            QuickLaunchMenu(state: state, chrome: state.chrome)
        }
    }
}
