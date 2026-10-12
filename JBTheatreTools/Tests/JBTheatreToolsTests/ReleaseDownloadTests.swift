import XCTest
import CryptoKit
@testable import JBTheatreTools

/// `--download`'s fetch-and-verify step. Every release here is synthetic, signed in-process by a throwaway Ed25519 key
/// in minisign's "ED" format (the suite key never appears), and `fetch` writes bytes from memory: no network at all.
final class ReleaseDownloadTests: XCTestCase {
    private var dir: URL!
    private let key = Curve25519.Signing.PrivateKey()
    private let keyId = Data([1, 2, 3, 4, 5, 6, 7, 8])
    private let asset = "Demo-Windows-x64.exe"
    private let payload = Data("synthetic windows build".utf8)

    override func setUp() {
        dir = FileManager.default.temporaryDirectory.appendingPathComponent("jbtt-download-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDown() { try? FileManager.default.removeItem(at: dir) }

    private var publicKey: String { (Data("Ed".utf8) + keyId + key.publicKey.rawRepresentation).base64EncodedString() }

    private func sign(_ manifest: Data, comment: String) throws -> String {
        let sig = try key.signature(for: Blake2b.hash512(manifest))
        let global = try key.signature(for: sig + Data(comment.utf8))
        return "untrusted comment: test\n\((Data("ED".utf8) + keyId + sig).base64EncodedString())\ntrusted comment: \(comment)\n\(global.base64EncodedString())\n"
    }

    private func sums(_ files: [String: Data]) -> Data {
        Data(files.map { "\(SHA256.hash(data: $0.value).map { String(format: "%02x", $0) }.joined())  \($0.key)\n" }.sorted().joined().utf8)
    }

    /// A release: name → bytes, SHA256SUMS listing `listed`, signed for `comment`.
    private func release(listed: [String: Data]? = nil, comment: String = "Demo v1.2.0", served: Data? = nil) throws -> [String: Data] {
        let manifest = sums(listed ?? [asset: payload, "Demo-macOS.zip": Data("mac".utf8)])
        return [asset: served ?? payload, "SHA256SUMS": manifest, "SHA256SUMS.minisig": Data(try sign(manifest, comment: comment).utf8)]
    }

    private func run(_ files: [String: Data], size: Int? = nil) async throws -> ReleaseDownload.Result {
        try await ReleaseDownload.run(folder: dir, assetName: asset, expectedSize: size ?? payload.count, repo: "Demo", tag: "v1.2.0",
                                      publicKey: publicKey) { name, dest in
            guard let data = files[name] else { throw GitHubError.assetNotFound(name) }
            try data.write(to: dest)
        }
    }

    private func folderNames() -> [String] {
        ((try? FileManager.default.contentsOfDirectory(atPath: dir.path)) ?? []).sorted()
    }

    func testDownloadsAndVerifiesTheThreeFiles() async throws {
        let r = try await run(try release())
        XCTAssertEqual(folderNames(), ["Demo-Windows-x64.exe", "SHA256SUMS", "SHA256SUMS.minisig"])
        XCTAssertEqual(try Data(contentsOf: r.path), payload)
        XCTAssertEqual(r.sums.lastPathComponent, "SHA256SUMS")
        XCTAssertEqual(r.sig.lastPathComponent, "SHA256SUMS.minisig")
        XCTAssertEqual(r.size, payload.count)
        XCTAssertEqual(r.sha256, SHA256.hash(data: payload).map { String(format: "%02x", $0) }.joined())
        // What lands in the folder passes --install-file's check as it stands.
        XCTAssertNoThrow(try ReleaseDownload.verify(file: r.path, assetName: asset, expectedSize: 0, sums: r.sums, sig: r.sig,
                                                    repo: "Demo", tag: "v1.2.0", publicKey: publicKey))
    }

    func testSignatureForAnotherReleaseIsRefusedAndNothingStays() async throws {
        await assertRejected(try release(comment: "Demo v1.1.0"))
    }

    func testTamperedBuildIsRefusedAndNothingStays() async throws {
        await assertRejected(try release(served: Data("tampered build!!!!!!!!!".utf8)))   // same size, other bytes
    }

    func testUnlistedBuildIsRefused() async throws {
        await assertRejected(try release(listed: ["Demo-macOS.zip": Data("mac".utf8)]))
    }

    func testSuiteKeyRefusesATestSignature() async throws {
        let files = try release()
        do {
            _ = try await ReleaseDownload.run(folder: dir, assetName: asset, expectedSize: payload.count, repo: "Demo", tag: "v1.2.0") { n, d in
                try files[n]!.write(to: d)
            }
            XCTFail("a manifest not signed by the suite key was accepted")
        } catch is ReleaseDownload.Failure {}
        XCTAssertEqual(folderNames(), [])
    }

    func testSizeOtherThanTheReleaseListsIsRefused() async throws {
        await assertRejected(try release(), size: payload.count + 1)
    }

    func testFailedFetchLeavesNothingAndKeepsWhatWasThere() async throws {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try Data("older, unrelated".utf8).write(to: dir.appendingPathComponent("SHA256SUMS"))
        var files = try release()
        files["SHA256SUMS.minisig"] = nil   // the third download fails
        do { _ = try await run(files); XCTFail("expected a failure") } catch GitHubError.assetNotFound {}
        XCTAssertEqual(folderNames(), ["SHA256SUMS"])
        XCTAssertEqual(try Data(contentsOf: dir.appendingPathComponent("SHA256SUMS")), Data("older, unrelated".utf8))
    }

    func testReplacesAnEarlierCopy() async throws {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        for n in [asset, "SHA256SUMS", "SHA256SUMS.minisig"] { try Data("old".utf8).write(to: dir.appendingPathComponent(n)) }
        let r = try await run(try release())
        XCTAssertEqual(try Data(contentsOf: r.path), payload)
        XCTAssertEqual(folderNames(), ["Demo-Windows-x64.exe", "SHA256SUMS", "SHA256SUMS.minisig"])
    }

    func testAssetNameMustBeAPlainFileName() {
        for bad in ["", "../x.exe", "a/b.exe", ".hidden", "SHA256SUMS", "SHA256SUMS.minisig", "two words.exe", "..\\x.exe"] {
            XCTAssertFalse(ReleaseDownload.isPlainAssetName(bad), bad)
        }
        for good in ["ShowDashboard-Windows-x64.exe", "Stagehand-macOS.zip", "PDFTools-Full-Windows-arm64.exe"] {
            XCTAssertTrue(ReleaseDownload.isPlainAssetName(good), good)
        }
    }

    private func assertRejected(_ files: [String: Data], size: Int? = nil, line: UInt = #line) async {
        do { _ = try await run(files, size: size); XCTFail("expected a rejection", line: line) }
        catch is ReleaseDownload.Failure {}
        catch { XCTFail("unexpected \(error)", line: line) }
        XCTAssertEqual(folderNames(), [], "partial files stayed", line: line)
    }
}
