import XCTest
@testable import JBTheatreTools

/// When the launcher offers to move itself, and the move (with the Bin and "is it open?" stubbed — never the real Bin).
final class LauncherHomeTests: XCTestCase {
    func testOffersOutsideApplicationsOnly() {
        let home = "/Users/u"
        XCTAssertTrue(LauncherHome.shouldOffer(bundlePath: "/Users/u/Downloads/JB Theatre Tools.app", homePath: home, kept: []))
        XCTAssertTrue(LauncherHome.shouldOffer(bundlePath: "/private/var/folders/x/AppTranslocation/Y/d/JB Theatre Tools.app", homePath: home, kept: []))
        XCTAssertFalse(LauncherHome.shouldOffer(bundlePath: "/Applications/JB Theatre Tools.app", homePath: home, kept: []))
        XCTAssertFalse(LauncherHome.shouldOffer(bundlePath: "/Applications/Theatre/JB Theatre Tools.app", homePath: home, kept: []))
        XCTAssertFalse(LauncherHome.shouldOffer(bundlePath: "/Users/u/Applications/JB Theatre Tools.app", homePath: home + "/", kept: []))
        XCTAssertTrue(LauncherHome.shouldOffer(bundlePath: "/ApplicationsX/JB Theatre Tools.app", homePath: home, kept: []))
    }

    func testAKeptOrChosenCopyIsNeverAskedAgain() {
        let mine = "/Volumes/Show/Tools/JB Theatre Tools.app"
        XCTAssertFalse(LauncherHome.shouldOffer(bundlePath: mine, homePath: "/Users/u", kept: [mine + "/"]))
        XCTAssertTrue(LauncherHome.shouldOffer(bundlePath: mine, homePath: "/Users/u", kept: ["/Users/u/Downloads/JB Theatre Tools.app"]))
        XCTAssertFalse(LauncherHome.shouldOffer(bundlePath: "/tmp/apps/JB Theatre Tools.app", homePath: "/Users/u", kept: [], testApplications: "/tmp/apps"))
    }

    private func tempDir() throws -> URL {
        let d = FileManager.default.temporaryDirectory.appendingPathComponent("jbtt-move-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: d, withIntermediateDirectories: true)
        return d
    }

    private func fakeApp(in dir: URL, _ name: String = "JB Theatre Tools.app", marker: String = "new") throws -> URL {
        let app = dir.appendingPathComponent(name)
        try FileManager.default.createDirectory(at: app.appendingPathComponent("Contents"), withIntermediateDirectories: true)
        try Data(marker.utf8).write(to: app.appendingPathComponent("Contents/marker"))
        return app
    }

    func testInstallCopiesInUnderTheFixedNameAndReplacesAnOlderCopyOnlyOnceTheNewOneIsInPlace() throws {
        let root = try tempDir(); defer { try? FileManager.default.removeItem(at: root) }
        let downloads = root.appendingPathComponent("Downloads"), apps = root.appendingPathComponent("Applications")
        try FileManager.default.createDirectory(at: downloads, withIntermediateDirectories: true)
        let source = try fakeApp(in: downloads, "JB Theatre Tools 2.app")
        _ = try fakeApp(in: apps, marker: "old")
        var binned: [(String, String)] = []
        let dest = try LauncherHome.install(source, into: apps, isOpen: { _ in false }) { url in
            // When the older copy is binned, the new one is already in place.
            let now = try String(contentsOf: apps.appendingPathComponent("JB Theatre Tools.app/Contents/marker"), encoding: .utf8)
            binned.append((try String(contentsOf: url.appendingPathComponent("Contents/marker"), encoding: .utf8), now))
            try FileManager.default.removeItem(at: url)
        }
        XCTAssertEqual(dest.lastPathComponent, "JB Theatre Tools.app")   // never "… 2.app"
        XCTAssertEqual(try String(contentsOf: dest.appendingPathComponent("Contents/marker"), encoding: .utf8), "new")
        XCTAssertEqual(binned.map(\.0), ["old"])
        XCTAssertEqual(binned.map(\.1), ["new"])
        XCTAssertTrue(FileManager.default.fileExists(atPath: source.path))   // the download is the caller's to bin, later
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: apps.path), ["JB Theatre Tools.app"])   // no temp left
    }

    func testAnOpenCopyAtTheDestinationStopsTheInstallUntouched() throws {
        let root = try tempDir(); defer { try? FileManager.default.removeItem(at: root) }
        let source = try fakeApp(in: root.appendingPathComponent("Downloads"))
        let apps = root.appendingPathComponent("Applications")
        let existing = try fakeApp(in: apps, marker: "old")
        XCTAssertThrowsError(try LauncherHome.install(source, into: apps, isOpen: { _ in true }) { _ in XCTFail("no bin") })
        XCTAssertEqual(try String(contentsOf: existing.appendingPathComponent("Contents/marker"), encoding: .utf8), "old")
        XCTAssertTrue(FileManager.default.fileExists(atPath: source.path))
    }

    func testKeepsANewerOrSameExistingLauncher() {
        XCTAssertTrue(LauncherHome.keepExisting(existing: "1.33.0", mine: "1.31.0"))
        XCTAssertTrue(LauncherHome.keepExisting(existing: "1.31.0", mine: "1.31.0"))
        XCTAssertTrue(LauncherHome.keepExisting(existing: "1.31.0-dev.4", mine: "1.31.0-dev.3"))
        XCTAssertFalse(LauncherHome.keepExisting(existing: "1.31.0-dev.3", mine: "1.31.0-dev.4"))
        XCTAssertFalse(LauncherHome.keepExisting(existing: "1.29.1", mine: "1.31.0"))
        XCTAssertFalse(LauncherHome.keepExisting(existing: nil, mine: "1.31.0"))
        XCTAssertFalse(LauncherHome.keepExisting(existing: " ", mine: "1.31.0"))
    }

    func testChooseFolderRefusesOwnAndAppDataFolders() {
        let own = ["/Users/u/Library/Application Support/JBTheatreTools", "/Users/u/Library/Application Support/PSN Tools"]
        XCTAssertNotNil(LauncherHome.refuseFolder("relative/Tools", own: own))
        XCTAssertNotNil(LauncherHome.refuseFolder(own[0], own: own))
        XCTAssertNotNil(LauncherHome.refuseFolder(own[0] + "/apps", own: own))
        XCTAssertNotNil(LauncherHome.refuseFolder(own[1] + "/", own: own))
        XCTAssertNil(LauncherHome.refuseFolder("/Applications", own: own))
        XCTAssertNil(LauncherHome.refuseFolder("/Users/u/Library/Application Support/JBTheatreTools2", own: own))
    }

    func testInside() {
        XCTAssertTrue(LauncherHome.isInside("/Users/u/Downloads/JB Theatre Tools.app", "/Users/u/Downloads"))
        XCTAssertTrue(LauncherHome.isInside("/Users/u/Downloads/x/JB Theatre Tools.app", "/Users/u/Downloads/"))
        XCTAssertFalse(LauncherHome.isInside("/Users/u/Downloads2/JB Theatre Tools.app", "/Users/u/Downloads"))
        XCTAssertFalse(LauncherHome.isInside("/Users/u/Desktop/JB Theatre Tools.app", "/Users/u/Downloads"))
    }
}
