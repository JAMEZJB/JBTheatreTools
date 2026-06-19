import Foundation

struct InstalledRecord: Codable {
    var version: String
    var path: String
    var installedAt: String
}

enum InstallError: LocalizedError {
    case noAppInZip
    case notInstalled
    case unzipFailed(Int32)

    var errorDescription: String? {
        switch self {
        case .noAppInZip: return "Downloaded archive did not contain a .app bundle."
        case .notInstalled: return "App is not installed."
        case .unzipFailed(let code): return "Could not extract the archive (ditto exit \(code))."
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

    // MARK: - Install / launch

    /// Extracts `downloadedZip` (a macOS app archive) and installs the contained `.app`.
    @discardableResult
    func install(app: CatalogApp, version: String, downloadedZip: URL) throws -> URL {
        let extractDir = cacheDir.appendingPathComponent("extract-\(app.id)", isDirectory: true)
        try? fm.removeItem(at: extractDir)
        try fm.createDirectory(at: extractDir, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: extractDir) }

        try ditto(extract: downloadedZip, to: extractDir)

        guard let bundle = firstAppBundle(in: extractDir) else { throw InstallError.noAppInZip }
        let dest = appsDir.appendingPathComponent(bundle.lastPathComponent)
        try? fm.removeItem(at: dest)
        try fm.moveItem(at: bundle, to: dest)

        var m = manifest()
        m[app.id] = InstalledRecord(version: version, path: dest.path, installedAt: Self.isoNow())
        writeManifest(m)
        return dest
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

    /// Finds the first `.app` bundle in `dir`, ignoring the `__MACOSX` metadata folder.
    private func firstAppBundle(in dir: URL) -> URL? {
        guard let items = try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil) else { return nil }
        return items.first { $0.pathExtension == "app" && $0.lastPathComponent != "__MACOSX" }
    }

    private static func isoNow() -> String {
        let f = ISO8601DateFormatter()
        return f.string(from: Date())
    }
}
