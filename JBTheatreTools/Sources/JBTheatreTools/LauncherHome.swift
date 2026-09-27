import AppKit

/// Where the launcher itself lives. Downloaded, it usually runs from Downloads (often translocated by Gatekeeper to a
/// read-only random path), so at start-up it offers to move itself into Applications — or a folder the person picks, or
/// to stay where it is. The Windows launcher offers its per-user programs folder the same way.
enum LauncherHome {
    /// UserDefaults: bundle paths moved to a chosen folder or kept there ("Keep Here") — never offered again.
    static let keptKey = "theatre.launcherHomes"

    /// Offer to move unless this copy is inside /Applications or ~/Applications (or the test stand-in), or is one the
    /// person moved to a folder of their choosing or chose to keep. Pure.
    static func shouldOffer(bundlePath: String, homePath: String, kept: [String], testApplications: String? = nil) -> Bool {
        let path = standard(bundlePath)
        let homes = ["/Applications", standard(homePath) + "/Applications"] + (testApplications.map { [standard($0)] } ?? [])
        if homes.contains(where: { path.hasPrefix($0 + "/") }) { return false }
        return !kept.map(standard).contains(path)
    }

    private static func standard(_ p: String) -> String {
        var s = (p as NSString).standardizingPath
        while s.count > 1 && s.hasSuffix("/") { s.removeLast() }
        return s
    }

    /// Where "Move to Applications" goes: /Applications when this account can write there, else ~/Applications
    /// (created if needed). `JBTT_APPLICATIONS_DIR` stands in for it in tests.
    static func applicationsFolder(fileManager fm: FileManager = .default) -> URL {
        if let test = ProcessInfo.processInfo.environment["JBTT_APPLICATIONS_DIR"], !test.isEmpty {
            return URL(fileURLWithPath: test, isDirectory: true)
        }
        let system = URL(fileURLWithPath: "/Applications", isDirectory: true)
        if fm.isWritableFile(atPath: system.path) { return system }
        return fm.homeDirectoryForCurrentUser.appendingPathComponent("Applications", isDirectory: true)
    }

    /// The real location of a translocated bundle (Gatekeeper runs a quarantined download from a read-only random
    /// path); nil when it isn't translocated or macOS won't say.
    static func originalOfTranslocated(_ bundle: URL) -> URL? {
        guard SelfUpdate.isTranslocated(bundle),
              let handle = dlopen("/System/Library/Frameworks/Security.framework/Security", RTLD_LAZY) else { return nil }
        defer { dlclose(handle) }
        typealias Fn = @convention(c) (CFURL, UnsafeMutablePointer<Unmanaged<CFError>?>?) -> Unmanaged<CFURL>?
        guard let sym = dlsym(handle, "SecTranslocateCreateOriginalPathForURL") else { return nil }
        let original = unsafeBitCast(sym, to: Fn.self)(bundle as CFURL, nil)?.takeRetainedValue()
        return original as URL?
    }

    /// The installed launcher's bundle name — always this, whatever the download was called ("… 2.app" must never
    /// become a second launcher in Applications).
    static let bundleName = "JB Theatre Tools.app"

    enum MoveError: LocalizedError {
        case openThere(String), notWritable(String)
        var errorDescription: String? {
            switch self {
            case .openThere(let folder): return "JB Theatre Tools is already in \(folder) and it's open. Quit that copy, then try again."
            case .notWritable(let folder): return "macOS doesn't let JB Theatre Tools write to \(folder). Choose another folder."
            }
        }
    }

    /// Installing would put an older (or the same) launcher over `existing` — then the copy already there is simply opened
    /// instead (a stale download must never downgrade a self-updated install). Pure.
    static func keepExisting(existing: String?, mine: String) -> Bool {
        guard let existing, !existing.trimmingCharacters(in: .whitespaces).isEmpty else { return false }
        return !AppState.versionIsNewer(mine, than: existing)
    }

    /// Why a folder picked with "Choose Folder…" can't take the launcher, or nil when it can: never the launcher's own
    /// data or install folders or an app's data folder (a later Uninstall / "remove its data" there would take the
    /// launcher with it) — nor inside one of them. Pure.
    static func refuseFolder(_ folder: String, own: [String]) -> String? {
        let f = standard(folder)
        guard f.hasPrefix("/") else { return "Choose a folder on a disk." }
        for o in own.map(standard) where f == o || f.hasPrefix(o + "/") {
            return "\(folder) is where JB Theatre Tools keeps its own files or an app's data. Choose another folder."
        }
        return nil
    }

    /// True when `path` is inside `folder`. Pure.
    static func isInside(_ path: String, _ folder: String) -> Bool { standard(path).hasPrefix(standard(folder) + "/") }

    /// The launcher version of the bundle at `url` (its CFBundleShortVersionString — the full dev tag), or nil.
    static func bundleVersion(at url: URL) -> String? {
        guard let data = try? Data(contentsOf: url.appendingPathComponent("Contents/Info.plist")),
              let plist = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any] else { return nil }
        return plist["CFBundleShortVersionString"] as? String
    }

    /// Copies `source` (this bundle — a translocated one's contents are the real app) into `folder` as
    /// "JB Theatre Tools.app" and clears the download quarantine on the copy (the person has already opened this app).
    /// An older copy there that isn't open is first set aside in the same folder, and binned only once the new copy is in
    /// place (put back if it can't be). The copy this runs from is NOT touched here — the caller bins a downloaded copy
    /// once the new one has opened. Returns the new bundle; on a failure nothing has changed.
    static func install(_ source: URL, into folder: URL, fileManager fm: FileManager = .default,
                        isOpen: (URL) -> Bool = { url in NSWorkspace.shared.runningApplications.contains { $0.bundleURL?.standardizedFileURL == url.standardizedFileURL } },
                        toBin: (URL) throws -> Void = { try FileManager.default.trashItem(at: $0, resultingItemURL: nil) }) throws -> URL {
        let dest = folder.appendingPathComponent(bundleName, isDirectory: true)
        if dest.standardizedFileURL == source.standardizedFileURL { return dest }
        do { try fm.createDirectory(at: folder, withIntermediateDirectories: true) }
        catch { throw MoveError.notWritable(folder.path) }
        guard fm.isWritableFile(atPath: folder.path) else { throw MoveError.notWritable(folder.path) }
        let exists = fm.fileExists(atPath: dest.path)
        if exists, isOpen(dest) { throw MoveError.openThere(folder.path) }
        let tag = UUID().uuidString.prefix(8)
        // Copy beside the destination under a temporary name first, so a half-copied app never sits at `dest`.
        let incoming = folder.appendingPathComponent(".\(bundleName).incoming-\(tag)", isDirectory: true)
        do { try fm.copyItem(at: source, to: incoming) }
        catch { try? fm.removeItem(at: incoming); throw error }
        var aside: URL? = nil
        if exists {
            let a = folder.appendingPathComponent(".\(bundleName).previous-\(tag)", isDirectory: true)
            do { try fm.moveItem(at: dest, to: a); aside = a }
            catch { try? fm.removeItem(at: incoming); throw error }
        }
        do { try fm.moveItem(at: incoming, to: dest) }
        catch {
            if let aside { try? fm.moveItem(at: aside, to: dest) }   // put the older copy back
            try? fm.removeItem(at: incoming)
            throw error
        }
        clearQuarantine(dest)
        if let aside {
            do { try toBin(aside); AppLog.shared.log("move: the older copy that was in \(folder.path) went to the Bin") }
            catch { AppLog.shared.log("move: left the older copy at \(aside.path): \(error.localizedDescription)") }
        }
        AppLog.shared.log("move: launcher now at \(dest.path) (from \(source.path))")
        return dest
    }

    /// Removes the download quarantine from the copy (recursively), so macOS doesn't translocate it again.
    private static func clearQuarantine(_ bundle: URL) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/xattr")
        p.arguments = ["-dr", "com.apple.quarantine", bundle.path]
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do { try p.run(); p.waitUntilExit() } catch { AppLog.shared.log("move: couldn't clear quarantine: \(error.localizedDescription)") }
    }
}
