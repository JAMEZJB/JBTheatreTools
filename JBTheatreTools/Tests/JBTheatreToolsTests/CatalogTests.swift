import XCTest
@testable import JBTheatreTools

/// Catalog decoding, the per-variant install-slot rules, and per-arch macOS asset selection.
final class CatalogTests: XCTestCase {
    private let json = """
    {"schemaVersion":1,"apps":[
      {"id":"plain","name":"Plain","blurb":"","owner":"o","repo":"Plain",
       "assets":{"macos":"Plain-macOS.zip","windows-x64":"Plain-Windows-x64.exe"}},
      {"id":"ndi","name":"NDI Tools","blurb":"","owner":"o","repo":"NDITools",
       "assets":{"macos":"NDITools-macOS.zip"},
       "variants":[
         {"id":"standard","label":"Standard","assets":{"macos":"NDITools-macOS.zip"}},
         {"id":"full","label":"Full","assets":{"macos-arm64":"NDITools-Full-macOS-arm64.zip","macos-x64":"NDITools-Full-macOS-x64.zip"}}
       ]}
    ]}
    """
    private func load() throws -> Catalog { try JSONDecoder().decode(Catalog.self, from: Data(json.utf8)) }

    func testVariantsDecodeAndDefault() throws {
        let c = try load()
        XCTAssertFalse(c.apps[0].hasVariants)
        XCTAssertNil(c.apps[0].variants)
        XCTAssertTrue(c.apps[1].hasVariants)
        XCTAssertEqual(c.apps[1].variants?.map(\.id), ["standard", "full"])
        XCTAssertNil(c.apps[0].whatsNew)   // optional fields decode as nil when absent
    }

    func testInstallSlotsAreIndependent() throws {
        let ndi = try load().apps[1]
        XCTAssertEqual(ndi.installKey(variantId: nil), "ndi")            // default slot = plain id (pre-variant installs stay valid)
        XCTAssertEqual(ndi.installKey(variantId: "standard"), "ndi")
        XCTAssertEqual(ndi.installKey(variantId: "full"), "ndi@full")    // its own slot → Standard and Full coexist
        XCTAssertEqual(ndi.variantSuffix("full"), " (Full)")
        XCTAssertEqual(ndi.variantSuffix("standard"), "")
        XCTAssertTrue(ndi.isDefaultVariant(nil))
        XCTAssertFalse(ndi.isDefaultVariant("full"))
        let plain = try load().apps[0]
        XCTAssertEqual(plain.installKey(variantId: "anything"), "plain")  // no variants → always the plain id
        XCTAssertEqual(plain.variantSuffix("anything"), "")
    }

    func testVariantAssetsFallBackToDefault() throws {
        let ndi = try load().apps[1]
        XCTAssertEqual(ndi.assets(variantId: nil)["macos"], "NDITools-macOS.zip")
        XCTAssertEqual(ndi.assets(variantId: "nope")["macos"], "NDITools-macOS.zip")   // unknown id → default variant
        XCTAssertEqual(ndi.assets(variantId: "full")["macos-arm64"], "NDITools-Full-macOS-arm64.zip")
    }

    func testPerArchMacAssetSelection() throws {
        let ndi = try load().apps[1]
        let arm = MacArch.isAppleSilicon
        XCTAssertEqual(MacArch.pick(from: ndi.assets(variantId: "full")),
                       arm ? "NDITools-Full-macOS-arm64.zip" : "NDITools-Full-macOS-x64.zip")
        XCTAssertEqual(MacArch.pick(from: ["macos": "u.zip"]), "u.zip")                                   // universal fallback
        XCTAssertEqual(MacArch.pick(from: ["macos": "u.zip", "macos-arm64": "a.zip", "macos-x64": "x.zip"]),
                       arm ? "a.zip" : "x.zip")                                                            // arch key wins
        XCTAssertNil(MacArch.pick(from: ["windows-x64": "w.exe"]))
        XCTAssertEqual(ndi.macAssetName(variantId: "standard"), "NDITools-macOS.zip")
    }
}
