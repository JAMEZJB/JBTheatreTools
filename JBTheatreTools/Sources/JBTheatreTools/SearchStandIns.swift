import Foundation
import CoreServices

/// Makes apps installed in the launcher folder findable in Spotlight, Alfred and Launchpad: a small stand-in app for each
/// in ~/Applications/JB Theatre Tools/, with the real app's name and icon, that opens the real app (the way Steam and
/// Chrome make their apps searchable). The real app stays in the launcher folder; an app installed to /Applications needs
/// no stand-in. Kept in step with installed.json on every install, update, move and removal (window and command line),
/// and checked at start-up. Only stand-ins this launcher made (marked in their Info.plist) are ever changed or removed.
struct SearchStandIns {
    /// ~/Applications/JB Theatre Tools (or a stand-in folder in tests).
    let root: URL
    /// The launcher folder: only apps installed here get a stand-in.
    let appsDir: URL

    static let markerKey = "JBTTStandInFor"
    static let versionKey = "JBTTStandInVersion"
    private static let executableName = "open-app"
    private var fm: FileManager { .default }

    /// The real folder, unless a test or a test-mode launcher says otherwise. Never ~/Applications under XCTest.
    static func defaultRoot(supportDir: URL, isTestSupport: Bool) -> URL {
        if let dir = ProcessInfo.processInfo.environment["JBTT_STANDIN_DIR"], !dir.isEmpty {
            return URL(fileURLWithPath: dir, isDirectory: true)
        }
        if isTestSupport || NSClassFromString("XCTestCase") != nil {
            return supportDir.appendingPathComponent("standins", isDirectory: true)
        }
        return FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Applications/JB Theatre Tools", isDirectory: true)
    }

    /// Brings the stand-ins in step with `manifest`. Best effort: a failure is logged and never fails an install.
    func sync(_ manifest: [String: InstalledRecord]) {
        let inLauncherFolder = appsDir.standardizedFileURL.path + "/"
        var wanted: [String: (app: URL, version: String)] = [:]   // stand-in file name → real app
        for rec in manifest.values {
            let app = URL(fileURLWithPath: rec.path).standardizedFileURL
            guard app.path.hasPrefix(inLauncherFolder), app.pathExtension == "app", fm.fileExists(atPath: app.path) else { continue }
            wanted[app.lastPathComponent] = (app, rec.version)
        }
        // Ours that no longer match an app in the launcher folder go (and nothing else in the folder is touched).
        let existing = (try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
        for item in existing where item.pathExtension == "app" {
            guard let target = Self.standInTarget(item) else { continue }   // not ours
            if let w = wanted[item.lastPathComponent], w.app.path == target { continue }
            remove(item)
        }
        for (name, w) in wanted {
            let standIn = root.appendingPathComponent(name, isDirectory: true)
            if Self.standInTarget(standIn) == w.app.path, Self.standInVersion(standIn) == w.version { continue }
            if fm.fileExists(atPath: standIn.path), Self.standInTarget(standIn) == nil { continue }   // someone else's: leave it
            make(standIn, for: w.app, version: w.version)
        }
        if wanted.isEmpty, let left = try? fm.contentsOfDirectory(atPath: root.path), left.filter({ !$0.hasPrefix(".") }).isEmpty {
            try? fm.removeItem(at: root)
        }
    }

    /// The real app a stand-in opens, or nil when `url` isn't a stand-in this launcher made.
    static func standInTarget(_ url: URL) -> String? {
        info(url)?[markerKey] as? String
    }

    static func standInVersion(_ url: URL) -> String? {
        info(url)?[versionKey] as? String
    }

    private static func info(_ url: URL) -> [String: Any]? {
        guard let data = try? Data(contentsOf: url.appendingPathComponent("Contents/Info.plist")) else { return nil }
        return try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any]
    }

    /// Builds the stand-in beside its place, then swaps it in, so Spotlight never sees half a bundle.
    private func make(_ standIn: URL, for app: URL, version: String) {
        let staging = root.appendingPathComponent(".\(standIn.lastPathComponent).\(UUID().uuidString)", isDirectory: true)
        do {
            try fm.createDirectory(at: staging.appendingPathComponent("Contents/MacOS"), withIntermediateDirectories: true)
            try fm.createDirectory(at: staging.appendingPathComponent("Contents/Resources"), withIntermediateDirectories: true)
            let name = standIn.deletingPathExtension().lastPathComponent
            var plist: [String: Any] = [
                "CFBundleName": name, "CFBundleDisplayName": name,
                "CFBundleIdentifier": "com.jamesbreedon.jbtt.standin." + Self.identifierPart(name),
                "CFBundleExecutable": Self.executableName, "CFBundlePackageType": "APPL",
                "CFBundleInfoDictionaryVersion": "6.0", "CFBundleShortVersionString": Self.plainVersion(version),
                "LSUIElement": true,   // only the real app shows in the Dock
                Self.markerKey: app.path, Self.versionKey: version,
            ]
            if let icon = Self.iconFile(of: app) {
                try fm.copyItem(at: icon, to: staging.appendingPathComponent("Contents/Resources/AppIcon.icns"))
                plist["CFBundleIconFile"] = "AppIcon"
            }
            let data = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
            try data.write(to: staging.appendingPathComponent("Contents/Info.plist"))
            let script = staging.appendingPathComponent("Contents/MacOS/\(Self.executableName)")
            try Data(Self.script(opening: app).utf8).write(to: script)
            try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
            if fm.fileExists(atPath: standIn.path) {
                _ = try fm.replaceItemAt(standIn, withItemAt: staging)
            } else {
                try fm.moveItem(at: staging, to: standIn)
            }
            // Spotlight / Alfred / Launchpad see it straight away. Never from tests: their stand-ins mustn't join this
            // Mac's list of apps ("Open With", Launchpad).
            if NSClassFromString("XCTestCase") == nil { LSRegisterURL(standIn as CFURL, true) }
        } catch {
            try? fm.removeItem(at: staging)
            AppLog.shared.log("search stand-in for \(app.lastPathComponent): \(error.localizedDescription)")
        }
    }

    private func remove(_ standIn: URL) {
        do { try fm.removeItem(at: standIn) }
        catch { AppLog.shared.log("search stand-in \(standIn.lastPathComponent): couldn't remove (\(error.localizedDescription))") }
    }

    /// The stand-in's program: opens the real app (passing on anything it was given, e.g. a dropped file).
    static func script(opening app: URL) -> String {
        let quoted = "'" + app.path.replacingOccurrences(of: "'", with: "'\\''") + "'"
        return "#!/bin/sh\n# A JB Theatre Tools search stand-in: opens the real app in the launcher folder.\nexec /usr/bin/open -a \(quoted) --args \"$@\"\n"
    }

    /// The real app's icon file (CFBundleIconFile in its Resources), if it has one.
    static func iconFile(of app: URL) -> URL? {
        guard let name = info(app)?["CFBundleIconFile"] as? String, !name.isEmpty else { return nil }
        let resources = app.appendingPathComponent("Contents/Resources")
        let file = name.hasSuffix(".icns") ? name : name + ".icns"
        let url = resources.appendingPathComponent(file)
        return FileManager.default.fileExists(atPath: url.path) ? url : nil
    }

    static func identifierPart(_ name: String) -> String {
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-."))
        return String(name.lowercased().unicodeScalars.map { allowed.contains($0) ? Character($0) : "-" })
    }

    /// "v1.2.3-dev.1" → "1.2.3" (CFBundleShortVersionString takes digits and dots).
    static func plainVersion(_ tag: String) -> String {
        let core = tag.drop { !$0.isNumber }.prefix { $0.isNumber || $0 == "." }
        return core.isEmpty ? "1.0" : String(core)
    }
}
