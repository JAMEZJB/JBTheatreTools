import Foundation

/// The suite's "New in vX.Y.Z:" lines, served by the download relay so they can be edited on the server
/// without shipping a launcher release. The bundled catalog's lines are the FALLBACK: the launcher fetches
/// this document alongside every update check (server/passphrase mode only), overlays it on the catalog,
/// and keeps the last good copy on disk so an offline start still shows the latest lines it saw.
///
/// Document (`<relay origin>/notes/whats-new.json`, same passphrase auth as the release endpoints):
///
///     { "schemaVersion": 1,
///       "apps": { "<catalog app id>": { "whatsNew": "one line", "whatsNewVersion": "v1.2.3" }, … } }
///
/// Rules: unknown ids are ignored; a missing key means "keep the bundled line"; an EMPTY `whatsNew` hides
/// the row's line; text is trimmed, whitespace-collapsed, control characters stripped and length-capped.
/// The launcher never interprets the text beyond displaying it.
struct WhatsNewNotes: Equatable, Sendable {
    struct Note: Equatable, Sendable {
        let whatsNew: String        // "" = hide the line
        let whatsNewVersion: String?
    }
    static let schemaVersion = 1
    static let maxLineLength = 160
    static let maxVersionLength = 24

    private(set) var notes: [String: Note]

    init(notes: [String: Note] = [:]) { self.notes = notes }

    var isEmpty: Bool { notes.isEmpty }
    subscript(id: String) -> Note? { notes[id] }

    /// Parses the relay document. Throws on anything that isn't the documented shape (so a stray HTML
    /// error page or a redirect body can never clobber the bundled lines).
    static func parse(_ data: Data) throws -> WhatsNewNotes {
        guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let apps = root["apps"] as? [String: Any] else { throw ParseError.badShape }
        if let v = root["schemaVersion"] as? Int, v > schemaVersion { throw ParseError.newerSchema(v) }
        var out: [String: Note] = [:]
        for (id, raw) in apps {
            guard let obj = raw as? [String: Any], let line = obj["whatsNew"] as? String else { continue }
            let version = (obj["whatsNewVersion"] as? String).flatMap { clean($0, max: maxVersionLength) }
            out[id] = Note(whatsNew: clean(line, max: maxLineLength) ?? "", whatsNewVersion: version)
        }
        return WhatsNewNotes(notes: out)
    }

    enum ParseError: Error { case badShape, newerSchema(Int) }

    /// Trim, strip control characters, collapse runs of whitespace, cap the length. nil for an empty result.
    static func clean(_ s: String, max: Int) -> String? {
        let stripped = s.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) }
        let collapsed = String(String.UnicodeScalarView(stripped))
            .split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")
        guard !collapsed.isEmpty else { return nil }
        return collapsed.count > max ? String(collapsed.prefix(max)).trimmingCharacters(in: .whitespaces) + "…" : collapsed
    }

    /// The line to show for an app: the relay's if it has one (empty = hide), else the catalog's.
    func resolved(for app: CatalogApp) -> (whatsNew: String?, whatsNewVersion: String?) {
        guard let n = notes[app.id] else { return (app.whatsNew, app.whatsNewVersion) }
        return (n.whatsNew.isEmpty ? nil : n.whatsNew, n.whatsNewVersion)
    }

    // MARK: - Disk cache (last good copy, for offline starts)

    static var cacheURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("JBTheatreTools", isDirectory: true)
            .appendingPathComponent("whats-new.json")
    }

    static func loadCached() -> WhatsNewNotes? {
        guard let data = try? Data(contentsOf: cacheURL) else { return nil }
        return try? parse(data)
    }

    /// Saves the raw relay bytes (already validated by `parse`) — the cache is the document itself.
    static func cache(_ data: Data) {
        let url = cacheURL
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: url, options: .atomic)
    }

    /// `<origin of the relay base>/notes/whats-new.json` — the base is the `/ghapi` pass-through root, and the
    /// notes live beside it, not under it.
    static func url(relayBase: String) -> URL? {
        guard let base = URL(string: relayBase), let scheme = base.scheme, let host = base.host else { return nil }
        var origin = "\(scheme)://\(host)"
        if let port = base.port { origin += ":\(port)" }
        return URL(string: "\(origin)/notes/whats-new.json")
    }
}
