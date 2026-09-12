import XCTest
@testable import JBTheatreTools

/// F8: an API-supplied tag/asset name used as a local filename component must not traverse or hide.
final class PathSafeTests: XCTestCase {
    func testKeepsLegitimateNames() {
        XCTAssertEqual(PathSafe.component("v1.2.3"), "v1.2.3")
        XCTAssertEqual(PathSafe.component("JBTheatreTools-macOS.zip"), "JBTheatreTools-macOS.zip")
        XCTAssertEqual(PathSafe.component("NDITools-Full-macOS-arm64.zip"), "NDITools-Full-macOS-arm64.zip")
    }

    func testNeutralisesTraversalHiddenAndSeparators() {
        for hostile in ["../../Library/LaunchAgents/x", "..\\..\\x", "/etc/passwd", ".ssh", "a/b/c", "a:b"] {
            let out = PathSafe.component(hostile)
            XCTAssertFalse(out.contains("/"), out)
            XCTAssertFalse(out.contains("\\"), out)
            XCTAssertFalse(out.contains(":"), out)
            XCTAssertFalse(out.hasPrefix("."), "must not start with a dot: \(out)")
        }
        XCTAssertEqual(PathSafe.component(""), "_")
        XCTAssertEqual(PathSafe.component("../x"), "_.._x")
    }
}
