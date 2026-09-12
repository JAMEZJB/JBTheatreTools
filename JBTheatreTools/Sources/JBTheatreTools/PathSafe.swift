import Foundation

/// Sanitises an API-supplied string (a release tag or asset name) before it's used as ONE local
/// filename component (audit F8). Real GitHub tags obey refname rules, but the relay returns whatever
/// JSON it emits — a hostile relay could answer `"tag_name": "../../Library/LaunchAgents/x"` and steer a
/// cache write outside the caches dir (before verification runs). This keeps only `[A-Za-z0-9._-+]`,
/// maps everything else (incl. `/` and `\`) to `_`, and never lets the result start with `.` — so no
/// component can traverse (`..`) or hide (`.foo`).
enum PathSafe {
    static func component(_ s: String) -> String {
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "._-+"))
        var cleaned = String(s.unicodeScalars.map { allowed.contains($0) ? Character($0) : "_" })
        if cleaned.isEmpty || cleaned.hasPrefix(".") { cleaned = "_" + cleaned }
        return cleaned
    }
}
