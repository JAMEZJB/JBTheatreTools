import Foundation

/// The activity history file (~/Library/Application Support/JBTheatreTools/history.json). Parsing, capping and
/// wording live in `ActivityHistory`; this is only the load / append / save, serialised by a lock and written
/// atomically so a crash mid-write can't corrupt the history. Best effort: history never gets in the way of an install.
enum HistoryStore {
    private static let lock = NSLock()
    private static var fileURL: URL { InstallManager.shared.supportDir.appendingPathComponent("history.json") }

    static func load() -> [ActivityEvent] {
        lock.lock(); defer { lock.unlock() }
        return ActivityHistory.parse(try? Data(contentsOf: fileURL))
    }

    static func add(app: String, name: String, action: String, from: String? = nil, to: String? = nil, note: String? = nil) {
        lock.lock(); defer { lock.unlock() }
        let list = ActivityHistory.append(ActivityHistory.parse(try? Data(contentsOf: fileURL)),
                                          ActivityEvent(at: Date(), app: app, name: name, action: action, from: from, to: to, note: note))
        do { try ActivityHistory.serialize(list).write(to: fileURL, options: .atomic) }
        catch { AppLog.shared.log("history: could not record \(action) \(app): \(error.localizedDescription)") }
    }
}
