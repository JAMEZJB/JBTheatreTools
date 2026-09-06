import Foundation

/// The shared app catalog (mirrors the repo-root `catalog.json`, bundled into the .app).
/// Both the macOS and Windows launchers read the same file so the catalog stays single-sourced.
struct Catalog: Decodable {
    let schemaVersion: Int
    let apps: [CatalogApp]
    /// JBTheatreTools' own release info, for the launcher self-update check.
    let selfInfo: SelfInfo?
    /// Built-in download-relay base URL for the default (passphrase) auth mode.
    let downloadServer: String?

    enum CodingKeys: String, CodingKey {
        case schemaVersion, apps, downloadServer
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
    /// Optional one-line "what's new" for this app's current release (shown under the row when present).
    let whatsNew: String?
    /// Optional version the whatsNew line refers to (e.g. "v1.5.0"), used to label it "New in vX.Y.Z:".
    let whatsNewVersion: String?
    let owner: String
    let repo: String
    /// Platform key → exact release-asset name. Keys: macos, windows-x64, windows-arm64,
    /// and optionally the per-arch macOS keys macos-arm64 / macos-x64 (for tools whose macOS
    /// build can't be universal, e.g. NDI Tools "Full"). When `variants` is present, this is the
    /// default variant's assets (so any variant-unaware code path still resolves a sane build).
    let assets: [String: String]

    /// Optional downloadable variants of the SAME app (e.g. NDI Tools "Standard" vs "Full"). When
    /// present with more than one entry, the row shows a variant toggle and install/status resolve
    /// against the SELECTED variant's assets. The first variant is the default.
    let variants: [AppVariant]?

    /// True when this app ships more than one variant → the launcher shows a Standard/Full toggle.
    var hasVariants: Bool { (variants?.count ?? 0) > 1 }

    /// The asset map for a given variant id (nil / unknown → the default = first variant, or the
    /// top-level `assets` when there are no variants).
    func assets(variantId: String?) -> [String: String] {
        guard let vs = variants, !vs.isEmpty else { return assets }
        return (vs.first { $0.id == variantId } ?? vs[0]).assets
    }

    /// The macOS launcher installs the macOS build of each tool, preferring an arch-specific
    /// build (macos-arm64 / macos-x64) when the catalog carries one, else the universal `macos`.
    var macAssetName: String? { MacArch.pick(from: assets) }

    /// Variant-aware macOS asset name (the selected variant's per-arch build).
    func macAssetName(variantId: String?) -> String? { MacArch.pick(from: assets(variantId: variantId)) }
}

/// One downloadable variant of an app (e.g. Standard / Full). `label` is the toggle text.
struct AppVariant: Decodable, Identifiable {
    let id: String
    let label: String
    let assets: [String: String]
}

/// The Mac's native hardware architecture, and per-arch macOS asset selection.
enum MacArch {
    /// True on Apple Silicon. Uses sysctl so it's correct even when the launcher runs under Rosetta.
    static let isAppleSilicon: Bool = {
        var value: Int32 = 0
        var size = MemoryLayout<Int32>.size
        if sysctlbyname("hw.optional.arm64", &value, &size, nil, 0) == 0 { return value == 1 }
        return false
    }()

    /// Picks the best macOS asset: the arch-specific build for this Mac when present, else the
    /// universal `macos` build. Returns nil only when the catalog has no usable macOS asset.
    static func pick(from assets: [String: String]) -> String? {
        let archKey = isAppleSilicon ? "macos-arm64" : "macos-x64"
        return assets[archKey] ?? assets["macos"]
    }
}

/// JBTheatreTools' own release info (for the self-update check).
struct SelfInfo: Decodable {
    let owner: String
    let repo: String
    let assets: [String: String]
    var macAssetName: String? { MacArch.pick(from: assets) }
}
