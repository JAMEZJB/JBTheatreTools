import XCTest
@testable import JBTheatreTools

/// The window and a command-line install can run at once on a Mac and share one support folder. Two managers on the
/// same folder stand in for two processes: each has its own lock descriptor (flock conflicts between descriptors, as
/// between processes) and its own copy of the manifest. Everything stays in a temporary folder; nothing is installed
/// to /Applications and the Trash is never used.
final class SupportLockTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("jbtt-lock-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    private func app(_ id: String) -> CatalogApp {
        CatalogApp(id: id, name: id, blurb: "", category: nil, whatsNew: nil, whatsNewVersion: nil,
                   owner: "o", repo: id, assets: [:], variants: nil)
    }

    /// A minimal `<id>.app` zipped the way releases are (ditto, bundle at the top).
    private func zip(_ id: String) throws -> URL {
        let src = root.appendingPathComponent("src-\(id)", isDirectory: true)
        let bundle = src.appendingPathComponent("\(id).app/Contents", isDirectory: true)
        try FileManager.default.createDirectory(at: bundle, withIntermediateDirectories: true)
        try Data("<plist><dict><key>CFBundleName</key><string>\(id)</string></dict></plist>".utf8)
            .write(to: bundle.appendingPathComponent("Info.plist"))
        let out = root.appendingPathComponent("\(id).zip")
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        p.arguments = ["-c", "-k", "--keepParent", src.appendingPathComponent("\(id).app").path, out.path]
        try p.run()
        p.waitUntilExit()
        XCTAssertEqual(p.terminationStatus, 0)
        return out
    }

    private func manager() -> InstallManager { InstallManager(supportDir: root) }

    func testTwoProcessesInstallingAtOnceKeepEveryRecord() throws {
        // Without the shared lock each read-modify-write of installed.json drops the other's new records.
        let a = manager(), b = manager()
        let ids = (0..<16).map { "app\($0)" }
        let zips = try ids.map(zip)
        let failures = NSLock()
        var errors: [String] = []
        DispatchQueue.concurrentPerform(iterations: ids.count) { i in
            let m = i % 2 == 0 ? a : b
            do { try m.install(app: app(ids[i]), version: "v1", downloadedZip: zips[i], toApplications: false) }
            catch { failures.lock(); errors.append("\(ids[i]): \(error)"); failures.unlock() }
        }
        XCTAssertEqual(errors, [])
        let manifest = manager().manifest()
        for id in ids {
            XCTAssertNotNil(manifest[id], "\(id) lost from installed.json")
            XCTAssertTrue(FileManager.default.fileExists(atPath: manifest[id]?.path ?? "/nonexistent"))
        }
    }

    func testAChangeStartsFromTheManifestOnDiskNotThisProcessCopy() throws {
        // The window read installed.json at start; a command-line install since must survive the window's next install.
        let window = manager(), cli = manager()
        XCTAssertTrue(window.manifest().isEmpty)
        try cli.install(app: app("fromcli"), version: "v1", downloadedZip: try zip("fromcli"), toApplications: false)
        try window.install(app: app("fromwindow"), version: "v1", downloadedZip: try zip("fromwindow"), toApplications: false)
        let manifest = manager().manifest()
        XCTAssertNotNil(manifest["fromcli"])
        XCTAssertNotNil(manifest["fromwindow"])
        XCTAssertNotNil(window.installedVersion("fromcli"), "the window now shows the other process's install")
    }

    func testAnInstallWaitsForAnotherProcessHoldingTheLock() throws {
        let a = manager(), b = manager()
        let late = try zip("late")
        let started = DispatchSemaphore(value: 0), finished = DispatchSemaphore(value: 0)
        let release = DispatchSemaphore(value: 0)
        DispatchQueue.global().async {
            try? a.lock.hold { started.signal(); release.wait() }
        }
        started.wait()
        DispatchQueue.global().async {
            try? b.install(app: self.app("late"), version: "v1", downloadedZip: late, toApplications: false)
            finished.signal()
        }
        XCTAssertEqual(finished.wait(timeout: .now() + 0.5), .timedOut, "the install ran while another process held the lock")
        XCTAssertNil(manager().manifest()["late"])
        release.signal()
        XCTAssertEqual(finished.wait(timeout: .now() + 10), .success)
        XCTAssertNotNil(manager().manifest()["late"])
    }

    func testABusyLockGivesUpWithBusyAfterItsWait() throws {
        let a = SupportLock(directory: root), b = SupportLock(directory: root)
        try a.hold {
            XCTAssertThrowsError(try b.hold(wait: 0.2) {}) { error in
                XCTAssertTrue(error is SupportLock.Busy)
                XCTAssertEqual(error.localizedDescription,
                               "Another JB Theatre Tools window or command is still installing. Try again when it has finished.")
            }
        }
        try b.hold(wait: 0.2) {}   // free again once released
    }

    func testTheLockIsReentrantOnItsThread() throws {
        let a = SupportLock(directory: root)
        try a.hold { try a.hold(wait: 0) {} }   // an install's manifest write syncs connectors.json under the same hold
        try SupportLock(directory: root).hold(wait: 0.2) {}   // and the outer release really released it
    }

    func testATakenLockStaysWithItsThread() throws {
        let a = SupportLock(directory: root)
        let done = DispatchSemaphore(value: 0)
        try a.hold {
            // Another thread of the same process waits too (it would otherwise interleave a manifest write).
            DispatchQueue.global().async {
                try? a.hold(wait: 5) {}
                done.signal()
            }
            XCTAssertEqual(done.wait(timeout: .now() + 0.4), .timedOut)
        }
        XCTAssertEqual(done.wait(timeout: .now() + 5), .success)
    }

    // MARK: - The window following installs another process made

    func testTheWindowSeesAnotherProcessesInstallUpdateAndRemovalAfterReloading() throws {
        let window = manager(), cli = manager()
        XCTAssertNil(window.installedVersion("sh"))   // the window has read (and cached) the list
        try cli.install(app: app("sh"), version: "v1", downloadedZip: try zip("sh"), toApplications: false)
        XCTAssertNil(window.installedVersion("sh"), "without a reload the window still shows its old copy")
        window.reloadFromDisk()
        XCTAssertEqual(window.installedVersion("sh"), "v1")
        try cli.install(app: app("sh"), version: "v2", downloadedZip: try zip("sh"), toApplications: false)
        window.reloadFromDisk()
        XCTAssertEqual(window.installedVersion("sh"), "v2", "an update made elsewhere stops being offered")
        try cli.uninstall("sh")
        window.reloadFromDisk()
        XCTAssertNil(window.installedVersion("sh"))
    }

    func testTheFolderWatchFiresWhenInstalledJsonIsReplaced() throws {
        // installed.json is written atomically (a temp file renamed over it): the window's watch on the support folder
        // must see that.
        let file = root.appendingPathComponent("installed.json")
        try Data("{}".utf8).write(to: file, options: .atomic)
        let fd = open(root.path, O_EVTONLY)
        XCTAssertGreaterThanOrEqual(fd, 0)
        let fired = expectation(description: "folder watch fired")
        fired.assertForOverFulfill = false
        let source = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd, eventMask: [.write, .rename, .delete],
                                                               queue: .main)
        source.setEventHandler { fired.fulfill() }
        source.setCancelHandler { close(fd) }
        source.resume()
        defer { source.cancel() }
        try Data(#"{"sh":{}}"#.utf8).write(to: file, options: .atomic)
        wait(for: [fired], timeout: 3)
    }
}
