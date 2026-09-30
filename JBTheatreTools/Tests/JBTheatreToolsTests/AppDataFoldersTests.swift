import XCTest
@testable import JBTheatreTools

/// Which folders "Uninstall and Remove Its Data" may touch. The Windows launcher carries the same name rules.
final class AppDataFoldersTests: XCTestCase {
    func testOrdinaryAppFolderNamesAreAccepted() {
        for n in ["PSN Tools", "Convert to it!", "p2r3-convert-updater", "ShowControlTools"] {
            XCTAssertTrue(AppDataFolders.isSafeName(n), n)
        }
    }

    func testPathsSharedFoldersAndOddNamesAreRefused() {
        for n: String? in [nil, "", "   ", ".", "..", ".config", "PSN Tools.", " PSN Tools", "..\\Windows", "PSN/Tools", "C:",
                           "*", "JBTheatreTools", "microsoft", "Application Support", "Documents", "Logs", String(repeating: "a", count: 65),
                           "com.apple.Safari", "COM.APPLE.x", "PSN Tools\n", "\tPSN Tools"] {
            XCTAssertFalse(AppDataFolders.isSafeName(n), n ?? "nil")
        }
    }

    func testBundleIds() {
        XCTAssertTrue(AppDataFolders.isSafeBundleId("com.jamesbreedon.psntools"))
        XCTAssertTrue(AppDataFolders.isSafeBundleId("com.jamesbreedon.pdftools.full"))
        XCTAssertTrue(AppDataFolders.isSafeBundleId("com.p2r3.convert"))
        for id: String? in [nil, "", "psntools", "com.jamesbreedon", "com.apple.Safari", "com..x", "com.x.y/../z", "com.x.y z"] {
            XCTAssertFalse(AppDataFolders.isSafeBundleId(id), id ?? "nil")
        }
    }

    func testLibraryLocations() {
        let lib = URL(fileURLWithPath: "/Users/u/Library")
        let paths = AppDataFolders.macPaths(folders: ["PSN Tools", "..", "psn tools"], bundleIds: ["com.jamesbreedon.psntools", "com.apple.x.y"],
                                            library: lib).map(\.path)
        XCTAssertEqual(paths, [
            "/Users/u/Library/Application Support/PSN Tools", "/Users/u/Library/Logs/PSN Tools", "/Users/u/Library/Caches/PSN Tools",
            "/Users/u/Library/WebKit/com.jamesbreedon.psntools", "/Users/u/Library/Caches/com.jamesbreedon.psntools",
            "/Users/u/Library/HTTPStorages/com.jamesbreedon.psntools", "/Users/u/Library/HTTPStorages/com.jamesbreedon.psntools.binarycookies",
            "/Users/u/Library/Saved Application State/com.jamesbreedon.psntools.savedState",
            "/Users/u/Library/Preferences/com.jamesbreedon.psntools.plist",
        ])
        XCTAssertEqual(AppDataFolders.macPaths(folders: nil, bundleIds: nil, library: lib), [])
    }

    /// Removal goes through the injected Bin (never the real one): missing items are skipped, a failure is reported.
    func testRemoveDataItemsUsesTheBinAndReportsFailures() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("jbtt-data-\(UUID().uuidString)")
        let a = dir.appendingPathComponent("A"), b = dir.appendingPathComponent("B"), gone = dir.appendingPathComponent("gone")
        try FileManager.default.createDirectory(at: a, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: b, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        var binned: [String] = []
        let failed = AppState.removeDataItems([a, gone, b]) { url in
            if url == b { throw CocoaError(.fileWriteNoPermission) }
            binned.append(url.lastPathComponent)
            try FileManager.default.removeItem(at: url)
        }
        XCTAssertEqual(binned, ["A"])
        XCTAssertEqual(failed.map(\.path), [b])
    }

    /// `claudeSince` names the FIRST TAG that has a connector. Dev builds carry the connector first, so a dev tag
    /// (v1.7.0-dev.1) must count — a bare core (v1.7.0) sorts AFTER its own dev builds and silently hid every app's
    /// connector from Stagehand (2026-09-30). Each value must itself pass the gate it defines.
    func testCatalogClaudeSinceCountsItsOwnTag() throws {
        let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
        let catalog = try Catalog.load(explicitPath: root.appendingPathComponent("catalog.json").path)
        for app in catalog.apps {
            guard let since = app.claudeSince else { continue }
            XCTAssertTrue(Connectors.hasConnector(app, installed: since), "\(app.id): \(since)")
            let core = since.split(separator: "-").first.map(String.init) ?? since
            // the first dev build of that version must already count (a bare "v1.7.0" fails this)
            XCTAssertTrue(Connectors.hasConnector(app, installed: core + "-dev.1"), "\(app.id): \(core)-dev.1 must count")
            XCTAssertTrue(Connectors.hasConnector(app, installed: core), "\(app.id): the release \(core) counts too")
        }
    }

    /// Every catalog app names at least one data folder, all of them pass the rules, and no two apps share one.
    func testCatalogDataEntries() throws {
        // The repo-root catalog.json (the one both launchers bundle), found from this file's path.
        let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
        let catalog = try Catalog.load(explicitPath: root.appendingPathComponent("catalog.json").path)
        var owner: [String: String] = [:], bundleOwner: [String: String] = [:]
        for app in catalog.apps {
            XCTAssertFalse((app.dataFolders ?? []).isEmpty, app.id)
            for n in app.dataFolders ?? [] {
                XCTAssertTrue(AppDataFolders.isSafeName(n), "\(app.id): \(n)")
                XCTAssertNil(owner.updateValue(app.id, forKey: n.lowercased()), "\(n) listed twice")
            }
            for id in app.bundleIds ?? [] {
                XCTAssertTrue(AppDataFolders.isSafeBundleId(id), "\(app.id): \(id)")
                XCTAssertNotEqual(id.lowercased(), "com.jamesbreedon.jbtheatretools", "\(app.id) names the launcher's own id")
                XCTAssertNil(bundleOwner.updateValue(app.id, forKey: id.lowercased()), "\(id) listed twice")
            }
        }
    }
}
