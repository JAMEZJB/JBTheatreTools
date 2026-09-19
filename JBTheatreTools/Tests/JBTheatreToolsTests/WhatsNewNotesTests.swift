import XCTest
@testable import JBTheatreTools

/// The relay-served, editable "New in" lines: parsing, sanitising, overlay semantics and URL derivation.
final class WhatsNewNotesTests: XCTestCase {
    private func app(_ id: String, whatsNew: String? = "bundled line", version: String? = "v1.0.0") -> CatalogApp {
        CatalogApp(id: id, name: id, blurb: "", category: nil, whatsNew: whatsNew, whatsNewVersion: version,
                   owner: "o", repo: "r", assets: [:], variants: nil)
    }

    func testParsesDocumentAndOverlays() throws {
        let json = #"{"schemaVersion":1,"apps":{"a":{"whatsNew":"Fresh line","whatsNewVersion":"v2.0.0"}}}"#
        let notes = try WhatsNewNotes.parse(Data(json.utf8))
        let a = notes.resolved(for: app("a"))
        XCTAssertEqual(a.whatsNew, "Fresh line"); XCTAssertEqual(a.whatsNewVersion, "v2.0.0")
        let b = notes.resolved(for: app("b"))          // not in the document → bundled line stands
        XCTAssertEqual(b.whatsNew, "bundled line"); XCTAssertEqual(b.whatsNewVersion, "v1.0.0")
    }

    func testEmptyLineHidesRowLine() throws {
        let notes = try WhatsNewNotes.parse(Data(#"{"apps":{"a":{"whatsNew":"   "}}}"#.utf8))
        XCTAssertNil(notes.resolved(for: app("a")).whatsNew)
        XCTAssertNil(notes.resolved(for: app("a")).whatsNewVersion)
    }

    func testMalformedEntriesAreSkippedNotFatal() throws {
        let json = #"{"apps":{"a":"nope","b":{"whatsNewVersion":"v1"},"c":{"whatsNew":"ok","whatsNewVersion":7}}}"#
        let notes = try WhatsNewNotes.parse(Data(json.utf8))
        XCTAssertNil(notes["a"]); XCTAssertNil(notes["b"])
        XCTAssertEqual(notes["c"]?.whatsNew, "ok"); XCTAssertNil(notes["c"]?.whatsNewVersion)
    }

    func testRejectsWrongShapeAndNewerSchema() {
        XCTAssertThrowsError(try WhatsNewNotes.parse(Data("<html>redirected</html>".utf8)))
        XCTAssertThrowsError(try WhatsNewNotes.parse(Data(#"{"notes":{}}"#.utf8)))
        XCTAssertThrowsError(try WhatsNewNotes.parse(Data(#"[]"#.utf8)))
        XCTAssertThrowsError(try WhatsNewNotes.parse(Data(#"{"schemaVersion":2,"apps":{}}"#.utf8)))
    }

    func testCleansControlCharsWhitespaceAndLength() {
        XCTAssertEqual(WhatsNewNotes.clean("  a\u{0}b \n\t c  ", max: 100), "ab c")
        XCTAssertNil(WhatsNewNotes.clean(" \u{7} ", max: 100))
        let long = String(repeating: "x", count: 200)
        let capped = WhatsNewNotes.clean(long, max: 160)!
        XCTAssertEqual(capped.count, 161); XCTAssertTrue(capped.hasSuffix("…"))
        XCTAssertEqual(WhatsNewNotes.clean("v1.2.3", max: 24), "v1.2.3")
    }

    func testNotesURLIsTheRelayOriginNotTheApiRoot() {
        XCTAssertEqual(WhatsNewNotes.url(relayBase: "https://jbtheatretools.jamesbreedon.com/ghapi")?.absoluteString,
                       "https://jbtheatretools.jamesbreedon.com/notes/whats-new.json")
        XCTAssertEqual(WhatsNewNotes.url(relayBase: "https://x.jamesbreedon.com:8443/ghapi/")?.absoluteString,
                       "https://x.jamesbreedon.com:8443/notes/whats-new.json")
        XCTAssertNil(WhatsNewNotes.url(relayBase: "not a url"))
    }

    func testRowStartsWithBundledLineAndOverlayUpdatesIt() throws {
        let row = AppState.Row(app: app("a"))
        XCTAssertEqual(row.whatsNew, "bundled line")
        let notes = try WhatsNewNotes.parse(Data(#"{"apps":{"a":{"whatsNew":"New","whatsNewVersion":"v9"}}}"#.utf8))
        let r = notes.resolved(for: row.app)
        row.whatsNew = r.whatsNew; row.whatsNewVersion = r.whatsNewVersion
        XCTAssertEqual(row.whatsNew, "New"); XCTAssertEqual(row.whatsNewVersion, "v9")
    }
}
