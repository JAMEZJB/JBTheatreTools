import XCTest
@testable import JBTheatreTools

/// The file side of the launcher's in-place update (`SelfUpdate`): the running bundle steps aside as "<name>.old",
/// the new one takes its exact path and name, a failed swap changes nothing, and only this bundle's leftovers match.
final class SelfUpdateTests: XCTestCase {
    private var dir: URL!
    private let fm = FileManager.default

    override func setUpWithError() throws {
        dir = fm.temporaryDirectory.appendingPathComponent("jbtt-selfupdate-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws { try? fm.removeItem(at: dir) }

    /// A stand-in bundle: a folder with one file saying which version it is.
    private func bundle(_ path: String, _ version: String) throws -> URL {
        let url = dir.appendingPathComponent(path, isDirectory: true)
        try fm.createDirectory(at: url.appendingPathComponent("Contents"), withIntermediateDirectories: true)
        try Data(version.utf8).write(to: url.appendingPathComponent("Contents/version"))
        return url
    }
    private func version(_ url: URL) -> String? {
        (try? Data(contentsOf: url.appendingPathComponent("Contents/version"))).map { String(decoding: $0, as: UTF8.self) }
    }

    func testSwapKeepsTheAppsNameAndPlace() throws {
        let current = try bundle("apps/My Launcher.app", "old")
        let new = try bundle("staging/JB Theatre Tools.app", "new")
        let old = try SelfUpdate.swap(newBundle: new, into: current, toBin: { try self.fm.removeItem(at: $0) })
        XCTAssertEqual(version(current), "new")
        XCTAssertEqual(old.lastPathComponent, "My Launcher.app.old")
        XCTAssertEqual(version(old), "old")
        XCTAssertFalse(fm.fileExists(atPath: new.path))
        XCTAssertFalse(fm.fileExists(atPath: current.path + ".new"))
    }

    func testFailedSwapChangesNothing() throws {
        let current = try bundle("apps/JB Theatre Tools.app", "old")
        let missing = dir.appendingPathComponent("staging/nothing.app")
        XCTAssertThrowsError(try SelfUpdate.swap(newBundle: missing, into: current, toBin: { try self.fm.removeItem(at: $0) }))
        XCTAssertEqual(version(current), "old")
        XCTAssertTrue(SelfUpdate.leftovers(beside: current).isEmpty)
    }

    func testAFolderThatCantBeChangedIsReported() throws {
        let current = try bundle("locked/JB Theatre Tools.app", "old")
        let new = try bundle("staging/JB Theatre Tools.app", "new")
        let locked = current.deletingLastPathComponent()
        try fm.setAttributes([.posixPermissions: 0o555], ofItemAtPath: locked.path)   // like /Applications for a standard user
        defer { try? fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: locked.path) }
        XCTAssertThrowsError(try SelfUpdate.swap(newBundle: new, into: current, toBin: { try self.fm.removeItem(at: $0) })) { error in
            guard case SelfUpdate.Failure.notWritable = error else { return XCTFail("expected notWritable, got \(error)") }
        }
        XCTAssertEqual(version(current), "old")
        XCTAssertEqual(version(new), "new")   // still staged: saved to Downloads instead
    }

    func testRollBackPutsTheOldAppBack() throws {
        let current = try bundle("apps/JB Theatre Tools.app", "old")
        let new = try bundle("staging/JB Theatre Tools.app", "new but broken")
        let old = try SelfUpdate.swap(newBundle: new, into: current, toBin: { try self.fm.removeItem(at: $0) })
        XCTAssertTrue(SelfUpdate.rollBack(current: current, old: old))
        XCTAssertEqual(version(current), "old")
        XCTAssertFalse(fm.fileExists(atPath: old.path))
        XCTAssertFalse(fm.fileExists(atPath: current.path + ".failed"))
    }

    func testLeftoversAreOnlyThisAppsOwn() throws {
        let current = try bundle("apps/JB Theatre Tools.app", "current")
        for name in ["JB Theatre Tools.app.old", "JB Theatre Tools.app.3.old", "JB Theatre Tools.app.new",
                     "JB Theatre Tools.app.x.old", "Other.app.old", "JB Theatre Tools (1).app.old"] {
            _ = try bundle("apps/\(name)", "x")
        }
        XCTAssertEqual(SelfUpdate.leftovers(beside: current).map(\.lastPathComponent).sorted(),
                       ["JB Theatre Tools.app.3.old", "JB Theatre Tools.app.new", "JB Theatre Tools.app.old"])
    }

    func testAnEarlierOldCopyGoesToTheBin() throws {
        let current = try bundle("apps/JB Theatre Tools.app", "current")
        _ = try bundle("apps/JB Theatre Tools.app.old", "earlier")
        var binned: [String] = []
        let free = SelfUpdate.freeOldURL(for: current, toBin: { binned.append($0.lastPathComponent); try self.fm.removeItem(at: $0) })
        XCTAssertEqual(free.lastPathComponent, "JB Theatre Tools.app.old")
        XCTAssertEqual(binned, ["JB Theatre Tools.app.old"])   // to the Bin (recoverable), never a recursive delete
    }

    func testAnOldCopyThatCantBeBinnedGetsANumberedName() throws {
        let current = try bundle("apps/JB Theatre Tools.app", "current")
        _ = try bundle("apps/JB Theatre Tools.app.old", "stuck")
        _ = try bundle("apps/JB Theatre Tools.app.2.old", "stuck too")
        let free = SelfUpdate.freeOldURL(for: current, toBin: { _ in throw CocoaError(.fileWriteNoPermission) })
        XCTAssertEqual(free.lastPathComponent, "JB Theatre Tools.app.3.old")
    }

    func testTranslocatedCopiesAreRecognised() {
        XCTAssertTrue(SelfUpdate.isTranslocated(URL(fileURLWithPath: "/private/var/folders/x/AppTranslocation/ABC/d/JB Theatre Tools.app")))
        XCTAssertFalse(SelfUpdate.isTranslocated(URL(fileURLWithPath: "/Applications/JB Theatre Tools.app")))
    }
}
