import AppKit

/// Launcher self-update, in place (macOS; the Windows launcher does the same with its exe). The new build is downloaded
/// and verified (size + suite-signed SHA256SUMS + hash) into the launcher's own data folder, unpacked there, and swapped
/// in for the RUNNING app bundle — same folder, same name, so the Dock, Launchpad and Spotlight keep finding it — and the
/// launcher restarts into it. A running bundle can be renamed: the old one steps aside as "<name>.old" and the new
/// launcher moves it to the Bin once it's up. The old launcher waits for the new one's window; if the new build quits
/// instead, the old bundle is put back. Where the folder can't be changed (a standard user in /Applications, or a copy
/// macOS runs from a temporary read-only place), the verified build is saved to Downloads instead, as before.
enum SelfUpdate {
    /// Set on the restarted launcher: "<previous pid>|<old bundle path>|<ready file>".
    static let afterUpdateVar = "JBTT_AFTER_UPDATE"

    enum Failure: LocalizedError {
        /// macOS runs this copy from a temporary read-only place (App Translocation: opened straight from a download).
        case translocated
        /// The folder the launcher lives in can't be changed by this user.
        case notWritable(String)
        case notTheLauncher
        case changedAfterVerify
        case cantRunHere(String)

        var errorDescription: String? {
            switch self {
            case .translocated:
                return "macOS is running JB Theatre Tools from a temporary copy (it was opened straight from the download), so it can't update itself. Move it to your Applications folder, open it from there, and it will."
            case .notWritable(let folder):
                return "JB Theatre Tools can't replace itself in \(folder) — this Mac user can't change files there."
            case .notTheLauncher:
                return "The download doesn't contain JB Theatre Tools — nothing was replaced."
            case .changedAfterVerify:
                return "The update changed after it was verified — nothing was replaced. Try again."
            case .cantRunHere(let why):
                return "The new version can't run on this Mac (\(why)) — nothing was replaced."
            }
        }
    }

    /// ~/Library/Application Support/JBTheatreTools/update — never beside the app until the swap itself.
    static var stagingDir: URL { InstallManager.shared.supportDir.appendingPathComponent("update", isDirectory: true) }

    /// True when macOS translocated this copy (read-only, random path — updating in place is impossible).
    static func isTranslocated(_ bundle: URL) -> Bool { bundle.path.contains("/AppTranslocation/") }

    // MARK: - The swap (file side; unit-tested)

    /// Puts `newBundle` in `current`'s place and returns where the old bundle went. The new bundle is first moved into
    /// the same folder under a temporary name (a copy when it's on another volume), then two renames in that folder —
    /// instant, so the app is never missing or half-written. On any failure nothing has changed.
    static func swap(newBundle: URL, into current: URL, fileManager fm: FileManager = .default,
                     toBin: (URL) throws -> Void = { try FileManager.default.trashItem(at: $0, resultingItemURL: nil) }) throws -> URL {
        let parent = current.deletingLastPathComponent()
        let incoming = parent.appendingPathComponent(current.lastPathComponent + ".new")
        try? fm.removeItem(at: incoming)
        do { try fm.moveItem(at: newBundle, to: incoming) }
        catch {
            try? fm.removeItem(at: incoming)
            throw isPermission(error) ? Failure.notWritable(parent.path) : error
        }
        let old = freeOldURL(for: current, fileManager: fm, toBin: toBin)
        do { try fm.moveItem(at: current, to: old) }
        catch {
            try? fm.removeItem(at: incoming)
            throw isPermission(error) ? Failure.notWritable(parent.path) : error
        }
        do { try fm.moveItem(at: incoming, to: current) }
        catch {
            do { try fm.moveItem(at: old, to: current) }
            catch { AppLog.shared.log("self-update: couldn't restore \(current.path): \(error.localizedDescription)") }
            try? fm.removeItem(at: incoming)
            throw error
        }
        return old
    }

    /// Undoes a swap whose new build didn't start: the new bundle steps aside and the old one takes its place back.
    static func rollBack(current: URL, old: URL, fileManager fm: FileManager = .default) -> Bool {
        let failed = current.deletingLastPathComponent().appendingPathComponent(current.lastPathComponent + ".failed")
        do {
            try? fm.removeItem(at: failed)
            try fm.moveItem(at: current, to: failed)
            try fm.moveItem(at: old, to: current)
            try? fm.removeItem(at: failed)
            AppLog.shared.log("self-update: rolled back \(current.path)")
            return true
        } catch {
            AppLog.shared.log("self-update: roll back of \(current.path) failed: \(error.localizedDescription)")
            if !fm.fileExists(atPath: current.path), fm.fileExists(atPath: failed.path) { try? fm.moveItem(at: failed, to: current) }
            return false
        }
    }

    /// The copies an update leaves beside `bundle`: "<name>.old", "<name>.<n>.old", and "<name>.new" (an interrupted swap).
    static func leftovers(beside bundle: URL, fileManager fm: FileManager = .default) -> [URL] {
        let parent = bundle.deletingLastPathComponent()
        let name = NSRegularExpression.escapedPattern(for: bundle.lastPathComponent)
        guard let re = try? NSRegularExpression(pattern: "^\(name)((\\.\\d+)?\\.old|\\.new)$", options: [.caseInsensitive]),
              let items = try? fm.contentsOfDirectory(atPath: parent.path) else { return [] }
        return items.filter { re.firstMatch(in: $0, range: NSRange($0.startIndex..., in: $0)) != nil }
            .sorted().map { parent.appendingPathComponent($0) }
    }

    /// "<name>.old" — an earlier old copy there goes to the Bin (recoverable, and never a half-finished recursive
    /// delete) — or the first free "<name>.<n>.old" when it can't be moved.
    static func freeOldURL(for bundle: URL, fileManager fm: FileManager = .default,
                           toBin: (URL) throws -> Void = { try FileManager.default.trashItem(at: $0, resultingItemURL: nil) }) -> URL {
        let parent = bundle.deletingLastPathComponent()
        let old = parent.appendingPathComponent(bundle.lastPathComponent + ".old")
        if !fm.fileExists(atPath: old.path) || (try? toBin(old)) != nil { return old }
        var n = 2
        while fm.fileExists(atPath: parent.appendingPathComponent("\(bundle.lastPathComponent).\(n).old").path) { n += 1 }
        return parent.appendingPathComponent("\(bundle.lastPathComponent).\(n).old")
    }

    private static func isPermission(_ error: Error) -> Bool {
        let ns = error as NSError
        if ns.domain == NSCocoaErrorDomain, [NSFileWriteNoPermissionError, NSFileReadNoPermissionError,
                                              NSFileWriteVolumeReadOnlyError].contains(ns.code) { return true }
        if let posix = ns.userInfo[NSUnderlyingErrorKey] as? NSError, posix.domain == NSPOSIXErrorDomain,
           [Int(EACCES), Int(EPERM), Int(EROFS)].contains(posix.code) { return true }
        return false
    }

    // MARK: - Restart hand-over

    enum StartResult { case started, stillStarting, quit }

    /// Opens `app` as a NEW instance, telling it (via `afterUpdateVar`) where to write `ready` once its window is up, and
    /// waits for that — or for it to quit, or for `timeout` with it still running (a slow start: left alone).
    static func startAndWait(app: URL, old: URL, ready: URL, timeout: TimeInterval) async throws -> StartResult {
        try? FileManager.default.removeItem(at: ready)
        let config = NSWorkspace.OpenConfiguration()
        config.createsNewApplicationInstance = true
        config.activates = true
        config.environment = [afterUpdateVar: "\(ProcessInfo.processInfo.processIdentifier)|\(old.path)|\(ready.path)"]
        let running = try await NSWorkspace.shared.openApplication(at: app, configuration: config)
        let until = Date().addingTimeInterval(timeout)
        while Date() < until {
            if FileManager.default.fileExists(atPath: ready.path) { try? FileManager.default.removeItem(at: ready); return .started }
            if running.isTerminated {
                let up = FileManager.default.fileExists(atPath: ready.path)
                try? FileManager.default.removeItem(at: ready)
                return up ? .started : .quit
            }
            try? await Task.sleep(nanoseconds: 250_000_000)
        }
        return .stillStarting
    }

    private struct AfterUpdate { let pid: pid_t; let old: URL; let ready: URL }
    @MainActor private static var afterUpdate: AfterUpdate?

    /// Call first thing at start-up: remembers (and clears, so apps opened from here don't inherit it) what the previous
    /// launcher passed on after an update.
    @MainActor static func readAfterUpdate() {
        guard let v = ProcessInfo.processInfo.environment[afterUpdateVar] else { return }
        unsetenv(afterUpdateVar)
        let parts = v.split(separator: "|", maxSplits: 2).map(String.init)
        guard parts.count == 3, let pid = pid_t(parts[0]) else { return }
        afterUpdate = AfterUpdate(pid: pid, old: URL(fileURLWithPath: parts[1]), ready: URL(fileURLWithPath: parts[2]))
    }

    /// Call once the window is showing: tells the previous launcher this one started (it then quits), and — only after an
    /// update — moves its old bundle to the Bin once that process has gone. A plain start only removes a "<name>.new" an
    /// interrupted update left; an "<name>.old" someone keeps as a backup is left alone.
    @MainActor private static var startedOnce = false

    @MainActor static func started() {
        guard !startedOnce else { return }   // every new window appears too (⌘N)
        startedOnce = true
        let after = afterUpdate
        afterUpdate = nil
        if let after { FileManager.default.createFile(atPath: after.ready.path, contents: Data("started".utf8)) }
        let bundle = Bundle.main.bundleURL
        Task.detached(priority: .utility) {
            if let after {
                for _ in 0..<60 where kill(after.pid, 0) == 0 { try? await Task.sleep(nanoseconds: 250_000_000) }   // ≤ 15 s
            }
            for item in leftovers(beside: bundle) where after != nil || item.lastPathComponent.hasSuffix(".new") {
                // A ".new" younger than ten minutes may be a swap in progress (the command line, another launcher).
                if item.lastPathComponent.hasSuffix(".new"),
                   let made = (try? item.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate,
                   made > Date().addingTimeInterval(-600) { continue }
                do {
                    if item.lastPathComponent.hasSuffix(".new") { try FileManager.default.removeItem(at: item) }
                    else { try FileManager.default.trashItem(at: item, resultingItemURL: nil) }   // recoverable from the Bin
                    AppLog.shared.log("self-update: removed \(item.lastPathComponent)")
                } catch {
                    AppLog.shared.log("self-update: couldn't remove \(item.path): \(error.localizedDescription)")
                }
            }
            clearStaleStaging()
        }
    }

    /// Drops staging leftovers more than an hour old — never a download another launcher is making right now.
    static func clearStaleStaging() {
        let fm = FileManager.default
        guard let items = try? fm.contentsOfDirectory(at: stagingDir, includingPropertiesForKeys: [.contentModificationDateKey]) else { return }
        let cutoff = Date().addingTimeInterval(-3600)
        for item in items {
            let date = (try? item.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast
            if date < cutoff { try? fm.removeItem(at: item) }
        }
    }

    // MARK: - Download + unpack

    struct Staged {
        let zip: URL          // the verified download
        let bundle: URL       // unpacked from it
        let tag: String
        let assetName: String
        let sha256: String    // the hash the SIGNED manifest lists for the zip
    }

    /// Downloads the newest launcher build, verifies it strictly, re-checks the zip against the SIGNED hash right before
    /// unpacking it, and checks the unpacked app is this launcher (same bundle identifier).
    static func download(_ s: SelfInfo, target: ReleaseInfo, client: GitHubClient) async throws -> Staged {
        guard let assetName = s.macAssetName, let asset = target.assets.first(where: { $0.name == assetName }) else {
            throw NSError(domain: "JBTheatreTools", code: 1, userInfo: [NSLocalizedDescriptionKey: "No macOS asset in \(target.tagName)."])
        }
        let fm = FileManager.default
        try fm.createDirectory(at: stagingDir, withIntermediateDirectories: true)
        clearStaleStaging()
        let pid = ProcessInfo.processInfo.processIdentifier
        let zip = stagingDir.appendingPathComponent("\(pid)-\(PathSafe.component(asset.name))")
        try await client.downloadAsset(owner: s.owner, repo: s.repo, assetId: asset.id, to: zip)
        var signedHash: String?
        let verification = try await AppState.verifyDownload(zip, asset: asset, release: target, app: CatalogApp.forSelf(s),
                                                             client: client, onVerifiedHash: { signedHash = $0 })
        guard verification == .verified, let signedHash else {
            try? fm.removeItem(at: zip)
            throw NSError(domain: "JBTheatreTools", code: 2, userInfo: [NSLocalizedDescriptionKey:
                "Couldn't verify the update — \(AppState.strictFailureReason(verification, assetName: asset.name)). Not saved."])
        }
        let unpacked = stagingDir.appendingPathComponent("\(pid)-unpacked", isDirectory: true)
        let bundle: URL = try await Task.detached(priority: .userInitiated) {
            let fm = FileManager.default
            // The file that's unpacked must be the file that was verified.
            guard try InstallManager.sha256Hex(of: zip).caseInsensitiveCompare(signedHash) == .orderedSame else {
                throw Failure.changedAfterVerify
            }
            try? fm.removeItem(at: unpacked)
            try fm.createDirectory(at: unpacked, withIntermediateDirectories: true)
            try InstallManager.shared.dittoExtract(zip, to: unpacked)
            guard let app = InstallManager.shared.appBundle(in: unpacked, preferring: "JB Theatre Tools"),
                  let info = Bundle(url: app), info.bundleIdentifier == Bundle.main.bundleIdentifier else { throw Failure.notTheLauncher }
            // Refuse a build this Mac can't open BEFORE swapping (a newer minimum macOS, or no slice for this Mac).
            if let min = info.infoDictionary?["LSMinimumSystemVersion"] as? String,
               AppState.versionIsNewer(min, than: { let v = ProcessInfo.processInfo.operatingSystemVersion
                                                    return "\(v.majorVersion).\(v.minorVersion).\(v.patchVersion)" }()) {
                throw Failure.cantRunHere("it needs macOS \(min) or later")
            }
            let archs = MachO.archs(ofApp: app)
            if !archs.isEmpty, !archs.contains(MacArch.isAppleSilicon ? "arm64" : "x86_64"), !(MacArch.isAppleSilicon && archs.contains("x86_64") && MacArch.rosettaInstalled) {
                throw Failure.cantRunHere("it has no build for this Mac")
            }
            return app
        }.value
        return Staged(zip: zip, bundle: bundle, tag: target.tagName, assetName: asset.name, sha256: signedHash)
    }

    /// The folder can't be changed: the verified zip goes to Downloads and Finder shows it (the old manual way).
    static func saveToDownloads(_ staged: Staged) throws -> URL {
        guard try InstallManager.sha256Hex(of: staged.zip).caseInsensitiveCompare(staged.sha256) == .orderedSame else {
            throw Failure.changedAfterVerify
        }
        let downloads = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
        let dest = downloads.appendingPathComponent(PathSafe.component(staged.assetName))
        try? FileManager.default.removeItem(at: dest)
        try FileManager.default.moveItem(at: staged.zip, to: dest)
        NSWorkspace.shared.activateFileViewerSelecting([dest])
        return dest
    }

    /// Removes this process's staging files (after a swap, or a failure).
    static func discard(_ staged: Staged) {
        let fm = FileManager.default
        try? fm.removeItem(at: staged.zip)
        try? fm.removeItem(at: staged.bundle.deletingLastPathComponent())
    }
}
