import XCTest
@testable import JBTheatreTools

/// How far the releases list is read: one page unless a long run of development builds has pushed the last full
/// release off it. No network: the pages are handed in.
final class ReleasePagingTests: XCTestCase {
    private func rel(_ tag: String, pre: Bool = false, draft: Bool = false) -> ReleaseInfo {
        ReleaseInfo(tagName: tag, assets: [], prerelease: pre, draft: draft)
    }

    private func devBuilds(_ range: ClosedRange<Int>) -> [ReleaseInfo] {
        range.reversed().map { rel("v2.0.0-dev.\($0)", pre: true) }
    }

    private struct Down: Error {}

    /// Serves `pages` (1-based) and records which were asked for.
    private final class Pages {
        var asked: [Int] = []
        let pages: [[ReleaseInfo]]
        var failFrom = Int.max
        init(_ pages: [[ReleaseInfo]]) { self.pages = pages }
        func fetch(_ page: Int) async throws -> [ReleaseInfo] {
            asked.append(page)
            if page >= failFrom { throw Down() }
            return page <= pages.count ? pages[page - 1] : []
        }
    }

    func testOnePageIsEnoughWhenItHoldsAFullRelease() async throws {
        let first = devBuilds(2...50) + [rel("v1.9.0")]   // a full page of 50
        let pages = Pages([first, [rel("v1.8.0")]])
        let all = try await ReleasePaging.collect(fetch: pages.fetch)
        XCTAssertEqual(pages.asked, [1])
        XCTAssertEqual(all.count, 50)
    }

    func testAShortFirstPageIsNeverFollowed() async throws {
        let pages = Pages([devBuilds(1...12)])
        let all = try await ReleasePaging.collect(fetch: pages.fetch)
        XCTAssertEqual(pages.asked, [1])
        XCTAssertEqual(all.count, 12)
    }

    func testMoreThanAPageOfDevelopmentBuildsStillFindsTheLastFullRelease() async throws {
        // 60 development builds since v1.9.0: the first page is all development builds.
        let pages = Pages([devBuilds(11...60), devBuilds(1...10) + [rel("v1.9.0"), rel("v1.8.0")]])
        let all = try await ReleasePaging.collect(fetch: pages.fetch)
        XCTAssertEqual(pages.asked, [1, 2])
        XCTAssertEqual(all.count, 62)
        XCTAssertEqual(AppState.latest(from: all, devChannel: false)?.tagName, "v1.9.0")
        XCTAssertNil(AppState.latest(from: Array(all.prefix(50)), devChannel: false),
                     "the first page alone has no release for a launcher that isn't on development builds")
    }

    func testAPreReleaseOrADraftDoesNotCountAsAFullRelease() async throws {
        let first = devBuilds(3...50) + [rel("v1.9.0-rc.1", pre: true), rel("v1.9.0", draft: true)]
        let pages = Pages([first, [rel("v1.8.0")]])
        let all = try await ReleasePaging.collect(fetch: pages.fetch)
        XCTAssertEqual(pages.asked, [1, 2])
        XCTAssertFalse(all.contains { $0.draft })
        XCTAssertEqual(all.last?.tagName, "v1.8.0")
    }

    func testReadingStopsAtThePageLimit() async throws {
        let pages = Pages((0..<5).map { p in devBuilds((p * 50 + 1)...(p * 50 + 50)) })
        let all = try await ReleasePaging.collect(maxPages: 3, fetch: pages.fetch)
        XCTAssertEqual(pages.asked, [1, 2, 3])
        XCTAssertEqual(all.count, 150)
    }

    func testAFailedLaterPageKeepsWhatTheFirstGave() async throws {
        let pages = Pages([devBuilds(1...50), [rel("v1.9.0")]])
        pages.failFrom = 2
        let all = try await ReleasePaging.collect(fetch: pages.fetch)
        XCTAssertEqual(pages.asked, [1, 2])
        XCTAssertEqual(all.count, 50)
    }

    func testAFailedFirstPageIsAnError() async {
        let pages = Pages([])
        pages.failFrom = 1
        do {
            _ = try await ReleasePaging.collect(fetch: pages.fetch)
            XCTFail("expected the error")
        } catch { XCTAssertTrue(error is Down) }
    }

    func testATagThatComesBackOnTheNextPageIsListedOnce() async throws {
        // A release published between the two reads moves the list down one.
        let pages = Pages([devBuilds(11...60), devBuilds(10...11) + [rel("v1.9.0")]])
        let all = try await ReleasePaging.collect(fetch: pages.fetch)
        XCTAssertEqual(all.filter { $0.tagName == "v2.0.0-dev.11" }.count, 1)
        XCTAssertEqual(all.count, 52)
    }
}
