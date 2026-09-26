import Foundation

/// The "Copy Diagnostics" report: everything useful for a support question, nothing secret — the same layout as
/// the Windows and Android launchers. Credentials are never passed in, and every log line is run through `redact`
/// as a second line of defence.
enum Diagnostics {
    static let logLines = 40

    struct AppLine: Equatable {
        let name: String
        let installed: String?
        let latest: String?
        let status: String
        let held: Bool
        /// Runs translated (Intel through Rosetta / x64 emulated on ARM). Defaulted so existing callers stay valid.
        var translated: Bool = false
    }

    struct Info {
        let launcherVersion: String
        let os: String
        let arch: String
        let authMode: String
        let relayHost: String?
        let devChannel: Bool
        let showLock: Bool
        let installLocation: String
        let apps: [AppLine]
        let logTail: [String]
        let now: Date
    }

    // swiftlint:disable force_try — constant patterns, exercised by the unit tests.
    private static let tokens = try! NSRegularExpression(pattern: #"gh[pousr]_[A-Za-z0-9]{8,}|github_pat_[A-Za-z0-9_]{8,}"#)
    private static let schemeValues = try! NSRegularExpression(
        pattern: #"\b(Bearer|Basic|token)\s+[A-Za-z0-9+/=._\-]{16,}"#, options: [.caseInsensitive])
    // swiftlint:enable force_try

    static func redact(_ line: String) -> String {
        let a = tokens.stringByReplacingMatches(in: line, range: NSRange(line.startIndex..., in: line), withTemplate: "[redacted]")
        return schemeValues.stringByReplacingMatches(in: a, range: NSRange(a.startIndex..., in: a), withTemplate: "$1 [redacted]")
    }

    static func build(_ i: Info) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "yyyy-MM-dd HH:mm:ss 'UTC'"
        var s = "JB Theatre Tools diagnostics — \(f.string(from: i.now))\n"
        s += "Launcher: v\(VersionDisplay.norm(i.launcherVersion))\n"
        s += "System: \(i.os) (\(i.arch))\n"
        s += "Downloads via: \(i.authMode)"
        if let host = i.relayHost, !host.isEmpty { s += " (\(host))" }
        s += "\n"
        s += "Install location: \(i.installLocation)\n"
        s += "Show lock: \(i.showLock ? "on" : "off") · Development builds: \(i.devChannel ? "on" : "off")\n"
        s += "\nApps (\(i.apps.count)):\n"
        for a in i.apps {
            s += "  \(a.name) — installed \(a.installed ?? "—"), latest \(a.latest ?? "—"), \(a.status)"
            if a.held { s += ", held" }
            if a.translated { s += ", runs translated" }
            s += "\n"
        }
        let tail = Array(i.logTail.suffix(logLines))
        s += "\nRecent log (\(tail.count) lines):\n"
        for l in tail { s += "  \(redact(l))\n" }
        return s
    }
}
