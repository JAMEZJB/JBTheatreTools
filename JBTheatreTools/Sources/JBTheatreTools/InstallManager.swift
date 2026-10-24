import Foundation
import CryptoKit
import AppKit   // NSWorkspace.runningApplications — refuse to replace a bundle that's open (audit F4)

/// Outcome of integrity-checking a downloaded asset (verify-if-present): the file passed the SHA-256
/// check, the release published no `SHA256SUMS` manifest at all, or it published one but this asset
/// isn't listed in it (e.g. a name mismatch). The two "unverified" cases are distinguished so the
/// user/logs can tell "no checksums" from "checksums exist but don't cover this file".
enum VerifyResult: Equatable {
    /// Suite-signed manifest, asset listed, hash matches — the only fully trusted outcome.
    case verified
    /// The release publishes no SHA256SUMS at all.
    case noManifest
    /// SHA256SUMS exists but doesn't list this asset.
    case assetNotListed
    /// Asset listed and hash matches, but the manifest carries no suite signature — so it only proves
    /// transport integrity, not authenticity. Accepted for explicit older-tag installs only (audit F1).
    case unsigned
}

struct InstalledRecord: Codable {
    var version: String
    var path: String
    var installedAt: String
    /// Path of the Finder alias this launcher created on the user's Desktop (nil = none).
    /// Optional so manifests written by older versions decode unchanged.
    var desktopAlias: String?
    /// Which variant (e.g. "standard" / "full") is installed, for apps that ship variants.
    /// Optional so manifests written by older versions decode unchanged (nil = single-variant app).
    var variant: String?
    /// The version this slot held before the most recent install changed it — the one-click "Roll Back" target.
    /// Nil until an install replaces a different version (older manifests have no field).
    var previousVersion: String?
}

enum InstallError: LocalizedError {
    case noAppInZip
    case notInstalled
    case unzipFailed(Int32)
    case sizeMismatch(expected: Int, got: Int)
    case checksumMismatch(String)
    case unverified(reason: String)
    /// The app being updated/relocated is currently running — replacing its bundle would break the
    /// running process mid-use (audit F4).
    case appRunning(String)
    /// Something the launcher didn't install already occupies the destination — don't destroy it (F6).
    case destinationOccupied(String)
    /// The app is set to run as Intel (or only has an Intel build) and Rosetta isn't installed on this Mac.
    case rosettaMissing

    var errorDescription: String? {
        switch self {
        case .noAppInZip: return "Downloaded archive did not contain a .app bundle."
        case .notInstalled: return "App is not installed."
        case .rosettaMissing: return "This app runs as Intel, which needs Rosetta — and Rosetta isn't installed on this Mac."
        case .unzipFailed(let code): return "Could not extract the archive (ditto exit \(code))."
        case .sizeMismatch(let expected, let got):
            return "Download is \(got) bytes but the release lists \(expected). Aborting install."
        case .checksumMismatch(let name):
            return "Checksum mismatch for \(name) — the download does not match the release's SHA256SUMS. Aborting install."
        case .unverified(let reason):
            return "Couldn't verify this download — \(reason). Install aborted for safety."
        case .appRunning(let name):
            return "Quit \(name) before updating it, then try again."
        case .destinationOccupied(let path):
            return "An app JB Theatre Tools didn't install is already at \(path). Remove or rename it first, then try again."
        }
    }
}

/// Pure, unit-tested decisions behind the install-safety guards (audit F4 + F6), so the FileManager /
/// NSWorkspace glue that calls them stays a thin, reasoned-about layer.
enum InstallGuard {
    /// True when `target` is one of the currently-running app bundles (path-normalised so a trailing
    /// slash or `.` doesn't defeat the match).
    static func isRunning(_ target: URL, amongRunning running: [URL]) -> Bool {
        let t = target.standardizedFileURL.path
        return running.contains { $0.standardizedFileURL.path == t }
    }

    /// The bundles of the apps running right now that a person has open: a headless helper (Claude's `--mcp` connector,
    /// a settings back up / restore, a self-check) isn't "open". Nobody can quit it, and it restarts itself on the new
    /// version.
    static func openBundles() -> [URL] {
        NSWorkspace.shared.runningApplications.compactMap { app in
            guard let url = app.bundleURL else { return nil }
            if let args = processArguments(app.processIdentifier), isHeadlessHelper(args) { return nil }
            return url
        }
    }

    static let headlessSwitches = ["--mcp", "--remote", "--describe", "--settings-describe", "--settings-export",
                                   "--settings-import", "--selfcheck", "--trigger-host", "--trigger-hosts"]

    /// True when a process's arguments run one of the headless helper modes.
    static func isHeadlessHelper(_ args: [String]) -> Bool {
        args.contains { a in headlessSwitches.contains { a.caseInsensitiveCompare($0) == .orderedSame || a.lowercased().hasPrefix($0 + "=") } }
    }

    /// A process's arguments (argv after the program), from the kernel; nil when they can't be read.
    static func processArguments(_ pid: pid_t) -> [String]? {
        var mib: [Int32] = [CTL_KERN, KERN_PROCARGS2, pid]
        var size = 0
        guard sysctl(&mib, 3, nil, &size, nil, 0) == 0, size > MemoryLayout<Int32>.size else { return nil }
        var buffer = [UInt8](repeating: 0, count: size)
        guard sysctl(&mib, 3, &buffer, &size, nil, 0) == 0 else { return nil }
        // argc, then the executable path, NUL padding, then argc NUL-terminated strings (argv[0] first).
        let argc = buffer.withUnsafeBytes { $0.load(as: Int32.self) }
        var i = MemoryLayout<Int32>.size
        while i < size, buffer[i] != 0 { i += 1 }   // the executable path
        while i < size, buffer[i] == 0 { i += 1 }   // its padding
        var args: [String] = []
        while i < size, args.count < Int(argc) {
            let start = i
            while i < size, buffer[i] != 0 { i += 1 }
            args.append(String(decoding: buffer[start..<i], as: UTF8.self))
            i += 1
        }
        return Array(args.dropFirst())
    }

    /// True when something occupies `destPath` that ISN'T the install this launcher recorded for the
    /// slot (`ourPath`) — i.e. replacing it would destroy a stranger. Nothing at dest → not a stranger.
    static func isStranger(destExists: Bool, destPath: String, ourPath: String?) -> Bool {
        destExists && ourPath != destPath
    }
}

/// Installs / tracks / launches the downloaded macOS apps.
///
/// macOS assets are `.zip` files containing a `.app`. Install = extract → move the `.app` into
/// the apps dir → record the version in a JSON manifest. Launch = `open` the bundle.
/// `@unchecked Sendable`: `AppState.install` runs the extract+move on a detached task so a 300 MB `ditto`
/// never blocks the main thread; the manifest is lock-guarded and the rest is per-call local state.
///
/// Every change to installed.json, the installed bundles and connectors.json holds `lock`, shared with every other
/// launcher process on this Mac (the window and a command-line install can run at once), and starts from the
/// manifest on disk — never this process's copy, which may predate another process's install.
final class InstallManager: @unchecked Sendable {
    static let shared = InstallManager()

    private let fm = FileManager.default
    let supportDir: URL
    let appsDir: URL
    let cacheDir: URL
    let manifestURL: URL
    let lock: SupportLock
    /// Stand-ins in ~/Applications/JB Theatre Tools that make launcher-folder apps findable in Spotlight and Alfred.
    let standIns: SearchStandIns
    private var connectorsURL: URL { supportDir.appendingPathComponent("connectors.json") }

    convenience init() {
        // JBTT_SUPPORT_DIR: a test stand-in for the whole support folder (installed.json, apps, connectors.json).
        let testSupport = ProcessInfo.processInfo.environment["JBTT_SUPPORT_DIR"].flatMap { $0.isEmpty ? nil : $0 }
        self.init(supportDir: testSupport.map { URL(fileURLWithPath: $0, isDirectory: true) })
    }

    /// `supportDir` nil = the real one; a folder = a stand-in that also holds the download cache (tests).
    init(supportDir testSupport: URL?) {
        let support = testSupport
            ?? fm.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("JBTheatreTools", isDirectory: true)
        supportDir = support
        appsDir = support.appendingPathComponent("apps", isDirectory: true)
        manifestURL = support.appendingPathComponent("installed.json")
        cacheDir = testSupport != nil ? support.appendingPathComponent("cache", isDirectory: true)
            : fm.urls(for: .cachesDirectory, in: .userDomainMask)[0].appendingPathComponent("JBTheatreTools", isDirectory: true)
        lock = SupportLock(directory: support)
        standIns = SearchStandIns(root: SearchStandIns.defaultRoot(supportDir: support, isTestSupport: testSupport != nil),
                                  appsDir: support.appendingPathComponent("apps", isDirectory: true))
        try? fm.createDirectory(at: appsDir, withIntermediateDirectories: true)
        try? fm.createDirectory(at: cacheDir, withIntermediateDirectories: true)
    }

    // MARK: - Manifest

    /// In-memory cache of the manifest. `manifest()` is on the hot path — the launcher UI calls
    /// `installedPath` / `installedVersion` from `AppIconImage` and every row on each redraw, and a
    /// drag-reorder redraws all rows per frame. Reading + JSON-decoding `installed.json` from disk on
    /// every one of those calls was blocking the main thread and making drag-reorder stutter. The in-memory
    /// copy (guarded by `manifestLock` for the off-main-actor callers) is current between this process's
    /// changes; every change re-reads the file under `lock` first, so another launcher process's install
    /// is never written over (and shows here from then on).
    private let manifestLock = NSLock()
    private var manifestCache: [String: InstalledRecord]?
    /// Resolution caches for the render hot path. `installedPath`/`installedVersion` (called by every row and
    /// icon on EVERY redraw) used to `fileExists`-stat the disk each call, and `installedDisplayName` re-read
    /// and re-parsed the bundle Info.plist each call — so a boot update-check (dozens of list re-renders ×
    /// dozens of apps) fired hundreds of synchronous disk hits on the main thread. These cache the resolved
    /// path (existence checked once) and display name per app id; both are cleared on any manifest write
    /// (install/uninstall/relocate), which is the only thing that can change what's on disk here.
    private var resolvedPath: [String: URL] = [:]
    private var resolvedPathDone = Set<String>()
    private var resolvedName: [String: String] = [:]
    private var resolvedNameDone = Set<String>()

    func manifest() -> [String: InstalledRecord] {
        manifestLock.lock(); defer { manifestLock.unlock() }
        return manifestUnlocked()
    }

    /// The manifest without taking the lock — for callers that already hold `manifestLock`.
    private func manifestUnlocked() -> [String: InstalledRecord] {
        if let cached = manifestCache { return cached }
        let m = readManifestFile()
        manifestCache = m
        return m
    }

    private func readManifestFile() -> [String: InstalledRecord] {
        guard let data = try? Data(contentsOf: manifestURL),
              let decoded = try? JSONDecoder().decode([String: InstalledRecord].self, from: data) else { return [:] }
        return decoded
    }

    /// The manifest as it is on disk now, adopted as this process's copy. The start of every change, under `lock`.
    private func reloadManifest() -> [String: InstalledRecord] {
        let m = readManifestFile()
        adopt(m)
        return m
    }

    /// The catalog, once known (set at start-up by the app and the command line): every manifest change then also
    /// refreshes connectors.json, so Stagehand's stable entry points follow installs, updates, removals and moves.
    private var connectorAppsValue: [CatalogApp] = []
    var connectorApps: [CatalogApp] {
        manifestLock.lock(); defer { manifestLock.unlock() }
        return connectorAppsValue
    }

    /// Starts following the manifest with connectors.json and brings it in step now — which also repairs what
    /// another launcher process left behind. May wait for the lock: the window calls it off the main thread.
    func followConnectors(_ apps: [CatalogApp], wait: TimeInterval = SupportLock.defaultWait) {
        manifestLock.lock(); connectorAppsValue = apps; manifestLock.unlock()
        syncConnectors(wait: wait)
    }

    /// Rewrites connectors.json and the search stand-ins from the manifest on disk, holding the lock (another process
    /// may be installing). Also repairs what another process, or an older launcher, left behind.
    func syncConnectors(wait: TimeInterval = SupportLock.defaultWait) {
        let apps = connectorApps
        do {
            try lock.hold(wait: wait) {
                let m = reloadManifest()
                if !apps.isEmpty { Connectors.sync(apps: apps, manifest: m, at: connectorsURL) }
                syncStandIns(m, apps: apps)
            }
        }
        catch { AppLog.shared.log("connectors.json: \(error.localizedDescription)") }
    }

    /// Caller holds `lock` (and started from `reloadManifest()`). Atomic, so another process never reads half a file.
    private func writeManifest(_ m: [String: InstalledRecord]) {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        if let data = try? enc.encode(m) { try? data.write(to: manifestURL, options: .atomic) }
        adopt(m)
        let apps = connectorApps
        if !apps.isEmpty { Connectors.sync(apps: apps, manifest: m, at: connectorsURL) }
        syncStandIns(m, apps: apps)   // Spotlight / Alfred stand-ins follow every install, update, move and removal
    }

    /// Only with the catalog known (its names are the ones people search for); without it nothing is made or removed.
    private func syncStandIns(_ m: [String: InstalledRecord], apps: [CatalogApp]) {
        guard !apps.isEmpty else { return }
        var names: [String: String] = [:]
        for app in apps {
            let editions: [String?] = app.hasVariants ? (app.variants ?? []).map(\.id) : [nil]
            for vid in editions { names[app.installKey(variantId: vid)] = app.name + app.variantSuffix(vid) }
        }
        standIns.sync(m, names: names)
    }

    /// The catalog, set as soon as it's loaded (before the connector sync runs in the background), so the first
    /// install-list write already names the stand-ins.
    func useCatalog(_ apps: [CatalogApp]) {
        manifestLock.lock(); connectorAppsValue = apps; manifestLock.unlock()
    }

    /// installed.json as it is on disk now, adopted as this process's copy: for the window when another launcher process
    /// (the command line, which Stagehand runs) has installed, updated or removed something.
    func reloadFromDisk() { _ = reloadManifest() }

    /// Makes `m` this process's copy of the manifest.
    private func adopt(_ m: [String: InstalledRecord]) {
        manifestLock.lock(); defer { manifestLock.unlock() }
        // Invalidate only the slots whose record actually changed (path or version) — wiping every entry made
        // the next render re-stat all 21 installed paths on the main thread after each install.
        let old = manifestCache ?? [:]
        for key in Set(old.keys).union(m.keys)
        where old[key]?.path != m[key]?.path || old[key]?.version != m[key]?.version {
            resolvedPath[key] = nil; resolvedPathDone.remove(key)
            resolvedName[key] = nil; resolvedNameDone.remove(key)
        }
        manifestCache = m
    }

    /// The installed path if the recorded bundle still exists — existence checked once, then cached (the lock
    /// makes it safe for off-main-actor CLI callers too). `manifestLock` is non-recursive, so this resolves
    /// everything under a single lock via `manifestUnlocked()`.
    /// The running copy of an install slot's app, if it's open right now (for the "quit it first?" prompt).
    func runningInstance(_ key: String) -> NSRunningApplication? {
        guard let path = installedPath(key) else { return nil }
        return NSWorkspace.shared.runningApplications.first { app in
            guard let url = app.bundleURL, InstallGuard.isRunning(path, amongRunning: [url]) else { return false }
            return !(InstallGuard.processArguments(app.processIdentifier).map(InstallGuard.isHeadlessHelper) ?? false)
        }
    }

    func installedPath(_ appId: String) -> URL? {
        manifestLock.lock(); defer { manifestLock.unlock() }
        return resolvedPathUnlocked(appId)
    }

    private func resolvedPathUnlocked(_ appId: String) -> URL? {
        if resolvedPathDone.contains(appId) { return resolvedPath[appId] }
        resolvedPathDone.insert(appId)
        guard let rec = manifestUnlocked()[appId] else { return nil }
        let url = URL(fileURLWithPath: rec.path)
        guard fm.fileExists(atPath: url.path) else { return nil }
        resolvedPath[appId] = url
        return url
    }

    func installedVersion(_ appId: String) -> String? {
        manifestLock.lock(); defer { manifestLock.unlock() }
        guard let rec = manifestUnlocked()[appId] else { return nil }
        return resolvedPathUnlocked(appId) != nil ? rec.version : nil
    }

    /// The installed app's own display name, read from its bundle `Info.plist` (the authoritative "what this
    /// app calls itself" — so an installed row is never wrong). Parsed once per app id, then cached.
    func installedDisplayName(_ appId: String) -> String? {
        manifestLock.lock(); defer { manifestLock.unlock() }
        if resolvedNameDone.contains(appId) { return resolvedName[appId] }
        resolvedNameDone.insert(appId)
        guard let appURL = resolvedPathUnlocked(appId) else { return nil }
        let plistURL = appURL.appendingPathComponent("Contents/Info.plist")
        guard let data = try? Data(contentsOf: plistURL),
              let obj = try? PropertyListSerialization.propertyList(from: data, format: nil),
              let dict = obj as? [String: Any] else { return nil }
        let name = (dict["CFBundleDisplayName"] as? String) ?? (dict["CFBundleName"] as? String)
        let trimmed = name?.trimmingCharacters(in: .whitespacesAndNewlines)
        let resolved = (trimmed?.isEmpty == false) ? trimmed : nil
        if let resolved { resolvedName[appId] = resolved }
        return resolved
    }

    // MARK: - Install / launch

    /// Extracts `downloadedZip` (a macOS app archive) and installs the contained `.app`.
    /// When `toApplications` is true the bundle is placed in the Applications folder (so it shows in
    /// Launchpad/Spotlight and launches without this launcher); otherwise in the managed apps dir.
    /// One-time migration from v1.15.0, where a non-default variant (e.g. NDI "Full") was recorded under
    /// the plain app id. Re-keys such a record to its own variant slot and renames the bundle with the
    /// variant suffix, so a later default-variant install can't clobber it. No-op when nothing matches.
    func migrateVariantSlots(_ apps: [CatalogApp], wait: TimeInterval = SupportLock.defaultWait) {
        // Almost always nothing to do: only take the shared lock (which may wait on another process) when there is.
        func pending(_ m: [String: InstalledRecord]) -> Bool {
            apps.contains { app in
                app.hasVariants && m[app.id].map { r in r.variant.map { !app.isDefaultVariant($0) } ?? false } == true
            }
        }
        guard pending(manifest()) else { return }
        do { try lock.hold(wait: wait) { migrateVariantSlotsHeld(apps) } }
        catch { AppLog.shared.log("variant slots not migrated now: \(error.localizedDescription)") }
    }

    private func migrateVariantSlotsHeld(_ apps: [CatalogApp]) {
        var m = reloadManifest()
        var changed = false
        for app in apps where app.hasVariants {
            guard let rec = m[app.id], let vid = rec.variant, !app.isDefaultVariant(vid) else { continue }
            let key = app.installKey(variantId: vid)
            guard m[key] == nil else { continue }   // that slot already has its own record — leave both alone
            var moved = rec
            let suffix = app.variantSuffix(vid)
            let current = URL(fileURLWithPath: rec.path)
            if !suffix.isEmpty, fm.fileExists(atPath: rec.path) {
                let dest = current.deletingLastPathComponent()
                    .appendingPathComponent(current.deletingPathExtension().lastPathComponent + suffix + ".app")
                if (try? fm.moveItem(at: current, to: dest)) != nil { moved.path = dest.path }
            }
            m[key] = moved
            m.removeValue(forKey: app.id)
            changed = true
        }
        if changed { writeManifest(m) }
    }

    @discardableResult
    func install(app: CatalogApp, version: String, downloadedZip: URL, toApplications: Bool, variant: String? = nil) throws -> URL {
        // Its own folder: another launcher process may be extracting the same app into this cache at the same time.
        let extractDir = cacheDir.appendingPathComponent("extract-\(app.id)-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: extractDir, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: extractDir) }

        try ditto(extract: downloadedZip, to: extractDir)

        guard let bundle = appBundle(in: extractDir, preferring: app.name) else { throw InstallError.noAppInZip }

        // Each variant is its own install slot (Light and Full can coexist), keyed by the app id for
        // the default variant and `<id>@<variant>` otherwise. Only the previous install of THIS slot is
        // touched — it may be in a different location if the setting changed.
        let key = app.installKey(variantId: variant)
        let destDir = toApplications ? applicationsInstallDir() : appsDir
        try? fm.createDirectory(at: destDir, withIntermediateDirectories: true)
        // A non-default variant's bundle is renamed (e.g. "NDI Tools (Full).app") so it can sit next to
        // the default variant's bundle in the same folder (the launcher dir or /Applications).
        let suffix = app.variantSuffix(variant)
        let bundleName = suffix.isEmpty
            ? bundle.lastPathComponent
            : bundle.deletingPathExtension().lastPathComponent + suffix + ".app"
        let dest = destDir.appendingPathComponent(bundleName)
        // From here to the manifest write, another launcher process mustn't change what's installed: it could
        // record this slot elsewhere, or write installed.json from a copy without this install.
        return try lock.hold {
            var m = reloadManifest()
            let ourPath = m[key]?.path

            let running = InstallGuard.openBundles()
            // F4: never replace a bundle that's currently running — our old install, or whatever sits at dest.
            for candidate in ([ourPath.map { URL(fileURLWithPath: $0) }, dest].compactMap { $0 }) {
                if InstallGuard.isRunning(candidate, amongRunning: running) { throw InstallError.appRunning(app.name) }
            }
            // F6: a bundle at dest that we didn't install is a stranger — surface it, don't destroy it.
            if InstallGuard.isStranger(destExists: fm.fileExists(atPath: dest.path), destPath: dest.path, ourPath: ourPath) {
                throw InstallError.destinationOccupied(dest.path)
            }
            // Remove our old install (may be elsewhere if the location setting changed) and our own dest copy.
            if let ourPath, ourPath != dest.path { try? fm.removeItem(at: URL(fileURLWithPath: ourPath)) }
            if fm.fileExists(atPath: dest.path) { try? fm.removeItem(at: dest) }   // reached only for our own copy
            try fm.moveItem(at: bundle, to: dest)

            // Preserve any desktop-alias path already recorded for this slot; update version/path/variant, and remember
            // what this install replaced (a reinstall of the same version keeps the older one).
            let prior = m[key]
            let previous: String? = prior.flatMap { p in
                VersionDisplay.equal(p.version, version) ? p.previousVersion : p.version
            }
            m[key] = InstalledRecord(version: version, path: dest.path, installedAt: Self.isoNow(),
                                     desktopAlias: prior?.desktopAlias, variant: variant, previousVersion: previous)
            writeManifest(m)
            return dest
        }
    }


    /// Where "install to the Applications folder" puts apps: `/Applications` when it's writable (admin
    /// users), otherwise `~/Applications`. Both appear in Launchpad & Spotlight, and neither needs an
    /// admin password — so non-admin users get a working install without a prompt.
    private func applicationsInstallDir() -> URL {
        let system = URL(fileURLWithPath: "/Applications", isDirectory: true)
        if fm.isWritableFile(atPath: system.path) { return system }
        return fm.homeDirectoryForCurrentUser.appendingPathComponent("Applications", isDirectory: true)
    }

    // MARK: - Relocation (keep all installs in the location the user picked)

    /// True if the installed app's bundle is NOT where the current `toApplications` setting wants it.
    func needsRelocation(_ appId: String, toApplications: Bool) -> Bool {
        guard let rec = manifest()[appId], fm.fileExists(atPath: rec.path) else { return false }
        let parent = URL(fileURLWithPath: rec.path).deletingLastPathComponent().standardizedFileURL.path
        return toApplications ? !isApplicationsParent(parent)
                              : parent != appsDir.standardizedFileURL.path
    }

    /// Moves an installed app's `.app` bundle to match the `toApplications` setting and updates the
    /// manifest path. No-op if it's already in the right place. Throws if the move fails (e.g. the app
    /// is running) so the caller can report it.
    func relocate(_ appId: String, toApplications: Bool) throws {
        try lock.hold {
            var m = reloadManifest()
            guard needsRelocation(appId, toApplications: toApplications), let rec = m[appId] else { return }
            let current = URL(fileURLWithPath: rec.path)
            let targetDir = toApplications ? applicationsInstallDir() : appsDir
            try fm.createDirectory(at: targetDir, withIntermediateDirectories: true)
            let dest = targetDir.appendingPathComponent(current.lastPathComponent)
            let running = InstallGuard.openBundles()
            // F4: don't move a bundle that's currently running (breaks the live process).
            if InstallGuard.isRunning(current, amongRunning: running) {
                throw InstallError.appRunning(installedDisplayName(appId) ?? appId)
            }
            if dest.standardizedFileURL.path != current.standardizedFileURL.path {
                // F6: don't destroy a stranger already sitting at the destination.
                if InstallGuard.isStranger(destExists: fm.fileExists(atPath: dest.path), destPath: dest.path, ourPath: rec.path) {
                    throw InstallError.destinationOccupied(dest.path)
                }
                if fm.fileExists(atPath: dest.path) { try? fm.removeItem(at: dest) }
            }
            let wasPinned = Dock.isPinned(rec.path)
            try fm.moveItem(at: current, to: dest)
            // A Dock tile stores the absolute path, so re-pin at the new location (one Dock restart).
            // Desktop aliases are bookmark-based and follow the move on their own.
            if wasPinned {
                Dock.unpin(rec.path, restartDock: false)
                Dock.pin(dest.path)
            }
            m[appId]?.path = dest.path
            writeManifest(m)
        }
    }

    private func isApplicationsParent(_ path: String) -> Bool {
        let user = fm.homeDirectoryForCurrentUser.appendingPathComponent("Applications").standardizedFileURL.path
        return path == "/Applications" || path == user
    }

    /// Launches the app's default-variant slot (CLI / single-variant apps).
    func launch(app: CatalogApp) throws { try launch(installKey: app.id) }

    /// Launches a specific install slot (the row's selected variant).
    func launch(installKey: String) throws {
        guard let path = installedPath(installKey) else { throw InstallError.notInstalled }
        let archs = MachO.archs(ofApp: path)
        // Set to run as Intel: a universal app is opened as Intel (`open --arch x86_64`); an Intel-only build runs
        // through Rosetta by itself. Either way Rosetta must be there — say so rather than failing silently.
        let asIntel = MacArch.prefersIntel(installKey) && archs.contains("x86_64") && archs.contains("arm64")
        let translated = MacArch.isAppleSilicon && (asIntel || (archs == ["x86_64"]))
        if translated, !MacArch.rosettaInstalled { throw InstallError.rosettaMissing }
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        proc.arguments = asIntel ? ["--arch", "x86_64", path.path] : [path.path]
        try proc.run()
    }

    /// Removes the installed `.app` bundle, any Desktop alias / Dock pin we created, and the
    /// manifest entry.
    func uninstall(_ appId: String) throws {
        try lock.hold {
            var m = reloadManifest()
            if let rec = m[appId] {
                if let alias = rec.desktopAlias { try? fm.removeItem(at: URL(fileURLWithPath: alias)) }
                Dock.unpin(rec.path)
                try? fm.removeItem(at: URL(fileURLWithPath: rec.path))
            }
            m.removeValue(forKey: appId)
            writeManifest(m)
        }
    }

    // MARK: - Desktop alias (per-app, user-requested from the row menu)

    /// True when this launcher has created a Desktop alias for the app and it still exists.
    func hasDesktopAlias(_ appId: String) -> Bool {
        guard let alias = manifest()[appId]?.desktopAlias else { return false }
        return fm.fileExists(atPath: alias)
    }

    /// Creates a Finder alias to the installed app on the user's Desktop (bookmark-based, so it
    /// keeps working if the app is later relocated between install locations).
    func addDesktopAlias(_ appId: String) throws {
        guard let appURL = installedPath(appId) else { throw InstallError.notInstalled }
        let desktop = fm.urls(for: .desktopDirectory, in: .userDomainMask)[0]
        let name = appURL.deletingPathExtension().lastPathComponent
        let aliasURL = desktop.appendingPathComponent(name)
        let data = try appURL.bookmarkData(options: .suitableForBookmarkFile,
                                           includingResourceValuesForKeys: nil, relativeTo: nil)
        try lock.hold {
            try? fm.removeItem(at: aliasURL)
            try URL.writeBookmarkData(data, to: aliasURL)
            var m = reloadManifest()
            m[appId]?.desktopAlias = aliasURL.path
            writeManifest(m)
        }
    }

    func removeDesktopAlias(_ appId: String) throws {
        try lock.hold {
            var m = reloadManifest()
            if let alias = m[appId]?.desktopAlias { try? fm.removeItem(at: URL(fileURLWithPath: alias)) }
            m[appId]?.desktopAlias = nil
            writeManifest(m)
        }
    }

    // MARK: - Details, storage, free space

    /// A copy of one slot's manifest record (for the details view and roll back), or nil.
    func record(_ key: String) -> InstalledRecord? { manifest()[key] }

    /// Bytes on disk for one installed slot's bundle. Walks the disk — call it off the main actor.
    func sizeOnDisk(_ key: String) -> Int64 {
        guard let path = installedPath(key) else { return 0 }
        return Self.directorySize(path)
    }

    /// Bytes on disk for every installed slot (wherever each bundle lives). Off the main actor.
    func installedSize() -> Int64 {
        ByteSize.sum(manifest().keys.map { sizeOnDisk($0) })
    }

    /// Bytes in the download cache (downloads awaiting verification, leftovers of interrupted ones).
    func cacheSize() -> Int64 { Self.directorySize(cacheDir) }

    /// Empties the download cache. Only call it when nothing is downloading or installing (the caller checks):
    /// installs extract inside the cache.
    func clearCache() {
        let items = (try? fm.contentsOfDirectory(at: cacheDir, includingPropertiesForKeys: nil)) ?? []
        for item in items { try? fm.removeItem(at: item) }
    }

    /// Total allocated size of the files under `url` (a bundle or folder); a plain file's own size; 0 if absent.
    static func directorySize(_ url: URL) -> Int64 {
        let fm = FileManager.default
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: url.path, isDirectory: &isDir) else { return 0 }
        let keys: [URLResourceKey] = [.isRegularFileKey, .totalFileAllocatedSizeKey, .fileSizeKey]
        if !isDir.boolValue {
            let v = try? url.resourceValues(forKeys: Set(keys))
            return Int64(v?.totalFileAllocatedSize ?? v?.fileSize ?? 0)
        }
        guard let walker = fm.enumerator(at: url, includingPropertiesForKeys: keys, options: [], errorHandler: { _, _ in true })
        else { return 0 }
        var total: Int64 = 0
        for case let file as URL in walker {
            guard let v = try? file.resourceValues(forKeys: Set(keys)), v.isRegularFile == true else { continue }
            total += Int64(v.totalFileAllocatedSize ?? v.fileSize ?? 0)
        }
        return total
    }

    /// Free bytes on the volume holding the download cache, or -1 if it can't be read. On macOS this is the
    /// "important usage" figure (it counts space the system can purge for an install, as Finder does).
    func freeSpace() -> Int64 {
        #if os(macOS)
        if let v = try? cacheDir.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]),
           let bytes = v.volumeAvailableCapacityForImportantUsage { return bytes }
        #endif
        if let attrs = try? fm.attributesOfFileSystem(forPath: cacheDir.path),
           let free = attrs[.systemFreeSize] as? NSNumber { return free.int64Value }
        return -1
    }

    // MARK: - Helpers

    /// Unpacks a zip with `ditto` (also the launcher's own self-update).
    func dittoExtract(_ zip: URL, to dir: URL) throws { try ditto(extract: zip, to: dir) }

    private func ditto(extract zip: URL, to dir: URL) throws {
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        proc.arguments = ["-x", "-k", zip.path, dir.path]
        try proc.run()
        proc.waitUntilExit()
        if proc.terminationStatus != 0 { throw InstallError.unzipFailed(proc.terminationStatus) }
    }

    /// Finds the `.app` bundle to install in `dir`, ignoring the `__MACOSX` metadata folder. When an
    /// archive contains more than one bundle, prefer the one whose name matches the catalog name, then
    /// fall back to alphabetical order so the choice is **deterministic** (rather than depending on the
    /// unspecified order of `contentsOfDirectory`, which could install a helper bundle non-reproducibly).
    func appBundle(in dir: URL, preferring expectedName: String) -> URL? {
        guard let items = try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil) else { return nil }
        let bundles = items
            .filter { $0.pathExtension == "app" && $0.lastPathComponent != "__MACOSX" }
            .sorted { $0.lastPathComponent.localizedCaseInsensitiveCompare($1.lastPathComponent) == .orderedAscending }
        let wanted = expectedName.trimmingCharacters(in: .whitespacesAndNewlines)
        if let match = bundles.first(where: {
            $0.deletingPathExtension().lastPathComponent.compare(wanted, options: .caseInsensitive) == .orderedSame
        }) { return match }
        return bundles.first
    }

    // MARK: - Download integrity (SHA-256)

    /// Returns the expected hex SHA-256 for `assetName` from a `SHA256SUMS` file body
    /// (standard `<hex>␠␠<filename>` lines), or nil if the asset isn't listed.
    static func expectedSHA256(forAsset assetName: String, inSums text: String) -> String? {
        for raw in text.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard let sep = line.firstIndex(where: { $0 == " " || $0 == "\t" }) else { continue }
            let hash = String(line[..<sep])
            var name = String(line[line.index(after: sep)...]).trimmingCharacters(in: .whitespaces)
            if name.hasPrefix("*") { name.removeFirst() }   // sha256sum "binary mode" marker
            if name == assetName { return hash }
        }
        return nil
    }

    /// Streams `url` through SHA-256 and returns the lowercase hex digest.
    static func sha256Hex(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    /// Verifies `file` against a `SHA256SUMS` body. Returns true when checksum-verified, false when the
    /// asset isn't listed (caller treats as "unverified, proceed"); throws `.checksumMismatch` on a real
    /// mismatch.
    static func verify(file: URL, assetName: String, sums: String) throws -> Bool {
        guard let expected = expectedSHA256(forAsset: assetName, inSums: sums) else { return false }
        let actual = try sha256Hex(of: file)
        guard actual.caseInsensitiveCompare(expected) == .orderedSame else {
            throw InstallError.checksumMismatch(assetName)
        }
        return true
    }

    private static func isoNow() -> String {
        let f = ISO8601DateFormatter()
        return f.string(from: Date())
    }
}
