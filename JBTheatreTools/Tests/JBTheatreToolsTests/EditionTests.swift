import XCTest
@testable import JBTheatreTools

/// --edition on the command line, and the per-edition file names in `--list --json` (same shape as the Windows CLI).
final class EditionTests: XCTestCase {
    private func pdf() -> CatalogApp {
        CatalogApp(id: "pdftools", name: "PDF Tools", blurb: "", category: nil, whatsNew: nil, whatsNewVersion: nil,
                   owner: "o", repo: "PDFTools",
                   assets: ["macos": "PDFTools-macOS.zip", "windows-x64": "PDFTools-Windows-x64.exe"],
                   variants: [AppVariant(id: "light", label: "Light", assets: ["macos": "PDFTools-macOS.zip", "windows-x64": "PDFTools-Windows-x64.exe"]),
                              AppVariant(id: "full", label: "Full", assets: ["macos-arm64": "PDFTools-Full-macOS-arm64.zip",
                                                                             "windows-x64": "PDFTools-Full-Windows-x64.zip"])])
    }

    func testAnEditionIsNamedByIdOrLabel() {
        let app = pdf()
        XCTAssertEqual(app.resolveEdition(nil), .some(nil))
        XCTAssertEqual(app.resolveEdition(""), .some(nil))
        XCTAssertEqual(app.resolveEdition("full"), .some("full"))
        XCTAssertEqual(app.resolveEdition("FULL"), .some("full"))
        XCTAssertEqual(app.resolveEdition("Light"), .some("light"))
        XCTAssertNil(app.resolveEdition("pro") as String??)
        XCTAssertEqual(app.installKey(variantId: "full"), "pdftools@full")
    }

    func testALegacyIdAndASingleEditionApp() {
        let ndi = CatalogApp(id: "nditools", name: "NDI Tools", blurb: "", category: nil, whatsNew: nil, whatsNewVersion: nil,
                             owner: "o", repo: "r", assets: [:],
                             variants: [AppVariant(id: "standard", label: "Light", assets: [:]), AppVariant(id: "full", label: "Full", assets: [:])])
        XCTAssertEqual(ndi.resolveEdition("light"), .some("standard"))
        XCTAssertEqual(ndi.editionChoices, "light (standard), full")
        let one = CatalogApp(id: "dmxtools", name: "DMX Tools", blurb: "", category: nil, whatsNew: nil, whatsNewVersion: nil,
                             owner: "o", repo: "r", assets: [:], variants: nil)
        XCTAssertNil(one.resolveEdition("full") as String??)
        XCTAssertEqual(one.resolveEdition(nil), .some(nil))
    }

    func testListEntryGivesEveryPlatformsFileNamePerEdition() throws {
        let e = CLI.listEntry(pdf(), installed: "v0.10.0")
        XCTAssertEqual(e["id"] as? String, "pdftools")
        XCTAssertEqual(e["installed"] as? String, "v0.10.0")
        XCTAssertEqual((e["assets"] as? [String: String])?["windows-x64"], "PDFTools-Windows-x64.exe")
        let editions = try XCTUnwrap(e["editions"] as? [[String: Any]])
        XCTAssertEqual(editions.map { $0["id"] as? String }, ["light", "full"])
        XCTAssertEqual(editions.map { $0["default"] as? Bool }, [true, false])
        XCTAssertEqual(editions.map { $0["slot"] as? String }, ["pdftools", "pdftools@full"])
        XCTAssertEqual((editions[1]["assets"] as? [String: String])?["windows-x64"], "PDFTools-Full-Windows-x64.zip")
        // Serialisable as the command prints it.
        XCTAssertNoThrow(try JSONSerialization.data(withJSONObject: e))
        let single = CLI.listEntry(CatalogApp(id: "dmxtools", name: "DMX", blurb: "", category: nil, whatsNew: nil, whatsNewVersion: nil,
                                              owner: "o", repo: "r", assets: ["macos": "DMX.zip"], variants: nil), installed: nil)
        XCTAssertTrue(single["editions"] is NSNull)
        XCTAssertTrue(single["installed"] is NSNull)
    }

    // MARK: - "Is it open?" ignores headless helpers (connectors, back up / restore, self-check)

    func testHeadlessHelpersAreNotOpen() {
        XCTAssertTrue(InstallGuard.isHeadlessHelper(["--mcp"]))
        XCTAssertTrue(InstallGuard.isHeadlessHelper(["--settings-export", "/tmp/x.json"]))
        XCTAssertTrue(InstallGuard.isHeadlessHelper(["--SelfCheck"]))
        XCTAssertFalse(InstallGuard.isHeadlessHelper([]))
        XCTAssertFalse(InstallGuard.isHeadlessHelper(["/Users/x/show.xlsx"]))
        XCTAssertFalse(InstallGuard.isHeadlessHelper(["--mcpx"]))
    }

    func testProcessArgumentsReadsARunningProcess() throws {
        let args = try XCTUnwrap(InstallGuard.processArguments(getpid()))
        XCTAssertEqual(args, Array(CommandLine.arguments.dropFirst()))
    }
}
