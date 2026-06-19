import Foundation

/// The shared app catalog (mirrors the repo-root `catalog.json`, bundled into the .app).
/// Both the macOS and Windows launchers read the same file so the catalog stays single-sourced.
struct Catalog: Decodable {
    let schemaVersion: Int
    let apps: [CatalogApp]
    /// JBTheatreTools' own release info, for the launcher self-update check.
    let selfInfo: SelfInfo?

    enum CodingKeys: String, CodingKey {
        case schemaVersion, apps
        case selfInfo = "self"
    }

    /// Loads the catalog from (1) an explicit path, (2) the app bundle, or
    /// (3) by walking up from the current directory (dev / CLI use).
    static func load(explicitPath: String? = nil) throws -> Catalog {
        let data = try loadData(explicitPath: explicitPath)
        return try JSONDecoder().decode(Catalog.self, from: data)
    }

    private static func loadData(explicitPath: String?) throws -> Data {
        let fm = FileManager.default
        if let p = explicitPath {
            return try Data(contentsOf: URL(fileURLWithPath: p))
        }
        if let url = Bundle.main.url(forResource: "catalog", withExtension: "json") {
            return try Data(contentsOf: url)
        }
        var dir = URL(fileURLWithPath: fm.currentDirectoryPath)
        for _ in 0..<6 {
            let candidate = dir.appendingPathComponent("catalog.json")
            if fm.fileExists(atPath: candidate.path) {
                return try Data(contentsOf: candidate)
            }
            dir.deleteLastPathComponent()
        }
        throw CocoaError(.fileNoSuchFile, userInfo: [
            NSLocalizedDescriptionKey: "catalog.json not found (not bundled and not in any parent of the working directory)."
        ])
    }
}

/// One installable app in the catalog.
struct CatalogApp: Decodable, Identifiable {
    let id: String
    let name: String
    let blurb: String
    let owner: String
    let repo: String
    /// Platform key → exact release-asset name. Keys: macos, windows-x64, windows-arm64.
    let assets: [String: String]

    /// The macOS launcher only ever installs the macOS build of each tool.
    var macAssetName: String? { assets["macos"] }
}

/// JBTheatreTools' own release info (for the self-update check).
struct SelfInfo: Decodable {
    let owner: String
    let repo: String
    let assets: [String: String]
    var macAssetName: String? { assets["macos"] }
}
