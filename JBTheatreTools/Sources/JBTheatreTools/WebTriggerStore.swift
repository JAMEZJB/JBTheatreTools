import Foundation
import Security

/// Stores the Dashboard web trigger's token (the shared word a Show Dashboard button sends) in the macOS Keychain,
/// mirroring `ServerAuthStore` (same service, its own account). Never written to disk in plaintext, never logged and
/// never shown back in Settings. Read once per launch, and only while the trigger is switched on (so a launcher with the
/// trigger off never asks the Keychain for it).
enum WebTriggerStore {
    private static let service = "com.jamesbreedon.jbtheatretools"
    private static let account = "web-trigger"

    private(set) static var cachedToken: String?

    private static var baseQuery: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }

    /// Whether a token is saved — does NOT return the secret, so it never triggers the Keychain prompt.
    static func exists() -> Bool {
        if cachedToken != nil { return true }
        var query = baseQuery
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        return SecItemCopyMatching(query as CFDictionary, nil) == errSecSuccess
    }

    /// The token, read from the Keychain at most once per launch; nil if none is saved (or access was denied).
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
        // SecItemUpdate first so an existing item's ACL ("Always Allow") is preserved.
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
