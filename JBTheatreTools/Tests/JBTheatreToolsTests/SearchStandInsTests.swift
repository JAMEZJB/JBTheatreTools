import XCTest
@testable import JBTheatreTools

/// Stand-ins that make launcher-folder apps findable in Spotlight and Alfred. Everything stays in a temporary folder;
/// nothing is written to ~/Applications, registered with Launch Services or opened.
final class SearchStandInsTests: XCTestCase {
    private var root: URL!
    private var apps: URL!
    private var standIns: SearchStandIns!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("jbtt-standin-\(UUID().uuidString)", isDirectory: true)
        apps = root.appendingPathComponent("support/apps", isDirectory: true)
        try FileManager.default.createDirectory(at: apps, withIntermediateDirectories: true)
        standIns = SearchStandIns(root: root.appendingPathComponent("Applications/JB Theatre Tools"), appsDir: apps)
    }

    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: root) }

    /// A minimal installed app with an icon, like the suite's bundles.
    @discardableResult
    private func realApp(_ name: String, in dir: URL? = nil) throws -> URL {
        let app = (dir ?? apps).appendingPathComponent("\(name).app")
        try FileManager.default.createDirectory(at: app.appendingPathComponent("Contents/Resources"), withIntermediateDirectories: true)
        let plist: [String: Any] = ["CFBundleName": name, "CFBundleIconFile": "icon.icns"]
        try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
            .write(to: app.appendingPathComponent("Contents/Info.plist"))
        try Data("icns".utf8).write(to: app.appendingPathComponent("Contents/Resources/icon.icns"))
        return app
    }

    private func rec(_ app: URL, _ version: String) -> InstalledRecord {
        InstalledRecord(version: version, path: app.path, installedAt: "")
    }

    private func standIn(_ name: String) -> URL { standIns.root.appendingPathComponent("\(name).app") }

    /// The catalog's names for each install slot.
    private let names = ["pdftools": "PDF Tools", "pdftools@full": "PDF Tools (Full)", "convert": "Convert Tools",
                         "showdashboard": "Show Dashboard"]

    func testStandInsUseTheCatalogNameNotTheBundles() throws {
        // Real bundles are called all sorts ("convert-to-it", "ShowDashboard", "Image Tools Full (Full)").
        let convert = try realApp("convert-to-it"), dash = try realApp("ShowDashboard")
        standIns.sync(["convert": rec(convert, "build-1"), "showdashboard": rec(dash, "v1.27.3")], names: names)
        XCTAssertEqual(SearchStandIns.standInTarget(standIn("Convert Tools")), convert.path)
        XCTAssertEqual(SearchStandIns.standInTarget(standIn("Show Dashboard")), dash.path)
        XCTAssertFalse(FileManager.default.fileExists(atPath: standIn("convert-to-it").path))
        // A slot the catalog doesn't name gets none (rather than a wrong one).
        let odd = try realApp("Mystery")
        standIns.sync(["mystery": rec(odd, "v1")], names: names)
        XCTAssertFalse(FileManager.default.fileExists(atPath: standIn("Mystery").path))
    }

    func testOlderStandInsAreRebuiltAndWronglyNamedOnesRemoved() throws {
        // 1.38.0 made them from the bundle name, with LSUIElement and no format number.
        let convert = try realApp("convert-to-it")
        let old = standIns.root.appendingPathComponent("convert-to-it.app")
        try FileManager.default.createDirectory(at: old.appendingPathComponent("Contents"), withIntermediateDirectories: true)
        let plist: [String: Any] = [SearchStandIns.markerKey: convert.path, SearchStandIns.versionKey: "build-1", "LSUIElement": true]
        try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0).write(to: old.appendingPathComponent("Contents/Info.plist"))
        let pdf = try realApp("PDF Tools")
        let sameName = standIn("PDF Tools")
        try FileManager.default.createDirectory(at: sameName.appendingPathComponent("Contents"), withIntermediateDirectories: true)
        try PropertyListSerialization.data(fromPropertyList: [SearchStandIns.markerKey: pdf.path, SearchStandIns.versionKey: "v1", "LSUIElement": true] as [String: Any],
                                           format: .xml, options: 0).write(to: sameName.appendingPathComponent("Contents/Info.plist"))
        standIns.sync(["convert": rec(convert, "build-1"), "pdftools": rec(pdf, "v1")], names: names)
        XCTAssertFalse(FileManager.default.fileExists(atPath: old.path), "the wrongly named one goes")
        XCTAssertNotNil(SearchStandIns.standInTarget(standIn("Convert Tools")))
        let info = try XCTUnwrap(SearchStandIns.info(sameName))
        XCTAssertEqual(info[SearchStandIns.formatKey] as? Int, SearchStandIns.format, "same version, but rebuilt in the new shape")
        XCTAssertNil(info["LSUIElement"])
    }

    func testEachLauncherFolderAppGetsAStandInThatOpensIt() throws {
        let pdf = try realApp("PDF Tools"), full = try realApp("PDF Tools (Full)")
        standIns.sync(["pdftools": rec(pdf, "v0.10.0"), "pdftools@full": rec(full, "v0.10.1-dev.2")], names: names)
        for (name, app) in [("PDF Tools", pdf), ("PDF Tools (Full)", full)] {
            let s = standIn(name)
            XCTAssertEqual(SearchStandIns.standInTarget(s), app.path)
            let script = try String(contentsOf: s.appendingPathComponent("Contents/MacOS/open-app"), encoding: .utf8)
            XCTAssertTrue(script.contains("exec /usr/bin/open -a '\(app.path)'"))
            let perms = try FileManager.default.attributesOfItem(atPath: s.appendingPathComponent("Contents/MacOS/open-app").path)[.posixPermissions] as? Int
            XCTAssertEqual(perms.map { $0 & 0o111 }, 0o111, "the stand-in's program must be executable")
            XCTAssertTrue(FileManager.default.fileExists(atPath: s.appendingPathComponent("Contents/Resources/AppIcon.icns").path))
            let info = try XCTUnwrap(NSDictionary(contentsOf: s.appendingPathComponent("Contents/Info.plist")))
            XCTAssertEqual(info["CFBundleName"] as? String, name)
            XCTAssertNil(info["LSUIElement"], "Spotlight leaves UI-element apps out of its results")
        }
    }

    func testAnUpdateRefreshesItAndAMoveToApplicationsOrRemovalTakesItAway() throws {
        let pdf = try realApp("PDF Tools")
        standIns.sync(["pdftools": rec(pdf, "v1.0.0")], names: names)
        standIns.sync(["pdftools": rec(pdf, "v1.1.0")], names: names)
        XCTAssertEqual(SearchStandIns.standInVersion(standIn("PDF Tools")), "v1.1.0")
        // Installed to /Applications instead: it's findable already, so the stand-in goes.
        let elsewhere = root.appendingPathComponent("Applications", isDirectory: true)
        let moved = try realApp("PDF Tools", in: elsewhere)
        standIns.sync(["pdftools": rec(moved, "v1.1.0")], names: names)
        XCTAssertFalse(FileManager.default.fileExists(atPath: standIn("PDF Tools").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: standIns.root.path), "an empty stand-in folder is tidied away")
    }

    func testSomeoneElsesAppInTheFolderIsNeverTouched() throws {
        try FileManager.default.createDirectory(at: standIns.root, withIntermediateDirectories: true)
        let foreign = try realApp("PDF Tools", in: standIns.root)   // not ours: no marker
        let pdf = try realApp("PDF Tools")
        standIns.sync(["pdftools": rec(pdf, "v1")], names: names)
        XCTAssertNil(SearchStandIns.standInTarget(foreign), "left as it was, not replaced")
        standIns.sync([:], names: names)
        XCTAssertTrue(FileManager.default.fileExists(atPath: foreign.path), "and never removed")
    }

    func testAPathWithAQuoteIsQuotedSafely() {
        let script = SearchStandIns.script(opening: URL(fileURLWithPath: "/x/James's Apps/A.app"))
        XCTAssertTrue(script.contains("'/x/James'\\''s Apps/A.app'"))
    }

    func testVersionAndIdentifierAreBundleSafe() {
        XCTAssertEqual(SearchStandIns.plainVersion("v1.37.0-dev.3"), "1.37.0")
        XCTAssertEqual(SearchStandIns.identifierPart("PDF Tools (Full)"), "pdf-tools--full-")
    }

    func testTestsNeverUseTheRealApplicationsFolder() {
        let r = SearchStandIns.defaultRoot(supportDir: root, isTestSupport: false)
        XCTAssertFalse(r.path.hasPrefix(FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications").path))
    }
}
