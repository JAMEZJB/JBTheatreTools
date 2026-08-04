import Foundation
import Security

/// Stores the download-server passphrase in the macOS Keychain (generic password, default keychain),
/// mirroring `TokenStore` (same service, different account). Never written to disk in plaintext and
/// never logged — the "never log the token" rule applies to the passphrase too.
enum ServerAuthStore {
    private static let service = "com.jamesbreedon.jbtheatretools"
    private static let account = "server-pass"

    /// In-memory cache for this process — keeps the Keychain read (and its prompt) to once per launch.
    private(set) static var cachedPassphrase: String?

    private static var baseQuery: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }

    /// Whether a passphrase is saved — does NOT return the secret, so it never triggers the prompt.
    static func exists() -> Bool {
        if cachedPassphrase != nil { return true }
        var query = baseQuery
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        return SecItemCopyMatching(query as CFDictionary, nil) == errSecSuccess
    }

    /// Returns the passphrase, reading the Keychain at most once per launch. May trigger the OS
    /// prompt on the first call after a version update (same cdhash behaviour as the PAT).
    static func load() -> String? {
        if let cached = cachedPassphrase { return cached }
        var query = baseQuery
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        guard status == errSecSuccess,
              let data = item as? Data,
              let pass = String(data: data, encoding: .utf8),
              !pass.isEmpty else { return nil }
        cachedPassphrase = pass
        return pass
    }

    @discardableResult
    static func save(_ passphrase: String) -> Bool {
        let data = Data(passphrase.utf8)
        // SecItemUpdate first so an existing item's ACL ("Always Allow") is preserved.
        if exists() {
            let status = SecItemUpdate(baseQuery as CFDictionary, [kSecValueData as String: data] as CFDictionary)
            if status == errSecSuccess { cachedPassphrase = passphrase; return true }
        }
        SecItemDelete(baseQuery as CFDictionary)
        var add = baseQuery
        add[kSecValueData as String] = data
        let status = SecItemAdd(add as CFDictionary, nil)
        if status == errSecSuccess { cachedPassphrase = passphrase; return true }
        return false
    }

    static func clear() {
        SecItemDelete(baseQuery as CFDictionary)
        cachedPassphrase = nil
    }
}
