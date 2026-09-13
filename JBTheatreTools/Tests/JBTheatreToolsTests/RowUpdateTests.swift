import XCTest
@testable import JBTheatreTools

/// F3: after an `await`, a row must be addressed by id — a concurrent drag/Move can permute `rows`, so a
/// captured index would write to the wrong app. `AppState.write` re-finds by id.
final class RowUpdateTests: XCTestCase {
    private func app(_ id: String) -> CatalogApp {
        CatalogApp(id: id, name: id, blurb: "", category: nil, whatsNew: nil, whatsNewVersion: nil,
                   owner: "o", repo: id, assets: [:], variants: nil)
    }

    func testWriteHitsRowByIdAfterReorder() {
        var rows = [AppState.Row(app: app("a")), AppState.Row(app: app("b")), AppState.Row(app: app("c"))]
        rows.reverse()   // a drag permuted rows mid-flight → now [c, b, a]; "a" is no longer at index 0
        AppState.write(into: &rows, id: "a") { $0.latest = "9.9.9"; $0.busy = true }
        XCTAssertEqual(rows.first { $0.id == "a" }?.latest, "9.9.9")
        XCTAssertEqual(rows.first { $0.id == "a" }?.busy, true)
        // The row that now sits at "a"'s old index (c) must be untouched.
        XCTAssertNil(rows.first { $0.id == "c" }?.latest)
        XCTAssertEqual(rows.first { $0.id == "c" }?.busy, false)
        XCTAssertNil(rows.first { $0.id == "b" }?.latest)
    }

    func testWriteMissingIdIsNoOp() {
        var rows = [AppState.Row(app: app("a"))]
        AppState.write(into: &rows, id: "does-not-exist") { $0.busy = true }
        XCTAssertEqual(rows[0].busy, false)
    }
}
