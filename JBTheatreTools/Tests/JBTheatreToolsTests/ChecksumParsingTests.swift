import XCTest
@testable import JBTheatreTools

/// SHA256SUMS parsing — the shapes `sha256sum`/`shasum` actually produce, incl. Windows line endings.
final class ChecksumParsingTests: XCTestCase {
    func testStandardShasumFormat() {
        let sums = "abc123  App-macOS.zip\ndef456  App-Windows-x64.exe\n"
        XCTAssertEqual(InstallManager.expectedSHA256(forAsset: "App-macOS.zip", inSums: sums), "abc123")
        XCTAssertEqual(InstallManager.expectedSHA256(forAsset: "App-Windows-x64.exe", inSums: sums), "def456")
        XCTAssertNil(InstallManager.expectedSHA256(forAsset: "Missing.zip", inSums: sums))
    }

    func testWindowsLineEndingsBinaryMarkerAndTabs() {
        let sums = "abc123 *App-macOS.zip\r\ndef456\tApp-Windows-x64.exe\r\n"
        XCTAssertEqual(InstallManager.expectedSHA256(forAsset: "App-macOS.zip", inSums: sums), "abc123")      // "*" binary marker
        XCTAssertEqual(InstallManager.expectedSHA256(forAsset: "App-Windows-x64.exe", inSums: sums), "def456") // tab separator
    }

    func testNameMustMatchExactly() {
        let sums = "abc123  App-macOS.zip\n"
        XCTAssertNil(InstallManager.expectedSHA256(forAsset: "App-macOS", inSums: sums))
        XCTAssertNil(InstallManager.expectedSHA256(forAsset: "app-macos.zip", inSums: sums))
        XCTAssertNil(InstallManager.expectedSHA256(forAsset: "App-macOS.zip", inSums: ""))
    }
}
