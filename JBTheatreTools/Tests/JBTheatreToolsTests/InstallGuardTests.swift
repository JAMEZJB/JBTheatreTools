import XCTest
@testable import JBTheatreTools

/// F4 (don't replace a running bundle) + F6 (don't destroy a stranger at the destination).
final class InstallGuardTests: XCTestCase {
    func testIsRunningMatchesNormalisedPaths() {
        let running = [URL(fileURLWithPath: "/Applications/Projector Control.app")]
        XCTAssertTrue(InstallGuard.isRunning(URL(fileURLWithPath: "/Applications/Projector Control.app"), amongRunning: running))
        XCTAssertTrue(InstallGuard.isRunning(URL(fileURLWithPath: "/Applications/Projector Control.app/"), amongRunning: running)) // trailing slash
        XCTAssertTrue(InstallGuard.isRunning(URL(fileURLWithPath: "/Applications/./Projector Control.app"), amongRunning: running)) // "." segment
        XCTAssertFalse(InstallGuard.isRunning(URL(fileURLWithPath: "/Applications/NDI Tools.app"), amongRunning: running))
        XCTAssertFalse(InstallGuard.isRunning(URL(fileURLWithPath: "/Applications/Projector Control.app"), amongRunning: []))
    }

    func testIsStranger() {
        // Nothing at dest → never a stranger (safe to install).
        XCTAssertFalse(InstallGuard.isStranger(destExists: false, destPath: "/Applications/NDI Tools.app", ourPath: nil))
        // dest is exactly what we recorded (replacing our own install in place) → not a stranger.
        XCTAssertFalse(InstallGuard.isStranger(destExists: true, destPath: "/Applications/NDI Tools.app",
                                               ourPath: "/Applications/NDI Tools.app"))
        // dest occupied by something we didn't install → stranger (must not be deleted).
        XCTAssertTrue(InstallGuard.isStranger(destExists: true, destPath: "/Applications/NDI Tools.app", ourPath: nil))
        XCTAssertTrue(InstallGuard.isStranger(destExists: true, destPath: "/Applications/NDI Tools.app",
                                              ourPath: "/Users/x/Library/Application Support/JBTheatreTools/apps/NDI Tools.app"))
    }
}
