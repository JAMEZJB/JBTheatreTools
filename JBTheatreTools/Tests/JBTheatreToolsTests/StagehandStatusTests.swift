import XCTest
@testable import JBTheatreTools

/// "Answering presses:" when Stagehand is installed but no copy answers: what Stagehand's own record says (the suite's
/// wording); the same words as the Windows launcher. Temporary folder; nothing started.
final class StagehandStatusTests: XCTestCase {
    private var stateDir: URL!
    private let now = ISO8601DateFormatter().date(from: "2026-11-19T03:00:00Z")!

    override func setUpWithError() throws {
        stateDir = FileManager.default.temporaryDirectory.appendingPathComponent("shstatus-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: TriggerRegistration.triggersDir(stateDir), withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: stateDir) }

    func testTheWordsFollowTheSuiteRules() {
        let cases: [(String, String, String, String)] = [
            ("failed: access is denied", "2026-11-19T02:00:00+0000", "stopped", "Not running — Stagehand couldn't start it (access is denied)"),
            ("failed", "", "stopped", "Not running — Stagehand couldn't start it (no reason given)"),
            ("started (ssh)", "2026-11-19T02:00:00+0000", "restarting", "Not running — the background copy is restarting; see Stagehand"),
            ("started (ssh)", "2026-11-19T02:59:30+0000", "stopped", "Starting the background copy…"),
            ("started (ssh)", "2026-11-12T02:59:30+0000", "stopped", "Not running — open Stagehand"),
            ("started (ssh)", "not a time", "stopped", "Not running — open Stagehand"),
            ("", "", "not registered", "Not running — open Stagehand"),
        ]
        for (result, at, state, want) in cases {
            XCTAssertEqual(StagehandStatus.words(result: result, at: at, hostState: state, now: now), want, result)
        }
    }

    func testItReadsStagehandsRecordAndTheSupervisorsStatus() throws {
        let triggers = TriggerRegistration.triggersDir(stateDir)
        XCTAssertEqual(StagehandStatus.notRunning(stateDir: stateDir, now: now, alive: { _ in true }), "Not running — open Stagehand")
        try Data(#"{"at":"2026-11-19T02:00:00+0000","via":"ssh","result":"failed: no desktop session"}"#.utf8)
            .write(to: triggers.appendingPathComponent("_ensure.json"))
        XCTAssertEqual(StagehandStatus.notRunning(stateDir: stateDir, now: now, alive: { _ in true }),
                       "Not running — Stagehand couldn't start it (no desktop session)")
        try Data("{}".utf8).write(to: TriggerRegistration.registrationURL(stateDir))
        try Data(#"{"pid":4242,"hosts":{"jbtheatretools":{"state":"restarting"}}}"#.utf8)
            .write(to: triggers.appendingPathComponent("_supervisor.json"))
        XCTAssertEqual(StagehandStatus.hostState(stateDir: stateDir, alive: { _ in true }), "restarting")
        XCTAssertEqual(StagehandStatus.hostState(stateDir: stateDir, alive: { _ in false }), "stopped")
    }

    func testTheAnsweringLineCarriesIt() {
        XCTAssertEqual(LauncherActions.answering(enabled: true, windowOwns: false, background: false, stagehandInstalled: true,
                                                 notRunning: "Starting the background copy…"),
                       "Answering presses: starting the background copy…")
        XCTAssertEqual(LauncherActions.answering(enabled: true, windowOwns: false, background: false, stagehandInstalled: true),
                       "Answering presses: not running — open Stagehand.")
    }
}
