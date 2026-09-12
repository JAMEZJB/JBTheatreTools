import Foundation

/// Guards the download-relay URL (audit F2).
///
/// The built-in relay URL lives in the catalog and is trusted. There is also a user-INVISIBLE emergency
/// override (`theatre.serverURL` in defaults) so a machine on an old build can be repointed if the relay
/// ever moves. That override was previously accepted with no checks — so anything that can write the
/// user's defaults (a stray `defaults write`, another app, a "support" one-liner) could silently redirect
/// every API call, sending the suite passphrase (`Authorization: Basic suite:<pass>`) to an attacker and
/// installing whatever it served. This clamps the override to **https on a jamesbreedon.com host**; a
/// value that isn't is ignored (the catalog URL stands), and a malformed value can no longer crash the
/// client's URL construction.
enum RelayPolicy {
    /// A relay override is honoured only when it parses, is `https`, and its host is `jamesbreedon.com`
    /// or a subdomain of it. Returns the trimmed URL string when allowed, else nil.
    static func validatedOverride(_ raw: String?) -> String? {
        guard let s = raw?.trimmingCharacters(in: .whitespacesAndNewlines), !s.isEmpty,
              let u = URL(string: s),
              u.scheme?.lowercased() == "https",
              let host = u.host?.lowercased(),
              host == "jamesbreedon.com" || host.hasSuffix(".jamesbreedon.com")
        else { return nil }
        return s
    }
}
