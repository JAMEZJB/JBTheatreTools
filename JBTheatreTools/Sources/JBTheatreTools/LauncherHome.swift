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

    enum MoveError: LocalizedError {
        case openThere(String), notWritable(String)
        var errorDescription: String? {
            switch self {
            case .openThere(let folder): return "JB Theatre Tools is already in \(folder) and it's open. Quit that copy, then try again."
            case .notWritable(let folder): return "macOS doesn't let JB Theatre Tools write to \(folder). Choose another folder."
            }
        }
    }

    /// Copies `source` (this bundle — a translocated one's contents are the real app) into `folder` under its real name,
    /// replacing an older copy there that isn't open (which goes to the Bin), clears the download quarantine on the copy
    /// (the person has already opened this app), then bins `original` — the downloaded copy — when there is one.
    /// Returns the new bundle. On a failure before the copy is in place nothing has changed.
    static func move(_ source: URL, original: URL?, into folder: URL, fileManager fm: FileManager = .default,
                     isOpen: (URL) -> Bool = { url in NSWorkspace.shared.runningApplications.contains { $0.bundleURL?.standardizedFileURL == url.standardizedFileURL } },
                     toBin: (URL) throws -> Void = { try FileManager.default.trashItem(at: $0, resultingItemURL: nil) }) throws -> URL {
        let name = (original ?? source).lastPathComponent
        let dest = folder.appendingPathComponent(name, isDirectory: true)
        if dest.standardizedFileURL == source.standardizedFileURL { return dest }
        do { try fm.createDirectory(at: folder, withIntermediateDirectories: true) }
        catch { throw MoveError.notWritable(folder.path) }
        guard fm.isWritableFile(atPath: folder.path) else { throw MoveError.notWritable(folder.path) }
        if fm.fileExists(atPath: dest.path) {
            if isOpen(dest) { throw MoveError.openThere(folder.path) }
        }
        // Copy beside the destination under a temporary name first, so a half-copied app never sits at `dest`.
        let incoming = folder.appendingPathComponent(".\(name).moving-\(UUID().uuidString.prefix(8))", isDirectory: true)
        do { try fm.copyItem(at: source, to: incoming) }
        catch { try? fm.removeItem(at: incoming); throw error }
        if fm.fileExists(atPath: dest.path) {
            do { try toBin(dest) } catch { try? fm.removeItem(at: incoming); throw error }
        }
        do { try fm.moveItem(at: incoming, to: dest) }
        catch { try? fm.removeItem(at: incoming); throw error }
        clearQuarantine(dest)
        if let original, original.standardizedFileURL != dest.standardizedFileURL, fm.fileExists(atPath: original.path) {
            do { try toBin(original); AppLog.shared.log("move: the downloaded copy \(original.path) went to the Bin") }
            catch { AppLog.shared.log("move: left the old copy \(original.path): \(error.localizedDescription)") }
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
