import Foundation

/// A tag as the launchers print it: "v" + the normalised version for a numeric tag ("1.2.0" and "v1.2.0" both →
/// "v1.2.0"), the tag itself otherwise (Convert's "build-20260912").
enum VersionDisplay {
    static func display(_ tag: String) -> String {
        let n = norm(tag)
        if let first = n.first, first.isNumber { return "v" + n }
        return tag.trimmingCharacters(in: .whitespaces)
    }

    /// Trimmed, without a leading "v" / "V" (the same normalisation as the version comparator).
    static func norm(_ s: String) -> String {
        var t = s.trimmingCharacters(in: .whitespaces)
        if t.hasPrefix("v") || t.hasPrefix("V") { t.removeFirst() }
        return t
    }

    static func equal(_ a: String, _ b: String) -> Bool { norm(a) == norm(b) }
}

/// The rules behind the launcher's unattended behaviour — scheduled checks, update notifications and automatic
/// updates — kept pure so they're unit-tested and identical on every launcher.
enum UpdatePolicy {
    /// The "While open, check again" choices (settings value → label), shortest first. Default: `defaultInterval`.
    static let intervals: [(raw: String, label: String)] = [
        ("1h", "Every hour"), ("4h", "Every 4 hours"), ("12h", "Every 12 hours"), ("24h", "Once a day"), ("off", "Never"),
    ]
    static let defaultInterval = "4h"

    /// The check interval for a settings value; nil = scheduled checks off. Unknown → the default.
    static func interval(_ raw: String?) -> TimeInterval? {
        switch raw ?? defaultInterval {
        case "off": return nil
        case "1h": return 3_600
        case "12h": return 12 * 3_600
        case "24h": return 24 * 3_600
        default: return 4 * 3_600
        }
    }

    /// True when a scheduled check should run now: scheduled checks are on and either nothing has been checked
    /// yet this session or the interval has passed (a clock that jumped backwards counts as due).
    static func isDue(lastCheck: Date?, now: Date, raw: String?) -> Bool {
        guard let interval = interval(raw) else { return false }
        guard let lastCheck else { return true }
        let elapsed = now.timeIntervalSince(lastCheck)
        return elapsed < 0 || elapsed >= interval
    }

    /// One app with an update on offer (not held).
    struct Pending: Equatable {
        let id: String
        let name: String
        let version: String
        var key: String { "\(id) \(VersionDisplay.norm(version))" }
    }

    /// Which pending updates are NEW (not notified before), and the notified set to store: the keys of everything
    /// currently pending — so the set never grows beyond what's on offer.
    static func notify(_ pending: [Pending], alreadyNotified: [String]) -> (toNotify: [Pending], notified: [String]) {
        let seen = Set(alreadyNotified)
        var keys: [String] = []
        for p in pending where !keys.contains(p.key) { keys.append(p.key) }
        return (pending.filter { !seen.contains($0.key) }, keys)
    }

    /// The keys to remember after a check: what's pending now, plus the earlier keys of apps whose check didn't
    /// complete this time — so one check that couldn't reach the feed doesn't make the next announce it again.
    static func remembered(_ notified: [String], alreadyNotified: [String], uncheckedIds: Set<String>) -> [String] {
        var out = notified
        for key in alreadyNotified where !out.contains(key) {
            if let id = key.split(separator: " ", maxSplits: 1).first, uncheckedIds.contains(String(id)) { out.append(key) }
        }
        return out
    }

    static func notificationTitle(_ count: Int) -> String { count == 1 ? "Update available" : "Updates available" }

    /// "DMX Tools v1.2.0, PSN Tools v0.4.1 and 2 more".
    static func notificationBody(_ items: [Pending]) -> String {
        let body = items.prefix(3).map { "\($0.name) \(VersionDisplay.display($0.version))" }.joined(separator: ", ")
        return items.count > 3 ? "\(body) and \(items.count - 3) more" : body
    }

    /// The summary after an automatic update run: "Updated DMX Tools to v1.2.0" / "Updated 3 apps".
    static func autoUpdateSummary(_ updated: [(name: String, version: String)]) -> String {
        updated.count == 1 ? "Updated \(updated[0].name) to \(VersionDisplay.display(updated[0].version))"
                           : "Updated \(updated.count) apps"
    }
}

/// When to show the launcher's own "what's new" after it has been updated.
enum LauncherWhatsNew {
    /// True on the first launch of a version newer than the last one seen. With nothing seen yet it's an update
    /// only when the launcher was already in use before (versions before 1.30 didn't record what they were) — a
    /// fresh install shows nothing, and the caller just records the current version.
    static func shouldShow(lastSeen: String?, current: String, existingInstall: Bool = false) -> Bool {
        guard let lastSeen, !lastSeen.trimmingCharacters(in: .whitespaces).isEmpty else { return existingInstall }
        return AppState.versionIsNewer(current, than: lastSeen)
    }
}

/// The pre-download disk-space check: refuse a download that can't possibly be installed rather than failing
/// half-way through an extract. An archive needs room for the download AND its extracted copy, so it's budgeted
/// at 3× its size; a single file at 2×; plus a fixed 50 MB margin.
enum DiskSpace {
    static let margin: Int64 = 50_000_000

    static func isArchive(_ assetName: String) -> Bool { assetName.lowercased().hasSuffix(".zip") }

    static func required(assetSize: Int64, assetName: String) -> Int64 {
        guard assetSize > 0 else { return margin }
        let factor: Int64 = isArchive(assetName) ? 3 : 2
        return assetSize > (Int64.max - margin) / factor ? Int64.max : assetSize * factor + margin
    }

    /// The refusal message, or nil when there's room. A negative `free` means "couldn't tell" — never block.
    static func shortfall(required: Int64, free: Int64) -> String? {
        free < 0 || free >= required
            ? nil
            : "Not enough disk space — needs about \(ByteSize.format(required)), \(ByteSize.format(free)) free."
    }
}
