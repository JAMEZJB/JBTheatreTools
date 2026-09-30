import Foundation

/// "Back up all apps" / "Restore all apps": the launcher's half of the suite's settings backup.
///
/// Each suite app backs up its own settings through its command line:
///
///     <app> --settings-describe                                      [--result FILE]
///     <app> --settings-export FILE [--include-secrets] --passphrase-stdin --result FILE
///     <app> --settings-import FILE [--no-secrets]      --passphrase-stdin --result FILE
///
/// (exit codes: 0 ok · 1 error · 2 passphrase needed or wrong · 3 wrong app · 4 not a backup · 5 the app is open ·
/// 64 bad usage). The launcher runs that for every installed app, adds its own settings in the same `.jbtt-settings`
/// format, and zips the lot into one `.jbtt-backup` with a `manifest.json`.
enum SuiteBackup {
    static let launcherId = "jbtheatretools"
    static let launcherName = "JB Theatre Tools"
    static let settingsFormat = "jbtt-settings"
    static let settingsSchema = 1
    static let settingsExt = "jbtt-settings"
    static let bundleFormat = "jbtt-suite-backup"
    static let bundleSchema = 1
    static let bundleExt = "jbtt-backup"
    static let launcherFile = "launcher.jbtt-settings"
    static let manifestFile = "manifest.json"
    static let maxFileBytes = 64 << 20
    static let describeTimeout: TimeInterval = 45
    static let runTimeout: TimeInterval = 180

    /// Bound into the seal so a sealed section can't be moved to another app's file.
    static func aad(_ appId: String) -> Data { Data("\(settingsFormat)|\(settingsSchema)|\(appId)".utf8) }

    static func isoNow(_ date: Date = Date()) -> String {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f.string(from: date)
    }

    /// Where a backup came from, shown before a restore ("from Show-PC-2, macOS 14.6").
    static func source() -> [String: String] {
        let v = ProcessInfo.processInfo.operatingSystemVersion
        let os = "macOS \(v.majorVersion).\(v.minorVersion)" + (v.patchVersion > 0 ? ".\(v.patchVersion)" : "")
        // The host name, as the apps' own backups record it (so "same computer" matches across them).
        var u = utsname()
        uname(&u)
        var name = withUnsafeBytes(of: &u.nodename) { String(decoding: $0.prefix { $0 != 0 }, as: UTF8.self) }
        if name.lowercased().hasSuffix(".local") { name = String(name.dropLast(6)) }
        return ["machine": name.isEmpty ? "this computer" : name, "os": os, "platform": "darwin"]
    }

    static func bundleFileName(_ date: Date = Date()) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd HHmm"
        return "JB Theatre Tools backup \(f.string(from: date)).\(bundleExt)"
    }

    enum Failure: LocalizedError, Equatable {
        case notABackup(String)
        case wrongApp(String)
        case newer(String)
        case passphraseNeeded
        case wrongPassphrase
        case other(String)

        var errorDescription: String? {
            switch self {
            case .notABackup(let m), .wrongApp(let m), .newer(let m), .other(let m): return m
            case .passphraseNeeded: return "The backup's passwords are protected. Enter its passphrase."
            case .wrongPassphrase: return "That passphrase doesn't open this backup."
            }
        }
    }

    static func jsonObject(_ data: Data) -> [String: Any]? {
        (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }

    static func jsonData(_ obj: Any) throws -> Data {
        try JSONSerialization.data(withJSONObject: obj, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
    }

    /// Written to a temporary file beside it, then moved over: a crash mid-write never leaves half a file.
    static func atomicWrite(_ data: Data, to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url, options: .atomic)
    }
}

// MARK: - The launcher's own settings (`launcher.jbtt-settings`)

/// Where the launcher keeps its preferences (UserDefaults normally; a JSON file for the command line's test home).
protocol LauncherPrefs: AnyObject {
    func prefValue(_ key: String) -> Any?
    func setPref(_ value: Any?, _ key: String)
}

extension UserDefaults: LauncherPrefs {
    func prefValue(_ key: String) -> Any? { object(forKey: key) }
    func setPref(_ value: Any?, _ key: String) {
        if let value { set(value, forKey: key) } else { removeObject(forKey: key) }
    }
}

/// A preferences stand-in in a JSON file (tests and the `JBTT_BACKUP_TEST_HOME` harness — never the real settings).
final class FilePrefs: LauncherPrefs {
    let url: URL
    private var values: [String: Any]
    init(url: URL) {
        self.url = url
        values = (try? Data(contentsOf: url)).flatMap(SuiteBackup.jsonObject) ?? [:]
    }
    func prefValue(_ key: String) -> Any? { values[key] }
    func setPref(_ value: Any?, _ key: String) {
        values[key] = value
        if let data = try? SuiteBackup.jsonData(values) { try? SuiteBackup.atomicWrite(data, to: url) }
    }
}

/// The launcher's saved passwords: the download passphrase and the GitHub token. Names only until the user ticks
/// "Include saved passwords" — only then is a value read.
protocol LauncherSecrets {
    /// Every password saved on this computer, by name, with its plain label. Never reads a value.
    func present() -> [(name: String, label: String)]
    func read() -> [String: String]
    func write(_ values: [String: String]) throws
}

struct KeychainLauncherSecrets: LauncherSecrets {
    func present() -> [(name: String, label: String)] {
        var out: [(String, String)] = []
        if ServerAuthStore.exists() { out.append(("downloadPassphrase", "Download passphrase")) }
        if TokenStore.exists() { out.append(("githubToken", "GitHub token")) }
        return out
    }
    func read() -> [String: String] {
        var out: [String: String] = [:]
        if ServerAuthStore.exists(), let v = ServerAuthStore.load() { out["downloadPassphrase"] = v }
        if TokenStore.exists(), let v = TokenStore.load() { out["githubToken"] = v }
        return out
    }
    func write(_ values: [String: String]) throws {
        if let v = values["downloadPassphrase"], !v.isEmpty, !ServerAuthStore.save(v) {
            throw SuiteBackup.Failure.other("Couldn't save the download passphrase in the Keychain.")
        }
        if let v = values["githubToken"], !v.isEmpty, !TokenStore.save(v) {
            throw SuiteBackup.Failure.other("Couldn't save the GitHub token in the Keychain.")
        }
    }
}

/// Passwords held in a JSON file (tests and the command line's test home only).
final class FileLauncherSecrets: LauncherSecrets {
    let url: URL
    init(url: URL) { self.url = url }
    private var values: [String: String] {
        ((try? Data(contentsOf: url)).flatMap(SuiteBackup.jsonObject) as? [String: String]) ?? [:]
    }
    func present() -> [(name: String, label: String)] {
        LauncherSettings.secretLabels.filter { values[$0.name]?.isEmpty == false }
    }
    func read() -> [String: String] { values }
    func write(_ new: [String: String]) throws {
        var v = values
        for (k, x) in new { v[k] = x }
        try SuiteBackup.atomicWrite(try SuiteBackup.jsonData(v), to: url)
    }
}

/// The launcher's settings in a platform-neutral shape, so a Mac backup restores onto a Windows show PC and back.
/// Machine-specific values (install homes, Intel/x64 choices, the relay override, notifications already sent) and
/// state (show lock, history, the log) are never included.
enum LauncherSettings {
    enum Kind { case string([String]?), bool, strings, map }

    /// neutral name → (this Mac's UserDefaults key, kind; string kinds list their allowed values).
    static let fields: [(name: String, key: String, kind: Kind)] = [
        ("appearance", "theatre.appearance", .string(["system", "light", "dark"])),
        ("viewMode", "theatre.viewMode", .string(["list", "grid"])),
        ("updateMode", "theatre.updateMode", .string(["everyLaunch", "manual", "never"])),
        ("autoCheckInterval", AppState.autoCheckKey, .string(UpdatePolicy.intervals.map(\.raw))),
        ("notifyUpdates", AppState.notifyKey, .bool),
        ("autoInstallUpdates", AppState.autoInstallKey, .bool),
        ("closeBehavior", "theatre.closeBehavior", .string(["quit", "keepRunning"])),
        ("installToApplications", "theatre.installToApplications", .bool),
        ("quickLaunchIcon", AppState.menuBarKey, .bool),
        ("authMode", "theatre.authMode", .string(["token", "server"])),
        ("devChannel", AppState.devChannelKey, .bool),
        ("appOrder", "theatre.appOrder", .strings),
        ("pinnedApps", "theatre.pinnedApps", .strings),
        ("hiddenApps", "theatre.hiddenApps", .strings),
        ("heldApps", AppState.heldKey, .strings),
        ("categoryOrder", "theatre.categoryOrder", .strings),
        ("collapsedCategories", "theatre.collapsedCategories", .strings),
        ("appVariants", "theatre.appVariants", .map),
    ]

    static let secretLabels: [(name: String, label: String)] = [
        ("downloadPassphrase", "Download passphrase"), ("githubToken", "GitHub token"),
    ]

    /// The Mac keeps the Pinned section under a control-character key; the file uses the plain word (as Windows does).
    private static let macPinned = "\u{1}pinned"

    static func export(_ prefs: LauncherPrefs) -> [String: Any] {
        var data: [String: Any] = [:]
        for f in fields {
            guard let v = prefs.prefValue(f.key) else { continue }
            switch f.kind {
            case .string: if let s = v as? String { data[f.name] = s }
            case .bool: if let b = v as? Bool { data[f.name] = b }
            case .strings:
                if var a = v as? [String] {
                    if f.name == "collapsedCategories" { a = a.map { $0 == macPinned ? "pinned" : $0 } }
                    data[f.name] = a
                }
            case .map: if let m = v as? [String: String] { data[f.name] = m }
            }
        }
        return data
    }

    /// Writes what the file holds that this Mac understands; anything unknown or out of range is left as it is.
    /// Returns the plain notes for the result ("Check these in Settings").
    @discardableResult
    static func apply(_ data: [String: Any], to prefs: LauncherPrefs) -> (attention: [[String: String]], warnings: [String]) {
        var attention: [[String: String]] = [], warnings: [String] = []
        for f in fields {
            guard let v = data[f.name] else { continue }
            switch f.kind {
            case .string(let allowed):
                guard let s = v as? String, allowed?.contains(s) ?? true else {
                    warnings.append("A launcher setting (\(f.name)) had a value this version doesn't know, so it was left as it is.")
                    continue
                }
                prefs.setPref(s, f.key)
            case .bool:
                guard let b = v as? Bool else { continue }
                if f.name == "notifyUpdates", b {
                    // macOS asks permission only from the switch itself, so it isn't switched on behind the user's back.
                    attention.append(["label": "Update notifications",
                                      "message": "Turn on \u{201C}Notify me when updates are available\u{201D} in Settings \u{2014} macOS asks for permission when you do."])
                    continue
                }
                prefs.setPref(b, f.key)
            case .strings:
                guard var a = v as? [String] else { continue }
                if f.name == "collapsedCategories" { a = a.map { $0 == "pinned" ? macPinned : $0 } }
                prefs.setPref(a, f.key)
            case .map:
                guard let m = v as? [String: String] else { continue }
                prefs.setPref(m, f.key)
            }
        }
        return (attention, warnings)
    }

    /// The whole `launcher.jbtt-settings` document. Passwords go in only when `includeSecrets`; with a passphrase they
    /// are sealed (PBKDF2), without one they are plain text (the sheet warns the user first).
    static func document(prefs: LauncherPrefs, secrets: LauncherSecrets, version: String,
                         includeSecrets: Bool, passphrase: String?, rounds: Int = SettingsSeal.pbkdf2Rounds) throws -> [String: Any] {
        let present = secrets.present()
        var doc: [String: Any] = [
            "format": SuiteBackup.settingsFormat, "schema": SuiteBackup.settingsSchema,
            "app": ["id": SuiteBackup.launcherId, "name": SuiteBackup.launcherName, "version": version],
            "created": SuiteBackup.isoNow(),
            "source": SuiteBackup.source(),
            "items": ["settings": ["type": "json", "label": "Launcher settings", "data": export(prefs)]],
            "secret_slots": present.map { ["id": "store:launcher:\($0.name)", "label": $0.label] },
            "secrets": NSNull(),
        ]
        if includeSecrets, !present.isEmpty {
            let wanted = Set(present.map(\.name))
            var values: [String: String] = [:]
            for (k, v) in secrets.read() where wanted.contains(k) && !v.isEmpty { values["store:launcher:\(k)"] = v }
            if !values.isEmpty {
                if let passphrase, !passphrase.isEmpty {
                    var box = try SettingsSeal.seal(try JSONSerialization.data(withJSONObject: values, options: [.sortedKeys]),
                                                    passphrase: passphrase, aad: SuiteBackup.aad(SuiteBackup.launcherId),
                                                    rounds: rounds)
                    box["protected"] = true
                    box["count"] = values.count
                    doc["secrets"] = box
                } else {
                    doc["secrets"] = ["protected": false, "count": values.count, "values": values]
                }
            }
        }
        return doc
    }

    struct Info {
        let version: String
        let created: String?
        let machine: String?
        let hasSecrets: Bool
        let protected: Bool
        let secretLabels: [String]
    }

    /// Checks a launcher settings file and says what's in it — never needs the passphrase.
    static func inspect(_ data: Data) throws -> (doc: [String: Any], info: Info) {
        guard data.count <= SuiteBackup.maxFileBytes, let doc = SuiteBackup.jsonObject(data),
              doc["format"] as? String == SuiteBackup.settingsFormat, doc["items"] is [String: Any] else {
            throw SuiteBackup.Failure.notABackup("The launcher's settings in this backup can't be read.")
        }
        guard let schema = (doc["schema"] as? NSNumber)?.intValue, schema <= SuiteBackup.settingsSchema else {
            throw SuiteBackup.Failure.newer("This backup was made by a newer JB Theatre Tools. Update it, then restore.")
        }
        let app = doc["app"] as? [String: Any] ?? [:]
        guard app["id"] as? String == SuiteBackup.launcherId else {
            throw SuiteBackup.Failure.wrongApp("This file is for \(app["name"] as? String ?? "another app"), not JB Theatre Tools.")
        }
        let secrets = doc["secrets"] as? [String: Any]
        let labels = (doc["secret_slots"] as? [[String: Any]] ?? []).compactMap { $0["label"] as? String }
        let source = doc["source"] as? [String: Any]
        return (doc, Info(version: app["version"] as? String ?? "", created: doc["created"] as? String,
                          machine: source?["machine"] as? String, hasSecrets: secrets != nil,
                          protected: secrets?["protected"] as? Bool == true, secretLabels: labels))
    }

    /// Checks the passphrase against the launcher's sealed passwords without changing anything — so a restore can stop
    /// before the first app is touched. No sealed passwords (or not restoring them): nothing to check.
    static func verifyPassphrase(_ data: Data, passphrase: String?, restoreSecrets: Bool) throws {
        let (doc, info) = try inspect(data)
        guard restoreSecrets, info.protected, let sec = doc["secrets"] as? [String: Any] else { return }
        guard let passphrase, !passphrase.isEmpty else { throw SuiteBackup.Failure.passphraseNeeded }
        do { _ = try SettingsSeal.unseal(sec, passphrase: passphrase, aad: SuiteBackup.aad(SuiteBackup.launcherId)) }
        catch SettingsSeal.SealError.wrongPassphrase { throw SuiteBackup.Failure.wrongPassphrase }
    }

    struct Outcome {
        var restored: [String] = []
        var attention: [[String: String]] = []
        var warnings: [String] = []
    }

    /// Restores the launcher's settings from a file. A wrong passphrase changes nothing.
    static func restore(_ data: Data, prefs: LauncherPrefs, secrets: LauncherSecrets,
                        passphrase: String?, restoreSecrets: Bool) throws -> Outcome {
        let (doc, _) = try inspect(data)
        // Open the passwords FIRST: a wrong passphrase must leave everything as it was.
        var values: [String: String] = [:]
        if restoreSecrets, let sec = doc["secrets"] as? [String: Any] {
            if sec["protected"] as? Bool == true {
                guard let passphrase, !passphrase.isEmpty else { throw SuiteBackup.Failure.passphraseNeeded }
                do {
                    let plain = try SettingsSeal.unseal(sec, passphrase: passphrase, aad: SuiteBackup.aad(SuiteBackup.launcherId))
                    values = (SuiteBackup.jsonObject(plain) as? [String: String]) ?? [:]
                } catch SettingsSeal.SealError.wrongPassphrase {
                    throw SuiteBackup.Failure.wrongPassphrase
                } catch let e as SettingsSeal.SealError {
                    throw SuiteBackup.Failure.other(e.localizedDescription)
                }
            } else {
                values = (sec["values"] as? [String: String]) ?? [:]
            }
        }
        var out = Outcome()
        if let item = (doc["items"] as? [String: Any])?["settings"] as? [String: Any], let d = item["data"] as? [String: Any] {
            let notes = apply(d, to: prefs)
            out.attention += notes.attention
            out.warnings += notes.warnings
            out.restored.append("Launcher settings")
        }
        var toWrite: [String: String] = [:]
        for (k, v) in values where k.hasPrefix("store:launcher:") && !v.isEmpty {
            toWrite[String(k.dropFirst("store:launcher:".count))] = v
        }
        if !toWrite.isEmpty {
            do { try secrets.write(toWrite) } catch { out.warnings.append(error.localizedDescription) }
        }
        let haveNow = Set(secrets.present().map(\.name))
        let missing = (doc["secret_slots"] as? [[String: Any]] ?? []).compactMap { slot -> String? in
            guard let id = slot["id"] as? String, id.hasPrefix("store:launcher:") else { return nil }
            let name = String(id.dropFirst("store:launcher:".count))
            return toWrite[name] == nil && !haveNow.contains(name) ? (slot["label"] as? String ?? name) : nil
        }
        if !missing.isEmpty {
            out.attention.append(["label": "Passwords",
                                  "message": "Enter them again in Settings: " + missing.joined(separator: "; ") + "."])
        }
        return out
    }
}

// MARK: - The bundle (`.jbtt-backup`)

struct SuiteBundleApp: Equatable {
    var id: String
    var name: String
    var version: String
    var file: String
    var edition: String?

    var json: [String: Any] {
        var d: [String: Any] = ["id": id, "name": name, "version": version, "file": file]
        if let edition { d["edition"] = edition }
        return d
    }
}

struct SuiteBundleManifest {
    var created: String
    var source: [String: String]
    var launcherVersion: String
    var apps: [SuiteBundleApp]
    var launcherFile: String?

    var json: [String: Any] {
        var d: [String: Any] = ["format": SuiteBackup.bundleFormat, "schema": SuiteBackup.bundleSchema,
                                "created": created, "source": source, "launcher_version": launcherVersion,
                                "apps": apps.map(\.json)]
        if let launcherFile { d["launcher"] = ["file": launcherFile] }
        return d
    }

    /// A file name inside the bundle is only ever a plain name ending .jbtt-settings — never a path.
    static func safeName(_ name: String) -> Bool {
        !name.isEmpty && name.count <= 128 && !name.hasPrefix(".") && name.hasSuffix(".\(SuiteBackup.settingsExt)")
            && !name.contains("/") && !name.contains("\\") && !name.contains(":") && !name.contains("\0")
    }

    static func parse(_ data: Data) throws -> SuiteBundleManifest {
        guard let d = SuiteBackup.jsonObject(data), d["format"] as? String == SuiteBackup.bundleFormat else {
            throw SuiteBackup.Failure.notABackup("That file isn't a JB Theatre Tools backup.")
        }
        guard let schema = (d["schema"] as? NSNumber)?.intValue, schema <= SuiteBackup.bundleSchema else {
            throw SuiteBackup.Failure.newer("This backup was made by a newer JB Theatre Tools. Update it, then restore.")
        }
        var apps: [SuiteBundleApp] = []
        var seen = Set<String>()
        for a in d["apps"] as? [[String: Any]] ?? [] {
            guard let id = a["id"] as? String, !id.isEmpty, let file = a["file"] as? String, safeName(file),
                  seen.insert(id).inserted else { continue }
            apps.append(SuiteBundleApp(id: id, name: a["name"] as? String ?? id, version: a["version"] as? String ?? "",
                                       file: file, edition: a["edition"] as? String))
        }
        var launcher = (d["launcher"] as? [String: Any])?["file"] as? String
        if let l = launcher, !safeName(l) { launcher = nil }
        return SuiteBundleManifest(created: d["created"] as? String ?? "",
                                   source: (d["source"] as? [String: String]) ?? [:],
                                   launcherVersion: d["launcher_version"] as? String ?? "",
                                   apps: apps, launcherFile: launcher)
    }
}

enum SuiteBundle {
    /// Zips `staging` (manifest.json + the .jbtt-settings files at its top level) into `dest`.
    static func write(staging: URL, to dest: URL) throws {
        let tmp = dest.deletingLastPathComponent().appendingPathComponent(".\(dest.lastPathComponent).\(ProcessInfo.processInfo.processIdentifier).tmp")
        try? FileManager.default.removeItem(at: tmp)
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        p.arguments = ["-c", "-k", "--norsrc", "--noextattr", "--noacl", staging.path, tmp.path]
        try p.run()
        p.waitUntilExit()
        guard p.terminationStatus == 0 else {
            try? FileManager.default.removeItem(at: tmp)
            throw SuiteBackup.Failure.other("Couldn't write the backup file (ditto exit \(p.terminationStatus)).")
        }
        if FileManager.default.fileExists(atPath: dest.path) { _ = try FileManager.default.replaceItemAt(dest, withItemAt: tmp) }
        else { try FileManager.default.moveItem(at: tmp, to: dest) }
    }

    struct Opened {
        let folder: URL
        let manifest: SuiteBundleManifest
        func url(_ file: String) -> URL { folder.appendingPathComponent(file) }
    }

    /// Unpacks a backup into a fresh temporary folder, refusing any entry that isn't a plain top-level file.
    static func open(_ url: URL) throws -> Opened {
        let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        guard size > 0, size <= 512 << 20 else { throw SuiteBackup.Failure.notABackup("That file isn't a JB Theatre Tools backup.") }
        let list = Process()
        list.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        list.arguments = ["-Z1", url.path]
        let out = Pipe()
        list.standardOutput = out
        list.standardError = FileHandle.nullDevice
        try list.run()
        let names = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            .split(whereSeparator: \.isNewline).map(String.init)
        list.waitUntilExit()
        guard list.terminationStatus == 0, names.contains(SuiteBackup.manifestFile) else {
            throw SuiteBackup.Failure.notABackup("That file isn't a JB Theatre Tools backup.")
        }
        for n in names where n != SuiteBackup.manifestFile && !n.hasPrefix("__MACOSX/") && !SuiteBundleManifest.safeName(n) {
            throw SuiteBackup.Failure.notABackup("This backup holds an unexpected file (\(n)), so it wasn't opened.")
        }
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("jbtt-restore-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let x = Process()
        x.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        x.arguments = ["-x", "-k", url.path, folder.path]
        try x.run()
        x.waitUntilExit()
        guard x.terminationStatus == 0 else {
            try? FileManager.default.removeItem(at: folder)
            throw SuiteBackup.Failure.notABackup("This backup can't be unpacked (it may be damaged).")
        }
        let manifest = try SuiteBundleManifest.parse(Data(contentsOf: folder.appendingPathComponent(SuiteBackup.manifestFile)))
        return Opened(folder: folder, manifest: manifest)
    }

    /// What a per-app file says about itself (for the Restore list), without the passphrase.
    struct AppFileInfo {
        let appId: String?
        let protected: Bool
        let hasSecrets: Bool
        let readable: Bool
    }

    static func appFileInfo(_ url: URL) -> AppFileInfo {
        guard let size = try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize, size <= SuiteBackup.maxFileBytes,
              let data = try? Data(contentsOf: url), let d = SuiteBackup.jsonObject(data),
              d["format"] as? String == SuiteBackup.settingsFormat else {
            return AppFileInfo(appId: nil, protected: false, hasSecrets: false, readable: false)
        }
        let sec = d["secrets"] as? [String: Any]
        return AppFileInfo(appId: (d["app"] as? [String: Any])?["id"] as? String,
                           protected: sec?["protected"] as? Bool == true, hasSecrets: sec != nil, readable: true)
    }
}

// MARK: - Running an app's settings command line

struct SettingsCLIRun {
    var exitCode: Int32?
    var result: [String: Any]?
    var timedOut = false
    var launchError: String?

    var ok: Bool { exitCode == 0 && result?["ok"] as? Bool == true }
    var message: String? { result?["error"] as? String }
    var code: String? { result?["code"] as? String }
}

enum SettingsCLI {
    /// Runs `executable` with `arguments` + `--result <tmp>` (and `--passphrase-stdin`, the passphrase on one line of
    /// stdin, when `passphrase` isn't nil). An app too old for these flags opens its window instead of answering, so the
    /// run is bounded: past `timeout` it is stopped (only the process started here) and reported as timed out.
    static func run(_ executable: URL, _ arguments: [String], passphrase: String?, timeout: TimeInterval) async -> SettingsCLIRun {
        await withCheckedContinuation { cont in
            DispatchQueue.global(qos: .userInitiated).async {
                cont.resume(returning: runBlocking(executable, arguments, passphrase: passphrase, timeout: timeout))
            }
        }
    }

    static func runBlocking(_ executable: URL, _ arguments: [String], passphrase: String?, timeout: TimeInterval) -> SettingsCLIRun {
        let resultURL = FileManager.default.temporaryDirectory.appendingPathComponent("jbtt-result-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: resultURL) }
        let p = Process()
        p.executableURL = executable
        p.arguments = arguments + (passphrase != nil ? ["--passphrase-stdin"] : []) + ["--result", resultURL.path]
        var env = ProcessInfo.processInfo.environment
        env.removeValue(forKey: "JBTT_SETTINGS_PASSPHRASE")   // only ever the stdin line
        p.environment = env
        p.currentDirectoryURL = FileManager.default.temporaryDirectory
        let input = Pipe()
        p.standardInput = input
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        let done = DispatchSemaphore(value: 0)
        p.terminationHandler = { _ in done.signal() }
        do { try p.run() } catch {
            return SettingsCLIRun(exitCode: nil, result: nil, timedOut: false, launchError: error.localizedDescription)
        }
        // An app that exits without reading (too old for the flags) closes the pipe: never let that raise SIGPIPE here.
        _ = fcntl(input.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1)
        if let passphrase { try? input.fileHandleForWriting.write(contentsOf: Data((passphrase + "\n").utf8)) }
        try? input.fileHandleForWriting.close()
        var run = SettingsCLIRun()
        if done.wait(timeout: .now() + timeout) == .timedOut {
            p.terminate()
            if done.wait(timeout: .now() + 3) == .timedOut {
                kill(p.processIdentifier, SIGKILL)
                _ = done.wait(timeout: .now() + 3)
            }
            run.timedOut = true
        } else {
            run.exitCode = p.terminationStatus
        }
        if let data = try? Data(contentsOf: resultURL) { run.result = SuiteBackup.jsonObject(data) }
        return run
    }
}

/// What one installed app can do, and what happened when it ran.
enum AppBackupProbe: Equatable {
    case ready(items: [String], secretCount: Int)
    /// Its installed version doesn't have the settings command line yet.
    case unsupported
    case failed(String)
}

enum AppImportOutcome: Equatable {
    case ok(attention: [String], warnings: [String], restartNeeded: Bool)
    case passphrase(wrong: Bool, message: String)
    case appOpen(String)
    case unsupported
    case failed(String)
}

enum AppExportOutcome: Equatable {
    case ok(secrets: Int, protected: Bool)
    case unsupported
    case failed(String)
}

enum SettingsCLIClassify {
    static let unsupportedMessage = "This app's version can't back up its settings yet \u{2014} update it."

    /// No answer, an answer that isn't the suite's result, or "bad usage": the app is too old for the command line.
    private static func looksUnsupported(_ r: SettingsCLIRun) -> Bool {
        r.launchError == nil && (r.result == nil || r.exitCode == 64 || r.code == "usage")
    }

    static func describe(_ r: SettingsCLIRun) -> AppBackupProbe {
        if r.ok {
            let items = (r.result?["items"] as? [Any])?.compactMap { $0 as? String } ?? []
            let count = ((r.result?["secrets"] as? [String: Any])?["count"] as? NSNumber)?.intValue ?? 0
            return .ready(items: items, secretCount: count)
        }
        if let e = r.launchError { return .failed("It couldn't be started (\(e)).") }
        if looksUnsupported(r) { return .unsupported }
        return .failed(r.message ?? "It stopped with an error (\(r.exitCode.map(String.init) ?? "?")).")
    }

    static func export(_ r: SettingsCLIRun) -> AppExportOutcome {
        if r.ok {
            return .ok(secrets: (r.result?["secrets"] as? NSNumber)?.intValue ?? 0,
                       protected: r.result?["protected"] as? Bool == true)
        }
        if let e = r.launchError { return .failed("It couldn't be started (\(e)).") }
        if r.timedOut && r.result == nil { return .unsupported }
        if looksUnsupported(r) { return .unsupported }
        return .failed(r.message ?? "It stopped with an error (\(r.exitCode.map(String.init) ?? "?")).")
    }

    /// Attention entries are `{label, message}` objects (or plain strings from an older module) → display lines.
    static func lines(_ v: Any?) -> [String] {
        (v as? [Any] ?? []).compactMap { e in
            if let s = e as? String { return s }
            if let d = e as? [String: Any] {
                let label = d["label"] as? String ?? "", msg = d["message"] as? String ?? ""
                return msg.isEmpty ? (label.isEmpty ? nil : label) : msg
            }
            return nil
        }
    }

    static func importResult(_ r: SettingsCLIRun) -> AppImportOutcome {
        if r.ok {
            return .ok(attention: lines(r.result?["attention"]), warnings: lines(r.result?["warnings"]),
                       restartNeeded: r.result?["restart_needed"] as? Bool == true)
        }
        if let e = r.launchError { return .failed("It couldn't be started (\(e)).") }
        switch r.exitCode {
        case 2: return .passphrase(wrong: r.code == "wrong_passphrase",
                                   message: r.message ?? "This backup's passwords are protected. Enter its passphrase.")
        case 5: return .appOpen(r.message ?? "Quit the app, then restore its settings.")
        default: break
        }
        if r.timedOut && r.result == nil { return .unsupported }
        if looksUnsupported(r) { return .unsupported }
        return .failed(r.message ?? "It stopped with an error (\(r.exitCode.map(String.init) ?? "?")).")
    }
}

// MARK: - Which installed apps take part

struct BackupSlot: Equatable {
    let installKey: String
    let version: String
    let edition: String?
    let executable: URL
}

struct BackupTarget: Identifiable, Equatable {
    let id: String          // catalog app id (without the edition)
    let name: String
    /// Installed editions, the Full one first (it has the most tools; the settings are the same folder).
    let slots: [BackupSlot]
}

extension SuiteBackup {
    /// The main executable inside an installed `.app` (the settings command line runs it directly, not via `open`).
    static func executable(ofApp bundle: URL) -> URL? {
        let plist = NSDictionary(contentsOf: bundle.appendingPathComponent("Contents/Info.plist"))
        let name = (plist?["CFBundleExecutable"] as? String) ?? bundle.deletingPathExtension().lastPathComponent
        let exe = bundle.appendingPathComponent("Contents/MacOS/\(name)")
        return FileManager.default.isExecutableFile(atPath: exe.path) ? exe : nil
    }

    /// Every installed app that can take part, in catalog order. Apps marked `settingsBackup: false` never do.
    static func targets(apps: [CatalogApp], installedVersion: (String) -> String?, installedPath: (String) -> URL?) -> [BackupTarget] {
        apps.compactMap { app in
            guard app.settingsBackup != false else { return nil }
            let editions: [String?] = app.hasVariants ? (app.variants ?? []).map(\.id).reversed() : [nil]
            let slots: [BackupSlot] = editions.compactMap { vid in
                let key = app.installKey(variantId: vid)
                guard let v = installedVersion(key), let path = installedPath(key), let exe = executable(ofApp: path) else { return nil }
                return BackupSlot(installKey: key, version: v, edition: app.hasVariants ? vid : nil, executable: exe)
            }
            return slots.isEmpty ? nil : BackupTarget(id: app.id, name: app.name, slots: slots)
        }
    }

    /// Asks each installed edition in turn (Full first) until one answers; the one that did is the one used.
    static func probe(_ target: BackupTarget) async -> (slot: BackupSlot?, probe: AppBackupProbe) {
        var last: AppBackupProbe = .unsupported
        for slot in target.slots {
            let r = await SettingsCLI.run(slot.executable, ["--settings-describe"], passphrase: nil, timeout: describeTimeout)
            let p = SettingsCLIClassify.describe(r)
            if case .ready = p { return (slot, p) }
            last = p
        }
        return (nil, last)
    }
}

// MARK: - Back up all / Restore all (shared by the Settings sheets and the command line)

/// One line of the combined result: an app (or the launcher) and how it went.
struct SuiteBackupLine: Identifiable, Equatable {
    enum State: Equatable { case ok, attention, unsupported, failed, skipped, retry }
    let id: String
    let name: String
    var state: State
    var detail: [String]
}

extension SuiteBackup {
    /// Runs every chosen app's export into one bundle at `dest`, plus the launcher's own settings when asked.
    /// Returns a line per app; throws only when nothing at all could be saved or the file can't be written.
    static func backUpAll(_ chosen: [(target: BackupTarget, slot: BackupSlot)], includeLauncher: Bool,
                          prefs: LauncherPrefs, secrets: LauncherSecrets, launcherVersion: String,
                          includeSecrets: Bool, passphrase: String?, dest: URL,
                          progress: @escaping (String) -> Void = { _ in }) async throws -> [SuiteBackupLine] {
        let fm = FileManager.default
        let staging = fm.temporaryDirectory.appendingPathComponent("jbtt-backup-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: staging) }
        var lines: [SuiteBackupLine] = []
        var entries: [SuiteBundleApp] = []
        let pass = (passphrase?.isEmpty == false) ? passphrase : nil
        for (target, slot) in chosen {
            progress("Backing up \(target.name)\u{2026}")
            let file = "\(target.id).\(settingsExt)"
            var args = ["--settings-export", staging.appendingPathComponent(file).path]
            if includeSecrets { args.append("--include-secrets") }
            let line: String? = includeSecrets ? (pass ?? "") : nil   // the stdin line (empty = no passphrase)
            let r = await SettingsCLI.run(slot.executable, args, passphrase: line, timeout: runTimeout)
            switch SettingsCLIClassify.export(r) {
            case .ok(let n, let prot) where n > 0 && pass != nil && !prot:
                // Asked for a passphrase but the app wrote its passwords in plain text: never keep that file.
                try? fm.removeItem(at: staging.appendingPathComponent(file))
                lines.append(SuiteBackupLine(id: target.id, name: target.name, state: .failed,
                                             detail: ["It didn't protect its passwords with the passphrase, so it was left out. Update it, or back up without passwords."]))
            case .ok(let n, let prot):
                entries.append(SuiteBundleApp(id: target.id, name: target.name, version: slot.version, file: file, edition: slot.edition))
                let note = n == 0 ? [] : [prot ? "\(n) saved password\(n == 1 ? "" : "s"), protected by the passphrase"
                                               : "\(n) saved password\(n == 1 ? "" : "s"), not protected"]
                lines.append(SuiteBackupLine(id: target.id, name: target.name, state: .ok, detail: note))
            case .unsupported:
                try? fm.removeItem(at: staging.appendingPathComponent(file))
                lines.append(SuiteBackupLine(id: target.id, name: target.name, state: .unsupported,
                                             detail: [SettingsCLIClassify.unsupportedMessage]))
            case .failed(let m):
                try? fm.removeItem(at: staging.appendingPathComponent(file))
                lines.append(SuiteBackupLine(id: target.id, name: target.name, state: .failed, detail: [m]))
            }
        }
        var launcherFileName: String?
        if includeLauncher {
            progress("Backing up JB Theatre Tools\u{2026}")
            do {
                let doc = try LauncherSettings.document(prefs: prefs, secrets: secrets, version: launcherVersion,
                                                        includeSecrets: includeSecrets, passphrase: pass)
                try atomicWrite(try jsonData(doc), to: staging.appendingPathComponent(launcherFile))
                launcherFileName = launcherFile
                let n = ((doc["secrets"] as? [String: Any])?["count"] as? NSNumber)?.intValue ?? 0
                lines.append(SuiteBackupLine(id: launcherId, name: launcherName, state: .ok,
                                             detail: n == 0 ? [] : ["\(n) saved password\(n == 1 ? "" : "s")"
                                                                    + (pass != nil ? ", protected by the passphrase" : ", not protected")]))
            } catch {
                lines.append(SuiteBackupLine(id: launcherId, name: launcherName, state: .failed, detail: [error.localizedDescription]))
            }
        }
        guard !entries.isEmpty || launcherFileName != nil else {
            throw Failure.other("Nothing could be backed up, so no file was saved.")
        }
        let manifest = SuiteBundleManifest(created: isoNow(), source: source(), launcherVersion: launcherVersion,
                                           apps: entries, launcherFile: launcherFileName)
        try atomicWrite(try jsonData(manifest.json), to: staging.appendingPathComponent(manifestFile))
        progress("Saving the backup\u{2026}")
        try SuiteBundle.write(staging: staging, to: dest)
        return lines
    }

    /// Restores one app from its file in an opened bundle, trying each installed edition until one answers.
    static func restoreApp(file: URL, slots: [BackupSlot], passphrase: String?, restoreSecrets: Bool) async -> AppImportOutcome {
        var last: AppImportOutcome = .unsupported
        for slot in slots {
            var args = ["--settings-import", file.path]
            if !restoreSecrets { args.append("--no-secrets") }
            let r = await SettingsCLI.run(slot.executable, args, passphrase: passphrase ?? "", timeout: runTimeout)
            let outcome = SettingsCLIClassify.importResult(r)
            if outcome != .unsupported { return outcome }
            last = outcome
        }
        return last
    }

    /// The combined-result line for one app's restore outcome.
    static func line(id: String, name: String, _ outcome: AppImportOutcome) -> SuiteBackupLine {
        switch outcome {
        case .ok(let attention, let warnings, let restart):
            var detail = attention + warnings
            if restart { detail.append("Restart \(name) to use the restored settings.") }
            return SuiteBackupLine(id: id, name: name, state: attention.isEmpty && !restart ? .ok : .attention, detail: detail)
        case .passphrase(_, let m): return SuiteBackupLine(id: id, name: name, state: .retry, detail: [m])
        case .appOpen(let m): return SuiteBackupLine(id: id, name: name, state: .retry, detail: [m])
        case .unsupported:
            return SuiteBackupLine(id: id, name: name, state: .unsupported,
                                   detail: ["This app's version can't restore its settings yet \u{2014} update it, then try again."])
        case .failed(let m): return SuiteBackupLine(id: id, name: name, state: .failed, detail: [m])
        }
    }
}
