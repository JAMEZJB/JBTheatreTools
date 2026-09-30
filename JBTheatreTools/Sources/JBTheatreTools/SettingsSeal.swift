import Foundation
import CryptoKit
import CommonCrypto

/// Passphrase sealing for the passwords part of a settings backup (`.jbtt-settings`). The same construction the suite's
/// apps use, so any of them can read what the launcher writes and the other way round:
///
///     key(64)   = PBKDF2-HMAC-SHA256(NFC passphrase, salt, 600 000 rounds)
///     enc, mac  = key[0..<32], key[32..<64]
///     stream    = HMAC-SHA256(enc, nonce ‖ counter as 8-byte big-endian), counter 0, 1, 2 … (32 bytes per block)
///     ct        = plaintext XOR stream
///     tag       = HMAC-SHA256(mac, "jbtt-seal-v1" ‖ len(aad) as 8-byte big-endian ‖ aad ‖ nonce ‖ ct)
///
/// The tag is checked (constant time) before anything is decrypted. The apps may also use scrypt as the key step; the
/// launcher only ever writes PBKDF2, and a file that asks for scrypt is reported as "made by a newer app".
enum SettingsSeal {
    static let algorithm = "hmac-sha256-ctr+hmac-sha256"
    static let pbkdf2Name = "pbkdf2-sha256"
    static let pbkdf2Rounds = 600_000
    /// Bounds a file may ask for, so a hostile backup can't make a restore hang.
    static let minRounds = 100_000
    static let maxRounds = 5_000_000
    private static let tagPrefix = Data("jbtt-seal-v1".utf8)

    enum SealError: LocalizedError, Equatable {
        case wrongPassphrase
        case damaged
        case unsupported
        case emptyPassphrase

        var errorDescription: String? {
            switch self {
            case .wrongPassphrase: return "That passphrase doesn't open this backup."
            case .damaged: return "The backup's password section is damaged."
            case .unsupported: return "The backup's password section was made by a newer app."
            case .emptyPassphrase: return "Enter a passphrase."
            }
        }
    }

    /// The passphrase as the key step sees it: Unicode NFC, then UTF-8 (so "é" typed either way opens the same file).
    static func normalized(_ passphrase: String) -> Data {
        Data(passphrase.precomposedStringWithCanonicalMapping.utf8)
    }

    static func pbkdf2(_ passphrase: String, salt: Data, rounds: Int, length: Int = 64) -> Data {
        let password = normalized(passphrase)
        var out = Data(count: length)
        let status = out.withUnsafeMutableBytes { outBuf in
            salt.withUnsafeBytes { saltBuf in
                password.withUnsafeBytes { pwBuf in
                    CCKeyDerivationPBKDF(
                        CCPBKDFAlgorithm(kCCPBKDF2),
                        pwBuf.baseAddress?.assumingMemoryBound(to: Int8.self), password.count,
                        saltBuf.baseAddress?.assumingMemoryBound(to: UInt8.self), salt.count,
                        CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), UInt32(rounds),
                        outBuf.baseAddress?.assumingMemoryBound(to: UInt8.self), length)
                }
            }
        }
        precondition(status == kCCSuccess, "PBKDF2 failed (\(status))")
        return out
    }

    static func stream(key: Data, nonce: Data, count: Int) -> Data {
        var out = Data(capacity: count + 32)
        var counter: UInt64 = 0
        let k = SymmetricKey(data: key)
        while out.count < count {
            var block = nonce
            withUnsafeBytes(of: counter.bigEndian) { block.append(contentsOf: $0) }
            out.append(contentsOf: HMAC<SHA256>.authenticationCode(for: block, using: k))
            counter += 1
        }
        return out.prefix(count)
    }

    static func tag(macKey: Data, aad: Data, nonce: Data, ct: Data) -> Data {
        Data(HMAC<SHA256>.authenticationCode(for: tagMessage(aad: aad, nonce: nonce, ct: ct), using: SymmetricKey(data: macKey)))
    }

    private static func xor(_ a: Data, _ b: Data) -> Data {
        Data(zip(a, b).map { $0 ^ $1 })
    }

    static func randomBytes(_ n: Int) -> Data {
        var d = Data(count: n)
        let status = d.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, n, $0.baseAddress!) }
        precondition(status == errSecSuccess, "no random bytes")
        return d
    }

    /// The sealed box as the file stores it (`secrets` minus `protected` / `count`, which the caller adds).
    /// `salt` / `nonce` / `rounds` are for the known-answer tests only.
    static func seal(_ plaintext: Data, passphrase: String, aad: Data,
                     salt: Data? = nil, nonce: Data? = nil, rounds: Int = pbkdf2Rounds) throws -> [String: Any] {
        guard !passphrase.isEmpty else { throw SealError.emptyPassphrase }
        let salt = salt ?? randomBytes(16)
        let nonce = nonce ?? randomBytes(16)
        let key = pbkdf2(passphrase, salt: salt, rounds: rounds)
        let enc = key.prefix(32), mac = key.suffix(32)
        let ct = xor(plaintext, stream(key: Data(enc), nonce: nonce, count: plaintext.count))
        return [
            "alg": algorithm,
            "kdf": ["name": pbkdf2Name, "iterations": rounds, "salt": salt.base64EncodedString()],
            "nonce": nonce.base64EncodedString(),
            "ct": ct.base64EncodedString(),
            "tag": tag(macKey: Data(mac), aad: aad, nonce: nonce, ct: ct).base64EncodedString(),
        ]
    }

    static func unseal(_ box: [String: Any], passphrase: String, aad: Data) throws -> Data {
        guard box["alg"] as? String == algorithm, let kdf = box["kdf"] as? [String: Any] else { throw SealError.unsupported }
        guard kdf["name"] as? String == pbkdf2Name else { throw SealError.unsupported }
        guard let rounds = (kdf["iterations"] as? NSNumber)?.intValue, (minRounds...maxRounds).contains(rounds) else {
            throw SealError.unsupported
        }
        func b64(_ v: Any?) throws -> Data {
            guard let s = v as? String, let d = Data(base64Encoded: s) else { throw SealError.damaged }
            return d
        }
        let salt = try b64(kdf["salt"]), nonce = try b64(box["nonce"]), ct = try b64(box["ct"]), given = try b64(box["tag"])
        let key = pbkdf2(passphrase, salt: salt, rounds: rounds)
        // CryptoKit's check compares in constant time.
        guard given.count == SHA256.byteCount,
              HMAC<SHA256>.isValidAuthenticationCode(given, authenticating: tagMessage(aad: aad, nonce: nonce, ct: ct),
                                                     using: SymmetricKey(data: key.suffix(32))) else {
            throw SealError.wrongPassphrase
        }
        return xor(ct, stream(key: Data(key.prefix(32)), nonce: nonce, count: ct.count))
    }

    private static func tagMessage(aad: Data, nonce: Data, ct: Data) -> Data {
        var msg = tagPrefix
        withUnsafeBytes(of: UInt64(aad.count).bigEndian) { msg.append(contentsOf: $0) }
        msg.append(aad)
        msg.append(nonce)
        msg.append(ct)
        return msg
    }
}
