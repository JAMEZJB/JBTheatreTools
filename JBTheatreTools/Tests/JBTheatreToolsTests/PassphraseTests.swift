import XCTest
@testable import JBTheatreTools

/// Verifies the passphrase reduction rule (case-insensitive; spaces & punctuation ignored). Uses only
/// SYNTHETIC inputs — the real suite phrases are secrets and must never appear in this public repo. The
/// server applies the same rule to its private allow-list.
final class PassphraseTests: XCTestCase {
    func testLowercases() {
        XCTAssertEqual(Passphrase.normalize("HELLO"), "hello")
        XCTAssertEqual(Passphrase.normalize("HeLLo"), "hello")
    }

    func testStripsSpacesAndPunctuation() {
        XCTAssertEqual(Passphrase.normalize("hello world"), "helloworld")
        XCTAssertEqual(Passphrase.normalize("  hello,  world!  "), "helloworld")
        XCTAssertEqual(Passphrase.normalize("don't panic"), "dontpanic")   // apostrophe dropped
        XCTAssertEqual(Passphrase.normalize("a-b_c.d"), "abcd")
    }

    func testCaseAndSpacingVariantsCollapseToOneToken() {
        let variants = ["Foo Bar Baz", "foo bar baz", "FOOBARBAZ", "  foo,bar.baz  ", "Foo-Bar-Baz"]
        let normalized = Set(variants.map(Passphrase.normalize))
        XCTAssertEqual(normalized, ["foobarbaz"])   // every spelling reduces to the same token
    }

    func testKeepsDigits() {
        XCTAssertEqual(Passphrase.normalize("Studio 54"), "studio54")
    }

    func testAllPunctuationYieldsEmpty() {
        XCTAssertEqual(Passphrase.normalize("!!! ,.- "), "")   // nothing to match → simply won't authenticate
    }
}
