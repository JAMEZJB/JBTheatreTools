import Foundation
import CryptoKit

/// Verifies minisign (Ed25519) signatures on release manifests.
///
/// Why: a `SHA256SUMS` fetched from the same release, over the same channel, as the payload only proves
/// the download wasn't corrupted in transit — it can't tell a hostile release (or a hostile relay) from a
/// real one. The controller signs every release's `SHA256SUMS` OFFLINE with the suite's minisign key,
/// and the launcher embeds the public key: a manifest is trusted only if that signature verifies.
/// The key never leaves the controller; this code only ever verifies. (Audit F1.)
///
/// minisign file format (`SHA256SUMS.minisig`):
///   untrusted comment: <ignored>
///   base64( alg(2) ‖ key id(8) ‖ signature(64) )      alg "ED" = Ed25519 over BLAKE2b-512(file); "Ed" = over the file
///   trusted comment: <text>
///   base64( global signature(64) )                     Ed25519 over ( signature ‖ trusted comment )
///
/// The controller's trusted comment is `<repo> <tag>`; verifying it against the release being installed
/// binds a manifest to ONE release, so a validly-signed manifest from another release can't be replayed.
enum Minisign {
    /// The suite's minisign public key (key id FC6699A439B42178). Rotating it means a launcher release.
    static let suitePublicKey = "RWR4IbQ5pJlm/OLhkB2EnuKqtxxz9/TKpCRycMpLJlZh5fxqMQDyZUoC"

    enum VerifyError: LocalizedError, Equatable {
        case malformed(String)
        case unknownKey
        case badSignature
        case badGlobalSignature
        case commentMismatch(expected: String, got: String)

        var errorDescription: String? {
            switch self {
            case .malformed(let why): return "The release's signature file is malformed (\(why))."
            case .unknownKey: return "The release's manifest was signed with a different key than the suite key."
            case .badSignature: return "The release's manifest signature does not verify."
            case .badGlobalSignature: return "The release's signature comment does not verify."
            case .commentMismatch(let expected, let got): return "The signed manifest is for “\(got)”, not “\(expected)”."
            }
        }
    }

    struct PublicKey {
        let keyId: Data      // 8 bytes
        let raw: Data        // 32 bytes

        init(base64: String) throws {
            guard let d = Data(base64Encoded: base64), d.count == 42, d.prefix(2) == Data("Ed".utf8) else {
                throw VerifyError.malformed("public key")
            }
            keyId = d.subdata(in: 2..<10)
            raw = d.subdata(in: 10..<42)
        }
    }

    struct Signature {
        /// "ED": the signature is over BLAKE2b-512 of the file (minisign's default); "Ed": over the raw file.
        let prehashed: Bool
        let keyId: Data
        let signature: Data          // 64 bytes
        let trustedComment: String
        let globalSignature: Data    // 64 bytes, over (signature ‖ trustedComment)
    }

    /// Parses a `.minisig` file. Throws `.malformed` on anything unexpected.
    static func parse(_ text: String) throws -> Signature {
        let lines = text.split(whereSeparator: \.isNewline).map { String($0).trimmingCharacters(in: .whitespaces) }
        guard lines.count >= 4 else { throw VerifyError.malformed("expected 4 lines, got \(lines.count)") }
        guard let blob = Data(base64Encoded: lines[1]), blob.count == 74 else { throw VerifyError.malformed("signature line") }
        let alg = String(decoding: blob.prefix(2), as: UTF8.self)
        guard alg == "ED" || alg == "Ed" else { throw VerifyError.malformed("algorithm “\(alg)”") }
        let prefix = "trusted comment:"
        guard lines[2].lowercased().hasPrefix(prefix) else { throw VerifyError.malformed("trusted comment line") }
        let comment = String(lines[2].dropFirst(prefix.count)).trimmingCharacters(in: .whitespaces)
        guard let global = Data(base64Encoded: lines[3]), global.count == 64 else { throw VerifyError.malformed("global signature") }
        return Signature(prehashed: alg == "ED",
                         keyId: blob.subdata(in: 2..<10),
                         signature: blob.subdata(in: 10..<74),
                         trustedComment: comment,
                         globalSignature: global)
    }

    /// Verifies `minisig` over `manifest` with the suite key, failing closed on any problem. When
    /// `expectedTrustedComment` is given (the controller signs `<repo> <tag>`), the signed comment must
    /// match it exactly, binding the manifest to that one release.
    static func verify(manifest: Data, minisig: String, expectedTrustedComment: String?,
                       publicKeyBase64: String = suitePublicKey) throws {
        let key = try PublicKey(base64: publicKeyBase64)
        let sig = try parse(minisig)
        guard sig.keyId == key.keyId else { throw VerifyError.unknownKey }
        let pk = try Curve25519.Signing.PublicKey(rawRepresentation: key.raw)

        // 1. The trusted comment is itself signed (over signature ‖ comment). Check it before using the
        //    comment for anything, so a tampered comment can't steer the release-binding check.
        guard pk.isValidSignature(sig.globalSignature, for: sig.signature + Data(sig.trustedComment.utf8)) else {
            throw VerifyError.badGlobalSignature
        }
        // 2. The manifest signature — over BLAKE2b-512(manifest) for "ED", the raw bytes for legacy "Ed".
        let message = sig.prehashed ? Blake2b.hash512(manifest) : manifest
        guard pk.isValidSignature(sig.signature, for: message) else { throw VerifyError.badSignature }
        // 3. Bind the manifest to the release being installed.
        if let expected = expectedTrustedComment, sig.trustedComment != expected {
            throw VerifyError.commentMismatch(expected: expected, got: sig.trustedComment)
        }
    }
}
