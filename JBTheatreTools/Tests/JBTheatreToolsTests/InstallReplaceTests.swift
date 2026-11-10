import XCTest
@testable import JBTheatreTools

/// Replacing an installed app: a new copy that can't be moved into place (a full disk, a failed copy to another
/// volume) must leave the copy that was there. Everything stays in a temporary folder; nothing is installed to
/// /Applications, nothing is downloaded and nothing is opened.
final class InstallReplaceTests: XCTestCase {
    private var root: URL!
    private let fm = FileManager.default

    override func setUpWithError() throws {
        root = fm.temporaryDirectory.appendingPathComponent("jbtt-replace-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? fm.removeItem(at: root)
    }

    private struct DiskFull: Error {}

    private func app(_ id: String) -> CatalogApp {
        CatalogApp(id: id, name: id, blurb: "", category: nil, whatsNew: nil, whatsNewVersion: nil,
                   owner: "o", repo: id, assets: [:], variants: nil)
    }

    /// A minimal `<id>.app` zipped the way releases are (ditto, bundle at the top), carrying `marker` so a test can
    /// tell one build from another.
    private func zip(_ id: String, marker: String) throws -> URL {
        let src = root.appendingPathComponent("src-\(UUID().uuidString)", isDirectory: true)
        let contents = src.appendingPathComponent("\(id).app/Contents", isDirectory: true)
        try fm.createDirectory(at: contents, withIntermediateDirectories: true)
        try Data("<plist><dict><key>CFBundleName</key><string>\(id)</string></dict></plist>".utf8)
            .write(to: contents.appendingPathComponent("Info.plist"))
        try Data(marker.utf8).write(to: contents.appendingPathComponent("marker.txt"))
        let out = root.appendingPathComponent("\(id)-\(marker).zip")
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        p.arguments = ["-c", "-k", "--keepParent", src.appendingPathComponent("\(id).app").path, out.path]
        try p.run()
        p.waitUntilExit()
        XCTAssertEqual(p.terminationStatus, 0)
        return out
    }

    private func marker(_ bundle: String) -> String? {
        try? String(contentsOfFile: bundle + "/Contents/marker.txt", encoding: .utf8)
    }

    private func manager() -> InstallManager { InstallManager(supportDir: root) }

    func testANewCopyThatCannotBeMovedInLeavesTheOldInstall() throws {
        let m = manager()
        let installed = try m.install(app: app("demo"), version: "v1", downloadedZip: try zip("demo", marker: "one"),
                                      toApplications: false)
        m.moveNewBundle = { _, _ in throw DiskFull() }
        XCTAssertThrowsError(try m.install(app: app("demo"), version: "v2", downloadedZip: try zip("demo", marker: "two"),
                                           toApplications: false)) { XCTAssertTrue($0 is DiskFull) }
        XCTAssertEqual(marker(installed.path), "one", "the app that was installed is gone")
        let record = manager().manifest()["demo"]
        XCTAssertEqual(record?.version, "v1")
        XCTAssertEqual(record?.path, installed.path)
        let left = try fm.contentsOfDirectory(atPath: installed.deletingLastPathComponent().path)
        XCTAssertEqual(left, [installed.lastPathComponent], "nothing else stays in the apps folder")
    }

    func testAnOldCopyElsewhereIsKeptWhenTheNewOneCannotBeMovedIn() throws {
        // The slot's record points at a copy in another folder (the install-location setting changed since).
        let m = manager()
        let first = try m.install(app: app("demo"), version: "v1", downloadedZip: try zip("demo", marker: "one"),
                                  toApplications: false)
        let other = root.appendingPathComponent("other", isDirectory: true)
        try fm.createDirectory(at: other, withIntermediateDirectories: true)
        let elsewhere = other.appendingPathComponent("demo.app")
        try fm.moveItem(at: first, to: elsewhere)
        try rewriteRecordPath("demo", to: elsewhere.path)

        let failing = manager()
        failing.moveNewBundle = { _, _ in throw DiskFull() }
        XCTAssertThrowsError(try failing.install(app: app("demo"), version: "v2",
                                                 downloadedZip: try zip("demo", marker: "two"), toApplications: false))
        XCTAssertEqual(marker(elsewhere.path), "one", "the app that was installed is gone")
        XCTAssertEqual(manager().manifest()["demo"]?.path, elsewhere.path)
        XCTAssertFalse(fm.fileExists(atPath: first.path))
    }

    func testAnOldCopyElsewhereIsRemovedOnceTheNewOneIsInstalled() throws {
        let m = manager()
        let first = try m.install(app: app("demo"), version: "v1", downloadedZip: try zip("demo", marker: "one"),
                                  toApplications: false)
        let other = root.appendingPathComponent("other", isDirectory: true)
        try fm.createDirectory(at: other, withIntermediateDirectories: true)
        let elsewhere = other.appendingPathComponent("demo.app")
        try fm.moveItem(at: first, to: elsewhere)
        try rewriteRecordPath("demo", to: elsewhere.path)

        let installed = try manager().install(app: app("demo"), version: "v2",
                                              downloadedZip: try zip("demo", marker: "two"), toApplications: false)
        XCTAssertEqual(marker(installed.path), "two")
        XCTAssertFalse(fm.fileExists(atPath: elsewhere.path), "the old copy stays behind in the other folder")
        XCTAssertEqual(manager().manifest()["demo"]?.path, installed.path)
    }

    func testAReplaceLeavesOnlyTheNewCopy() throws {
        let m = manager()
        let first = try m.install(app: app("demo"), version: "v1", downloadedZip: try zip("demo", marker: "one"),
                                  toApplications: false)
        // What a replace that was cut short would have left beside it.
        let stale = first.deletingLastPathComponent().appendingPathComponent(".demo.app.replaced-earlier", isDirectory: true)
        try fm.createDirectory(at: stale, withIntermediateDirectories: true)
        let second = try m.install(app: app("demo"), version: "v2", downloadedZip: try zip("demo", marker: "two"),
                                   toApplications: false)
        XCTAssertEqual(second.path, first.path)
        XCTAssertEqual(marker(second.path), "two")
        XCTAssertEqual(try fm.contentsOfDirectory(atPath: second.deletingLastPathComponent().path), ["demo.app"])
        XCTAssertEqual(manager().manifest()["demo"]?.version, "v2")
        XCTAssertEqual(manager().manifest()["demo"]?.previousVersion, "v1")
    }

    func testSwapInPutsTheOldBundleBackAndClearsAPartCopy() throws {
        let folder = root.appendingPathComponent("swap", isDirectory: true)
        let dest = folder.appendingPathComponent("demo.app", isDirectory: true)
        let new = root.appendingPathComponent("new.app", isDirectory: true)
        try fm.createDirectory(at: dest, withIntermediateDirectories: true)
        try fm.createDirectory(at: new, withIntermediateDirectories: true)
        try Data("old".utf8).write(to: dest.appendingPathComponent("which"))
        try Data("new".utf8).write(to: new.appendingPathComponent("which"))
        // A copy to another volume that fails part-way leaves a half bundle at the destination.
        XCTAssertThrowsError(try InstallManager.swapIn(new, at: dest, moveNew: { _, to in
            try FileManager.default.createDirectory(at: to, withIntermediateDirectories: true)
            try Data("half".utf8).write(to: to.appendingPathComponent("which"))
            throw DiskFull()
        }))
        XCTAssertEqual(try String(contentsOf: dest.appendingPathComponent("which"), encoding: .utf8), "old")
        XCTAssertEqual(try fm.contentsOfDirectory(atPath: folder.path), ["demo.app"])
        XCTAssertTrue(fm.fileExists(atPath: new.path), "the new bundle is left for the caller to clear")

        try InstallManager.swapIn(new, at: dest)
        XCTAssertEqual(try String(contentsOf: dest.appendingPathComponent("which"), encoding: .utf8), "new")
        XCTAssertEqual(try fm.contentsOfDirectory(atPath: folder.path), ["demo.app"])
    }

    func testSwapInWithNothingThereLeavesNothingWhenTheMoveFails() throws {
        let folder = root.appendingPathComponent("swap", isDirectory: true)
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        let dest = folder.appendingPathComponent("demo.app", isDirectory: true)
        XCTAssertThrowsError(try InstallManager.swapIn(root.appendingPathComponent("missing.app"), at: dest))
        XCTAssertEqual(try fm.contentsOfDirectory(atPath: folder.path), [])
    }

    // MARK: - The zip that's unpacked is the zip that was verified

    func testAZipThatChangedAfterItWasVerifiedIsNotInstalled() throws {
        let m = manager()
        let good = try zip("demo", marker: "one")
        let signedHash = try InstallManager.sha256Hex(of: good)
        let installed = try m.install(app: app("demo"), version: "v1", downloadedZip: good, toApplications: false,
                                      expectedSHA256: signedHash.uppercased())   // case doesn't matter
        XCTAssertEqual(marker(installed.path), "one")

        // The next download is verified, then swapped for another build while the launcher waits.
        let next = try zip("demo", marker: "two")
        let nextHash = try InstallManager.sha256Hex(of: next)
        try fm.removeItem(at: next)
        try fm.copyItem(at: try zip("demo", marker: "swapped"), to: next)
        XCTAssertThrowsError(try m.install(app: app("demo"), version: "v2", downloadedZip: next, toApplications: false,
                                           expectedSHA256: nextHash)) { error in
            guard case InstallError.changedAfterVerify = error else { return XCTFail("\(error)") }
            XCTAssertEqual(error.localizedDescription,
                           "The download changed after it was verified — nothing was installed. Try again.")
        }
        XCTAssertEqual(marker(installed.path), "one", "the swapped build was installed")
        XCTAssertEqual(manager().manifest()["demo"]?.version, "v1")
        XCTAssertFalse(fm.fileExists(atPath: next.path), "the changed file stays in the cache")
        XCTAssertEqual(try fm.contentsOfDirectory(atPath: m.cacheDir.path), [], "nothing was unpacked")
    }

    private func rewriteRecordPath(_ key: String, to path: String) throws {
        let url = manager().manifestURL
        var all = try JSONDecoder().decode([String: InstalledRecord].self, from: Data(contentsOf: url))
        all[key]?.path = path
        try JSONEncoder().encode(all).write(to: url, options: .atomic)
    }
}
