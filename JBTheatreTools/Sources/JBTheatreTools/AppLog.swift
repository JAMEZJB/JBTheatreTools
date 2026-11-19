import Foundation
import AppKit

/// The launcher's log: `~/Library/Logs/JBTheatreTools/JBTheatreTools.log` (the suite's verbose logging rule).
///
/// One line per event, the same shape in every app:
///
///     2026-11-19 14:03:22.417  DEBUG  [github] GET api.github.com/repos/…/releases → 200, 4,210 bytes, 318 ms
///
/// - INFO / WARN / ERROR are always written: start-up, what was asked and how it ended, settings changes, problems.
/// - DEBUG is written only while "Verbose logging (for troubleshooting)" is on (Settings, key `verbose`): every
///   conversation with GitHub or the download server, every verification, every install step, and each decision.
///
/// Secrets never reach the file: call sites don't pass them, and `redact` masks anything of a known secret shape (and any
/// credential the app has registered with `registerSecret`) as a second line of defence. Writes are queued off the
/// calling thread, so logging never slows the window; a full disk or an unwritable folder means no log, never a crash.
final class AppLog: @unchecked Sendable {
    static let shared = AppLog()

    enum Level: String {
        case debug = "DEBUG", info = "INFO ", warn = "WARN ", error = "ERROR"
    }

    /// The setting's key in the app's preferences.
    static let verboseKey = "verbose"
    /// One line is cut to about this many characters, with "…".
    static let maxLine = 2000
    static let normalLimit = 1_000_000, normalKeep = 2
    static let verboseLimit = 5_000_000, verboseKeep = 3

    let fileURL: URL
    private let fm = FileManager.default
    private let queue = DispatchQueue(label: "theatre.applog")
    private let defaults: UserDefaults
    private let stateLock = NSLock()
    private var verboseCache: Bool
    private var verboseCheckedAt = Date()
    private var secrets: [String] = []

    /// `directory`: where the log lives. Tests pass a temporary folder; the app uses `~/Library/Logs/JBTheatreTools`.
    /// Under XCTest (or with `JBTT_LOG_DIR` set) the shared log is redirected to a temporary folder, never the real one.
    init(directory: URL? = nil, defaults: UserDefaults = .standard) {
        let dir: URL
        if let directory {
            dir = directory
        } else if let env = ProcessInfo.processInfo.environment["JBTT_LOG_DIR"], !env.isEmpty {
            dir = URL(fileURLWithPath: env, isDirectory: true)
        } else if NSClassFromString("XCTestCase") != nil {
            dir = FileManager.default.temporaryDirectory.appendingPathComponent("jbtt-test-logs-\(ProcessInfo.processInfo.processIdentifier)", isDirectory: true)
        } else {
            dir = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("Logs/JBTheatreTools", isDirectory: true)
        }
        try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
        fileURL = dir.appendingPathComponent("JBTheatreTools.log")
        self.defaults = defaults
        verboseCache = defaults.bool(forKey: Self.verboseKey)
    }

    // MARK: - The switch

    /// Verbose logging on. Re-read from the preferences at most every two seconds, so the background copy of the launcher
    /// (a separate process) follows the switch without a restart.
    var verbose: Bool {
        stateLock.lock(); defer { stateLock.unlock() }
        if Date().timeIntervalSince(verboseCheckedAt) > 2 {
            verboseCache = defaults.bool(forKey: Self.verboseKey)
            verboseCheckedAt = Date()
        }
        return verboseCache
    }

    /// Settings: switch verbose on or off. Takes effect at once, is remembered, and is recorded in the log.
    func setVerbose(_ on: Bool) {
        stateLock.lock()
        let was = verboseCache
        verboseCache = on
        verboseCheckedAt = Date()
        stateLock.unlock()
        defaults.set(on, forKey: Self.verboseKey)
        guard was != on else { return }
        // The line itself is written at INFO so it is there whichever way the switch went.
        info("settings", "verbose logging \(was ? "on" : "off") → \(on ? "on" : "off")")
    }

    // MARK: - Writing

    func debug(_ area: String, _ message: @autoclosure () -> String) {
        guard verbose else { return }
        write(.debug, area, message())
    }
    func info(_ area: String, _ message: String) { write(.info, area, message) }
    func warn(_ area: String, _ message: String) { write(.warn, area, message) }

    /// One plain line saying what failed and why, then (the technical detail) the error's type and message below it.
    func error(_ area: String, _ message: String, _ error: Error? = nil) {
        var text = message
        if let error {
            let ns = error as NSError
            text += "\n    \(type(of: error)): \(error.localizedDescription) [\(ns.domain) \(ns.code)]"
        }
        write(.error, area, text)
    }

    /// Existing free-text lines ("install x FAILED: …", "web trigger: …"): INFO, the area taken from a short word before the
    /// first colon, else from the first word. Lines holding "FAILED" or "couldn't" are warnings.
    func log(_ message: String) {
        let area = Self.area(of: message)
        let lower = message.lowercased()
        let level: Level = lower.contains("failed") || lower.contains("couldn't") || lower.contains("can't") ? .warn : .info
        write(level, area, message)
    }

    static func area(of message: String) -> String {
        var m = Substring(message)
        if m.hasPrefix("[") , let close = m.firstIndex(of: "]") { return String(m[m.index(after: m.startIndex)..<close]) }
        if let colon = m.firstIndex(of: ":"), m.distance(from: m.startIndex, to: colon) <= 24 {
            let head = String(m[m.startIndex..<colon])
            if !head.contains("/") && !head.contains("\\") && head.split(separator: " ").count <= 2 {
                return head.lowercased().replacingOccurrences(of: " ", with: "-")
            }
        }
        m = m.drop { $0 == " " }
        let first = m.split(separator: " ").first.map(String.init) ?? "app"
        return first.lowercased().filter { $0.isLetter || $0 == "-" }.isEmpty ? "app" : first.lowercased().filter { $0.isLetter || $0 == "-" }
    }

    /// Queues the line. The text is redacted and cut here, on the caller's thread, so no secret ever sits in the queue.
    func write(_ level: Level, _ area: String, _ message: String) {
        let when = Date()
        let body = redact(message)
        queue.async { [self] in
            var out = ""
            let stamp = Self.stamp(when)
            for (i, raw) in body.split(separator: "\n", omittingEmptySubsequences: false).enumerated() {
                let line = Self.cut(String(raw))
                out += i == 0 ? "\(stamp)  \(level.rawValue)  [\(area)] \(line)\n" : "\(line)\n"
            }
            append(out)
        }
    }

    private func append(_ text: String) {
        guard let data = text.data(using: .utf8) else { return }
        rotateIfNeeded(adding: data.count)
        if fm.fileExists(atPath: fileURL.path), let handle = try? FileHandle(forWritingTo: fileURL) {
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: data)
        } else {
            try? data.write(to: fileURL)
        }
    }

    /// The log rolls at 1 MB (5 MB with verbose on) and keeps 2 older files (3 with verbose on): `.log.1` is the newest.
    private func rotateIfNeeded(adding: Int) {
        let verboseNow = verbose
        let limit = verboseNow ? Self.verboseLimit : Self.normalLimit
        let keep = verboseNow ? Self.verboseKeep : Self.normalKeep
        guard let size = (try? fm.attributesOfItem(atPath: fileURL.path)[.size] as? Int), size + adding > limit else { return }
        let base = fileURL.path
        try? fm.removeItem(atPath: "\(base).\(keep)")
        for n in stride(from: keep - 1, through: 1, by: -1) {
            if fm.fileExists(atPath: "\(base).\(n)") { try? fm.moveItem(atPath: "\(base).\(n)", toPath: "\(base).\(n + 1)") }
        }
        try? fm.moveItem(atPath: base, toPath: "\(base).1")
        var n = keep + 1   // files left over from a longer verbose run
        while fm.fileExists(atPath: "\(base).\(n)") { try? fm.removeItem(atPath: "\(base).\(n)"); n += 1 }
    }

    static func cut(_ line: String) -> String {
        line.count > maxLine ? String(line.prefix(maxLine - 1)) + "…" : line
    }

    // MARK: - Reading

    /// The last `count` lines of the log (at most 400), optionally only those holding `filter`; already masked.
    /// Waits for pending writes.
    func tail(_ count: Int, filter: String? = nil) -> [String] {
        queue.sync {}
        guard let data = try? Data(contentsOf: fileURL) else { return [] }
        var lines = String(decoding: data.suffix(400_000), as: UTF8.self).split(separator: "\n", omittingEmptySubsequences: true).map(String.init)
        if let f = filter?.trimmingCharacters(in: .whitespaces), !f.isEmpty {
            lines = lines.filter { $0.range(of: f, options: .caseInsensitive) != nil }
        }
        return lines.suffix(min(max(count, 0), 400)).map { redact($0) }
    }

    /// Settings → Open Log: shows the file in Finder (creating it if there is none yet).
    func open() {
        queue.sync {}
        if !fm.fileExists(atPath: fileURL.path) { try? Data().write(to: fileURL) }
        NSWorkspace.shared.activateFileViewerSelecting([fileURL])
    }

    /// Waits until every queued line is on disk (tests, and the end of a command-line run).
    func flush() { queue.sync {} }

    // MARK: - Start-up header

    struct HeaderInfo {
        var mode: String            // "window", "command line", "background web trigger"
        var version: String
        var settings: [(String, String)]
    }

    /// Always written, verbose or not: what is running and the settings that explain its behaviour (never a secret).
    func writeHeader(_ h: HeaderInfo) {
        let bundle = Bundle.main.bundlePath
        let built = bundle.hasSuffix(".app")
        info("start", "==== JB Theatre Tools \(h.version) (\(h.mode)) ====")
        info("start", "system: macOS \(ProcessInfo.processInfo.operatingSystemVersionString), \(Self.processor)")
        info("start", "running as: \(built ? "the built app" : "run from source (not a built app)") at \(bundle)")
        for (k, v) in h.settings { info("start", "setting \(k): \(v)") }
        info("start", "verbose logging: \(verbose ? "ON (every step is recorded)" : "off (switch it on in Settings to trace a problem)")")
    }

    static var processor: String {
        var size = 0
        sysctlbyname("hw.machine", nil, &size, nil, 0)
        var buf = [CChar](repeating: 0, count: max(size, 1))
        sysctlbyname("hw.machine", &buf, &size, nil, 0)
        var s = String(cString: buf)
        var translated: Int32 = 0
        var tsize = MemoryLayout<Int32>.size
        if sysctlbyname("sysctl.proc_translated", &translated, &tsize, nil, 0) == 0, translated == 1 { s += " (running translated through Rosetta)" }
        return s
    }

    // MARK: - Secrets

    /// Tells the log a credential the app now holds, so any exact copy of it is masked wherever it turns up (an error
    /// message, a URL, a header). Short values are ignored: they would mask ordinary words.
    func registerSecret(_ value: String?) {
        guard let value, value.count >= 6 else { return }
        stateLock.lock(); defer { stateLock.unlock() }
        if !secrets.contains(value) { secrets.append(value) }
        let b64 = Data(value.utf8).base64EncodedString()
        if !secrets.contains(b64) { secrets.append(b64) }
    }

    func redact(_ text: String) -> String {
        stateLock.lock()
        let known = secrets
        stateLock.unlock()
        var s = text
        for secret in known { s = s.replacingOccurrences(of: secret, with: "<redacted>") }
        return Self.redactShapes(s)
    }

    // swiftlint:disable force_try — constant patterns, exercised by the unit tests.
    private static let shapes: [(NSRegularExpression, String)] = [
        (try! NSRegularExpression(pattern: #"gh[pousr]_[A-Za-z0-9]{8,}|github_pat_[A-Za-z0-9_]{8,}"#), "<redacted>"),
        (try! NSRegularExpression(pattern: #"\b(Bearer|Basic|token)\s+[A-Za-z0-9+/=._\-]{8,}"#, options: [.caseInsensitive]), "$1 <redacted>"),
        (try! NSRegularExpression(pattern: #"(?i)\b(authorization)\s*[:=]\s*[^\s,;]+(\s+[^\s,;]+)?"#), "$1: <redacted>"),
        (try! NSRegularExpression(pattern: #"(?i)([?&"']?\b(?:access_?token|token|passphrase|password|passwd|pass|secret|api_?key|key)\b["']?\s*[=:]\s*)(?:"[^"]*"|'[^']*'|[^\s&,;"']+)"#), "$1<redacted>"),
        (try! NSRegularExpression(pattern: #"(?i)(--(?:token|server-pass|passphrase|password)\s+)\S+"#), "$1<redacted>"),
        (try! NSRegularExpression(pattern: #"(?i)(X-Amz-Signature|X-Amz-Credential|X-Amz-Security-Token|sig|signature)=[^&\s]+"#), "$1=<redacted>"),
    ]
    // swiftlint:enable force_try

    static func redactShapes(_ line: String) -> String {
        var s = line
        for (re, template) in shapes {
            s = re.stringByReplacingMatches(in: s, range: NSRange(s.startIndex..., in: s), withTemplate: template)
        }
        return s
    }

    // MARK: - Formatting helpers (shared by the call sites)

    /// "api.github.com/repos/o/r/releases": the host and path of a URL, with the query (which can carry a signature) dropped.
    static func hostPath(_ url: URL?) -> String {
        guard let url else { return "(no address)" }
        return (url.host ?? "?") + (url.port.map { ":\($0)" } ?? "") + url.path
    }

    static func ms(since t0: Date) -> Int { Int(Date().timeIntervalSince(t0) * 1000) }

    static func bytes(_ n: Int64) -> String {
        let f = NumberFormatter(); f.numberStyle = .decimal; f.locale = Locale(identifier: "en_GB")
        return (f.string(from: NSNumber(value: n)) ?? "\(n)") + " bytes"
    }

    private static func stamp(_ d: Date) -> String {
        var t = time_t(d.timeIntervalSince1970)
        var parts = tm()
        localtime_r(&t, &parts)
        let ms = Int((d.timeIntervalSince1970 - floor(d.timeIntervalSince1970)) * 1000)
        return String(format: "%04d-%02d-%02d %02d:%02d:%02d.%03d", Int(parts.tm_year) + 1900, Int(parts.tm_mon) + 1,
                      Int(parts.tm_mday), Int(parts.tm_hour), Int(parts.tm_min), Int(parts.tm_sec), ms)
    }
}

extension AppLog {
    /// The header for this start: the version and the settings that explain behaviour (never a credential).
    /// `mode`: "window", "command line", "background web trigger".
    func writeStartHeader(mode: String, detail: String? = nil) {
        let d = UserDefaults.standard
        let version = (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "unknown"
        var host = "(none)"
        if let base = AppState.serverOverride ?? (try? Catalog.load())?.downloadServer, let u = URL(string: base) { host = u.host ?? "?" }
        var settings: [(String, String)] = [
            ("download access", "\(AppState.authMode.rawValue) (relay host \(host))"),
            ("install location", d.bool(forKey: "theatre.installToApplications") ? "the Applications folder" : "the launcher's own folder"),
            ("update checks", d.string(forKey: AppState.autoCheckKey) ?? "default"),
            ("development builds", d.bool(forKey: AppState.devChannelKey) ? "on" : "off"),
            ("show lock", d.bool(forKey: AppState.showLockKey) ? "on" : "off"),
            ("web trigger", d.bool(forKey: AppState.webTriggerEnabledKey)
                ? "on, \(d.string(forKey: AppState.webTriggerBindKey) ?? "127.0.0.1"):\(d.object(forKey: AppState.webTriggerPortKey) != nil ? String(d.integer(forKey: AppState.webTriggerPortKey)) : "default port")"
                : "off"),
            ("data folder", InstallManager.shared.supportDir.path),
            ("log file", fileURL.path),
        ]
        if let detail { settings.insert(("started with", detail), at: 0) }
        writeHeader(HeaderInfo(mode: mode, version: version, settings: settings))
    }

    /// A command line with the value after each credential flag hidden, for the header.
    static func maskedArguments(_ args: [String]) -> String {
        var out: [String] = []
        var hide = false
        for a in args {
            if hide { out.append("<redacted>"); hide = false; continue }
            out.append(a)
            if ["--token", "--server-pass", "--passphrase", "--password"].contains(a) { hide = true }
        }
        return out.joined(separator: " ")
    }
}
