import Foundation

/// The two small files the launcher shares with Stagehand (the suite app that controls what Claude may do in each app):
///
/// - `claude.json` — Claude's switch, show lock and permissions, read by every app on each call. The launcher writes only
///   the suite-wide `showLock`, so its show lock, Stagehand's and each app's own switch stay in step.
/// - `connectors.json` — for every installed app whose build is a Claude connector, a command that keeps working across
///   updates. Stagehand starts only these (an app without a connector would open its window instead).
///
/// Both live next to `installed.json` (~/Library/Application Support/JBTheatreTools/) and are written atomically.
enum ClaudeSettingsFile {
    static var url: URL { InstallManager.shared.supportDir.appendingPathComponent("claude.json") }

    /// The suite-wide show lock in claude.json, or nil when there's no file or it can't be read.
    static func suiteShowLock(at url: URL = url) -> Bool? {
        guard let data = try? Data(contentsOf: url), let d = SuiteBackup.jsonObject(data),
              (d["schema"] as? NSNumber)?.intValue == 1 else { return nil }
        return d["showLock"] as? Bool ?? false
    }

    /// Sets the suite-wide show lock, keeping everything else in the file. An unreadable file is replaced by a fresh,
    /// disconnected one rather than refusing the switch: the operator is never blocked.
    static func setSuiteShowLock(_ on: Bool, at url: URL = url) {
        var d: [String: Any] = ["schema": 1, "enabled": false, "showLock": false, "apps": [String: Any]()]
        if let data = try? Data(contentsOf: url), let old = SuiteBackup.jsonObject(data),
           (old["schema"] as? NSNumber)?.intValue == 1 {
            d = old
        }
        guard d["showLock"] as? Bool != on || !FileManager.default.fileExists(atPath: url.path) else { return }
        d["showLock"] = on
        do { try writePrivate(try SuiteBackup.jsonData(d), to: url) }
        catch { AppLog.shared.log("claude.json: couldn't save show lock (\(error.localizedDescription))") }
    }

    /// Atomic, readable by this user only.
    static func writePrivate(_ data: Data, to url: URL) throws {
        let fm = FileManager.default
        try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let tmp = url.deletingLastPathComponent().appendingPathComponent(".\(url.lastPathComponent).\(UUID().uuidString).tmp")
        guard fm.createFile(atPath: tmp.path, contents: data, attributes: [.posixPermissions: 0o600]) else {
            throw CocoaError(.fileWriteUnknown)
        }
        do {
            if fm.fileExists(atPath: url.path) { _ = try fm.replaceItemAt(url, withItemAt: tmp) }
            else { try fm.moveItem(at: tmp, to: url) }
        } catch {
            try? fm.removeItem(at: tmp)
            throw error
        }
    }
}

enum Connectors {
    static var url: URL { InstallManager.shared.supportDir.appendingPathComponent("connectors.json") }

    /// True when `installed` is at or past the catalog's `claudeSince`.
    static func hasConnector(_ app: CatalogApp, installed: String) -> Bool {
        guard let since = app.claudeSince, !since.isEmpty else { return false }
        return !AppState.versionIsNewer(since, than: installed)
    }

    /// One entry per app id (the Full edition when both are installed), only for builds that are connectors.
    static func entries(apps: [CatalogApp], manifest: [String: InstalledRecord]) -> [String: [String: Any]] {
        var out: [String: [String: Any]] = [:]
        for app in apps where app.claudeSince != nil {
            let editions: [String?] = app.hasVariants ? (app.variants ?? []).map(\.id).reversed() : [nil]
            for vid in editions {
                let key = app.installKey(variantId: vid)
                guard let rec = manifest[key], hasConnector(app, installed: rec.version),
                      let exe = SuiteBackup.executable(ofApp: URL(fileURLWithPath: rec.path)) else { continue }
                var e: [String: Any] = ["name": app.name, "command": exe.path, "args": ["--mcp"], "version": rec.version]
                if app.hasVariants, let vid { e["edition"] = vid }
                out[app.id] = e
                break
            }
        }
        return out
    }

    /// Rewrites connectors.json when what it should say has changed (after every install, update, removal or move).
    static func sync(apps: [CatalogApp], manifest: [String: InstalledRecord], at url: URL = url) {
        let doc: [String: Any] = ["schema": 1, "apps": entries(apps: apps, manifest: manifest)]
        guard let data = try? SuiteBackup.jsonData(doc) else { return }
        if let old = try? Data(contentsOf: url), old == data { return }
        do { try SuiteBackup.atomicWrite(data, to: url) }
        catch { AppLog.shared.log("connectors.json: couldn't save (\(error.localizedDescription))") }
    }
}
