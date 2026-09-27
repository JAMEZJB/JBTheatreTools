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

    func testMoveCopiesInReplacesAnOlderCopyAndBinsTheDownload() throws {
        let root = try tempDir(); defer { try? FileManager.default.removeItem(at: root) }
        let downloads = root.appendingPathComponent("Downloads"), apps = root.appendingPathComponent("Applications")
        try FileManager.default.createDirectory(at: downloads, withIntermediateDirectories: true)
        let source = try fakeApp(in: downloads)
        _ = try fakeApp(in: apps, marker: "old")
        var binned: [String] = []
        let dest = try LauncherHome.move(source, original: source, into: apps, isOpen: { _ in false }) { url in
            binned.append(url.path); try FileManager.default.removeItem(at: url)
        }
        XCTAssertEqual(dest.path, apps.appendingPathComponent("JB Theatre Tools.app").path)
        XCTAssertEqual(try String(contentsOf: dest.appendingPathComponent("Contents/marker"), encoding: .utf8), "new")
        XCTAssertEqual(binned, [dest.path, source.path])   // the older copy, then the download
        XCTAssertFalse(FileManager.default.fileExists(atPath: source.path))
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: apps.path), ["JB Theatre Tools.app"])   // no temp left
    }

    func testTranslocatedCopyTakesTheRealName() throws {
        let root = try tempDir(); defer { try? FileManager.default.removeItem(at: root) }
        let source = try fakeApp(in: root.appendingPathComponent("AppTranslocation"), "JB Theatre Tools.app")
        let original = root.appendingPathComponent("Downloads/JB Theatre Tools 2.app")
        let dest = try LauncherHome.move(source, original: original, into: root.appendingPathComponent("Apps"), isOpen: { _ in false }) { _ in
            XCTFail("nothing to bin: the original isn't on disk here")
        }
        XCTAssertEqual(dest.lastPathComponent, "JB Theatre Tools 2.app")
    }

    func testAnOpenCopyAtTheDestinationStopsTheMoveUntouched() throws {
        let root = try tempDir(); defer { try? FileManager.default.removeItem(at: root) }
        let source = try fakeApp(in: root.appendingPathComponent("Downloads"))
        let apps = root.appendingPathComponent("Applications")
        let existing = try fakeApp(in: apps, marker: "old")
        XCTAssertThrowsError(try LauncherHome.move(source, original: source, into: apps, isOpen: { _ in true }) { _ in XCTFail("no bin") })
        XCTAssertEqual(try String(contentsOf: existing.appendingPathComponent("Contents/marker"), encoding: .utf8), "old")
        XCTAssertTrue(FileManager.default.fileExists(atPath: source.path))
    }
}
