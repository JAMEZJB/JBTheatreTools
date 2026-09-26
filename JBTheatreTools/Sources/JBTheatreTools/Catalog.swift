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
    /// Category section order for the launcher list/grid (apps carry a matching `category`). Any category
    /// an app uses that isn't listed here is appended after these, alphabetically.
    let categories: [String]?

    enum CodingKeys: String, CodingKey {
        case schemaVersion, apps, downloadServer, categories
        case selfInfo = "self"
    }

    /// Loads the catalog from (1) an explicit path, (2) the app bundle, or
    /// (3) by walking up from the current directory (dev / CLI use).
    static func load(explicitPath: String? = nil) throws -> Catalog {
        let data = try loadData(explicitPath: explicitPath)
        return try JSONDecoder().decode(Catalog.self, from: data)
    }

    private static func loadData(explicitPath: String?) throws -> Data {
        if let p = explicitPath {
            return try Data(contentsOf: URL(fileURLWithPath: p))
        }
        if let url = Bundle.main.url(forResource: "catalog", withExtension: "json") {
            return try Data(contentsOf: url)
        }
        // No walking up from the CWD (audit F9): trusting the first catalog.json found in a parent of an
        // arbitrary working directory would let a downloaded folder's catalog dictate owners/repos/relay.
        // The shipped .app always has it bundled; bare-binary / dev use must pass --catalog explicitly.
        throw CocoaError(.fileNoSuchFile, userInfo: [
            NSLocalizedDescriptionKey: "catalog.json is not bundled; pass --catalog <path>."
        ])
    }
}

/// One installable app in the catalog.
struct CatalogApp: Decodable, Identifiable, Sendable {
    let id: String
    let name: String
    let blurb: String
    /// Which launcher section this app appears under (e.g. "Show control"). Optional for older catalogs.
    let category: String?
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

    /// Optional downloadable variants of the SAME app (e.g. NDI Tools "Light" vs "Full"). When
    /// present with more than one entry, the row shows a variant toggle and install/status resolve
    /// against the SELECTED variant's assets. The first variant is the default.
    let variants: [AppVariant]?

    /// True when this app ships more than one variant → the launcher shows a Light/Full toggle.
    var hasVariants: Bool { (variants?.count ?? 0) > 1 }

    /// The asset map for a given variant id (nil / unknown → the default = first variant, or the
    /// top-level `assets` when there are no variants).
    func assets(variantId: String?) -> [String: String] {
        guard let vs = variants, !vs.isEmpty else { return assets }
        return (vs.first { $0.id == variantId } ?? vs[0]).assets
    }

    /// The macOS launcher installs the macOS build of each tool, preferring an arch-specific
    /// build (macos-arm64 / macos-x64) when the catalog carries one, else the universal `macos` —
    /// or the Intel build when this slot is set to run as Intel (see `MacArch`).
    var macAssetName: String? { macAssetName(variantId: nil) }

    /// Variant-aware macOS asset name (the selected variant's per-arch build).
    func macAssetName(variantId: String?) -> String? { macPick(variantId: variantId)?.name }

    /// The build this Mac installs for a slot, and whether it runs translated (Rosetta).
    func macPick(variantId: String?) -> MacArch.Pick? {
        MacArch.pick(from: assets(variantId: variantId), appleSilicon: MacArch.isAppleSilicon,
                     intel: MacArch.prefersIntel(installKey(variantId: variantId)))
    }

    /// "Run as Intel (Rosetta)" is offered for this slot: an Apple silicon Mac, and a build that can run either way.
    func macCanChooseIntel(variantId: String?) -> Bool {
        MacArch.canChoose(assets(variantId: variantId), appleSilicon: MacArch.isAppleSilicon)
    }

    /// True when `variantId` is this app's default (first) variant, or the app has no variants.
    func isDefaultVariant(_ variantId: String?) -> Bool {
        guard hasVariants, let first = variants?.first?.id else { return true }
        return variantId == nil || variantId == first
    }

    /// The label of a variant id, or nil.
    func variantLabel(_ variantId: String?) -> String? {
        guard let vid = variantId else { return nil }
        return variants?.first { $0.id == vid }?.label
    }

    /// The install-manifest key for a variant. Each variant is its OWN install slot, so Light and
    /// Full can be installed side by side. The default variant keeps the plain app id (so installs
    /// made before variants existed stay valid); other variants are `<id>@<variant>`.
    func installKey(variantId: String?) -> String {
        isDefaultVariant(variantId) ? id : "\(id)@\(variantId!)"
    }

    /// A suffix for on-disk names of a non-default variant (" (Full)"), so its bundle/shortcut can sit
    /// next to the default variant's without colliding. Empty for the default variant.
    func variantSuffix(_ variantId: String?) -> String {
        guard !isDefaultVariant(variantId), let label = variantLabel(variantId) else { return "" }
        return " (\(label))"
    }
}

/// One downloadable variant of an app (e.g. Light / Full). `label` is the toggle text.
struct AppVariant: Decodable, Identifiable, Sendable {
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
        pick(from: assets, appleSilicon: isAppleSilicon, intel: false)?.name
    }

    /// One slot's build: the asset to install, and whether it runs translated by Rosetta.
    struct Pick: Equatable {
        let name: String
        let translated: Bool
    }

    /// The build for this Mac. `intel` (Apple silicon only, the slot's "Run as Intel" choice): the Intel build when
    /// the catalog has one, else the universal build (opened as Intel). Without it: the Apple silicon build, else the
    /// universal one — and only when neither exists, the Intel build (translated). An Intel Mac takes the Intel or
    /// universal build as before. Pure, for the tests.
    static func pick(from assets: [String: String], appleSilicon: Bool, intel: Bool) -> Pick? {
        guard appleSilicon else { return (assets["macos-x64"] ?? assets["macos"]).map { Pick(name: $0, translated: false) } }
        if intel, let x = assets["macos-x64"] ?? assets["macos"] { return Pick(name: x, translated: true) }
        if let native = assets["macos-arm64"] ?? assets["macos"] { return Pick(name: native, translated: false) }
        return assets["macos-x64"].map { Pick(name: $0, translated: true) }
    }

    /// The choice exists only when the slot can run both ways: an Apple silicon build (or a universal one) AND an
    /// Intel build (or a universal one). An Apple-silicon-only edition (Image Tools Full) never offers it.
    static func canChoose(_ assets: [String: String], appleSilicon: Bool) -> Bool {
        appleSilicon && (assets["macos-arm64"] != nil || assets["macos"] != nil)
            && (assets["macos-x64"] != nil || assets["macos"] != nil)
    }

    /// Install slots set to run as Intel on this Mac (the row's ⋯ → "Run as Intel (Rosetta)"). Per Mac, not synced.
    static let intelSlotsKey = "theatre.intelSlots"

    static func prefersIntel(_ slotKey: String) -> Bool {
        isAppleSilicon && (UserDefaults.standard.stringArray(forKey: intelSlotsKey) ?? []).contains(slotKey)
    }

    /// Rosetta 2 is installed (it's an optional macOS component on Apple silicon).
    static var rosettaInstalled: Bool {
        FileManager.default.fileExists(atPath: "/Library/Apple/usr/share/rosetta/rosetta")
    }
}

/// Which CPU architectures an executable contains — read from its Mach-O header (thin or universal).
enum MachO {
    /// "x86_64" / "arm64" found in the header; empty when the data isn't a Mach-O executable.
    static func archs(_ data: Data) -> Set<String> {
        let b = [UInt8](data.prefix(4096))
        guard b.count >= 8 else { return [] }
        func be32(_ o: Int) -> UInt32? { o + 4 <= b.count ? b[o..<o + 4].reduce(0) { $0 << 8 | UInt32($1) } : nil }
        func le32(_ o: Int) -> UInt32? { o + 4 <= b.count ? b[o..<o + 4].reversed().reduce(0) { $0 << 8 | UInt32($1) } : nil }
        func name(_ cpu: UInt32) -> String? {
            switch cpu { case 0x0100_0007: return "x86_64"; case 0x0100_000C: return "arm64"; default: return nil }
        }
        switch be32(0) {
        case 0xCAFE_BABE, 0xCAFE_BABF:                     // universal: big-endian table of fat_arch(_64) entries
            let stride = be32(0) == 0xCAFE_BABF ? 32 : 20
            guard let n = be32(4), n > 0, n < 16 else { return [] }
            return Set((0..<Int(n)).compactMap { i in be32(8 + i * stride).flatMap(name) })
        case 0xCFFA_EDFE:                                   // thin 64-bit, little-endian on disk
            return le32(4).flatMap(name).map { [$0] } ?? []
        default:
            return []
        }
    }

    /// The architectures of an installed `.app` (its main executable). Empty when it can't be read.
    static func archs(ofApp bundle: URL) -> Set<String> {
        let exe = (NSDictionary(contentsOf: bundle.appendingPathComponent("Contents/Info.plist"))?["CFBundleExecutable"] as? String)
            ?? bundle.deletingPathExtension().lastPathComponent
        guard let h = FileHandle(forReadingAtPath: bundle.appendingPathComponent("Contents/MacOS/\(exe)").path) else { return [] }
        defer { try? h.close() }
        return archs((try? h.read(upToCount: 4096)) ?? Data())
    }
}

/// JBTheatreTools' own release info (for the self-update check).
struct SelfInfo: Decodable {
    let owner: String
    let repo: String
    let assets: [String: String]
    /// The launcher's own one-line what's-new (the offline fallback for the "Updated to vX" notes).
    var whatsNew: String? = nil
    var whatsNewVersion: String? = nil
    var macAssetName: String? { MacArch.pick(from: assets) }
}

extension CatalogApp {
    /// A synthetic catalog entry for the launcher itself, so its self-update download goes through the
    /// same verification path (size + suite-signed SHA256SUMS + hash) as every app install.
    static func forSelf(_ s: SelfInfo) -> CatalogApp {
        CatalogApp(id: "jbtheatretools", name: "JB Theatre Tools", blurb: "", category: nil, whatsNew: nil,
                   whatsNewVersion: nil, owner: s.owner, repo: s.repo, assets: s.assets, variants: nil)
    }
}
