import Foundation

/// A machine's setup — which apps (and editions) are installed at which versions, which are held, and optionally
/// the list layout — saved to a file and replayed on another machine (any launcher: the ids are the catalog's).
/// Format (schema 1), identical on macOS, Windows and Android:
///
///     {"kind":"jbtheatretools-setup","schemaVersion":1,"createdAt":"…","createdBy":"…",
///      "apps":[{"id":"nditools","variant":"full","version":"v2.0.0","held":true}],
///      "layout":{"pinned":[],"hidden":[],"order":[],"categoryOrder":[],"collapsed":[]}}
///
/// "variant" is omitted for an app's default edition. Parsing is strict about shape and size (JSON `true` only
/// counts as held; a string or number never does), and tolerant of junk entries.
struct SetupProfile: Equatable {
    static let kind = "jbtheatretools-setup"
    static let schemaVersion = 1
    static let maxBytes = 1_000_000
    static let maxEntries = 500

    struct Entry: Equatable {
        let id: String
        let variant: String?
        let version: String?
        let held: Bool
    }

    struct Layout: Equatable {
        var pinned: [String]
        var hidden: [String]
        var order: [String]
        var categoryOrder: [String]
        var collapsed: [String]
    }

    struct FormatError: LocalizedError, Equatable {
        let message: String
        var errorDescription: String? { message }
    }

    var createdAt = ""
    var createdBy = ""
    var apps: [Entry] = []
    var layout: Layout? = nil

    // MARK: Parsing (Decodable, field by field, so a wrong type is "absent" rather than coerced)

    private struct AnyValue: Decodable { init(from decoder: Decoder) throws {} }   // consumes and ignores one value

    private struct RawEntry: Decodable {
        let id, variant, version: String?
        let held: Bool
        enum K: String, CodingKey { case id, variant, version, held }
        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: K.self)
            id = try? c.decodeIfPresent(String.self, forKey: .id)
            variant = try? c.decodeIfPresent(String.self, forKey: .variant)
            version = try? c.decodeIfPresent(String.self, forKey: .version)
            held = ((try? c.decodeIfPresent(Bool.self, forKey: .held)) ?? nil) == true
        }
    }

    private struct RawLayout: Decodable {
        var pinned: [String] = [], hidden: [String] = [], order: [String] = [], categoryOrder: [String] = [], collapsed: [String] = []
        enum K: String, CodingKey { case pinned, hidden, order, categoryOrder, collapsed }
        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: K.self)
            pinned = RawLayout.ids(c, .pinned)
            hidden = RawLayout.ids(c, .hidden)
            order = RawLayout.ids(c, .order)
            categoryOrder = RawLayout.ids(c, .categoryOrder)
            collapsed = RawLayout.ids(c, .collapsed)
        }
        private static func ids(_ c: KeyedDecodingContainer<K>, _ key: K) -> [String] {
            guard var arr = try? c.nestedUnkeyedContainer(forKey: key) else { return [] }
            var out: [String] = []
            while !arr.isAtEnd {
                if let s = try? arr.decode(String.self) {
                    if !s.trimmingCharacters(in: .whitespaces).isEmpty, out.count < SetupProfile.maxEntries { out.append(s) }
                } else if (try? arr.decode(AnyValue.self)) == nil { break }
            }
            return out
        }
    }

    private struct RawProfile: Decodable {
        let kind: String?
        let schemaVersion: Int?
        let createdAt, createdBy: String?
        let apps: [RawEntry]?
        let layout: RawLayout?
        enum K: String, CodingKey { case kind, schemaVersion, createdAt, createdBy, apps, layout }
        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: K.self)
            kind = try? c.decodeIfPresent(String.self, forKey: .kind)
            schemaVersion = try? c.decodeIfPresent(Int.self, forKey: .schemaVersion)
            createdAt = try? c.decodeIfPresent(String.self, forKey: .createdAt)
            createdBy = try? c.decodeIfPresent(String.self, forKey: .createdBy)
            if var arr = try? c.nestedUnkeyedContainer(forKey: .apps) {
                var list: [RawEntry] = []
                while !arr.isAtEnd {
                    if let e = try? arr.decode(RawEntry.self) { list.append(e) }
                    else if (try? arr.decode(AnyValue.self)) == nil { break }   // skip a non-object entry
                }
                apps = list
            } else {
                apps = nil
            }
            layout = try? c.decodeIfPresent(RawLayout.self, forKey: .layout)
        }
    }

    static func parse(_ data: Data) throws -> SetupProfile {
        guard data.count <= maxBytes else { throw FormatError(message: "This file is too large to be a setup file.") }
        guard let raw = try? JSONDecoder().decode(RawProfile.self, from: data), raw.kind == kind else {
            throw FormatError(message: "This file isn't a JB Theatre Tools setup file.")
        }
        guard let schema = raw.schemaVersion, schema >= 1 else {
            throw FormatError(message: "This setup file has no valid schemaVersion.")
        }
        if schema > schemaVersion {
            throw FormatError(message: "This setup file was made by a newer JB Theatre Tools — update the launcher first.")
        }
        guard let rawApps = raw.apps else { throw FormatError(message: "This setup file lists no apps.") }
        var entries: [Entry] = []
        for a in rawApps {
            if entries.count >= maxEntries { break }
            guard let id = a.id?.trimmingCharacters(in: .whitespaces), !id.isEmpty else { continue }
            let variant = a.variant?.trimmingCharacters(in: .whitespaces)
            let version = a.version?.trimmingCharacters(in: .whitespaces)
            entries.append(Entry(id: id, variant: variant?.isEmpty == false ? variant : nil,
                                 version: version?.isEmpty == false ? version : nil, held: a.held))
        }
        let layout = raw.layout.map { Layout(pinned: $0.pinned, hidden: $0.hidden, order: $0.order,
                                             categoryOrder: $0.categoryOrder, collapsed: $0.collapsed) }
        return SetupProfile(createdAt: raw.createdAt ?? "", createdBy: raw.createdBy ?? "", apps: entries, layout: layout)
    }

    // MARK: Writing

    func serialize() -> Data {
        var root: [String: Any] = [
            "kind": Self.kind, "schemaVersion": Self.schemaVersion, "createdAt": createdAt, "createdBy": createdBy,
            "apps": apps.map { e -> [String: Any] in
                var o: [String: Any] = ["id": e.id]
                if let v = e.variant { o["variant"] = v }
                if let v = e.version { o["version"] = v }
                if e.held { o["held"] = true }
                return o
            },
        ]
        if let l = layout {
            root["layout"] = ["pinned": l.pinned, "hidden": l.hidden, "order": l.order,
                              "categoryOrder": l.categoryOrder, "collapsed": l.collapsed]
        }
        return (try? JSONSerialization.data(withJSONObject: root, options: [.prettyPrinted, .sortedKeys])) ?? Data()
    }

    static func timestamp(_ now: Date) -> String { ISO8601DateFormatter().string(from: now) }

    /// A suggested file name: "JB Theatre Tools setup 2026-09-25.json" (the local date).
    static func suggestedFileName(_ now: Date, calendar: Calendar = .current) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = calendar.timeZone
        f.dateFormat = "yyyy-MM-dd"
        return "JB Theatre Tools setup \(f.string(from: now)).json"
    }
}

/// What importing a setup file would do on this machine — computed before anything installs (the preview).
enum SetupPlanner {
    /// One catalog app as the planner needs it: its editions in order (empty = no editions).
    struct CatalogEntry {
        let id: String
        let name: String
        let variants: [(id: String, label: String)]
    }

    /// One slot to install. `variantId` nil = the default edition; `tag` nil = the latest release.
    struct Install: Equatable {
        let appId: String
        let variantId: String?
        let tag: String?
        let label: String
    }

    struct Plan: Equatable {
        var toInstall: [Install]
        var alreadyInstalled: [String]
        var skipped: [String]
        var holdIds: [String]
    }

    static func build(_ profile: SetupProfile, catalog: [CatalogEntry], installedKeys: Set<String>,
                      supportsVariants: Bool = true) -> Plan {
        var byId: [String: CatalogEntry] = [:]
        for c in catalog where byId[c.id] == nil { byId[c.id] = c }
        var plan = Plan(toInstall: [], alreadyInstalled: [], skipped: [], holdIds: [])
        var seenKeys = Set<String>()
        for e in profile.apps {
            guard let app = byId[e.id] else { plan.skipped.append("\(e.id) — not in this launcher's catalog"); continue }
            var variant = e.variant
            var variantLabel: String?
            if let v = variant {
                guard let idx = app.variants.firstIndex(where: { $0.id == v }) else {
                    plan.skipped.append("\(app.name) (\(v)) — no such edition"); continue
                }
                if idx == 0 { variant = nil }
                else if !supportsVariants { plan.skipped.append("\(app.name) (\(app.variants[idx].label)) — not available here"); continue }
                else { variantLabel = app.variants[idx].label }
            }
            let key = variant.map { "\(app.id)@\($0)" } ?? app.id
            guard seenKeys.insert(key).inserted else { continue }
            let label = variantLabel.map { "\(app.name) (\($0))" } ?? app.name
            if e.held && !plan.holdIds.contains(app.id) { plan.holdIds.append(app.id) }
            if installedKeys.contains(key) { plan.alreadyInstalled.append(label); continue }
            plan.toInstall.append(Install(appId: app.id, variantId: variant, tag: e.held ? e.version : nil, label: label))
        }
        return plan
    }

    /// The preview text shown before an import runs.
    static func summary(_ plan: Plan) -> String {
        var s = ""
        if plan.toInstall.isEmpty { s += "Nothing to install — this machine already has every app in the file." }
        else {
            s += "Install \(plan.toInstall.count) app\(plan.toInstall.count == 1 ? "" : "s"):"
            for i in plan.toInstall {
                s += "\n  • " + i.label + (i.tag.map { " \(VersionDisplay.display($0)) (held)" } ?? "")
            }
        }
        if !plan.alreadyInstalled.isEmpty {
            s += "\n\nAlready installed (left as they are): " + plan.alreadyInstalled.joined(separator: ", ")
        }
        if !plan.holdIds.isEmpty {
            s += "\n\nHeld at their versions: \(plan.holdIds.count) app\(plan.holdIds.count == 1 ? "" : "s")"
        }
        if !plan.skipped.isEmpty {
            s += "\n\nSkipped:\n  • " + plan.skipped.joined(separator: "\n  • ")
        }
        return s
    }
}
