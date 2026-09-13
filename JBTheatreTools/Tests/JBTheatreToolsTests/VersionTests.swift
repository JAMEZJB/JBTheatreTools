import XCTest
@testable import JBTheatreTools

/// Version comparison and "latest release" selection — the logic behind Update / Up to date.
final class VersionTests: XCTestCase {
    private func rel(_ tag: String, pre: Bool = false) -> ReleaseInfo {
        ReleaseInfo(tagName: tag, assets: [], prerelease: pre, draft: false)
    }

    func testNumericNotStringComparison() {
        XCTAssertTrue(AppState.versionIsNewer("v1.10.0", than: "v1.9.9"))   // a string compare would say 1.10 < 1.9
        XCTAssertFalse(AppState.versionIsNewer("1.2", than: "1.2.0"))       // same version, different segment count
        XCTAssertFalse(AppState.versionIsNewer("1.2.0", than: "1.2"))
        XCTAssertTrue(AppState.versionIsNewer("2.0.0", than: "1.99.99"))
        XCTAssertFalse(AppState.versionIsNewer("1.0.0", than: "1.0.0"))
        XCTAssertFalse(AppState.versionIsNewer("1.0.0", than: "1.0.1"))     // never offers a downgrade
    }

    func testPrefixAndSuffixTolerance() {
        XCTAssertTrue(AppState.versionIsNewer("V1.1.0", than: "v1.0.0"))
        XCTAssertTrue(AppState.versionIsNewer("1.1.0-beta", than: "1.0.0")) // numeric prefix of each segment
    }

    /// Date-style rolling tags (Convert ships `build-YYYYMMDD`, not semver): the digit run inside the
    /// segment must order them, and `latest(...)` must pick the newest build (not an arbitrary one).
    func testDateTaggedBuildsCompareByDate() {
        XCTAssertTrue(AppState.versionIsNewer("build-20260926", than: "build-20260912"))
        XCTAssertFalse(AppState.versionIsNewer("build-20260912", than: "build-20260926"))
        XCTAssertFalse(AppState.versionIsNewer("build-20260912", than: "build-20260912"))
        XCTAssertEqual(
            AppState.latest(from: [rel("build-20260912"), rel("build-20260926"), rel("build-20260826")])?.tagName,
            "build-20260926")
    }

    func testLatestPrefersHighestStableRegardlessOfOrder() {
        let latest = AppState.latest(from: [rel("v1.2.0"), rel("v1.10.0"), rel("v1.9.0"), rel("v2.0.0", pre: true)])
        XCTAssertEqual(latest?.tagName, "v1.10.0")   // highest STABLE; the newer prerelease is ignored
    }

    func testLatestFallsBackToPrereleasesOnlyWhenNothingElse() {
        XCTAssertEqual(AppState.latest(from: [rel("v0.1.0", pre: true), rel("v0.2.0", pre: true)])?.tagName, "v0.2.0")
        XCTAssertNil(AppState.latest(from: []))
    }
}
