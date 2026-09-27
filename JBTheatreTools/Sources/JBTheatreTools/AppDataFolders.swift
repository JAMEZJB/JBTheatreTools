import Foundation

/// Where an app keeps its own settings, logs and caches (outside its bundle), from the catalog's `dataFolders` (folder
/// names) and `bundleIds` — so "Uninstall" can offer to remove them too. Pure (the Library folder is passed in) and
/// word-for-word the Windows AppDataFolders name rules.
enum AppDataFolders {
    /// Shared folders no app entry may ever name, whatever the catalog says (compared case-insensitively).
    private static let reserved: Set<String> = [
        "jbtheatretools", "jb theatre tools", "microsoft", "packages", "programs", "temp", "google", "apple", "caches",
        "logs", "application support", "preferences", "webkit", "httpstorages", "saved application state", "documents",
        "desktop", "downloads", "library", "appdata", "local", "roaming", "locallow", "application data", "mozilla",
    ]

    /// A single, ordinary folder name: 1–64 characters, no path separators or wildcards, not "." / "..", no leading or
    /// trailing space or dot, and not one of the shared folders above.
    static func isSafeName(_ name: String?) -> Bool {
        guard let name, !name.trimmingCharacters(in: .whitespaces).isEmpty, name.count <= 64,
              name.trimmingCharacters(in: .whitespaces) == name, !name.hasPrefix("."), !name.hasSuffix(".") else { return false }
        if name.contains(where: { "/\\:*?\"<>|\0".contains($0) }) { return false }
        return !reserved.contains(name.lowercased())
    }

    /// A reverse-DNS app identifier with at least three parts (never Apple's own).
    static func isSafeBundleId(_ id: String?) -> Bool {
        guard let id, id.count <= 128, !id.lowercased().hasPrefix("com.apple.") else { return false }
        let parts = id.split(separator: ".", omittingEmptySubsequences: false)
        return parts.count >= 3 && parts.allSatisfy { p in
            !p.isEmpty && p.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-") }
        }
    }

    /// The Library locations for an app: each folder name under Application Support, Logs and Caches; each bundle id's
    /// WebKit data, cache, HTTP storage, saved window state and preferences. Unsafe entries are skipped; order is stable.
    static func macPaths(folders: [String]?, bundleIds: [String]?, library: URL) -> [URL] {
        var out: [URL] = []
        var seen = Set<String>()
        func add(_ url: URL) { if seen.insert(url.path.lowercased()).inserted { out.append(url) } }
        for name in (folders ?? []) where isSafeName(name) {
            for root in ["Application Support", "Logs", "Caches"] {
                add(library.appendingPathComponent(root, isDirectory: true).appendingPathComponent(name, isDirectory: true))
            }
        }
        for id in (bundleIds ?? []) where isSafeBundleId(id) {
            add(library.appendingPathComponent("WebKit/\(id)", isDirectory: true))
            add(library.appendingPathComponent("Caches/\(id)", isDirectory: true))
            add(library.appendingPathComponent("HTTPStorages/\(id)", isDirectory: true))
            add(library.appendingPathComponent("HTTPStorages/\(id).binarycookies"))
            add(library.appendingPathComponent("Saved Application State/\(id).savedState", isDirectory: true))
            add(library.appendingPathComponent("Preferences/\(id).plist"))
        }
        return out
    }
}
