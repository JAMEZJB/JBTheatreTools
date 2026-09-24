import XCTest
@testable import JBTheatreTools

/// Semver pre-release ordering and the Dev-channel release pick.
final class DevChannelTests: XCTestCase {
    private func rel(_ tag: String, pre: Bool = false) -> ReleaseInfo {
        try! JSONDecoder().decode(ReleaseInfo.self, from: Data(#"{"tag_name":"\#(tag)","prerelease":\#(pre),"draft":false,"assets":[]}"#.utf8))
    }

    func testPreReleasesSortBeforeTheirRelease() {
        XCTAssertTrue(AppState.versionIsNewer("0.1.0-dev.2", than: "0.1.0-dev.1"))
        XCTAssertTrue(AppState.versionIsNewer("v0.1.0", than: "v0.1.0-dev.2"))
        XCTAssertTrue(AppState.versionIsNewer("0.1.0-dev.1", than: "0.0.9"))
        XCTAssertTrue(AppState.versionIsNewer("0.1.0-dev.10", than: "0.1.0-dev.9"))
        XCTAssertFalse(AppState.versionIsNewer("1.2", than: "1.2.0"))
        XCTAssertFalse(AppState.versionIsNewer("1.2.0", than: "1.2"))
        XCTAssertTrue(AppState.versionIsNewer("build-20260921", than: "build-20260915"))
        XCTAssertTrue(AppState.isDevTag("v0.1.0-dev.1"))
        XCTAssertFalse(AppState.isDevTag("v0.1.0"))
    }

    func testDevChannelOffNeverPicksADevBuild() {
        XCTAssertEqual(AppState.latest(from: [rel("v0.2.0-dev.1", pre: true), rel("v0.1.0")], devChannel: false)?.tagName, "v0.1.0")
        XCTAssertNil(AppState.latest(from: [rel("v0.1.0-dev.1", pre: true)], devChannel: false))
        XCTAssertEqual(AppState.latest(from: [rel("v0.1.0-rc1", pre: true)], devChannel: false)?.tagName, "v0.1.0-rc1")
    }

    func testDevChannelOnPicksTheHighestIncludingDevBuilds() {
        let list = [rel("v0.1.0"), rel("v0.2.0-dev.1", pre: true), rel("v0.2.0-dev.2", pre: true)]
        XCTAssertEqual(AppState.latest(from: list, devChannel: true)?.tagName, "v0.2.0-dev.2")
        XCTAssertEqual(AppState.latest(from: list + [rel("v0.2.0")], devChannel: true)?.tagName, "v0.2.0")
    }
}
