import Foundation

/// Adds / removes an app in the macOS Dock's pinned ("persistent") apps.
///
/// There is no public API for Dock pinning; the supported-in-practice mechanism (used by MDM and
/// setup tooling alike) is editing the Dock's own preferences domain (`com.apple.dock`,
/// `persistent-apps`) and restarting the Dock. The restart makes the Dock vanish/reappear for about
/// a second — acceptable for an explicit user action, but calls here batch it via `restartDock:`.
/// All operations are best-effort: a failure never blocks the caller (mirrors the Windows
/// shortcuts philosophy).
enum Dock {
    private static let domain = "com.apple.dock"
    private static let key = "persistent-apps"

    /// True when `appPath` is already a pinned Dock tile.
    static func isPinned(_ appPath: String) -> Bool {
        pinnedIndex(of: appPath, in: tiles()) != nil
    }

    /// Pins the app (no-op if already pinned). Returns whether the Dock now needs a restart.
    @discardableResult
    static func pin(_ appPath: String, restartDock: Bool = true) -> Bool {
        var apps = tiles()
        guard pinnedIndex(of: appPath, in: apps) == nil else { return false }
        let url = URL(fileURLWithPath: appPath, isDirectory: true)
        apps.append([
            "tile-type": "file-tile",
            "tile-data": ["file-data": ["_CFURLString": url.absoluteString, "_CFURLStringType": 15]],
        ])
        write(apps)
        if restartDock { restart() }
        return true
    }

    /// Unpins the app (no-op if not pinned). Returns whether the Dock now needs a restart.
    @discardableResult
    static func unpin(_ appPath: String, restartDock: Bool = true) -> Bool {
        var apps = tiles()
        guard let i = pinnedIndex(of: appPath, in: apps) else { return false }
        apps.remove(at: i)
        write(apps)
        if restartDock { restart() }
        return true
    }

    /// Relaunches the Dock so a pin change takes effect.
    static func restart() {
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/usr/bin/killall")
        proc.arguments = ["Dock"]
        try? proc.run()
    }

    // MARK: - Plumbing

    private static func tiles() -> [[String: Any]] {
        UserDefaults(suiteName: domain)?.array(forKey: key) as? [[String: Any]] ?? []
    }

    private static func write(_ apps: [[String: Any]]) {
        guard let ud = UserDefaults(suiteName: domain) else { return }
        ud.set(apps, forKey: key)
        ud.synchronize()
    }

    /// Finds the tile whose file URL resolves to `appPath` (paths compared standardised, so the
    /// percent-encoding and trailing slash of `_CFURLString` don't matter).
    private static func pinnedIndex(of appPath: String, in apps: [[String: Any]]) -> Int? {
        let want = URL(fileURLWithPath: appPath).standardizedFileURL.path
        for (i, tile) in apps.enumerated() {
            guard let data = tile["tile-data"] as? [String: Any],
                  let file = data["file-data"] as? [String: Any],
                  let urlString = file["_CFURLString"] as? String,
                  let url = URL(string: urlString) else { continue }
            if url.standardizedFileURL.path == want { return i }
        }
        return nil
    }
}
