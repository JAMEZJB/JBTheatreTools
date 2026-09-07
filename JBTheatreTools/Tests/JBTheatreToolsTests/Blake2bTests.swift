import XCTest
@testable import JBTheatreTools

/// BLAKE2b-512 against the RFC 7693 known answers and Python's hashlib on block boundaries.
final class Blake2bTests: XCTestCase {
    private func hex(_ d: Data) -> String { d.map { String(format: "%02x", $0) }.joined() }
    private func bytes(_ n: Int) -> Data { Data((0..<n).map { UInt8($0 % 256) }) }

    func testRFC7693KnownAnswers() {
        XCTAssertEqual(hex(Blake2b.hash512(Data())),
            "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce")
        XCTAssertEqual(hex(Blake2b.hash512(Data("abc".utf8))),
            "ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923")
    }

    func testBlockBoundariesMatchPythonHashlib() {
        // Exactly one 128-byte block (must be compressed as the FINAL block, not a full one).
        XCTAssertEqual(hex(Blake2b.hash512(bytes(128))),
            "2319e3789c47e2daa5fe807f61bec2a1a6537fa03f19ff32e87eecbfd64b7e0e8ccff439ac333b040f19b0c4ddd11a61e24ac1fe0f10a039806c5dcc0da3d115")
        // One full block + 1 byte.
        XCTAssertEqual(hex(Blake2b.hash512(bytes(129))),
            "f59711d44a031d5f97a9413c065d1e614c417ede998590325f49bad2fd444d3e4418be19aec4e11449ac1a57207898bc57d76a1bcf3566292c20c683a5c4648f")
        // Exactly two blocks.
        XCTAssertEqual(hex(Blake2b.hash512(bytes(256))),
            "1ecc896f34d3f9cac484c73f75f6a5fb58ee6784be41b35f46067b9c65c63a6794d3d744112c653f73dd7deb6666204c5a9bfa5b46081fc10fdbe7884fa5cbf8")
        // Two full blocks + a partial one.
        XCTAssertEqual(hex(Blake2b.hash512(bytes(300))),
            "d9cf5983dc6b34c0fa1f0226926855ad3eccd2bcdcd8f8053b9a80664d33b5afcc32fd21c70ea14f4ef50ca97c3203c4d1803159f0e01bb6cb1d1c83db52b63c")
    }
}
