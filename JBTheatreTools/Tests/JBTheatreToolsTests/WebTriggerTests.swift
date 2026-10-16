import XCTest
@testable import JBTheatreTools

/// The launcher's web trigger — the SAME cases as the Windows Core.Tests (WebTriggerTests.cs): the contract's pure parts,
/// the actions against a stand-in launcher, the HTTP answers, and the real listener on 127.0.0.1 (port 0). Never the real
/// settings, apps or Keychain.
final class WebTriggerTests: XCTestCase {
    private let word = "synthetic-trigger-word"

    /// A stand-in launcher (the closures LauncherActionHost wants).
    private final class Fake {
        var lock = false
        var downloads = 2
        var ticked: Set<String> = ["dmxtools", "nditools", "psntools"]
        var installed: [String: String] = ["dmxtools": "v1.12.0", "nditools": "v0.4.0", "nditools@full": "v0.4.0"]
        var busy: Set<String> = []
        var open: Set<String> = []
        var launched: [String] = []
        var lines: [String] = []
        var launchError: String?
        var lastCheck: Date? = Date().addingTimeInterval(-14 * 60)

        struct Failure: LocalizedError { let errorDescription: String? }

        var host: LauncherActionHost {
            LauncherActionHost(
                showLock: { self.lock },
                turnShowLockOn: { source in
                    self.lock = true
                    self.lines.append("show lock on (\(source))")
                    let n = self.downloads
                    self.downloads = 0
                    return n
                },
                apps: {
                    [
                        LauncherActions.App(id: "dmxtools", name: "DMX Tools", slots: [.init(key: "dmxtools", suffix: "")]),
                        LauncherActions.App(id: "nditools", name: "NDI Tools",
                                            slots: [.init(key: "nditools@full", suffix: " (Full)"), .init(key: "nditools", suffix: "")]),
                        LauncherActions.App(id: "psntools", name: "PSN Tools", slots: [.init(key: "psntools", suffix: "")]),
                        LauncherActions.App(id: "powercalc", name: "PowerCalc", slots: [.init(key: "powercalc", suffix: "")]),
                    ]
                },
                tickedApps: { self.ticked },
                installedVersion: { self.installed[$0] },
                slotBusy: { self.busy.contains($0) },
                isOpen: { self.open.contains($0) },
                launch: { key in
                    if let e = self.launchError { throw Failure(errorDescription: e) }
                    self.launched.append(key)
                },
                facts: { LauncherActions.Facts(showLock: self.lock, installed: 11, updates: 2, lastCheck: self.lastCheck, version: "1.36.0") },
                log: { self.lines.append($0) })
        }
    }

    private func run(_ fake: Fake, _ id: String) -> (Int, [String: Any]) {
        LauncherActions.run(fake.host, id: id, source: "web trigger from 127.0.0.1", now: Date())
    }

    // MARK: - The contract's pure parts

    func testActionIds() {
        for (id, ok) in [("status", true), ("open-dmxtools", true), ("show-lock-on", true), ("a.b_c-1", true), ("", false),
                         ("-x", false), ("Open-dmx", false), ("open dmx", false), ("open/dmx", false), ("é", false)] {
            XCTAssertEqual(WebTriggerContract.validActionId(id), ok, id)
        }
        XCTAssertTrue(WebTriggerContract.validActionId(String(repeating: "a", count: 64)))
        XCTAssertFalse(WebTriggerContract.validActionId(String(repeating: "a", count: 65)))
    }

    func testBindProblems() {
        let address = "The trigger address must be an IP address of this computer, e.g. 127.0.0.1."
        let every = "Listening on every network (0.0.0.0) needs a token: set one first."
        let cases: [(String, String, Bool, String)] = [
            ("127.0.0.1", "42154", false, ""), ("192.0.2.5", "42154", false, ""), ("::1", "42154", false, ""),
            ("0.0.0.0", "42154", true, ""), ("0.0.0.0", "42154", false, every), ("::", "42154", false, every),
            ("localhost", "42154", false, address), ("127.1", "42154", false, address), ("127.0.0.01", "42154", false, address),
            ("", "42154", false, address), ("127.0.0.1", "port", false, "The trigger port must be a number."),
            ("127.0.0.1", "80", false, "The trigger port must be between 1024 and 65535."),
            ("127.0.0.1", "65536", false, "The trigger port must be between 1024 and 65535."),
        ]
        for (bind, port, token, want) in cases {
            XCTAssertEqual(WebTriggerContract.bindProblem(bind: bind, port: port, tokenSet: token), want, "\(bind) \(port)")
        }
    }

    func testHostHeader() {
        for (host, ok) in [("", true), ("127.0.0.1:42154", true), ("127.0.0.1", true), ("localhost:42154", true), ("LOCALHOST", true),
                           ("[::1]:42154", true), ("192.0.2.20:42154", true), ("evil.example:42154", false), ("evil.example", false),
                           ("127.0.0.1.nip.example", false)] {
            XCTAssertEqual(WebTriggerContract.hostOK(host), ok, host)
        }
    }

    func testTokenRules() {
        XCTAssertEqual(WebTriggerContract.tokenProblem("eight-ch"), "")
        XCTAssertNotEqual(WebTriggerContract.tokenProblem("short"), "")
        XCTAssertNotEqual(WebTriggerContract.tokenProblem("two\nlines-here"), "")
        XCTAssertNotEqual(WebTriggerContract.tokenProblem(String(repeating: "x", count: 201)), "")
        XCTAssertTrue(WebTriggerContract.tokenMatches(word, word))
        XCTAssertFalse(WebTriggerContract.tokenMatches("", word))
        XCTAssertFalse(WebTriggerContract.tokenMatches(word + "x", word))
    }

    func testStatusLineWording() {
        let now = Date(timeIntervalSince1970: 1_792_180_800)
        XCTAssertEqual(LauncherActions.statusLine(.init(showLock: false, installed: 11, updates: 2, lastCheck: now.addingTimeInterval(-14 * 60), version: "1.36.0"), now: now),
                       "Show lock off · 11 apps installed · 2 updates waiting (last check 14 min ago) · launcher 1.36.0")
        XCTAssertEqual(LauncherActions.statusLine(.init(showLock: true, installed: 1, updates: 0, lastCheck: nil, version: "v1.36.0"), now: now),
                       "Show lock on · 1 app installed · no update check yet · launcher 1.36.0")
        XCTAssertEqual(LauncherActions.statusLine(.init(showLock: false, installed: 0, updates: 1, lastCheck: now.addingTimeInterval(-20), version: "1.36.0"), now: now),
                       "Show lock off · 0 apps installed · 1 update waiting (last check just now) · launcher 1.36.0")
        XCTAssertEqual(LauncherActions.checkAge(now.addingTimeInterval(-3 * 3600), now: now), "3 hours ago")
        XCTAssertEqual(LauncherActions.checkAge(now.addingTimeInterval(-61 * 60), now: now), "1 hour ago")
        XCTAssertEqual(LauncherActions.checkAge(now.addingTimeInterval(-2 * 86400), now: now), "2 days ago")
    }

    // MARK: - The actions

    func testListing() {
        let fake = Fake()
        XCTAssertEqual(LauncherActions.listing(fake.host).map(\.id),
                       ["status", "show-lock-on", "open-dmxtools", "open-nditools", "open-psntools"])   // powercalc isn't ticked
        fake.ticked = []
        XCTAssertEqual(LauncherActions.listing(fake.host).map(\.id), ["status", "show-lock-on"])
        XCTAssertEqual(run(fake, "open-dmxtools").0, 404)
    }

    func testStatusIsReadOnly() {
        let fake = Fake()
        let (status, reply) = run(fake, "status")
        XCTAssertEqual(status, 200)
        XCTAssertEqual(reply["summary"] as? String, "Show lock off · 11 apps installed · 2 updates waiting (last check 14 min ago) · launcher 1.36.0")
        XCTAssertEqual(reply["apps_installed"] as? Int, 11)
        XCTAssertFalse(fake.lock)
        XCTAssertTrue(fake.launched.isEmpty)
    }

    func testShowLockOnAndAlreadyOn() {
        let fake = Fake()
        XCTAssertEqual(run(fake, "show-lock-on").1["summary"] as? String, "Show lock on (2 downloads stopped)")
        XCTAssertTrue(fake.lock)
        XCTAssertTrue(fake.lines.contains("show lock on (web trigger from 127.0.0.1)"))
        XCTAssertEqual(run(fake, "show-lock-on").1["summary"] as? String, "Show lock is already on")
        XCTAssertEqual(run(fake, "show-lock-off").0, 404)   // there is no "off", on purpose
    }

    func testOpenStartsTheFullEditionFirst() {
        let fake = Fake()
        let (status, reply) = run(fake, "open-nditools")
        XCTAssertEqual(status, 200)
        XCTAssertEqual(reply["summary"] as? String, "Opened NDI Tools (Full) 0.4.0")
        XCTAssertEqual(fake.launched, ["nditools@full"])
        XCTAssertTrue(fake.lines.contains("launched nditools@full (web trigger from 127.0.0.1)"))
        XCTAssertEqual(run(fake, "open-dmxtools").1["summary"] as? String, "Opened DMX Tools 1.12.0")
    }

    func testOpenWhenAlreadyOpenStartsNothing() {
        let fake = Fake()
        fake.open = ["nditools"]   // the Light edition is open: a press never opens a second copy
        let (status, reply) = run(fake, "open-nditools")
        XCTAssertEqual(status, 200)
        XCTAssertEqual(reply["summary"] as? String, "NDI Tools is already open")
        XCTAssertTrue(fake.launched.isEmpty)
    }

    func testOpenRefusals() {
        let fake = Fake()
        XCTAssertEqual(run(fake, "open-psntools").0, 422)
        XCTAssertEqual(run(fake, "open-psntools").1["summary"] as? String, "PSN Tools isn't installed on this computer")
        XCTAssertEqual(run(fake, "open-powercalc").0, 404)
        XCTAssertEqual(run(fake, "open-nosuchapp").0, 404)
        fake.busy = ["dmxtools"]
        XCTAssertEqual(run(fake, "open-dmxtools").0, 409)
        fake.busy = []
        fake.launchError = "synthetic failure"
        let (status, reply) = run(fake, "open-dmxtools")
        XCTAssertEqual(status, 502)
        XCTAssertEqual(reply["summary"] as? String, "Couldn't open DMX Tools: synthetic failure")
        XCTAssertTrue(fake.launched.isEmpty)
    }

    // MARK: - The HTTP answers (no socket)

    private func request(_ method: String = "POST", _ path: String, body: String = "{}", headers: [String: String] = [:],
                         contentType: String = "application/json") -> WebTriggerHTTP.Request {
        var h = ["host": "127.0.0.1:42154", "content-type": contentType]
        for (k, v) in headers { h[k.lowercased()] = v }
        let data = Data(body.utf8)
        return WebTriggerHTTP.Request(method: method, target: path, headers: h, body: data.count <= WebTriggerContract.maxBody ? data : nil,
                                      length: data.count)
    }

    private func answer(_ req: WebTriggerHTTP.Request, want: String = "", fake: Fake = Fake(),
                        log: @escaping (String) -> Void = { _ in }) async -> (Int, String, Bool) {
        let (reply, text) = await WebTriggerHTTP.respond(req, ip: "127.0.0.1", product: "JB Theatre Tools", expected: want,
                                                         run: { id, source in
                                                             let (s, r) = LauncherActions.run(fake.host, id: id, source: source, now: Date())
                                                             return WebTriggerReply(s, r)
                                                         },
                                                         listing: { LauncherActions.listing(fake.host) },
                                                         log: log)
        return (reply.status, text ? reply.line : String(decoding: reply.json, as: UTF8.self), text)
    }

    func testHttpAnswers() async {
        var r = await answer(request("POST", "/actions/status?reply=text"))
        XCTAssertEqual(r.0, 200)
        XCTAssertEqual(r.1, "Show lock off · 11 apps installed · 2 updates waiting (last check 14 min ago) · launcher 1.36.0")
        r = await answer(request("GET", "/actions?reply=text"))
        XCTAssertEqual(r.1, "status, show-lock-on, open-dmxtools, open-nditools, open-psntools")
        r = await answer(request("GET", "/health?reply=text"))
        XCTAssertEqual(r.1, "JB Theatre Tools web trigger")
        do { let a = await answer(request("GET", "/nothing")); XCTAssertEqual(a.0, 404) }
        do { let a = await answer(request("DELETE", "/actions/status")); XCTAssertEqual(a.0, 501) }
        do { let a = await answer(request("POST", "/actions/status/extra")); XCTAssertEqual(a.0, 404) }
        do { let a = await answer(request("POST", "/actions/status", body: "")); XCTAssertEqual(a.0, 200) }   // an empty body reads as {}
        do { let a = await answer(request("POST", "/actions/" + String(repeating: "a", count: 70))); XCTAssertEqual(a.0, 404) }
    }

    func testBrowserShapedRequestsAreRefused() async {
        let fake = Fake()
        do { let a = await answer(request("POST", "/actions/show-lock-on", headers: ["Origin": "http://evil.example"]), fake: fake); XCTAssertEqual(a.0, 403) }
        do { let a = await answer(request("POST", "/actions/show-lock-on", headers: ["Host": "evil.example"]), fake: fake); XCTAssertEqual(a.0, 403) }
        do { let a = await answer(request("POST", "/actions/show-lock-on", body: "token=x", contentType: "application/x-www-form-urlencoded"), fake: fake); XCTAssertEqual(a.0, 400) }
        do { let a = await answer(request("POST", "/actions/show-lock-on", contentType: "text/plain"), fake: fake); XCTAssertEqual(a.0, 400) }
        do { let a = await answer(request("POST", "/actions/show-lock-on", body: "{not json"), fake: fake); XCTAssertEqual(a.0, 400) }
        do { let a = await answer(request("POST", "/actions/status", body: "{\"pad\":\"" + String(repeating: "x", count: 4096) + "\"}"), fake: fake); XCTAssertEqual(a.0, 400) }
        XCTAssertFalse(fake.lock)
    }

    func testTokenInBodyHeaderOrBearer_NeverEchoedOrLogged() async {
        var lines: [String] = []
        let log: (String) -> Void = { lines.append($0) }
        do { let a = await answer(request("POST", "/actions/status"), want: word, log: log); XCTAssertEqual(a.0, 403) }
        let wrong = await answer(request("POST", "/actions/status", body: "{\"token\":\"wrong-word-here\"}"), want: word, log: log)
        XCTAssertEqual(wrong.0, 403)
        XCTAssertFalse(wrong.1.contains("wrong-word-here"))
        let right = await answer(request("POST", "/actions/status", body: "{\"token\":\"\(word)\"}"), want: word, log: log)
        XCTAssertEqual(right.0, 200)
        XCTAssertFalse(right.1.contains(word))
        do { let a = await answer(request("POST", "/actions/status", headers: ["X-Token": word]), want: word, log: log); XCTAssertEqual(a.0, 200) }
        do { let a = await answer(request("POST", "/actions/status", headers: ["Authorization": "Bearer \(word)"]), want: word, log: log); XCTAssertEqual(a.0, 200) }
        XCTAssertTrue(lines.contains("web trigger: refused /actions/status from 127.0.0.1 (wrong or no token)"))
        XCTAssertFalse(lines.contains { $0.contains(word) || $0.contains("wrong-word-here") })
    }

    func testParse() {
        guard case .request(let r) = WebTriggerHTTP.parse(Data("POST /actions/status?reply=text HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}".utf8)) else {
            return XCTFail("not parsed")
        }
        XCTAssertEqual(r.method, "POST")
        XCTAssertEqual(r.headers["content-type"], "application/json")
        XCTAssertEqual(r.body, Data("{}".utf8))
        if case .needMore = WebTriggerHTTP.parse(Data("POST /actions/status HTTP/1.1\r\nContent-Length: 2\r\n\r\n{".utf8)) {} else { XCTFail("needs more") }
        if case .bad = WebTriggerHTTP.parse(Data("NONSENSE\r\n\r\n".utf8)) {} else { XCTFail("bad") }
    }

    // MARK: - The real listener (loopback, port 0)

    func testListenerEndToEndAndExclusivePort() async throws {
        let fake = Fake()
        let server = WebTriggerServer(run: { id, source in
            await MainActor.run {
                let (s, r) = LauncherActions.run(fake.host, id: id, source: source, now: Date())
                return WebTriggerReply(s, r)
            }
        }, listing: { await MainActor.run { LauncherActions.listing(fake.host) } }, log: { _ in })
        let err = await server.start(bind: "127.0.0.1", port: 0)
        XCTAssertEqual(err, "")
        defer { server.stop() }
        XCTAssertTrue(server.running)
        var req = URLRequest(url: URL(string: "http://127.0.0.1:\(server.port)/actions/status?reply=text")!)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = Data("{}".utf8)
        let (data, response) = try await URLSession.shared.data(for: req)
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 200)
        XCTAssertTrue(String(decoding: data, as: UTF8.self).hasPrefix("Show lock off · 11 apps installed"))

        // A second listener on the same port is refused (never shared).
        let other = WebTriggerServer(run: { _, _ in WebTriggerReply(200, [:]) }, listing: { [] }, log: { _ in })
        let second = await other.start(bind: "127.0.0.1", port: server.port)
        XCTAssertTrue(second.hasPrefix("The web trigger couldn't listen on 127.0.0.1:\(server.port) ("), second)
        XCTAssertFalse(other.running)
    }

    func testGuideNamesEveryStatusAndThePort() {
        let all = WebTriggerGuide.sections(port: 42154).map { $0.heading + "\n" + $0.body }.joined(separator: "\n")
        for code in ["200", "502", "400", "403", "404", "409", "422"] { XCTAssertTrue(all.contains(code + " —"), code) }
        XCTAssertTrue(all.contains("42154"))
        XCTAssertEqual(WebTriggerContract.defaultPort, 42154)
    }

    func testTheTokenIsASecretInTheLauncherBackup() {
        XCTAssertTrue(LauncherSettings.secretLabels.contains { $0.name == "webTriggerToken" && $0.label == "Web trigger token" })
        XCTAssertTrue(LauncherSettings.fields.contains { $0.name == "webTriggerApps" })
        XCTAssertFalse(LauncherSettings.fields.contains { $0.name.hasPrefix("webTrigger") && $0.name != "webTriggerApps" })
    }
}
