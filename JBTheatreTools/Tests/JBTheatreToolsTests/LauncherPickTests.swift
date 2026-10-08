import XCTest
@testable import JBTheatreTools

/// Which launcher release is offered: only one that carries this platform's build (same cases as Windows / Android).
final class LauncherPickTests: XCTestCase {
    private let mac = "JBTheatreTools-macOS.zip"
    private func r(_ tag: String, _ pre: Bool, _ assets: String...) -> ReleaseInfo {
        ReleaseInfo(tagName: tag, assets: assets.enumerated().map { ReleaseAsset(id: $0.offset, name: $0.element, size: 1) },
                    prerelease: pre, draft: false)
    }
    private func pick(_ current: String, dev: Bool, _ rs: [ReleaseInfo]) -> String? {
        AppState.pickLauncher(rs, hasBuild: { $0.assets.contains { $0.name == self.mac } }, current: current, devChannel: dev)?.tagName
    }

    func testADevBuildWithoutThisPlatformsBuildIsSkipped() {
        let winOnly = r("v1.32.1-dev.1", true, "JBTheatreTools-Windows-x64.exe")
        let release = r("v1.32.0", false, mac, "JBTheatreTools-Windows-x64.exe")
        XCTAssertNil(pick("1.32.0", dev: true, [winOnly, release]))
        XCTAssertEqual(pick("1.31.0", dev: true, [winOnly, release]), "v1.32.0")
    }

    func testNewestBuildCarryingReleaseWins() {
        let a = r("v1.32.1-dev.2", true, mac), b = r("v1.32.1-dev.1", true, "JBTheatreTools-Windows-x64.exe")
        XCTAssertEqual(pick("1.32.0", dev: true, [b, a]), "v1.32.1-dev.2")
        XCTAssertNil(pick("1.32.0", dev: false, [b, a]))
    }

    func testBackToTheReleaseFromADevBuild() {
        let release = r("v1.32.0", false, mac)
        XCTAssertEqual(pick("1.32.1-dev.3", dev: false, [release]), "v1.32.0")
        XCTAssertNil(pick("1.32.1-dev.3", dev: true, [release]))
        XCTAssertNil(pick("1.32.0", dev: false, [r("v1.32.0", false, "JBTheatreTools-Windows-x64.exe")]))
    }
}
