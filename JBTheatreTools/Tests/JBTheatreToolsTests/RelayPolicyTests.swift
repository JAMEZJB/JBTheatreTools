import XCTest
@testable import JBTheatreTools

/// F2: the invisible relay-URL override must be clamped to https on a jamesbreedon.com host, so a stray
/// `defaults write` can't redirect the passphrase to an attacker (or crash the client with a bad URL).
final class RelayPolicyTests: XCTestCase {
    func testAllowsHttpsJamesbreedonHosts() {
        XCTAssertEqual(RelayPolicy.validatedOverride("https://jbtheatretools.jamesbreedon.com/ghapi"),
                       "https://jbtheatretools.jamesbreedon.com/ghapi")
        XCTAssertEqual(RelayPolicy.validatedOverride("https://jamesbreedon.com/ghapi"), "https://jamesbreedon.com/ghapi")
        XCTAssertEqual(RelayPolicy.validatedOverride("  https://relay2.jamesbreedon.com/ghapi  "),
                       "https://relay2.jamesbreedon.com/ghapi")   // trimmed
    }

    func testRejectsEverythingElse() {
        for bad in [
            nil, "", "   ",
            "http://jbtheatretools.jamesbreedon.com/ghapi",     // not https → passphrase in cleartext
            "https://evil.example/ghapi",                       // wrong host
            "https://jamesbreedon.com.evil.com/ghapi",          // suffix trick
            "https://eviljamesbreedon.com/ghapi",               // no dot before the domain
            "https://jamesbreedon.com evil",                    // space → URL(string:) still parses? must not slip through
            "not a url",
            "ftp://jamesbreedon.com/x",
        ] as [String?] {
            XCTAssertNil(RelayPolicy.validatedOverride(bad), "should reject \(bad ?? "nil")")
        }
    }
}
