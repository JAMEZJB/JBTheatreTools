import Foundation
import Security

/// The running app's code identity, used to detect when the Keychain will re-prompt.
///
/// macOS ties a Keychain item's ACL to the creating binary's code signature (cdhash). Ad-hoc
/// signed builds get a new cdhash every release, so the cdhash is the exact signal for "this is a
/// different build than the one that saved the token → the OS will prompt". We gate the explainer
/// on a change in this value. Falls back to the build number if the cdhash can't be read.
enum CodeIdentity {
    static func current() -> String {
        if let hash = cdHash() { return "cdhash:" + hash }
        let build = (Bundle.main.infoDictionary?["CFBundleVersion"] as? String) ?? "0"
        return "build:" + build
    }

    private static func cdHash() -> String? {
        var selfCode: SecCode?
        guard SecCodeCopySelf([], &selfCode) == errSecSuccess, let selfCode = selfCode else { return nil }
        var staticCode: SecStaticCode?
        guard SecCodeCopyStaticCode(selfCode, [], &staticCode) == errSecSuccess,
              let staticCode = staticCode else { return nil }
        var info: CFDictionary?
        guard SecCodeCopySigningInformation(staticCode, SecCSFlags(rawValue: 0), &info) == errSecSuccess,
              let dict = info as? [String: Any],
              let data = dict[kSecCodeInfoUnique as String] as? Data else { return nil }
        return data.map { String(format: "%02x", $0) }.joined()
    }
}
