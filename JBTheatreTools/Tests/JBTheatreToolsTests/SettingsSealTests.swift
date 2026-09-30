import XCTest
@testable import JBTheatreTools

/// The passwords part of a settings backup must be byte-for-byte what the suite's apps write and read, so these check
/// the launcher's PBKDF2 seal against the shared known-answer vectors (Fixtures/settings-seal-vectors.json).
final class SettingsSealTests: XCTestCase {
    private struct Vector {
        let kdf: [String: Any]
        let passphrase: String
        let aad: Data
        let plaintext: Data
        let salt: Data
        let nonce: Data
        let ct: String
        let tag: String
    }

    private func hex(_ s: String) -> Data {
        var d = Data()
        var i = s.startIndex
        while i < s.endIndex {
            let j = s.index(i, offsetBy: 2)
            d.append(UInt8(s[i..<j], radix: 16)!)
            i = j
        }
        return d
    }

    private func vectors() throws -> [Vector] {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "settings-seal-vectors", withExtension: "json",
                                                  subdirectory: "Fixtures"))
        let root = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        let list = try XCTUnwrap(root["vectors"] as? [[String: Any]])
        return list.map {
            Vector(kdf: $0["kdf"] as! [String: Any], passphrase: $0["passphrase"] as! String,
                   aad: Data(($0["aad"] as! String).utf8), plaintext: Data(($0["plaintext"] as! String).utf8),
                   salt: hex($0["salt_hex"] as! String), nonce: hex($0["nonce_hex"] as! String),
                   ct: $0["ct_b64"] as! String, tag: $0["tag_b64"] as! String)
        }
    }

    private func pbkdf2Vector() throws -> Vector {
        try XCTUnwrap(vectors().first { $0.kdf["name"] as? String == "pbkdf2-sha256" })
    }

    func testReproducesThePBKDF2VectorExactly() throws {
        let v = try pbkdf2Vector()
        let rounds = try XCTUnwrap((v.kdf["iterations"] as? NSNumber)?.intValue)
        XCTAssertEqual(rounds, SettingsSeal.pbkdf2Rounds)
        let pw = v.passphrase
        let box = try SettingsSeal.seal(v.plaintext, passphrase: pw, aad: v.aad,
                                        salt: v.salt, nonce: v.nonce, rounds: rounds)
        XCTAssertEqual(box["ct"] as? String, v.ct)
        XCTAssertEqual(box["tag"] as? String, v.tag)
        XCTAssertEqual(box["alg"] as? String, "hmac-sha256-ctr+hmac-sha256")
        let kdf = try XCTUnwrap(box["kdf"] as? [String: Any])
        XCTAssertEqual(kdf["name"] as? String, "pbkdf2-sha256")
        XCTAssertEqual(kdf["salt"] as? String, v.salt.base64EncodedString())
        XCTAssertEqual(box["nonce"] as? String, v.nonce.base64EncodedString())
    }

    func testOpensTheVectorAndRefusesAWrongPassphrase() throws {
        let v = try pbkdf2Vector()
        var kdf = v.kdf
        kdf["salt"] = v.salt.base64EncodedString()
        let box: [String: Any] = ["alg": SettingsSeal.algorithm, "kdf": kdf, "nonce": v.nonce.base64EncodedString(),
                                  "ct": v.ct, "tag": v.tag]
        let pw = v.passphrase, bad = pw + "!"
        XCTAssertEqual(try SettingsSeal.unseal(box, passphrase: pw, aad: v.aad), v.plaintext)
        XCTAssertThrowsError(try SettingsSeal.unseal(box, passphrase: bad, aad: v.aad)) {
            XCTAssertEqual($0 as? SettingsSeal.SealError, .wrongPassphrase)
        }
        // A backup for another app (different aad) doesn't open either.
        XCTAssertThrowsError(try SettingsSeal.unseal(box, passphrase: pw, aad: Data("jbtt-settings|1|psntools".utf8)))
    }

    func testTamperedCiphertextIsRefused() throws {
        let box = try SettingsSeal.seal(Data("secret".utf8), passphrase: "pw", aad: Data("a".utf8), rounds: 100_000)
        var ct = Data(base64Encoded: box["ct"] as! String)!
        ct[0] ^= 1
        var bad = box
        bad["ct"] = ct.base64EncodedString()
        XCTAssertThrowsError(try SettingsSeal.unseal(bad, passphrase: "pw", aad: Data("a".utf8))) {
            XCTAssertEqual($0 as? SettingsSeal.SealError, .wrongPassphrase)
        }
    }

    func testScryptBoxesAreReportedAsNewer() throws {
        let v = try XCTUnwrap(vectors().first { $0.kdf["name"] as? String == "scrypt" })
        var kdf = v.kdf
        kdf["salt"] = v.salt.base64EncodedString()
        let box: [String: Any] = ["alg": SettingsSeal.algorithm, "kdf": kdf, "nonce": v.nonce.base64EncodedString(),
                                  "ct": v.ct, "tag": v.tag]
        let pw = v.passphrase
        XCTAssertThrowsError(try SettingsSeal.unseal(box, passphrase: pw, aad: v.aad)) {
            XCTAssertEqual($0 as? SettingsSeal.SealError, .unsupported)
        }
    }

    func testHostileRoundCountsAreRefused() {
        let box: [String: Any] = ["alg": SettingsSeal.algorithm,
                                  "kdf": ["name": "pbkdf2-sha256", "iterations": 50_000_000, "salt": "AAAA"],
                                  "nonce": "AAAA", "ct": "", "tag": ""]
        XCTAssertThrowsError(try SettingsSeal.unseal(box, passphrase: "x", aad: Data())) {
            XCTAssertEqual($0 as? SettingsSeal.SealError, .unsupported)
        }
    }

    func testPassphraseIsNFCNormalised() throws {
        let composed = "caf\u{00E9}", decomposed = "cafe\u{0301}"
        XCTAssertEqual(SettingsSeal.normalized(composed), SettingsSeal.normalized(decomposed))
        let a = composed, b = decomposed
        let box = try SettingsSeal.seal(Data("x".utf8), passphrase: a, aad: Data(), rounds: 100_000)
        XCTAssertEqual(try SettingsSeal.unseal(box, passphrase: b, aad: Data()), Data("x".utf8))
    }

    func testEmptyPassphraseIsRefused() {
        XCTAssertThrowsError(try SettingsSeal.seal(Data("x".utf8), passphrase: "", aad: Data()))
    }
}
