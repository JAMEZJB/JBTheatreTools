import Foundation

/// One line of the install history: what happened to which app, and when.
struct ActivityEvent: Equatable {
    let at: Date
    /// Catalog id (install key for a non-default edition, e.g. "nditools@full").
    let app: String
    /// Display name at the time (with any edition suffix).
    let name: String
    /// install · update · downgrade · reinstall · uninstall · failed
    let action: String
    var from: String? = nil
    var to: String? = nil
    var note: String? = nil
}

/// The launcher's activity history (history.json in Application Support): a JSON array, oldest first, capped at
/// `cap` entries — the same format and wording as the Windows and Android launchers. Pure: parsing, appending and
/// wording; the file IO lives in `HistoryStore`. A damaged file reads as empty history rather than failing a launch.
enum ActivityHistory {
    static let cap = 300
    static let maxNoteLength = 200

    /// Classifies a successful install by the version it replaced.
    static func action(from: String?, to: String) -> String {
        guard let from, !from.isEmpty else { return "install" }
        if AppState.versionIsNewer(to, than: from) { return "update" }
        if AppState.versionIsNewer(from, than: to) { return "downgrade" }
        return "reinstall"
    }

    static func parse(_ data: Data?) -> [ActivityEvent] {
        guard let data, let root = try? JSONSerialization.jsonObject(with: data) as? [Any] else { return [] }
        return root.compactMap { item in
            guard let o = item as? [String: Any],
                  let at = RelativeAge.parseISO(o["at"] as? String),
                  let app = o["app"] as? String, !app.isEmpty,
                  let action = o["action"] as? String, !action.isEmpty else { return nil }
            return ActivityEvent(at: at, app: app, name: (o["name"] as? String) ?? app, action: action,
                                 from: o["from"] as? String, to: o["to"] as? String, note: o["note"] as? String)
        }
    }

    static func serialize(_ events: [ActivityEvent]) -> Data {
        let stamp = ISO8601DateFormatter()   // "2026-09-25T13:02:00Z" (UTC, whole seconds)
        let array: [[String: Any]] = events.map { e in
            var o: [String: Any] = ["at": stamp.string(from: e.at), "app": e.app, "name": e.name, "action": e.action]
            if let v = e.from { o["from"] = v }
            if let v = e.to { o["to"] = v }
            if let v = e.note { o["note"] = v }
            return o
        }
        return (try? JSONSerialization.data(withJSONObject: array, options: [.prettyPrinted, .sortedKeys])) ?? Data("[]".utf8)
    }

    /// Appends, trimming the note, and drops the oldest entries past the cap.
    static func append(_ existing: [ActivityEvent], _ event: ActivityEvent) -> [ActivityEvent] {
        var e = event
        if var note = e.note?.trimmingCharacters(in: .whitespacesAndNewlines) {
            if note.utf16.count > maxNoteLength {
                note = String(decoding: Array(note.utf16.prefix(maxNoteLength)), as: UTF16.self)
                    .trimmingCharacters(in: .whitespaces) + "…"
            }
            e.note = note.isEmpty ? nil : note
        }
        var list = existing + [e]
        if list.count > cap { list.removeFirst(list.count - cap) }
        return list
    }

    /// "Updated DMX Tools v1.1.0 → v1.2.0".
    static func describe(_ e: ActivityEvent) -> String {
        func v(_ t: String?) -> String { t.map { " " + VersionDisplay.display($0) } ?? "" }
        switch e.action {
        case "install": return "Installed \(e.name)\(v(e.to))"
        case "update": return "Updated \(e.name)\(v(e.from)) →\(v(e.to))"
        case "downgrade": return "Rolled back \(e.name)\(v(e.from)) →\(v(e.to))"
        case "reinstall": return "Reinstalled \(e.name)\(v(e.to))"
        case "uninstall": return "Removed \(e.name)\(v(e.from))"
        case "failed": return "Couldn't install \(e.name)\(v(e.to))" + (e.note.map { ": \($0)" } ?? "")
        default: return "\(e.action) \(e.name)\(v(e.to))"
        }
    }

    /// "Today 14:02", "Yesterday 09:10" or "12 Sep 2026 14:02", in the calendar's time zone.
    static func when(_ at: Date, now: Date, calendar: Calendar = .current) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = calendar.timeZone
        f.dateFormat = "HH:mm"
        let time = f.string(from: at)
        if calendar.isDate(at, inSameDayAs: now) { return "Today \(time)" }
        if let yesterday = calendar.date(byAdding: .day, value: -1, to: now), calendar.isDate(at, inSameDayAs: yesterday) {
            return "Yesterday \(time)"
        }
        f.dateFormat = "d MMM yyyy HH:mm"
        return f.string(from: at)
    }
}
