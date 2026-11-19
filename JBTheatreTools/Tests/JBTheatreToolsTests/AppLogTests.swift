import XCTest
import Network
@testable import JBTheatreTools

/// The launcher's log: the switch, the header, the line format, rotation, secrets and volume. Everything writes to a
/// temporary folder and a private preferences suite, and everything that talks to anything talks to 127.0.0.1.
final class AppLogTests: XCTestCase {
    private var dir: URL!
    private var suite: String!
    private var defaults: UserDefaults!

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory.appendingPathComponent("jbtt-log-\(UUID().uuidString)", isDirectory: true)
        suite = "jbtt-log-tests-\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suite)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
        defaults.removePersistentDomain(forName: suite)
    }

    private func makeLog() -> AppLog { AppLog(directory: dir, defaults: defaults) }

    private func text(_ log: AppLog) -> String {
        log.flush()
        return (try? String(contentsOf: log.fileURL, encoding: .utf8)) ?? ""
    }

    // MARK: - The switch

    func testTheSwitchChangesTheLevelAtOnceAndSurvivesARestart() {
        let log = makeLog()
        XCTAssertFalse(log.verbose)
        log.debug("test", "before the switch")
        log.setVerbose(true)
        log.debug("test", "after the switch")
        XCTAssertTrue(log.verbose)
        let t = text(log)
        XCTAssertFalse(t.contains("before the switch"))
        XCTAssertTrue(t.contains("after the switch"))
        XCTAssertTrue(t.contains("[settings] verbose logging off → on"))
        // A new process reads the saved setting.
        let again = makeLog()
        XCTAssertTrue(again.verbose)
        again.setVerbose(false)
        again.debug("test", "now off again")
        XCTAssertFalse(text(again).contains("now off again"))
        XCTAssertFalse(makeLog().verbose)
    }

    func testAnotherProcessChangingTheSettingIsFollowedWithinSeconds() throws {
        let log = makeLog()
        XCTAssertFalse(log.verbose)
        defaults.set(true, forKey: AppLog.verboseKey)
        // The copy of the launcher that runs in the background re-reads the setting at most every two seconds.
        let deadline = Date().addingTimeInterval(6)
        while !log.verbose && Date() < deadline { Thread.sleep(forTimeInterval: 0.25) }
        XCTAssertTrue(log.verbose)
    }

    // MARK: - Header and format

    func testTheHeaderIsAlwaysWrittenAndNamesTheRunningThing() {
        let log = makeLog()
        log.writeHeader(AppLog.HeaderInfo(mode: "window", version: "9.9.9", settings: [("download access", "server"), ("show lock", "off")]))
        let t = text(log)
        XCTAssertTrue(t.contains("==== JB Theatre Tools 9.9.9 (window) ===="))
        XCTAssertTrue(t.contains("system: macOS"))
        XCTAssertTrue(t.contains("running as:"))
        XCTAssertTrue(t.contains("setting download access: server"))
        XCTAssertTrue(t.contains("verbose logging: off"))
        log.setVerbose(true)
        log.writeHeader(AppLog.HeaderInfo(mode: "window", version: "9.9.9", settings: []))
        XCTAssertTrue(text(log).contains("verbose logging: ON"))
    }

    func testLineFormat() throws {
        let log = makeLog()
        log.info("github", "one event")
        log.warn("github", "a warning")
        log.error("github", "it failed", URLError(.timedOut))
        let lines = text(log).split(separator: "\n").map(String.init)
        let re = try NSRegularExpression(pattern: #"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  (DEBUG|INFO |WARN |ERROR)  \[[a-z-]+\] .+"#)
        for l in lines.prefix(3) where !l.hasPrefix("    ") {
            XCTAssertNotNil(re.firstMatch(in: l, range: NSRange(l.startIndex..., in: l)), l)
        }
        XCTAssertTrue(lines[0].contains("  INFO   [github] one event"))
        XCTAssertTrue(lines[1].contains("  WARN   [github] a warning"))
        XCTAssertTrue(lines[2].contains("  ERROR  [github] it failed"))
        XCTAssertTrue(lines[3].hasPrefix("    "), "the technical detail follows on its own line: \(lines)")
    }

    func testAnOldStyleLineGetsAnAreaAndALevel() {
        let log = makeLog()
        log.log("install helo FAILED: no space")
        log.log("web trigger: not listening")
        let t = text(log)
        XCTAssertTrue(t.contains("WARN   [install] install helo FAILED"))
        XCTAssertTrue(t.contains("INFO   [web-trigger] web trigger: not listening"))
    }

    func testALongLineIsCut() {
        let log = makeLog()
        log.info("test", String(repeating: "x", count: 5000))
        let line = text(log).split(separator: "\n")[0]
        XCTAssertLessThanOrEqual(line.count, AppLog.maxLine + 60)
        XCTAssertTrue(line.hasSuffix("…"))
    }

    // MARK: - Rotation and volume

    func testTheLogRollsAndKeepsTwoOlderFiles() throws {
        let log = makeLog()
        let chunk = String(repeating: "y", count: 1500)
        for _ in 0..<2600 { log.info("test", chunk) }   // about 3.9 MB written, 1 MB limit
        log.flush()
        let fm = FileManager.default
        XCTAssertTrue(fm.fileExists(atPath: log.fileURL.path + ".1"))
        XCTAssertTrue(fm.fileExists(atPath: log.fileURL.path + ".2"))
        XCTAssertFalse(fm.fileExists(atPath: log.fileURL.path + ".3"))
        let size = try XCTUnwrap(fm.attributesOfItem(atPath: log.fileURL.path)[.size] as? Int)
        XCTAssertLessThanOrEqual(size, AppLog.normalLimit)
    }

    func testBurstsOfDebugWriteNothingWhenVerboseIsOff() {
        let log = makeLog()
        for i in 0..<5000 { log.debug("test", "packet \(i)") }
        XCTAssertEqual(text(log), "")
    }

    func testTailReturnsMaskedLastLinesAndHonoursTheFilter() {
        let log = makeLog()
        for i in 0..<500 { log.info("test", "line \(i)") }
        log.info("other", "needle here")
        XCTAssertEqual(log.tail(1000).count, 400, "at most 400 lines")
        XCTAssertEqual(log.tail(5, filter: "needle").count, 1)
        XCTAssertEqual(log.tail(3).last?.contains("needle here"), true)
    }

    // MARK: - Secrets

    func testKnownSecretShapesNeverReachTheFile() {
        let log = makeLog()
        log.setVerbose(true)
        log.registerSecret("placeholder-passphrase-words")
        let token = "ghp_placeholderplaceholder1234"
        let samples = [
            "Authorization: Bearer \(token)",
            "GET https://example.invalid/x?token=synthetic-secret-value&a=1",
            "credential \(token) in an exception message",
            "--server-pass placeholder-cli-value --token synthetic-cli-token",
            "the passphrase was placeholder-passphrase-words and its base64 \(Data("placeholder-passphrase-words".utf8).base64EncodedString())",
            "https://s3.example.invalid/f?X-Amz-Signature=syntheticsig&X-Amz-Credential=syntheticcred",
        ]
        for s in samples { log.info("test", s); log.debug("test", s); log.error("test", s, NSError(domain: "t", code: 1, userInfo: [NSLocalizedDescriptionKey: s])) }
        let t = text(log)
        for secret in ["ghp_placeholder", "synthetic-secret-value", "placeholder-cli-value", "synthetic-cli-token",
                       "placeholder-passphrase-words", "syntheticsig", "syntheticcred",
                       Data("placeholder-passphrase-words".utf8).base64EncodedString()] {
            XCTAssertFalse(t.contains(secret), "\(secret) reached the log")
        }
        XCTAssertTrue(t.contains("<redacted>"))
    }

    func testTheCommandLineHeaderHidesCredentialValues() {
        XCTAssertEqual(AppLog.maskedArguments(["--list", "--token", "synthetic-x", "--server-pass", "placeholder-y", "--json"]),
                       "--list --token <redacted> --server-pass <redacted> --json")
    }

    // MARK: - Real conversations, on 127.0.0.1

    /// A one-answer HTTP stand-in on loopback (port from the system).
    private final class Stand {
        let listener: NWListener
        let queue = DispatchQueue(label: "stand")
        var port: UInt16 { listener.port?.rawValue ?? 0 }
        init(status: Int, body: String) throws {
            listener = try NWListener(using: .tcp, on: .any)
            listener.newConnectionHandler = { c in
                c.start(queue: self.queue)
                c.receive(minimumIncompleteLength: 1, maximumLength: 65536) { _, _, _, _ in
                    let head = "HTTP/1.1 \(status) X\r\nContent-Type: application/json\r\nContent-Length: \(body.utf8.count)\r\nConnection: close\r\n\r\n"
                    c.send(content: Data((head + body).utf8), completion: .contentProcessed { _ in c.cancel() })
                }
            }
            let ready = DispatchSemaphore(value: 0)
            listener.stateUpdateHandler = { if case .ready = $0 { ready.signal() } }
            listener.start(queue: queue)
            _ = ready.wait(timeout: .now() + 10)
        }
        func stop() { listener.cancel() }
    }

    func testAReleaseListIsLoggedWithTheHostStatusSizeAndTimeButNotTheCredential() async throws {
        let stand = try Stand(status: 200, body: "[]")
        defer { stand.stop() }
        let log = AppLog.shared
        let was = log.verbose
        log.setVerbose(true)
        defer { log.setVerbose(was) }
        let client = GitHubClient(serverBase: "http://127.0.0.1:\(stand.port)/ghapi", passphrase: "placeholder suite phrase")
        _ = try await client.releasesPage(owner: "example", repo: "demo", page: 1)
        let t = text(log)
        XCTAssertTrue(t.contains("releases of example/demo page 1: GET 127.0.0.1:\(stand.port)/ghapi/repos/example/demo/releases"), t)
        XCTAssertTrue(t.contains("→ 200"))
        XCTAssertTrue(t.contains(" ms"))
        XCTAssertFalse(t.contains("placeholder suite phrase"))
        XCTAssertFalse(t.contains("placeholdersuitephrase"))
    }

    func testARefusedConnectionIsExplainedInPlainWords() async throws {
        // Find a loopback port nothing listens on: open one, note it, close it.
        let probe = try Stand(status: 200, body: "[]")
        let port = probe.port
        probe.stop()
        try await Task.sleep(nanoseconds: 300_000_000)
        let log = AppLog.shared
        let client = GitHubClient(serverBase: "http://127.0.0.1:\(port)/ghapi", passphrase: "placeholder suite phrase")
        do { _ = try await client.releasesPage(owner: "example", repo: "demo", page: 1); XCTFail("should have failed") } catch {}
        let t = text(log)
        XCTAssertTrue(t.contains("refused: nothing is listening at that address and port"), t)
        XCTAssertTrue(t.contains("releases of example/demo page 1"))
        XCTAssertFalse(t.contains("placeholder suite phrase"))
    }

    func testTheWebTriggerLogsWhoAskedAndHowItEnded() async throws {
        let log = AppLog.shared
        let was = log.verbose
        log.setVerbose(true)
        defer { log.setVerbose(was) }
        let server = WebTriggerServer(run: { _, _ in WebTriggerReply(200, ["ok": true, "summary": "done"]) }, listing: { [] }, log: { _ in })
        let err = await server.start(bind: "127.0.0.1", port: 0)
        XCTAssertEqual(err, "")
        defer { server.stop() }
        var req = URLRequest(url: URL(string: "http://127.0.0.1:\(server.port)/actions/status?reply=text&token=synthetic-query-secret")!)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = Data("{}".utf8)
        _ = try await URLSession.shared.data(for: req)
        let deadline = Date().addingTimeInterval(10)
        var t = ""
        repeat { t = text(log); if t.contains("[web-trigger]") { break }; try await Task.sleep(nanoseconds: 100_000_000) } while Date() < deadline
        XCTAssertTrue(t.contains("[web-trigger] POST /actions/status from 127.0.0.1 → 200"), t)
        XCTAssertFalse(t.contains("synthetic-query-secret"))
    }
}
