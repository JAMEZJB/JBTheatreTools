import XCTest
@testable import JBTheatreTools

/// Signature verification against the REAL signed manifest from the public JBTheatreTools v1.15.0
/// release (Fixtures/SHA256SUMS + .minisig, signed offline by the controller with the suite key), so
/// these prove interoperability with the actual signing side — not just self-consistency.
final class MinisignTests: XCTestCase {
    private let release = "JBTheatreTools v1.15.0"
    private var manifest: Data { fixture("SHA256SUMS", nil) }
    private var minisig: String { String(decoding: fixture("SHA256SUMS", "minisig"), as: UTF8.self) }

    private func fixture(_ name: String, _ ext: String?) -> Data {
        guard let url = Bundle.module.url(forResource: name, withExtension: ext, subdirectory: "Fixtures") else {
            XCTFail("missing fixture \(name).\(ext ?? "")"); return Data()
        }
        return (try? Data(contentsOf: url)) ?? Data()
    }

    func testRealReleaseManifestVerifies() {
        XCTAssertEqual(manifest.count, 287)
        XCTAssertNoThrow(try Minisign.verify(manifest: manifest, minisig: minisig, expectedTrustedComment: release))
    }

    func testParseExtractsFields() throws {
        let sig = try Minisign.parse(minisig)
        XCTAssertTrue(sig.prehashed, "controller signs in minisign's default \"ED\" mode (Ed25519 over BLAKE2b-512)")
        XCTAssertEqual(sig.trustedComment, release)
        XCTAssertEqual(sig.signature.count, 64)
        XCTAssertEqual(sig.globalSignature.count, 64)
        // key id is a little-endian u64 → printed big-endian by minisign
        XCTAssertEqual(sig.keyId.reversed().map { String(format: "%02X", $0) }.joined(), "FC6699A439B42178")
    }

    func testTamperedManifestFails() {
        XCTAssertThrowsError(try Minisign.verify(manifest: manifest + Data("x".utf8), minisig: minisig,
                                                 expectedTrustedComment: release)) {
            XCTAssertEqual($0 as? Minisign.VerifyError, .badSignature)
        }
    }

    func testManifestIsBoundToItsOwnRelease() {
        // A validly-signed manifest from v1.15.0 must not be accepted for another release (replay).
        XCTAssertThrowsError(try Minisign.verify(manifest: manifest, minisig: minisig,
                                                 expectedTrustedComment: "JBTheatreTools v1.14.0")) {
            XCTAssertEqual($0 as? Minisign.VerifyError,
                           .commentMismatch(expected: "JBTheatreTools v1.14.0", got: release))
        }
    }

    func testTamperedTrustedCommentFails() {
        // Editing the comment to claim another release breaks the GLOBAL signature (checked first).
        let forged = minisig.replacingOccurrences(of: "v1.15.0", with: "v1.15.1")
        XCTAssertThrowsError(try Minisign.verify(manifest: manifest, minisig: forged,
                                                 expectedTrustedComment: "JBTheatreTools v1.15.1")) {
            XCTAssertEqual($0 as? Minisign.VerifyError, .badGlobalSignature)
        }
    }

    func testDifferentKeyIdIsRejected() throws {
        var raw = try XCTUnwrap(Data(base64Encoded: Minisign.suitePublicKey))
        raw.replaceSubrange(2..<10, with: Data(repeating: 0, count: 8))   // same key bytes, different key id
        XCTAssertThrowsError(try Minisign.verify(manifest: manifest, minisig: minisig, expectedTrustedComment: release,
                                                 publicKeyBase64: raw.base64EncodedString())) {
            XCTAssertEqual($0 as? Minisign.VerifyError, .unknownKey)
        }
    }

    func testMalformedInputsFailClosed() {
        for bad in ["", "garbage", "a\nb\nc", "untrusted comment: x\nbm90YmFzZTY0\ntrusted comment: y\nzz"] {
            XCTAssertThrowsError(try Minisign.verify(manifest: manifest, minisig: bad, expectedTrustedComment: release),
                                 "input: \(bad.debugDescription)") {
                if case .malformed = $0 as? Minisign.VerifyError {} else { XCTFail("expected .malformed, got \($0)") }
            }
        }
    }
}
