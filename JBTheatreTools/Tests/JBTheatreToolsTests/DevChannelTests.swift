import XCTest
@testable import JBTheatreTools

/// Semver pre-release ordering and the Dev-channel release pick.
final class DevChannelTests: XCTestCase {
    private func rel(_ tag: String, pre: Bool = false, assets: [String] = []) -> ReleaseInfo {
        let list = assets.enumerated().map { #"{"id":\#($0.offset + 1),"name":"\#($0.element)","size":1}"# }.joined(separator: ",")
        return try! JSONDecoder().decode(ReleaseInfo.self, from: Data(#"{"tag_name":"\#(tag)","prerelease":\#(pre),"draft":false,"assets":[\#(list)]}"#.utf8))
    }

    /// PDF Tools v0.9.2-dev.1 ships only the Light zip: with dev on, Light gets the dev build and Full stays on
    /// its release (it used to read "No macOS build").
    func testDevPickIsPerEdition() {
        let list = [rel("v0.9.1", assets: ["PDFTools-macOS.zip", "PDFTools-Full-macOS-arm64.zip"]),
                    rel("v0.9.2-dev.1", pre: true, assets: ["PDFTools-macOS.zip"])]
        XCTAssertEqual(AppState.latest(from: list, for: "PDFTools-macOS.zip", devChannel: true)?.tagName, "v0.9.2-dev.1")
        XCTAssertEqual(AppState.latest(from: list, for: "PDFTools-Full-macOS-arm64.zip", devChannel: true)?.tagName, "v0.9.1")
        XCTAssertEqual(AppState.latest(from: list, for: "PDFTools-Full-macOS-arm64.zip", devChannel: false)?.tagName, "v0.9.1")
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
