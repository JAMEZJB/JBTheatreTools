import Foundation

/// "3 days ago" for a release date — identical wording on every launcher. Counts whole elapsed days (24-hour
/// periods); a date in the future (clock skew) reads as "today".
enum RelativeAge {
    static func describe(_ date: Date, now: Date) -> String {
        let seconds = now.timeIntervalSince(date)
        let days = seconds <= 0 ? 0 : Int64((seconds / 86_400).rounded(.down))
        switch days {
        case ..<1: return "today"
        case 1: return "yesterday"
        case ..<7: return "\(days) days ago"
        case ..<30: let w = days / 7; return w == 1 ? "1 week ago" : "\(w) weeks ago"
        case ..<365: let m = max(1, days / 30); return m == 1 ? "1 month ago" : "\(m) months ago"
        default: let y = days / 365; return y == 1 ? "1 year ago" : "\(y) years ago"
        }
    }

    /// Parses GitHub's `published_at` (ISO 8601, UTC); nil when absent or malformed. Shared formatters: this runs in
    /// every row's render, and building an ISO8601DateFormatter each time is the expensive part.
    static func parseISO(_ iso: String?) -> Date? {
        guard let iso = iso?.trimmingCharacters(in: .whitespaces), !iso.isEmpty else { return nil }
        if let d = isoPlain.date(from: iso) { return d }
        return isoFractional.date(from: iso)
    }
    private static let isoPlain = ISO8601DateFormatter()
    private static let isoFractional: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    /// "12 Sep 2026" in the given calendar's time zone (the details and release-notes lines).
    static func shortDate(_ date: Date, calendar: Calendar = .current) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = calendar.timeZone
        f.dateFormat = "d MMM yyyy"
        return f.string(from: date)
    }
}
