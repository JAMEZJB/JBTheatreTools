import XCTest
@testable import JBTheatreTools

/// The hidden `--trigger-host` copy and its Stagehand registration. Loopback (127.0.0.1, port 0) and a temp folder
/// standing in for STAGEHAND_HOME only — never the real state folder, never the real Stagehand.
final class TriggerHostTests: XCTestCase {
    private var home: URL!

    override func setUpWithError() throws {
        home = FileManager.default.temporaryDirectory.appendingPathComponent("jbtt-host-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: home)
    }

    private var stateDir: URL { TriggerRegistration.stateDir(environment: ["STAGEHAND_HOME": home.path]) }

    // MARK: - Where Stagehand looks

    func testStateDir() {
        XCTAssertEqual(TriggerRegistration.stateDir(environment: ["STAGEHAND_HOME": "/tmp/sandbox"]).path, "/tmp/sandbox/stagehand")
        let fakeHome = URL(fileURLWithPath: "/Users/synthetic", isDirectory: true)
        XCTAssertEqual(TriggerRegistration.stateDir(environment: [:], home: fakeHome).path,
                       "/Users/synthetic/Library/Application Support/JBTheatreTools/stagehand")
        XCTAssertEqual(TriggerRegistration.stateDir(environment: ["STAGEHAND_HOME": ""], home: fakeHome).path,
                       "/Users/synthetic/Library/Application Support/JBTheatreTools/stagehand")
    }

    // MARK: - Registration

    func testRegisterWritesThePrivateFileAndUnregisterRemovesIt() throws {
        let exe = "/Applications/Synthetic Launcher.app/Contents/MacOS/JBTheatreTools"
        let when = Date(timeIntervalSince1970: 1_800_000_000)
        XCTAssertTrue(TriggerRegistration.register(exe: exe, stateDir: stateDir, now: when))
        let url = stateDir.appendingPathComponent("triggers/jbtheatretools.json")
        XCTAssertEqual(url, TriggerRegistration.registrationURL(stateDir))
        let doc = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        XCTAssertEqual(doc["app_id"] as? String, "jbtheatretools")
        XCTAssertEqual(doc["name"] as? String, "JB Theatre Tools")
        XCTAssertEqual(doc["exe"] as? String, exe)
        XCTAssertEqual(doc["args"] as? [String], ["--trigger-host"])
        XCTAssertEqual(doc["registered"] as? String, "2027-01-15T08:00:00Z")
        let mode = try XCTUnwrap(FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? Int)
        XCTAssertEqual(mode & 0o777, 0o600)
        // Atomic: nothing half-written is left beside it.
        let left = try FileManager.default.contentsOfDirectory(atPath: url.deletingLastPathComponent().path)
        XCTAssertEqual(left, ["jbtheatretools.json"])

        // Re-registering (every start while it's on) refreshes the path.
        XCTAssertTrue(TriggerRegistration.register(exe: "/Applications/Newer.app/Contents/MacOS/JBTheatreTools", stateDir: stateDir))
        let again = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        XCTAssertEqual(again["exe"] as? String, "/Applications/Newer.app/Contents/MacOS/JBTheatreTools")

        XCTAssertTrue(TriggerRegistration.unregister(stateDir: stateDir))
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
        XCTAssertFalse(TriggerRegistration.unregister(stateDir: stateDir), "nothing left to remove")
    }

    func testHostMarkSaysWhoIsAnswering() throws {
        XCTAssertFalse(TriggerRegistration.backgroundAnswering(stateDir: stateDir))
        TriggerRegistration.markHost(pid: 4242, port: 42154, stateDir: stateDir)
        XCTAssertTrue(TriggerRegistration.backgroundAnswering(stateDir: stateDir, alive: { $0 == 4242 }))
        XCTAssertFalse(TriggerRegistration.backgroundAnswering(stateDir: stateDir, alive: { _ in false }), "a dead copy isn't answering")
        let mode = try XCTUnwrap(FileManager.default.attributesOfItem(atPath: TriggerRegistration.hostMarkURL(stateDir).path)[.posixPermissions] as? Int)
        XCTAssertEqual(mode & 0o777, 0o600)
        XCTAssertTrue(TriggerRegistration.hostMarkURL(stateDir).lastPathComponent.hasPrefix("_"), "Stagehand skips it")
        TriggerRegistration.clearHost(pid: 1, stateDir: stateDir)
        XCTAssertTrue(FileManager.default.fileExists(atPath: TriggerRegistration.hostMarkURL(stateDir).path), "only its own mark is removed")
        TriggerRegistration.clearHost(pid: 4242, stateDir: stateDir)
        XCTAssertFalse(FileManager.default.fileExists(atPath: TriggerRegistration.hostMarkURL(stateDir).path))
    }

    func testAnsweringWords() {
        XCTAssertEqual(LauncherActions.answering(enabled: false, windowOwns: false, background: false, stagehandInstalled: true), "")
        XCTAssertEqual(LauncherActions.answering(enabled: true, windowOwns: true, background: true, stagehandInstalled: true),
                       "Answering presses: this window.")
        XCTAssertEqual(LauncherActions.answering(enabled: true, windowOwns: false, background: true, stagehandInstalled: true),
                       "Answering presses: the background copy.")
        XCTAssertEqual(LauncherActions.answering(enabled: true, windowOwns: false, background: false, stagehandInstalled: true),
                       "Answering presses: not running — open Stagehand.")
        XCTAssertTrue(LauncherActions.answering(enabled: true, windowOwns: false, background: false, stagehandInstalled: false)
            .hasSuffix("Install Stagehand so dashboard buttons keep working when JB Theatre Tools is closed."))
        XCTAssertEqual(LauncherActions.backgroundNote,
                       "Dashboard buttons keep working when JB Theatre Tools is closed: Stagehand keeps a hidden copy running for them.")
        let shown = LauncherActions.describe(enabled: true, bind: "127.0.0.1", running: false, address: "", port: 42154, error: "",
                                             waiting: true, background: true)
        XCTAssertFalse(shown.problem, "our own background copy holding the port is fine")
    }

    // MARK: - Asking Stagehand (stubbed — the real Stagehand never runs here)

    func testEnsureRunsStagehandWithTheEnsureArgsAndNeverThrows() throws {
        var calls: [(URL, [String])] = []
        let exe = URL(fileURLWithPath: "/Applications/Synthetic Stagehand.app/Contents/MacOS/Stagehand")
        XCTAssertTrue(StagehandLink.ensure(exe, spawn: { calls.append(($0, $1)) }, log: { _ in }))
        XCTAssertEqual(calls.count, 1)
        XCTAssertEqual(calls.first?.0, exe)
        XCTAssertEqual(calls.first?.1, ["--trigger-hosts", "--ensure"])
        var lines: [String] = []
        XCTAssertFalse(StagehandLink.ensure(exe, spawn: { _, _ in throw CocoaError(.fileNoSuchFile) }, log: { lines.append($0) }))
        XCTAssertEqual(lines.count, 1)
    }

    func testStagehandProgramInsideItsApp() throws {
        let app = home.appendingPathComponent("Stagehand.app", isDirectory: true)
        let macos = app.appendingPathComponent("Contents/MacOS", isDirectory: true)
        try FileManager.default.createDirectory(at: macos, withIntermediateDirectories: true)
        let plist: NSDictionary = ["CFBundleExecutable": "StagehandBin"]
        XCTAssertTrue(plist.write(to: app.appendingPathComponent("Contents/Info.plist"), atomically: true))
        XCTAssertNil(StagehandLink.executable(installed: app), "no program yet")
        let bin = macos.appendingPathComponent("StagehandBin")
        XCTAssertTrue(FileManager.default.createFile(atPath: bin.path, contents: Data("#!/bin/sh\n".utf8),
                                                    attributes: [.posixPermissions: 0o755]))
        XCTAssertEqual(StagehandLink.executable(installed: app)?.path, bin.path)
        XCTAssertNil(StagehandLink.executable(installed: nil))
    }

    // MARK: - Dispatch

    func testTriggerHostDispatchComesFirst() {
        XCTAssertEqual(LaunchMode.of(["--trigger-host"]), .triggerHost)
        XCTAssertEqual(LaunchMode.of(["--list", "--trigger-host"]), .triggerHost)
        XCTAssertEqual(LaunchMode.of(["--list"]), .cli)
        XCTAssertEqual(LaunchMode.of([]), .gui)
        XCTAssertEqual(LaunchMode.of(["-NSDocumentRevisionsDebugMode", "YES"]), .gui)
    }

    /// The hidden copy never builds the model or a window: the host's source names neither, and main.swift dispatches
    /// it before the app (and its AppState) exist.
    func testTriggerHostNeverTouchesTheWindowOrTheModel() throws {
        let sources = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().appendingPathComponent("Sources/JBTheatreTools")
        let host = try String(contentsOf: sources.appendingPathComponent("TriggerHost.swift"))
        for banned in ["AppState(", "NSApplication", "NSApp.", "JBTheatreToolsApp", "import AppKit", "import SwiftUI"] {
            XCTAssertFalse(host.contains(banned), banned)
        }
        let main = try String(contentsOf: sources.appendingPathComponent("main.swift"))
        let dispatch = try XCTUnwrap(main.range(of: "TriggerHostMain.run()"))
        let app = try XCTUnwrap(main.range(of: "JBTheatreToolsApp.main()"))
        XCTAssertLessThan(dispatch.lowerBound, app.lowerBound)
    }

    // MARK: - The hidden copy's loop (loopback, port 0)

    private func plainServer() -> WebTriggerServer {
        WebTriggerServer(run: { _, _ in WebTriggerReply(200, ["ok": true, "summary": "synthetic"]) }, listing: { [] }, log: { _ in })
    }

    @MainActor
    private func eventually(_ done: () -> Bool) async -> Bool {
        for _ in 0..<100 {
            if done() { return true }
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
        return done()
    }

    /// The window holds the port → the hidden copy waits; the window quits → the copy takes over and marks itself;
    /// the trigger is switched off in settings → it lets go and ends with 0.
    @MainActor
    func testHostWaitsForTheWindowTakesOverAndStopsWhenSwitchedOff() async throws {
        let window = plainServer()
        let started = await window.start(bind: "127.0.0.1", port: 0)
        XCTAssertEqual(started, "")
        let port = window.port
        var settings = TriggerSettings(enabled: true, bind: "127.0.0.1", port: port)
        var lines: [String] = []
        let runner = TriggerHostRunner(owner: WebTriggerOwner(server: plainServer(), retry: 0.1), settings: { settings },
                                       readToken: { "" }, stateDir: stateDir, log: { lines.append($0) })

        let first = await runner.tick()
        XCTAssertNil(first)
        XCTAssertEqual(runner.owner.state, .waiting)
        XCTAssertFalse(TriggerRegistration.backgroundAnswering(stateDir: stateDir))

        window.stop()   // the window quits
        let tookOver = await eventually { runner.owner.state == .owning }
        XCTAssertTrue(tookOver)
        XCTAssertEqual(runner.owner.server.port, port)
        XCTAssertTrue(TriggerRegistration.backgroundAnswering(stateDir: stateDir), "Settings can tell the copy is answering")

        // Still on: another pass changes nothing.
        let second = await runner.tick()
        XCTAssertNil(second)
        XCTAssertTrue(runner.owner.server.running)

        settings.enabled = false
        let code = await runner.tick()
        XCTAssertEqual(code, 0)
        XCTAssertFalse(runner.owner.server.running)
        XCTAssertFalse(TriggerRegistration.backgroundAnswering(stateDir: stateDir))
        XCTAssertTrue(lines.contains { $0.contains("switched off") })
    }

    /// The loop itself ends (rather than spinning) once settings say off.
    @MainActor
    func testRunEndsWithZeroWhenTheTriggerIsOff() async {
        let runner = TriggerHostRunner(owner: WebTriggerOwner(server: plainServer(), retry: 0.1),
                                       settings: { TriggerSettings(enabled: false, bind: "127.0.0.1", port: 0) },
                                       readToken: { "" }, stateDir: stateDir, log: { _ in })
        let code = await runner.run(every: 0.05)
        XCTAssertEqual(code, 0)
    }

    /// A bad saved address (every network without a token) isn't served, and the copy keeps re-reading settings.
    @MainActor
    func testHostRefusesWhatTheWindowWouldRefuse() async {
        var lines: [String] = []
        let runner = TriggerHostRunner(owner: WebTriggerOwner(server: plainServer(), retry: 0.1),
                                       settings: { TriggerSettings(enabled: true, bind: "0.0.0.0", port: 0) },
                                       readToken: { "" }, stateDir: stateDir, log: { lines.append($0) })
        let code = await runner.tick()
        XCTAssertNil(code)
        XCTAssertFalse(runner.owner.server.running)
        XCTAssertTrue(lines.contains { $0.contains("needs a token") })
        runner.owner.stop()
    }

    /// The launcher is running → the hidden copy lets go of the port (the launcher's own Owner takes it); gone → it serves.
    @MainActor
    func testHostLetsTheRunningLauncherAnswer() async throws {
        var launcherUp = false
        let probe = plainServer()
        _ = await probe.start(bind: "127.0.0.1", port: 0)
        let port = probe.port
        probe.stop()
        let runner = TriggerHostRunner(owner: WebTriggerOwner(server: plainServer(), retry: 0.1),
                                       settings: { TriggerSettings(enabled: true, bind: "127.0.0.1", port: port) },
                                       readToken: { "" }, stateDir: stateDir, windowOpen: { launcherUp }, log: { _ in })
        _ = await runner.tick()
        XCTAssertEqual(runner.owner.state, .owning)
        XCTAssertTrue(TriggerRegistration.backgroundAnswering(stateDir: stateDir))
        launcherUp = true
        _ = await runner.tick()
        XCTAssertFalse(runner.owner.server.running)
        XCTAssertFalse(TriggerRegistration.backgroundAnswering(stateDir: stateDir))
        launcherUp = false
        _ = await runner.tick()
        XCTAssertEqual(runner.owner.state, .owning)
        runner.owner.stop()
    }

    func testWindowMark() {
        XCTAssertFalse(TriggerRegistration.windowRunning(stateDir: stateDir, isLauncher: { _ in true }))
        TriggerRegistration.markWindow(pid: 4242, stateDir: stateDir)
        XCTAssertTrue(TriggerRegistration.windowRunning(stateDir: stateDir, isLauncher: { $0 == 4242 }))
        XCTAssertFalse(TriggerRegistration.windowRunning(stateDir: stateDir, ownPid: 4242, isLauncher: { _ in true }), "not itself")
        XCTAssertFalse(TriggerRegistration.windowRunning(stateDir: stateDir, isLauncher: { _ in false }), "a dead or reused pid")
        TriggerRegistration.clearWindow(pid: 1, stateDir: stateDir)
        XCTAssertTrue(TriggerRegistration.windowRunning(stateDir: stateDir, isLauncher: { _ in true }))
        TriggerRegistration.clearWindow(pid: 4242, stateDir: stateDir)
        XCTAssertFalse(TriggerRegistration.windowRunning(stateDir: stateDir, isLauncher: { _ in true }))
    }
}

/// The hidden copy's REAL action host and press handler (HeadlessTriggerHost), against stand-ins: a throwaway
/// UserDefaults suite, a temp claude.json and install folder, and stubbed launch / "is it open" — never the real
/// settings, apps or Keychain.
final class HeadlessTriggerHostTests: XCTestCase {
    private var root: URL!
    private var suite = ""
    private var defaults: UserDefaults!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("jbtt-headless-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        suite = "jbtt-tests-\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suite)
    }

    override func tearDownWithError() throws {
        defaults.removePersistentDomain(forName: suite)
        try? FileManager.default.removeItem(at: root)
    }

    private let apps = [
        LauncherActions.App(id: "dmxtools", name: "DMX Tools", slots: [.init(key: "dmxtools", suffix: "")]),
        LauncherActions.App(id: "psntools", name: "PSN Tools", slots: [.init(key: "psntools", suffix: "")]),
    ]

    private func press(_ run: WebTriggerServer.Runner, _ id: String) async -> (Int, [String: Any]) {
        let reply = await run(id, "web trigger from 127.0.0.1")
        let dict = (try? JSONSerialization.jsonObject(with: reply.json) as? [String: Any]) ?? [:]
        return (reply.status, dict)
    }

    func testPressesReadSettingsEachTimeLockOpenAndTurnedOff() async throws {
        // One installed app (the manifest written as the window would).
        let support = root.appendingPathComponent("support", isDirectory: true)
        let bundle = support.appendingPathComponent("apps/DMX Tools.app", isDirectory: true)
        try FileManager.default.createDirectory(at: bundle, withIntermediateDirectories: true)
        let manifest: [String: [String: String]] = ["dmxtools": ["version": "v1.12.0", "path": bundle.path, "installedAt": "2026-01-01T00:00:00Z"]]
        try JSONSerialization.data(withJSONObject: manifest).write(to: support.appendingPathComponent("installed.json"))
        let installs = InstallManager(supportDir: support)
        let claude = root.appendingPathComponent("claude.json")
        var launched: [String] = []
        var open: Set<String> = []
        var enabled = true
        // Every field given, so the real shared install manager / claude.json are never touched.
        let deps = HeadlessTriggerHost.Deps(defaults: defaults, claudeURL: claude, installs: installs,
                                            isOpen: { open.contains($0) }, launch: { launched.append($0) }, version: "1.37.0")
        let host = HeadlessTriggerHost.make(apps: apps, deps: deps, log: { _ in })
        let run = HeadlessTriggerHost.runner(host: host, enabled: { enabled }, reload: { installs.reloadFromDisk() })

        // Nothing ticked yet → no such action; ticked in the window (saved settings) → the very next press opens it.
        var (status, reply) = await press(run, "open-dmxtools")
        XCTAssertEqual(status, 404)
        defaults.set(["dmxtools", "psntools"], forKey: AppState.webTriggerAppsKey)
        (status, reply) = await press(run, "open-dmxtools")
        XCTAssertEqual(status, 200)
        XCTAssertEqual(reply["started"] as? Bool, true)
        XCTAssertEqual(launched, ["dmxtools"])
        open = ["dmxtools"]
        (status, reply) = await press(run, "open-dmxtools")
        XCTAssertEqual(status, 200)
        XCTAssertEqual(reply["started"] as? Bool, false, "already open: nothing starts")
        XCTAssertEqual(launched, ["dmxtools"])
        (status, _) = await press(run, "open-psntools")
        XCTAssertEqual(status, 422, "ticked but not installed")

        // Show lock on: the launcher's setting and the shared claude.json lock.
        (status, reply) = await press(run, "show-lock-on")
        XCTAssertEqual(status, 200)
        XCTAssertEqual(reply["changed"] as? Bool, true)
        XCTAssertTrue(defaults.bool(forKey: AppState.showLockKey))
        XCTAssertEqual(ClaudeSettingsFile.suiteShowLock(at: claude), true)
        (status, reply) = await press(run, "status")
        XCTAssertEqual(status, 200)
        XCTAssertEqual(reply["show_lock"] as? Bool, true)
        XCTAssertEqual(reply["apps_installed"] as? Int, 1)

        // Switched off in the window → refused from the very next press, with a proper reason phrase.
        enabled = false
        let off = await run("status", "web trigger from 127.0.0.1")
        XCTAssertEqual(off.status, 503)
        XCTAssertEqual(off.line, "the web trigger is off")
        let wire = String(decoding: WebTriggerHTTP.response(off, text: true, product: "JB Theatre Tools"), as: UTF8.self)
        XCTAssertTrue(wire.hasPrefix("HTTP/1.1 503 Service Unavailable\r\n"), wire)
    }

    func testHiddenCopyTokenWord() {
        XCTAssertEqual(WebTriggerStore.hiddenWord(.none), "")
        XCTAssertEqual(WebTriggerStore.hiddenWord(.token("synthetic-trigger-word")), "synthetic-trigger-word")
        let a = WebTriggerStore.hiddenWord(.unreadable), b = WebTriggerStore.hiddenWord(.unreadable)
        XCTAssertEqual(a.count, 72)
        XCTAssertNotEqual(a, b, "an unreadable token refuses every press with an unguessable stand-in")
    }
}
