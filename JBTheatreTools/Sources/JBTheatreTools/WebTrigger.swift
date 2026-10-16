import Foundation
import Network
import CryptoKit

/// The suite's web trigger, as the launcher offers it: a small local HTTP endpoint that runs an action by id, so a Show
/// Dashboard (Node-RED) "Saved action" button can drive this computer's launcher. The same contract as every suite app
/// and as the Windows launcher (Core/WebTrigger.cs — keep the two identical):
///
///     POST http://<this computer>:<port>/actions/<action-id>[?reply=text]
///     Content-Type: application/json
///     {} or {"token": "<the token, if one is set>"}       (also accepted: X-Token / "Authorization: Bearer")
///
/// Reply: JSON {ok, action, name, summary, seconds, ...}; ?reply=text gives the one-line summary instead. Status: 200 done
/// · 502 it ran but something failed · 400 not a JSON request · 403 token, or a browser-shaped request · 404 no such action
/// · 409 busy (one run at a time, never queued) · 422 can't run as things are. GET /actions lists [{id, name}]; GET / or
/// /health names the app.
///
/// Deliberately narrow: OFF until switched on in Settings; 127.0.0.1 unless another address of this Mac is chosen, and
/// every address (0.0.0.0) only with a token; ids only — the request never carries a value, path or text; a request with
/// an Origin header, a non-JSON body or a Host that isn't an IP literal (or localhost) is refused; the token is compared in
/// constant time and never echoed or logged; the port is never shared (no address reuse); it listens in the launcher's
/// window process, or in the hidden `--trigger-host` copy Stagehand keeps running (TriggerHost.swift) — never in any
/// other command-line run. Whichever of the two has the port answers; the other tries again every 3 s
/// (WebTriggerOwner), so the port moves on its own when one goes away.
enum WebTriggerContract {
    static let version = "1.0.0"
    static let maxBody = 4096
    /// The launcher's web-trigger port in the suite's registry (the apps use 42130-42153).
    static let defaultPort = 42154
    static let defaultBind = "127.0.0.1"
    /// How often a taken port is tried again (the suite's Owner rule).
    static let retrySeconds: TimeInterval = 3
    private static let idChars = Set("abcdefghijklmnopqrstuvwxyz0123456789-._")

    /// [a-z0-9][a-z0-9._-]{0,63} — the only ids the trigger (and Show Dashboard's control) accept.
    static func validActionId(_ id: String) -> Bool {
        guard let first = id.first, id.count <= 64, first.isASCII, first.isLetter || first.isNumber else { return false }
        return id.allSatisfy { idChars.contains($0) }
    }

    /// A strict IP literal: dotted-quad IPv4 (no short forms, no leading zeros) or IPv6 (no zone).
    struct IPLiteral: Equatable {
        let text: String
        let isV6: Bool
        let isUnspecified: Bool
    }

    static func parseIP(_ raw: String?) -> IPLiteral? {
        let s = (raw ?? "").trimmingCharacters(in: .whitespaces)
        if s.isEmpty || s.contains("%") { return nil }
        if s.contains(":") {
            guard let v6 = IPv6Address(s) else { return nil }
            return IPLiteral(text: s, isV6: true, isUnspecified: v6.rawValue == IPv6Address.any.rawValue)
        }
        let parts = s.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return nil }
        for p in parts {
            guard (1...3).contains(p.count), p.allSatisfy({ $0.isASCII && $0.isNumber }),
                  !(p.count > 1 && p.first == "0"), let n = Int(p), n <= 255 else { return nil }
        }
        return IPLiteral(text: s, isV6: false, isUnspecified: s == "0.0.0.0")
    }

    /// "" if (bind, port) is an acceptable trigger address, else why not (plain words for the Settings panel).
    static func bindProblem(bind: String?, port: String?, tokenSet: Bool) -> String {
        guard let addr = parseIP(bind) else { return "The trigger address must be an IP address of this computer, e.g. 127.0.0.1." }
        if addr.isUnspecified && !tokenSet { return "Listening on every network (0.0.0.0) needs a token: set one first." }
        guard let n = Int((port ?? "").trimmingCharacters(in: .whitespaces)) else { return "The trigger port must be a number." }
        if n < 1024 || n > 65535 { return "The trigger port must be between 1024 and 65535." }
        return ""
    }

    /// "" or why a new token can't be used.
    static func tokenProblem(_ token: String) -> String {
        let bad = token.count < 8 || token.count > 200 || token.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) }
        return bad ? "A token is 8 to 200 characters on one line." : ""
    }

    /// The Host header must name an IP literal or localhost — a rebound DNS name never reaches the actions.
    static func hostOK(_ raw: String?) -> Bool {
        let h = (raw ?? "").trimmingCharacters(in: .whitespaces)
        if h.isEmpty { return true }   // HTTP/1.0 clients may omit it
        var name: String
        if h.hasPrefix("[") {
            guard let end = h.firstIndex(of: "]") else { return false }
            name = String(h[h.index(after: h.startIndex)..<end])
        } else if h.filter({ $0 == ":" }).count == 1, let colon = h.firstIndex(of: ":") {
            name = String(h[..<colon])
        } else {
            name = h
        }
        return name.caseInsensitiveCompare("localhost") == .orderedSame || parseIP(name) != nil
    }

    /// True when `given` is the token — in constant time (both are hashed first, so even the length isn't revealed).
    static func tokenMatches(_ given: String, _ want: String) -> Bool {
        let a = Array(SHA256.hash(data: Data(given.utf8))), b = Array(SHA256.hash(data: Data(want.utf8)))
        var diff: UInt8 = 0
        for i in 0..<a.count { diff |= a[i] ^ b[i] }
        return diff == 0
    }

    /// The contract's reply. `summary` is the one line ?reply=text returns — short and plain.
    static func reply(ok: Bool, action: String, name: String, summary: String, seconds: Double,
                      _ details: [String: Any] = [:]) -> [String: Any] {
        var out: [String: Any] = ["ok": ok, "action": action, "name": name, "summary": summary,
                                  "seconds": (seconds * 10).rounded() / 10]
        for (k, v) in details { out[k] = v }
        return out
    }

    /// Settings, while another program (or a launcher that is still closing) has the port.
    static func waitingText(port: Int) -> String {
        "Waiting for port \(port): another program, or a copy of JB Theatre Tools that is still closing, is using it. "
            + "Trying again every \(Int(retrySeconds)) seconds."
    }

    static func busy() -> (Int, [String: Any]) {
        (409, ["ok": false, "error": "Another action is already running.", "summary": "busy: another action is running"])
    }

    static func notFound(_ actionId: String) -> (Int, [String: Any]) {
        (404, ["ok": false, "action": actionId, "error": "No saved action called '\(actionId)'.", "summary": "no action '\(actionId)'"])
    }
}

/// One answer, encoded (Sendable, so it can come back from the main actor to the listener's queue).
struct WebTriggerReply: Sendable {
    let status: Int
    let json: Data
    /// What ?reply=text returns: the summary, or the error when there is no summary.
    let line: String

    init(_ status: Int, _ dict: [String: Any]) {
        self.status = status
        var d = dict
        if d["seconds"] == nil { d["seconds"] = 0.0 }
        json = (try? JSONSerialization.data(withJSONObject: d, options: [.withoutEscapingSlashes])) ?? Data("{}".utf8)
        let summary = d["summary"] as? String ?? ""
        line = summary.isEmpty ? (d["error"] as? String ?? "") : summary
    }
}

struct WebTriggerAction: Sendable, Equatable {
    let id: String
    let name: String
}

/// A non-blocking run lock: `tryEnter()` is false while another run holds it (answer 409).
final class OneAtATime: @unchecked Sendable {
    private let lock = NSLock()
    private var held = false
    var busy: Bool { lock.lock(); defer { lock.unlock() }; return held }
    func tryEnter() -> Bool {
        lock.lock(); defer { lock.unlock() }
        if held { return false }
        held = true
        return true
    }
    func exit() { lock.lock(); held = false; lock.unlock() }
}

/// The HTTP side of the contract, kept free of sockets so it is unit-tested directly.
enum WebTriggerHTTP {
    static let maxHeaderBytes = 8192

    struct Request {
        let method: String
        let target: String
        /// Lower-cased names; the first of a repeated header counts.
        let headers: [String: String]
        let body: Data?
        /// Content-Length (0 when absent, -1 when unreadable).
        let length: Int
    }

    enum Parsed {
        case needMore
        case bad
        case request(Request)
    }

    static func parse(_ buffer: Data) -> Parsed {
        let buf = Data(buffer)   // indices from 0
        guard let end = buf.range(of: Data("\r\n\r\n".utf8)) else { return buf.count >= maxHeaderBytes ? .bad : .needMore }
        if end.lowerBound > maxHeaderBytes { return .bad }
        let head = String(decoding: buf[0..<end.lowerBound], as: UTF8.self)
        let lines = head.components(separatedBy: "\r\n")
        let first = lines[0].split(separator: " ", omittingEmptySubsequences: true).map(String.init)
        guard first.count == 3, first[2].hasPrefix("HTTP/1.") else { return .bad }
        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":"), colon != line.startIndex else { return .bad }
            let name = line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()
            if headers[name] == nil { headers[name] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces) }
        }
        var length = 0
        if let cl = headers["content-length"] {
            length = (!cl.isEmpty && cl.allSatisfy({ $0.isASCII && $0.isNumber })) ? (Int(cl) ?? -1) : -1
        }
        var body: Data?
        if length >= 0 && length <= WebTriggerContract.maxBody {
            let start = end.upperBound
            if buf.count - start < length { return .needMore }
            body = Data(buf[start..<(start + length)])
        }
        return .request(Request(method: first[0], target: first[1], headers: headers, body: body, length: length))
    }

    static func splitTarget(_ target: String) -> (path: String, query: String) {
        var t = target
        if !t.hasPrefix("/"), let url = URL(string: t), url.scheme != nil {
            t = url.path.isEmpty ? "/" : url.path
            if let q = url.query { t += "?" + q }
        }
        guard let q = t.firstIndex(of: "?") else { return (t, "") }
        return (String(t[..<q]), String(t[t.index(after: q)...]))
    }

    static func queryValue(_ query: String, _ key: String) -> String {
        for pair in query.split(separator: "&") {
            guard let eq = pair.firstIndex(of: "="), eq != pair.startIndex else { continue }
            func dec(_ s: Substring) -> String {
                let plus = s.replacingOccurrences(of: "+", with: " ")
                return plus.removingPercentEncoding ?? plus
            }
            if dec(pair[..<eq]) == key { return dec(pair[pair.index(after: eq)...]) }
        }
        return ""
    }

    private static func printable(_ s: String) -> String {
        String(s.prefix(64).map { (c: Character) -> Character in
            if let v = c.asciiValue, v < 32 || v == 127 { return "?" }
            return c
        })
    }

    private static let notFoundRoute: [String: Any] = ["ok": false, "error": "Not found. POST /actions/<action-id> runs a saved action."]

    /// Answers one request. `run` does the action (on the main actor), `listing` gives GET /actions.
    static func respond(_ req: Request, ip: String, product: String, expected token: String,
                        run: (String, String) async -> WebTriggerReply,
                        listing: () async -> [WebTriggerAction],
                        log: (String) -> Void) async -> (reply: WebTriggerReply, text: Bool) {
        let (path, query) = splitTarget(req.target)
        let text = queryValue(query, "reply") == "text"
        if let origin = req.headers["origin"], !origin.isEmpty {
            return (WebTriggerReply(403, ["ok": false, "error": "Requests from a web page are refused."]), text)
        }
        if !WebTriggerContract.hostOK(req.headers["host"]) {
            return (WebTriggerReply(403, ["ok": false, "error": "Use this computer's IP address (or localhost) in the URL."]), text)
        }
        if req.method == "GET" {
            if path == "/" || path == "/health" {
                return (WebTriggerReply(200, ["ok": true, "summary": "\(product) web trigger"]), text)
            }
            if path == "/actions" {
                let items = await listing()
                return (WebTriggerReply(200, [
                    "ok": true, "actions": items.map { ["id": $0.id, "name": $0.name] },
                    "summary": items.isEmpty ? "no saved actions" : items.map(\.id).joined(separator: ", "),
                ]), text)
            }
            return (WebTriggerReply(404, notFoundRoute), text)
        }
        guard req.method == "POST" else { return (WebTriggerReply(501, ["ok": false, "error": "Unsupported method."]), text) }
        let parts = path.split(separator: "/").map(String.init)
        guard parts.count == 2, parts[0] == "actions" else { return (WebTriggerReply(404, notFoundRoute), text) }
        let ctype = (req.headers["content-type"] ?? "").split(separator: ";").first.map {
            $0.trimmingCharacters(in: .whitespaces).lowercased() } ?? ""
        guard ctype == "application/json" else {
            return (WebTriggerReply(400, ["ok": false, "error": "Send a JSON body (e.g. {} or {\"token\": \"…\"})."]), text)
        }
        guard req.length >= 0, req.length <= WebTriggerContract.maxBody, let bodyData = req.body else {
            return (WebTriggerReply(400, ["ok": false, "error": "The request body is too large."]), text)
        }
        let body: Any
        if bodyData.isEmpty {
            body = [String: Any]()
        } else if let parsed = try? JSONSerialization.jsonObject(with: bodyData, options: [.fragmentsAllowed]) {
            body = parsed
        } else {
            return (WebTriggerReply(400, ["ok": false, "error": "The body isn't valid JSON."]), text)
        }
        var given = ((body as? [String: Any])?["token"] as? String) ?? ""
        if given.isEmpty { given = req.headers["x-token"] ?? "" }
        let auth = req.headers["authorization"] ?? ""
        if given.isEmpty, auth.lowercased().hasPrefix("bearer ") { given = String(auth.dropFirst(7)) }
        if !token.isEmpty && !WebTriggerContract.tokenMatches(given, token) {
            log("web trigger: refused /actions/\(printable(parts[1])) from \(ip) (wrong or no token)")
            return (WebTriggerReply(403, ["ok": false, "error": "Wrong or missing token."]), text)
        }
        let actionId = parts[1]
        guard WebTriggerContract.validActionId(actionId) else {
            let (s, r) = WebTriggerContract.notFound(String(actionId.prefix(64)))
            return (WebTriggerReply(s, r), text)
        }
        return (await run(actionId, "web trigger from \(ip)"), text)
    }

    static func reasonPhrase(_ status: Int) -> String {
        switch status {
        case 200: return "OK"
        case 400: return "Bad Request"
        case 403: return "Forbidden"
        case 404: return "Not Found"
        case 409: return "Conflict"
        case 422: return "Unprocessable Entity"
        case 501: return "Not Implemented"
        case 502: return "Bad Gateway"
        case 503: return "Service Unavailable"
        default: return "Status"
        }
    }

    static func response(_ reply: WebTriggerReply, text: Bool, product: String) -> Data {
        var body = text ? Data((reply.line + "\n").utf8) : reply.json
        if !text { body.append(contentsOf: Array("\n".utf8)) }
        let server = product.filter { $0.isASCII && ($0.isLetter || $0.isNumber) } + "-trigger"
        let head = "HTTP/1.1 \(reply.status) \(reasonPhrase(reply.status))\r\n"
            + "Server: \(server)\r\n"
            + "Content-Type: \(text ? "text/plain; charset=utf-8" : "application/json; charset=utf-8")\r\n"
            + "Content-Length: \(body.count)\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n\r\n"
        return Data(head.utf8) + body
    }
}

/// Owns the listening socket (Network.framework). `run(actionId, source)` does the work on the main actor, as if the
/// person had pressed the button; `listing()` gives GET /actions; the token is set by the app ("" = none); `log` gets one
/// plain line per start / stop / refusal (never the token).
final class WebTriggerServer: @unchecked Sendable {
    typealias Runner = @Sendable (_ actionId: String, _ source: String) async -> WebTriggerReply
    typealias Lister = @Sendable () async -> [WebTriggerAction]

    let product: String
    private let run: Runner
    private let listing: Lister
    private let log: @Sendable (String) -> Void
    private let queue = DispatchQueue(label: "com.jamesbreedon.jbtheatretools.web-trigger")
    private let lock = NSLock()
    private var listener: NWListener?
    private var tokenValue = ""
    private var connections = 0
    private var _address = ""
    private var _port = 0
    private var _error = ""
    private var _portTaken = false

    init(product: String = "JB Theatre Tools", run: @escaping Runner, listing: @escaping Lister,
         log: @escaping @Sendable (String) -> Void) {
        self.product = product
        self.run = run
        self.listing = listing
        self.log = log
    }

    var running: Bool { locked { listener != nil } }
    var address: String { locked { _address } }
    var port: Int { locked { _port } }
    var error: String {
        get { locked { _error } }
        set { locked { _error = newValue } }
    }
    /// The last start failed because another program (or a launcher that is still closing) has the port.
    var portTaken: Bool { locked { _portTaken } }

    func setToken(_ token: String) { locked { tokenValue = token } }

    private func locked<T>(_ body: () -> T) -> T {
        lock.lock(); defer { lock.unlock() }
        return body()
    }

    /// (Re)starts on bind:port. Returns "" or a plain reason (e.g. the port is taken). Port 0 = the system picks one
    /// (tests); `port` is then the real one.
    func start(bind: String, port: Int) async -> String {
        stop()
        guard let ip = WebTriggerContract.parseIP(bind), (0...65535).contains(port) else {
            return fail("The web trigger couldn't listen on \(bind):\(port) (not an IP address).", taken: false)
        }
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = false   // a taken port is an error, never shared
        let nwPort: NWEndpoint.Port = port == 0 ? .any : (NWEndpoint.Port(rawValue: UInt16(port)) ?? .any)
        params.requiredLocalEndpoint = NWEndpoint.hostPort(host: NWEndpoint.Host(ip.text), port: nwPort)
        let newListener: NWListener
        do { newListener = try NWListener(using: params) } catch {
            return fail("The web trigger couldn't listen on \(bind):\(port) (\(error.localizedDescription)).", taken: false)
        }
        let once = Once()
        return await withCheckedContinuation { (cont: CheckedContinuation<String, Never>) in
            newListener.stateUpdateHandler = { [weak self] state in
                guard let self else { return }
                switch state {
                case .ready:
                    guard once.claim() else { return }
                    let real = Int(newListener.port?.rawValue ?? UInt16(port))
                    self.locked {
                        self.listener = newListener
                        self._port = real
                        self._address = "\(bind):\(real)"
                        self._error = ""
                        self._portTaken = false
                    }
                    self.log("web trigger listening on \(bind):\(real)")
                    cont.resume(returning: "")
                case .failed(let err), .waiting(let err):
                    newListener.cancel()
                    if once.claim() {
                        cont.resume(returning: self.fail(
                            "The web trigger couldn't listen on \(bind):\(port) (\(WebTriggerServer.reason(err, port))).",
                            taken: WebTriggerServer.inUse(err)))
                    } else {
                        self.lost(newListener, err)
                    }
                default:
                    break
                }
            }
            newListener.newConnectionHandler = { [weak self] conn in self?.accept(conn) }
            newListener.start(queue: queue)
        }
    }

    private func fail(_ message: String, taken: Bool) -> String {
        locked { _error = message; _portTaken = taken }
        return message
    }

    private static func inUse(_ err: NWError) -> Bool {
        if case .posix(let code) = err { return code == .EADDRINUSE || code == .EACCES }
        return false
    }

    private static func reason(_ err: NWError, _ port: Int) -> String {
        if case .posix(let code) = err {
            if code == .EADDRINUSE || code == .EACCES { return "port \(port) is already in use by another program" }
            if code == .EADDRNOTAVAIL { return "that address isn't one of this computer's" }
        }
        return err.localizedDescription
    }

    /// A listener that stopped by itself after it was running (e.g. the address went away).
    private func lost(_ dead: NWListener, _ err: NWError) {
        let was: String? = locked {
            guard listener === dead else { return nil }
            listener = nil
            _error = "The web trigger stopped listening (\(err.localizedDescription))."
            let a = _address
            _address = ""
            return a
        }
        if let was { log("web trigger stopped (\(was)): \(err.localizedDescription)") }
    }

    /// Stops listening. Never waits for a request in progress (it finishes on its own), so stopping from the main actor
    /// can't deadlock against an action that is waiting for it.
    func stop() {
        let (old, addr): (NWListener?, String) = locked {
            let l = listener, a = _address
            listener = nil
            _address = ""
            return (l, a)
        }
        guard let old else { return }
        old.stateUpdateHandler = nil
        old.newConnectionHandler = nil
        old.cancel()
        log("web trigger stopped (\(addr))")
    }

    private func accept(_ conn: NWConnection) {
        let ok: Bool = locked {
            guard connections < 16 else { return false }
            connections += 1
            return true
        }
        guard ok else { conn.cancel(); return }   // too many at once: drop, never queue
        WebTriggerConnection(conn: conn, server: self).begin()
    }

    fileprivate func connectionDone() { locked { connections = max(0, connections - 1) } }

    fileprivate var callbackQueue: DispatchQueue { queue }

    fileprivate func answer(_ req: WebTriggerHTTP.Request, ip: String) async -> Data {
        let expected = locked { tokenValue }
        let (reply, text) = await WebTriggerHTTP.respond(req, ip: ip, product: product, expected: expected,
                                                         run: { id, source in await self.run(id, source) },
                                                         listing: { await self.listing() },
                                                         log: log)
        return WebTriggerHTTP.response(reply, text: text, product: product)
    }
}

/// Keeps owning the trigger's port (the suite's "Owner" rule, the same as Core/WebTrigger.cs on Windows): it listens on
/// bind:port, and when the port is TAKEN — a copy of the launcher that is still closing after an update, or another
/// program — it tries again every `retry` seconds instead of giving up, until the port is free (then it listens) or
/// `stop()` (the trigger switched off, or new settings). Any other problem (an address that isn't this computer's) is
/// reported once and not retried. Every start and stop of the listener runs one after another, so a retry in flight and
/// a new setting can never both bind. `onChange` is called on each change of state (Settings shows "waiting").
@MainActor
final class WebTriggerOwner {
    enum State: Equatable { case idle, owning, waiting, failed }

    let server: WebTriggerServer
    let retry: TimeInterval
    var onChange: (() -> Void)?
    private let log: (String) -> Void
    private(set) var state: State = .idle
    private var target = ""
    private var generation = 0
    private var tail: Task<Void, Never>?
    private var loop: Task<Void, Never>?

    init(server: WebTriggerServer, retry: TimeInterval = 3, log: @escaping (String) -> Void = { _ in }) {
        self.server = server
        self.retry = retry
        self.log = log
    }

    var waiting: Bool { state == .waiting }

    /// Runs `body` after every earlier start/stop has finished.
    private func serial<T>(_ body: @escaping @MainActor () async -> T) async -> T {
        let previous = tail
        let work = Task { @MainActor () -> T in
            await previous?.value
            return await body()
        }
        tail = Task { _ = await work.value }
        return await work.value
    }

    private func set(_ new: State) {
        guard new != state else { return }
        state = new
        onChange?()
    }

    /// Listens on bind:port. "" = listening now, or waiting for a taken port (`state` says which); else why not.
    @discardableResult
    func own(bind: String, port: Int) async -> String {
        let wanted = "\(bind):\(port)"
        if wanted == target && ((state == .owning && server.running) || state == .waiting) { return "" }
        generation += 1
        let gen = generation
        loop?.cancel()
        loop = nil
        target = wanted
        return await serial { [self] in
            guard gen == generation else { return "" }
            let err = await server.start(bind: bind, port: port)
            guard gen == generation else {   // switched off or changed while it started: let go again
                if err.isEmpty { server.stop() }
                return ""
            }
            if err.isEmpty { set(.owning); return "" }
            guard server.portTaken else { set(.failed); return err }
            log("web trigger: port \(port) is taken — trying again every \(Int(retry)) seconds")
            set(.waiting)
            startRetrying(bind: bind, port: port, gen: gen)
            return ""
        }
    }

    private func startRetrying(bind: String, port: Int, gen: Int) {
        let nanos = UInt64(max(retry, 0.05) * 1_000_000_000)
        loop = Task { @MainActor [weak self] in
            while true {
                try? await Task.sleep(nanoseconds: nanos)
                guard let owner = self, !Task.isCancelled, gen == owner.generation else { return }
                let done: Bool = await owner.serial {
                    guard gen == owner.generation else { return true }
                    let err = await owner.server.start(bind: bind, port: port)
                    guard gen == owner.generation else {
                        if err.isEmpty { owner.server.stop() }
                        return true
                    }
                    if err.isEmpty {
                        owner.log("web trigger: port \(port) is free again — listening")
                        owner.set(.owning)
                        return true
                    }
                    if !owner.server.portTaken { owner.set(.failed); return true }
                    return false
                }
                if done { return }
            }
        }
    }

    /// Stops listening and stops trying (the trigger switched off, or the app is quitting). The port closes at once.
    func stop() {
        generation += 1
        loop?.cancel()
        loop = nil
        target = ""
        server.stop()
        set(.idle)
    }
}

/// Claimed once, from any thread.
private final class Once: @unchecked Sendable {
    private let lock = NSLock()
    private var done = false
    func claim() -> Bool {
        lock.lock(); defer { lock.unlock() }
        if done { return false }
        done = true
        return true
    }
}

/// One request per connection: read the head (and a small body), answer, close. Everything here runs on the server's
/// queue except the action itself.
private final class WebTriggerConnection: @unchecked Sendable {
    private let conn: NWConnection
    private let server: WebTriggerServer
    private var buffer = Data()
    private var finished = false

    init(conn: NWConnection, server: WebTriggerServer) {
        self.conn = conn
        self.server = server
    }

    func begin() {
        conn.start(queue: server.callbackQueue)
        server.callbackQueue.asyncAfter(deadline: .now() + 10) { [self] in self.finish() }   // a slow or silent client
        receive()
    }

    private func receive() {
        conn.receive(minimumIncompleteLength: 1, maximumLength: 8192) { [self] data, _, isComplete, error in
            if self.finished { return }
            if let data { self.buffer.append(data) }
            switch WebTriggerHTTP.parse(self.buffer) {
            case .needMore:
                if error != nil || isComplete { self.finish() } else { self.receive() }
            case .bad:
                self.send(WebTriggerHTTP.response(WebTriggerReply(400, ["ok": false, "error": "Bad request."]),
                                                  text: false, product: self.server.product))
            case .request(let req):
                let ip = self.remoteIP()
                let server = self.server
                Task { [self] in
                    let data = await server.answer(req, ip: ip)
                    server.callbackQueue.async { [self] in self.send(data) }
                }
            }
        }
    }

    private func remoteIP() -> String {
        if case .hostPort(let host, _) = conn.endpoint {
            switch host {
            case .ipv4(let a): return "\(a)"
            case .ipv6(let a): return "\(a)"
            case .name(let n, _): return n
            @unknown default: return "?"
            }
        }
        return "?"
    }

    private func send(_ data: Data) {
        if finished { return }
        conn.send(content: data, completion: .contentProcessed { [self] _ in self.finish() })
    }

    private func finish() {
        if finished { return }
        finished = true
        conn.cancel()
        server.connectionDone()
    }
}
