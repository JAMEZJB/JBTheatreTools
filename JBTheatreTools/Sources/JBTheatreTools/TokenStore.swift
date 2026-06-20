import Foundation
import Security

/// Stores the GitHub PAT in the macOS Keychain (generic password, default keychain).
/// The token is never written to disk in plaintext and never logged.
///
/// Notes on the macOS Keychain access prompt (verified behaviour):
///  - The "<app> wants to use your confidential information…" prompt fires when the running code's
///    identity no longer satisfies the item's ACL/designated-requirement — i.e. when the code
///    signature (cdhash) changes. Because the app is ad-hoc signed, every NEW VERSION has a new
///    cdhash, so the prompt appears once on the first token read after each update. (The
///    `KeychainExplainer` warns the user just before this.)
///  - `exists()` checks presence WITHOUT returning the secret data, so it never prompts — used for
///    `hasToken` so launching never triggers the dialog.
///  - `save()` uses `SecItemUpdate` when the item already exists so a re-save preserves the ACL
///    (delete+add would wipe any "Always Allow" grant — a known footgun).
///  - The loaded token is cached in memory so the Keychain is read at most once per launch
///    (at most one prompt per session).
enum TokenStore {
    private static let service = "com.jamesbreedon.jbtheatretools"
    private static let account = "github-pat"

    /// In-memory cache of the token for this process — keeps the Keychain read to once per launch.
    private(set) static var cachedToken: String?

    private static var baseQuery: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }

    /// Whether a token is saved — does NOT return the secret, so it never triggers the prompt.
    static func exists() -> Bool {
        if cachedToken != nil { return true }
        var query = baseQuery
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        return SecItemCopyMatching(query as CFDictionary, nil) == errSecSuccess
    }

    /// Returns the token, reading the Keychain at most once per launch. May trigger the OS prompt
    /// on the first call after a version update. Returns nil if absent or the user denied access.
    static func load() -> String? {
        if let cached = cachedToken { return cached }
        var query = baseQuery
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        guard status == errSecSuccess,
              let data = item as? Data,
              let token = String(data: data, encoding: .utf8),
              !token.isEmpty else { return nil }
        cachedToken = token
        return token
    }

    @discardableResult
    static func save(_ token: String) -> Bool {
        let data = Data(token.utf8)
        // Prefer SecItemUpdate so an existing item's ACL ("Always Allow" grants) is preserved.
        if exists() {
            let status = SecItemUpdate(baseQuery as CFDictionary, [kSecValueData as String: data] as CFDictionary)
            if status == errSecSuccess { cachedToken = token; return true }
        }
        SecItemDelete(baseQuery as CFDictionary)
        var add = baseQuery
        add[kSecValueData as String] = data
        let status = SecItemAdd(add as CFDictionary, nil)
        if status == errSecSuccess { cachedToken = token; return true }
        return false
    }

    static func clear() {
        SecItemDelete(baseQuery as CFDictionary)
        cachedToken = nil
    }
}
