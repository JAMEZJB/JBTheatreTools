import Foundation

/// The status half of the find bar: which rows to show.
enum StatusFilter: String, CaseIterable, Identifiable {
    case all, installed, updates, notInstalled
    var id: String { rawValue }
    var label: String {
        switch self {
        case .all: return "All"
        case .installed: return "Installed"
        case .updates: return "Updates"
        case .notInstalled: return "Not installed"
        }
    }
}

/// Find & filter for the app list — the same rules on every launcher. The query is split on whitespace and
/// EVERY word must appear (case-insensitively) somewhere in the app's name, blurb, category or id, so
/// "dmx tools" and "tools dmx" both find DMX Tools.
enum AppFilter {
    static func tokens(_ query: String?) -> [String] {
        (query ?? "").split(whereSeparator: { $0.isWhitespace }).map { $0.lowercased() }
    }

    static func matchesQuery(_ query: String?, _ fields: String?...) -> Bool {
        let tokens = tokens(query)
        if tokens.isEmpty { return true }
        let hay = fields.compactMap { $0 }.filter { !$0.isEmpty }.map { $0.lowercased() }
        return tokens.allSatisfy { t in hay.contains { $0.contains(t) } }
    }

    /// - Parameters:
    ///   - installed: the row's selected edition is installed.
    ///   - updateAvailable: an update is on offer and the app isn't held.
    ///   - installable: not installed and a build exists for this Mac.
    static func matchesStatus(_ filter: StatusFilter, installed: Bool, updateAvailable: Bool, installable: Bool) -> Bool {
        switch filter {
        case .all: return true
        case .installed: return installed
        case .updates: return updateAvailable
        case .notInstalled: return !installed && installable
        }
    }

    /// True when the list is narrowed — reordering is disabled then (a drag over a subset would scramble the
    /// hidden rows' order) and collapsed sections are shown open so matches can't hide.
    static func isActive(_ query: String?, _ filter: StatusFilter) -> Bool { !tokens(query).isEmpty || filter != .all }
}
