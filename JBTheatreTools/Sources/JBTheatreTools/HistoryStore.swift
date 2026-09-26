import Foundation

/// The activity history file (~/Library/Application Support/JBTheatreTools/history.json). Parsing, capping and
/// wording live in `ActivityHistory`; this is only the load / append / save, serialised by a lock and written
/// atomically so a crash mid-write can't corrupt the history. Best effort: history never gets in the way of an install.
enum HistoryStore {
    /// One serial queue owns the file: `add` never blocks the caller (it was a read + parse + atomic rewrite of up to
    /// 500 entries on the main actor for every install), and `load` waits for queued writes so it sees them.
    private static let queue = DispatchQueue(label: "theatre.history", qos: .utility)
    private static var fileURL: URL { InstallManager.shared.supportDir.appendingPathComponent("history.json") }

    static func load() -> [ActivityEvent] {
        queue.sync { ActivityHistory.parse(try? Data(contentsOf: fileURL)) }
    }

    /// Waits for queued writes (the command line calls this before it exits, or the entry would be lost).
    static func flush() { queue.sync {} }

    static func add(app: String, name: String, action: String, from: String? = nil, to: String? = nil, note: String? = nil) {
        let event = ActivityEvent(at: Date(), app: app, name: name, action: action, from: from, to: to, note: note)
        queue.async {
            let data = try? Data(contentsOf: fileURL)
            // A file that exists but isn't a JSON list (damaged): keep it aside rather than overwrite the history.
            if let data, !data.isEmpty, (try? JSONSerialization.jsonObject(with: data)) as? [Any] == nil {
                let aside = fileURL.deletingLastPathComponent().appendingPathComponent("history.json.bad")
                try? FileManager.default.removeItem(at: aside)
                try? FileManager.default.moveItem(at: fileURL, to: aside)
                AppLog.shared.log("history: history.json was damaged — kept as history.json.bad, starting a new one")
            }
            let list = ActivityHistory.append(ActivityHistory.parse(data), event)
            do { try ActivityHistory.serialize(list).write(to: fileURL, options: .atomic) }
            catch { AppLog.shared.log("history: could not record \(action) \(app): \(error.localizedDescription)") }
        }
    }
}
