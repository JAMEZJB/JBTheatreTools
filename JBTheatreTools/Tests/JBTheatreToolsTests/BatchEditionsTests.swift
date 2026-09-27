import XCTest
@testable import JBTheatreTools

/// Which editions Download All works on. The Windows launcher carries the same cases.
final class BatchEditionsTests: XCTestCase {
    private let lightFull = ["light", "full"]
    private func installed(_ slots: String?...) -> (String?) -> Bool { { slots.contains($0) } }

    func testSingleEditionAppIsOneSlot() {
        XCTAssertEqual(BatchEditions.forDownloadAll(nil, shown: nil, includeFull: false, isInstalled: installed()), [nil])
        XCTAssertEqual(BatchEditions.forDownloadAll(["only"], shown: "only", includeFull: true, isInstalled: installed()), [nil])
    }

    func testRowShowingLightFetchesLight() {
        XCTAssertEqual(BatchEditions.forDownloadAll(lightFull, shown: "light", includeFull: false, isInstalled: installed()), ["light"])
    }

    func testRowSwitchedToFullFetchesFullNotLight() {
        XCTAssertEqual(BatchEditions.forDownloadAll(lightFull, shown: "full", includeFull: false, isInstalled: installed()), ["full"])
    }

    func testInstalledEditionsStayIncludedWhateverTheRowShows() {
        XCTAssertEqual(BatchEditions.forDownloadAll(lightFull, shown: "full", includeFull: false, isInstalled: installed("light")), ["light", "full"])
        XCTAssertEqual(BatchEditions.forDownloadAll(lightFull, shown: "light", includeFull: false, isInstalled: installed("full")), ["light", "full"])
    }

    func testPlusFullEditionsTakesEveryEdition() {
        XCTAssertEqual(BatchEditions.forDownloadAll(lightFull, shown: "light", includeFull: true, isInstalled: installed()), ["light", "full"])
    }

    func testUnknownOrMissingChoiceFallsBackToTheFirstEdition() {
        XCTAssertEqual(BatchEditions.forDownloadAll(lightFull, shown: nil, includeFull: false, isInstalled: installed()), ["light"])
        XCTAssertEqual(BatchEditions.forDownloadAll(lightFull, shown: "gone", includeFull: false, isInstalled: installed()), ["light"])
    }
}
