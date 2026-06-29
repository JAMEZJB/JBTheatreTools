import Foundation
import CryptoKit

/// Outcome of integrity-checking a downloaded asset (verify-if-present): the file passed the SHA-256
/// check, the release published no `SHA256SUMS` manifest at all, or it published one but this asset
/// isn't listed in it (e.g. a name mismatch). The two "unverified" cases are distinguished so the
/// user/logs can tell "no checksums" from "checksums exist but don't cover this file".
enum VerifyResult: Equatable {
    case verified
    case noManifest
    case assetNotListed
}

struct InstalledRecord: Codable {
    var version: String
    var path: String
    var installedAt: String
}

enum InstallError: LocalizedError {
    case noAppInZip
    case notInstalled
    case unzipFailed(Int32)
    case sizeMismatch(expected: Int, got: Int)
    case checksumMismatch(String)
    case unverified(reason: String)

    var errorDescription: String? {
        switch self {
        case .noAppInZip: return "Downloaded archive did not contain a .app bundle."
        case .notInstalled: return "App is not installed."
        case .unzipFailed(let code): return "Could not extract the archive (ditto exit \(code))."
        case .sizeMismatch(let expected, let got):
            return "Download is \(got) bytes but the release lists \(expected). Aborting install."
        case .checksumMismatch(let name):
            return "Checksum mismatch for \(name) — the download does not match the release's SHA256SUMS. Aborting install."
        case .unverified(let reason):
            return "Couldn't verify this download — \(reason). Install aborted for safety."
        }
    }
}

/// Installs / tracks / launches the downloaded macOS apps.
///
/// macOS assets are `.zip` files containing a `.app`. Install = extract → move the `.app` into
/// the apps dir → record the version in a JSON manifest. Launch = `open` the bundle.
final class InstallManager {
    static let shared = InstallManager()

    private let fm = FileManager.default
    let supportDir: URL
    let appsDir: URL
    let cacheDir: URL
    let manifestURL: URL

    init() {
        let support = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("JBTheatreTools", isDirectory: true)
        supportDir = support
        appsDir = support.appendingPathComponent("apps", isDirectory: true)
        manifestURL = support.appendingPathComponent("installed.json")
        cacheDir = fm.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("JBTheatreTools", isDirectory: true)
        try? fm.createDirectory(at: appsDir, withIntermediateDirectories: true)
        try? fm.createDirectory(at: cacheDir, withIntermediateDirectories: true)
    }

    // MARK: - Manifest

    func manifest() -> [String: InstalledRecord] {
        guard let data = try? Data(contentsOf: manifestURL),
              let m = try? JSONDecoder().decode([String: InstalledRecord].self, from: data)
        else { return [:] }
        return m
    }

    private func writeManifest(_ m: [String: InstalledRecord]) {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        if let data = try? enc.encode(m) { try? data.write(to: manifestURL) }
    }

    func installedVersion(_ appId: String) -> String? {
        guard let rec = manifest()[appId], fm.fileExists(atPath: rec.path) else { return nil }
        return rec.version
    }

    func installedPath(_ appId: String) -> URL? {
        guard let rec = manifest()[appId] else { return nil }
        let url = URL(fileURLWithPath: rec.path)
        return fm.fileExists(atPath: url.path) ? url : nil
    }

    /// The installed app's own display name, read live from its bundle `Info.plist`.
    /// This is the authoritative "what this app calls itself" — so an installed row is never wrong.
    func installedDisplayName(_ appId: String) -> String? {
        guard let appURL = installedPath(appId) else { return nil }
        let plistURL = appURL.appendingPathComponent("Contents/Info.plist")
        guard let data = try? Data(contentsOf: plistURL),
              let obj = try? PropertyListSerialization.propertyList(from: data, format: nil),
              let dict = obj as? [String: Any] else { return nil }
        let name = (dict["CFBundleDisplayName"] as? String) ?? (dict["CFBundleName"] as? String)
        let trimmed = name?.trimmingCharacters(in: .whitespacesAndNewlines)
        return (trimmed?.isEmpty == false) ? trimmed : nil
    }

    // MARK: - Install / launch

    /// Extracts `downloadedZip` (a macOS app archive) and installs the contained `.app`.
    /// When `toApplications` is true the bundle is placed in the Applications folder (so it shows in
    /// Launchpad/Spotlight and launches without this launcher); otherwise in the managed apps dir.
    @discardableResult
    func install(app: CatalogApp, version: String, downloadedZip: URL, toApplications: Bool) throws -> URL {
        let extractDir = cacheDir.appendingPathComponent("extract-\(app.id)", isDirectory: true)
        try? fm.removeItem(at: extractDir)
        try fm.createDirectory(at: extractDir, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: extractDir) }

        try ditto(extract: downloadedZip, to: extractDir)

        guard let bundle = appBundle(in: extractDir, preferring: app.name) else { throw InstallError.noAppInZip }

        // Remove any previous install first — it may be in a different location if the setting changed.
        if let old = manifest()[app.id] { try? fm.removeItem(at: URL(fileURLWithPath: old.path)) }

        let destDir = toApplications ? applicationsInstallDir() : appsDir
        try? fm.createDirectory(at: destDir, withIntermediateDirectories: true)
        let dest = destDir.appendingPathComponent(bundle.lastPathComponent)
        try? fm.removeItem(at: dest)
        try fm.moveItem(at: bundle, to: dest)

        var m = manifest()
        m[app.id] = InstalledRecord(version: version, path: dest.path, installedAt: Self.isoNow())
        writeManifest(m)
        return dest
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
        guard needsRelocation(appId, toApplications: toApplications) else { return }
        var m = manifest()
        guard let rec = m[appId] else { return }
        let current = URL(fileURLWithPath: rec.path)
        let targetDir = toApplications ? applicationsInstallDir() : appsDir
        try fm.createDirectory(at: targetDir, withIntermediateDirectories: true)
        let dest = targetDir.appendingPathComponent(current.lastPathComponent)
        if dest.standardizedFileURL.path != current.standardizedFileURL.path { try? fm.removeItem(at: dest) }
        try fm.moveItem(at: current, to: dest)
        m[appId]?.path = dest.path
        writeManifest(m)
    }

    private func isApplicationsParent(_ path: String) -> Bool {
        let user = fm.homeDirectoryForCurrentUser.appendingPathComponent("Applications").standardizedFileURL.path
        return path == "/Applications" || path == user
    }

    func launch(app: CatalogApp) throws {
        guard let path = installedPath(app.id) else { throw InstallError.notInstalled }
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        proc.arguments = [path.path]
        try proc.run()
    }

    /// Removes the installed `.app` bundle and its manifest entry.
    func uninstall(_ appId: String) throws {
        var m = manifest()
        if let rec = m[appId] {
            try? fm.removeItem(at: URL(fileURLWithPath: rec.path))
        }
        m.removeValue(forKey: appId)
        writeManifest(m)
    }

    // MARK: - Helpers

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
    private func appBundle(in dir: URL, preferring expectedName: String) -> URL? {
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
