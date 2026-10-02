import XCTest
@testable import JBTheatreTools

/// Which download failures a batch retries once (the Windows launcher's IsTransient covers the same ground).
final class TransientErrorTests: XCTestCase {
    func testNetworkHiccupsAreRetried() {
        XCTAssertTrue(AppState.isTransient(URLError(.timedOut)))
        XCTAssertTrue(AppState.isTransient(URLError(.networkConnectionLost)))
        XCTAssertTrue(AppState.isTransient(URLError(.notConnectedToInternet)))
        XCTAssertTrue(AppState.isTransient(NSError(domain: NSPOSIXErrorDomain, code: Int(ECONNRESET))))
    }

    func testCancelsRefusalsAndVerificationFailuresAreNot() {
        XCTAssertFalse(AppState.isTransient(URLError(.cancelled)))
        XCTAssertFalse(AppState.isTransient(URLError(.userAuthenticationRequired)))
        XCTAssertFalse(AppState.isTransient(NSError(domain: "JBTT", code: 1, userInfo: [NSLocalizedDescriptionKey: "sha256 mismatch"])))
        XCTAssertFalse(AppState.isTransient(CocoaError(.fileWriteNoPermission)))
    }
}
