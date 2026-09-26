import XCTest
@testable import JBTheatreTools

/// v1.30 feature logic — the SAME cases as the Windows Core.Tests (FeatureLogicTests.cs) and the Android
/// FeatureLogicTest.kt, so the three launchers stay word-for-word identical.
final class FeatureLogicTests: XCTestCase {
    private let utc: Calendar = { var c = Calendar(identifier: .gregorian); c.timeZone = TimeZone(identifier: "UTC")!; return c }()
    private func date(_ iso: String) -> Date { RelativeAge.parseISO(iso)! }

    func testByteSizes() {
        let cases: [(Int64, String)] = [
            (0, "0 bytes"), (1, "1 byte"), (999, "999 bytes"), (1000, "1.0 KB"), (1049, "1.0 KB"), (1050, "1.1 KB"),
            (9_949, "9.9 KB"), (9_950, "10 KB"), (12_400_000, "12 MB"), (4_200_000, "4.2 MB"), (450_000_000, "450 MB"),
            (999_499, "999 KB"), (999_500, "1.0 MB"), (1_234_000_000, "1.2 GB"), (5_000_000_000_000_000, "5000 TB"),
            (-5, "0 bytes"),
        ]
        for (bytes, expected) in cases { XCTAssertEqual(ByteSize.format(bytes), expected, "bytes=\(bytes)") }
        XCTAssertEqual(ByteSize.sum([10, -1, 0, 20]), 30)
        XCTAssertEqual(ByteSize.sum([Int64.max - 1, 5]), Int64.max)
    }

    func testRelativeAges() {
        let now = date("2026-09-25T12:00:00Z")
        let cases: [(Double, String)] = [
            (-3, "today"), (0, "today"), (0.9, "today"), (1, "yesterday"), (1.99, "yesterday"), (2, "2 days ago"),
            (6.5, "6 days ago"), (7, "1 week ago"), (13, "1 week ago"), (14, "2 weeks ago"), (29, "4 weeks ago"),
            (30, "1 month ago"), (59, "1 month ago"), (60, "2 months ago"), (364, "12 months ago"), (365, "1 year ago"),
            (730, "2 years ago"),
        ]
        for (d, expected) in cases {
            XCTAssertEqual(RelativeAge.describe(now.addingTimeInterval(-d * 86_400), now: now), expected, "days=\(d)")
        }
        XCTAssertEqual(RelativeAge.parseISO("2026-09-12T08:30:00Z"), Date(timeIntervalSince1970: 1_789_201_800))
        XCTAssertNil(RelativeAge.parseISO(nil))
        XCTAssertNil(RelativeAge.parseISO(""))
        XCTAssertNil(RelativeAge.parseISO("not a date"))
        XCTAssertEqual(RelativeAge.shortDate(date("2026-09-02T07:05:00Z"), calendar: utc), "2 Sep 2026")
    }

    func testFindAndFilter() {
        XCTAssertTrue(AppFilter.matchesQuery("dmx", "DMX Tools", "Art-Net monitor", "Show control", "dmxtools"))
        XCTAssertTrue(AppFilter.matchesQuery("  tools   DMX ", "DMX Tools", "", nil, "dmxtools"))
        XCTAssertTrue(AppFilter.matchesQuery("show art-net", "DMX Tools", "Art-Net monitor", "Show control", "dmxtools"))
        XCTAssertFalse(AppFilter.matchesQuery("dmx cisco", "DMX Tools", "Art-Net monitor", "Show control", "dmxtools"))
        XCTAssertTrue(AppFilter.matchesQuery("", "anything"))
        XCTAssertTrue(AppFilter.matchesQuery(nil, "anything"))
        XCTAssertFalse(AppFilter.matchesQuery("x", nil, ""))

        XCTAssertTrue(AppFilter.matchesStatus(.all, installed: false, updateAvailable: false, installable: false))
        XCTAssertTrue(AppFilter.matchesStatus(.installed, installed: true, updateAvailable: false, installable: false))
        XCTAssertFalse(AppFilter.matchesStatus(.installed, installed: false, updateAvailable: false, installable: true))
        XCTAssertTrue(AppFilter.matchesStatus(.updates, installed: true, updateAvailable: true, installable: false))
        XCTAssertFalse(AppFilter.matchesStatus(.updates, installed: true, updateAvailable: false, installable: false))
        XCTAssertTrue(AppFilter.matchesStatus(.notInstalled, installed: false, updateAvailable: false, installable: true))
        XCTAssertFalse(AppFilter.matchesStatus(.notInstalled, installed: false, updateAvailable: false, installable: false))
        XCTAssertFalse(AppFilter.matchesStatus(.notInstalled, installed: true, updateAvailable: false, installable: true))

        XCTAssertFalse(AppFilter.isActive("  ", .all))
        XCTAssertTrue(AppFilter.isActive("a", .all))
        XCTAssertTrue(AppFilter.isActive(nil, .updates))
    }

    func testReleaseNotes() {
        XCTAssertEqual(ReleaseNotesText.plain(nil), ReleaseNotesText.empty)
        XCTAssertEqual(ReleaseNotesText.plain(" \n\n "), ReleaseNotesText.empty)
        let md = "## What's new\r\n\r\n- **Faster** start-up (`--fast`)\n* Fixed [the crash](https://x.y/z) on _launch_\n  - nested ~~old~~ item\n\n\n\n### Notes\n> Quoted *text*\n\n---\n1. Step one\n![shot](a.png)\n<!-- hidden\ncomment -->Done &amp; dusted <br/>"
        let expected = "What's new\n\n• Faster start-up (--fast)\n• Fixed the crash on launch\n  • nested old item\n\nNotes\nQuoted text\n\n1. Step one\nDone & dusted"
        XCTAssertEqual(ReleaseNotesText.plain(md), expected)
        XCTAssertEqual(ReleaseNotesText.plain("Run:\n```bash\n**not bold** `x`\n```\nafter"), "Run:\n**not bold** `x`\nafter")
        XCTAssertEqual(ReleaseNotesText.plain("use install_to_applications and 2 * 3 * 4"), "use install_to_applications and 2 * 3 * 4")
        XCTAssertEqual(ReleaseNotesText.plain("a_b_c"), "a_b_c")
        XCTAssertEqual(String(ReleaseNotesText.plain("- [x] Task done").drop(while: { $0 == "•" || $0 == " " })), "Task done")
        XCTAssertEqual(ReleaseNotesText.plain("see <https://x.y>"), "see https://x.y")
        XCTAssertEqual(ReleaseNotesText.plain("1\\*2"), "1*2")
        let long = ReleaseNotesText.plain(String(repeating: "a", count: 30_000))
        XCTAssertEqual(long.count, ReleaseNotesText.maxLength + 2)
        XCTAssertTrue(long.hasSuffix("\n…"))
    }

    func testScheduledChecks() {
        XCTAssertNil(UpdatePolicy.interval("off"))
        XCTAssertEqual(UpdatePolicy.interval("1h"), 3_600)
        XCTAssertEqual(UpdatePolicy.interval("4h"), 14_400)
        XCTAssertEqual(UpdatePolicy.interval("12h"), 43_200)
        XCTAssertEqual(UpdatePolicy.interval("24h"), 86_400)
        XCTAssertEqual(UpdatePolicy.interval("garbage"), 14_400)
        XCTAssertEqual(UpdatePolicy.interval(nil), 14_400)
        XCTAssertTrue(UpdatePolicy.intervals.contains { $0.raw == UpdatePolicy.defaultInterval })
        let now = date("2026-09-25T12:00:00Z")
        XCTAssertFalse(UpdatePolicy.isDue(lastCheck: nil, now: now, raw: "off"))
        XCTAssertTrue(UpdatePolicy.isDue(lastCheck: nil, now: now, raw: "4h"))
        XCTAssertFalse(UpdatePolicy.isDue(lastCheck: now.addingTimeInterval(-3.9 * 3_600), now: now, raw: "4h"))
        XCTAssertTrue(UpdatePolicy.isDue(lastCheck: now.addingTimeInterval(-4 * 3_600), now: now, raw: "4h"))
        XCTAssertTrue(UpdatePolicy.isDue(lastCheck: now.addingTimeInterval(3_600), now: now, raw: "4h"))
        XCTAssertFalse(UpdatePolicy.isDue(lastCheck: now.addingTimeInterval(-59 * 60), now: now, raw: "1h"))
    }

    func testNotifications() {
        let pending = [UpdatePolicy.Pending(id: "dmx", name: "DMX Tools", version: "v1.2.0"),
                       UpdatePolicy.Pending(id: "psn", name: "PSN Tools", version: "0.4.1")]
        let (toNotify, notified) = UpdatePolicy.notify(pending, alreadyNotified: ["dmx 1.2.0", "gone 9.9.9"])
        XCTAssertEqual(toNotify.map(\.id), ["psn"])
        XCTAssertEqual(notified, ["dmx 1.2.0", "psn 0.4.1"])
        XCTAssertTrue(UpdatePolicy.notify(pending, alreadyNotified: notified).toNotify.isEmpty)
        XCTAssertEqual(UpdatePolicy.notify([.init(id: "dmx", name: "DMX Tools", version: "v1.3.0")], alreadyNotified: notified).toNotify.count, 1)
        // A check that failed for dmx keeps its key, so the next good check doesn't announce v1.2.0 again.
        let afterFailure = UpdatePolicy.notify(Array(pending.dropFirst()), alreadyNotified: notified).notified
        XCTAssertEqual(UpdatePolicy.remembered(afterFailure, alreadyNotified: notified, uncheckedIds: ["dmx"]), ["psn 0.4.1", "dmx 1.2.0"])
        XCTAssertEqual(UpdatePolicy.remembered(afterFailure, alreadyNotified: notified, uncheckedIds: []), ["psn 0.4.1"])
        XCTAssertTrue(UpdatePolicy.notify(pending, alreadyNotified: UpdatePolicy.remembered(
            afterFailure, alreadyNotified: notified, uncheckedIds: ["dmx"])).toNotify.isEmpty)

        let items: [UpdatePolicy.Pending] = [
            .init(id: "a", name: "A", version: "1.0.0"), .init(id: "b", name: "B", version: "v2.0"),
            .init(id: "c", name: "Convert", version: "build-20260912"), .init(id: "d", name: "D", version: "1"),
            .init(id: "e", name: "E", version: "1"),
        ]
        XCTAssertEqual(UpdatePolicy.notificationBody(items), "A v1.0.0, B v2.0, Convert build-20260912 and 2 more")
        XCTAssertEqual(UpdatePolicy.notificationBody(Array(items.prefix(1))), "A v1.0.0")
        XCTAssertEqual(UpdatePolicy.notificationTitle(1), "Update available")
        XCTAssertEqual(UpdatePolicy.notificationTitle(2), "Updates available")
        XCTAssertEqual(UpdatePolicy.autoUpdateSummary([("DMX Tools", "1.2.0")]), "Updated DMX Tools to v1.2.0")
        XCTAssertEqual(UpdatePolicy.autoUpdateSummary([("A", "1"), ("B", "2")]), "Updated 2 apps")
        XCTAssertEqual(VersionDisplay.display("1.2.0"), "v1.2.0")
        XCTAssertEqual(VersionDisplay.display("V1.2.0"), "v1.2.0")
        XCTAssertEqual(VersionDisplay.display(" build-20260912 "), "build-20260912")
    }

    func testHistoryClassifiesRoundTripsAndCaps() {
        XCTAssertEqual(ActivityHistory.action(from: nil, to: "v1.0.0"), "install")
        XCTAssertEqual(ActivityHistory.action(from: "v1.0.0", to: "v1.1.0"), "update")
        XCTAssertEqual(ActivityHistory.action(from: "v1.1.0", to: "v1.0.0"), "downgrade")
        XCTAssertEqual(ActivityHistory.action(from: "v1.1.0", to: "1.1.0"), "reinstall")

        let t = date("2026-09-25T13:02:00Z")
        var list: [ActivityEvent] = []
        for i in 0..<(ActivityHistory.cap + 5) {
            list = ActivityHistory.append(list, ActivityEvent(at: t.addingTimeInterval(Double(60 * i)), app: "dmx",
                                                              name: "DMX Tools", action: "update", from: "1.0", to: "1.\(i)"))
        }
        XCTAssertEqual(list.count, ActivityHistory.cap)
        XCTAssertEqual(list[0].to, "1.5")
        XCTAssertEqual(ActivityHistory.parse(ActivityHistory.serialize(list)), list)

        let e = ActivityHistory.append([], ActivityEvent(at: t, app: "a", name: "A", action: "failed",
                                                         note: "  " + String(repeating: "x", count: 300) + " "))[0]
        XCTAssertEqual(e.note?.count, ActivityHistory.maxNoteLength + 1)
        XCTAssertTrue(e.note!.hasSuffix("…"))
        XCTAssertNil(ActivityHistory.append([], ActivityEvent(at: t, app: "a", name: "A", action: "failed", note: "  "))[0].note)

        XCTAssertTrue(ActivityHistory.parse(Data("{not json".utf8)).isEmpty)
        XCTAssertTrue(ActivityHistory.parse(Data("{}".utf8)).isEmpty)
        XCTAssertTrue(ActivityHistory.parse(nil).isEmpty)
        let one = ActivityHistory.parse(Data(#"[{"at":"2026-09-25T13:02:00Z","app":"a","action":"install","to":"1.0"},{"app":"b"},7]"#.utf8))
        XCTAssertEqual(one.count, 1)
        XCTAssertEqual(one.first?.name, "a")
    }

    func testHistoryWording() {
        let t = date("2026-09-25T13:02:00Z")
        func ev(_ action: String, _ from: String? = nil, _ to: String? = nil, _ note: String? = nil) -> ActivityEvent {
            ActivityEvent(at: t, app: "d", name: "DMX Tools", action: action, from: from, to: to, note: note)
        }
        XCTAssertEqual(ActivityHistory.describe(ev("install", nil, "1.0.0")), "Installed DMX Tools v1.0.0")
        XCTAssertEqual(ActivityHistory.describe(ev("update", "v1.0.0", "v1.1.0")), "Updated DMX Tools v1.0.0 → v1.1.0")
        XCTAssertEqual(ActivityHistory.describe(ev("downgrade", "1.1.0", "1.0.0")), "Rolled back DMX Tools v1.1.0 → v1.0.0")
        XCTAssertEqual(ActivityHistory.describe(ev("reinstall", "1.0.0", "1.0.0")), "Reinstalled DMX Tools v1.0.0")
        XCTAssertEqual(ActivityHistory.describe(ev("uninstall", "1.0.0")), "Removed DMX Tools v1.0.0")
        XCTAssertEqual(ActivityHistory.describe(ev("uninstall")), "Removed DMX Tools")
        XCTAssertEqual(ActivityHistory.describe(ev("failed", nil, "1.1.0", "offline")), "Couldn't install DMX Tools v1.1.0: offline")
        let now = date("2026-09-25T18:00:00Z")
        XCTAssertEqual(ActivityHistory.when(date("2026-09-25T14:02:00Z"), now: now, calendar: utc), "Today 14:02")
        XCTAssertEqual(ActivityHistory.when(date("2026-09-24T09:10:00Z"), now: now, calendar: utc), "Yesterday 09:10")
        XCTAssertEqual(ActivityHistory.when(date("2026-09-02T07:05:00Z"), now: now, calendar: utc), "2 Sep 2026 07:05")
    }

    private let catalog: [SetupPlanner.CatalogEntry] = [
        .init(id: "dmx", name: "DMX Tools", variants: []),
        .init(id: "ndi", name: "NDI Tools", variants: [("standard", "Light"), ("full", "Full")]),
        .init(id: "psn", name: "PSN Tools", variants: []),
    ]

    private func sample() -> SetupProfile {
        SetupProfile(createdAt: "2026-09-25T12:00:00Z", createdBy: "JB Theatre Tools 1.30.0 (Windows)", apps: [
            .init(id: "dmx", variant: nil, version: "v1.2.0", held: true),
            .init(id: "ndi", variant: "standard", version: "v2.0.0", held: false),
            .init(id: "ndi", variant: "full", version: "v2.0.0", held: false),
            .init(id: "psn", variant: nil, version: "0.4.1", held: false),
            .init(id: "gone", variant: nil, version: "1.0", held: false),
            .init(id: "ndi", variant: "ultra", version: "1.0", held: false),
        ], layout: .init(pinned: ["dmx"], hidden: ["psn"], order: ["ndi", "dmx"], categoryOrder: ["Networking"], collapsed: []))
    }

    func testSetupRoundTripsAndRejectsForeignFiles() throws {
        XCTAssertEqual(try SetupProfile.parse(sample().serialize()), sample())
        let bad = [
            "[]",
            #"{"kind":"something-else","schemaVersion":1,"apps":[]}"#,
            #"{"kind":"jbtheatretools-setup","apps":[]}"#,
            #"{"kind":"jbtheatretools-setup","schemaVersion":2,"apps":[]}"#,
            #"{"kind":"jbtheatretools-setup","schemaVersion":1}"#,
            "not json",
            String(repeating: " ", count: SetupProfile.maxBytes + 1),
        ]
        for b in bad { XCTAssertThrowsError(try SetupProfile.parse(Data(b.utf8)), "accepted: \(b.prefix(40))") }
        let junk = try SetupProfile.parse(Data(#"{"kind":"jbtheatretools-setup","schemaVersion":1,"apps":[7,{"id":""},{"id":" dmx ","variant":"","held":"yes"}]}"#.utf8))
        XCTAssertEqual(junk.apps, [.init(id: "dmx", variant: nil, version: nil, held: false)])
        XCTAssertNil(junk.layout)
        // A number is never "held" (the desktop JSON bridge would read 1 as true).
        let numeric = try SetupProfile.parse(Data(#"{"kind":"jbtheatretools-setup","schemaVersion":1,"apps":[{"id":"dmx","held":1}]}"#.utf8))
        XCTAssertFalse(numeric.apps[0].held)
    }

    func testSetupPlans() {
        let plan = SetupPlanner.build(sample(), catalog: catalog, installedKeys: ["psn"])
        XCTAssertEqual(plan.toInstall.map(\.label), ["DMX Tools", "NDI Tools", "NDI Tools (Full)"])
        XCTAssertEqual(plan.toInstall[0].tag, "v1.2.0")
        XCTAssertNil(plan.toInstall[1].tag)
        XCTAssertNil(plan.toInstall[1].variantId)
        XCTAssertEqual(plan.toInstall[2].variantId, "full")
        XCTAssertEqual(plan.alreadyInstalled, ["PSN Tools"])
        XCTAssertEqual(plan.holdIds, ["dmx"])
        XCTAssertEqual(plan.skipped.count, 2)
        XCTAssertTrue(plan.skipped.contains("gone — not in this launcher's catalog"))
        XCTAssertTrue(plan.skipped.contains("NDI Tools (ultra) — no such edition"))

        let android = SetupPlanner.build(sample(), catalog: catalog, installedKeys: [], supportsVariants: false)
        XCTAssertTrue(android.toInstall.allSatisfy { $0.variantId == nil })
        XCTAssertTrue(android.skipped.contains("NDI Tools (Full) — not available here"))

        let twice = SetupProfile(apps: [.init(id: "dmx", variant: nil, version: nil, held: false),
                                        .init(id: "dmx", variant: nil, version: nil, held: false)])
        XCTAssertEqual(SetupPlanner.build(twice, catalog: catalog, installedKeys: []).toInstall.count, 1)

        let s = SetupPlanner.summary(plan)
        XCTAssertTrue(s.hasPrefix("Install 3 apps:\n  • DMX Tools v1.2.0 (held)\n  • NDI Tools\n  • NDI Tools (Full)"))
        XCTAssertTrue(s.contains("Already installed (left as they are): PSN Tools"))
        XCTAssertTrue(s.contains("Held at their versions: 1 app"))
        XCTAssertTrue(s.contains("Skipped:\n  • "))
        XCTAssertTrue(SetupPlanner.summary(SetupPlanner.build(SetupProfile(), catalog: catalog, installedKeys: [])).hasPrefix("Nothing to install"))
        XCTAssertEqual(SetupProfile.suggestedFileName(date("2026-09-25T23:00:00Z"), calendar: utc), "JB Theatre Tools setup 2026-09-25.json")
    }

    func testSetupSkipsDevBuildsWhenDevIsOff() {
        let file = SetupProfile(apps: [.init(id: "dmx", variant: nil, version: "v1.3.0-dev.2", held: true),
                                       .init(id: "psn", variant: nil, version: "1.6.1", held: true)])
        let off = SetupPlanner.build(file, catalog: catalog, installedKeys: [], allowDevTags: false)
        XCTAssertEqual(off.toInstall.map(\.appId), ["psn"])
        XCTAssertEqual(off.holdIds, ["psn"])           // the skipped dev entry isn't held either
        XCTAssertEqual(off.skipped, ["DMX Tools v1.3.0-dev.2 — a development build (not switched on here)"])
        let on = SetupPlanner.build(file, catalog: catalog, installedKeys: [], allowDevTags: true)
        XCTAssertEqual(on.toInstall.map(\.appId), ["dmx", "psn"])
        XCTAssertEqual(on.toInstall[0].tag, "v1.3.0-dev.2")
    }

    func testSignedManifestNeedsBothFiles() {
        func rel(_ names: [String]) -> ReleaseInfo {
            ReleaseInfo(tagName: "v1.0.0", assets: names.enumerated().map { ReleaseAsset(id: $0.offset, name: $0.element, size: 1) },
                        prerelease: false, draft: false)
        }
        XCTAssertTrue(AppState.hasSignedManifest(rel(["A.zip", "SHA256SUMS", "SHA256SUMS.minisig"])))
        XCTAssertFalse(AppState.hasSignedManifest(rel(["A.zip", "SHA256SUMS"])))
        XCTAssertFalse(AppState.hasSignedManifest(rel(["A.zip"])))
    }

    func testMacBuildPick() {
        let universal = ["macos": "A-macOS.zip"]
        let split = ["macos-arm64": "A-Full-macOS-arm64.zip", "macos-x64": "A-Full-macOS-x64.zip"]
        let armOnly = ["macos-arm64": "A-Full-macOS-arm64.zip"]
        let intelOnly = ["macos-x64": "A-macOS-x64.zip"]
        typealias P = MacArch.Pick
        // Apple silicon, native
        XCTAssertEqual(MacArch.pick(from: universal, appleSilicon: true, intel: false), P(name: "A-macOS.zip", translated: false))
        XCTAssertEqual(MacArch.pick(from: split, appleSilicon: true, intel: false), P(name: "A-Full-macOS-arm64.zip", translated: false))
        XCTAssertEqual(MacArch.pick(from: intelOnly, appleSilicon: true, intel: false), P(name: "A-macOS-x64.zip", translated: true))
        // Apple silicon, set to run as Intel
        XCTAssertEqual(MacArch.pick(from: universal, appleSilicon: true, intel: true), P(name: "A-macOS.zip", translated: true))
        XCTAssertEqual(MacArch.pick(from: split, appleSilicon: true, intel: true), P(name: "A-Full-macOS-x64.zip", translated: true))
        XCTAssertEqual(MacArch.pick(from: armOnly, appleSilicon: true, intel: true), P(name: "A-Full-macOS-arm64.zip", translated: false))
        // Intel Mac: never translated, the choice doesn't apply
        XCTAssertEqual(MacArch.pick(from: split, appleSilicon: false, intel: true), P(name: "A-Full-macOS-x64.zip", translated: false))
        XCTAssertNil(MacArch.pick(from: armOnly, appleSilicon: false, intel: false))
        // When the choice is offered
        XCTAssertTrue(MacArch.canChoose(universal, appleSilicon: true))
        XCTAssertTrue(MacArch.canChoose(split, appleSilicon: true))
        XCTAssertFalse(MacArch.canChoose(armOnly, appleSilicon: true))
        XCTAssertFalse(MacArch.canChoose(intelOnly, appleSilicon: true))
        XCTAssertFalse(MacArch.canChoose(universal, appleSilicon: false))
    }

    func testMachOArchs() {
        func be(_ v: UInt32) -> [UInt8] { [UInt8(v >> 24), UInt8(v >> 16 & 0xFF), UInt8(v >> 8 & 0xFF), UInt8(v & 0xFF)] }
        func le(_ v: UInt32) -> [UInt8] { be(v).reversed() }
        func fatEntry(_ cpu: UInt32) -> [UInt8] { be(cpu) + be(0) + be(0) + be(0) + be(0) }
        let fat = Data(be(0xCAFE_BABE) + be(2) + fatEntry(0x0100_0007) + fatEntry(0x0100_000C))
        XCTAssertEqual(MachO.archs(fat), ["x86_64", "arm64"])
        XCTAssertEqual(MachO.archs(Data(le(0xFEED_FACF) + le(0x0100_000C) + le(0))), ["arm64"])
        XCTAssertEqual(MachO.archs(Data(le(0xFEED_FACF) + le(0x0100_0007) + le(0))), ["x86_64"])
        XCTAssertEqual(MachO.archs(Data("#!/bin/sh\necho hi\n".utf8)), [])
        XCTAssertEqual(MachO.archs(Data()), [])
        // A real bundle on every Mac
        XCTAssertFalse(MachO.archs(ofApp: URL(fileURLWithPath: "/System/Applications/Calculator.app")).isEmpty)
    }

    func testDiskSpace() {
        XCTAssertEqual(DiskSpace.required(assetSize: 100_000_000, assetName: "NDITools-Full-macOS-arm64.zip"), 3 * 100_000_000 + DiskSpace.margin)
        XCTAssertEqual(DiskSpace.required(assetSize: 100_000_000, assetName: "DMXTools-Windows-x64.exe"), 2 * 100_000_000 + DiskSpace.margin)
        XCTAssertEqual(DiskSpace.required(assetSize: 0, assetName: "x.zip"), DiskSpace.margin)
        XCTAssertEqual(DiskSpace.required(assetSize: Int64.max / 2, assetName: "x.zip"), Int64.max)
        XCTAssertNil(DiskSpace.shortfall(required: 1000, free: 1000))
        XCTAssertNil(DiskSpace.shortfall(required: 1000, free: -1))
        XCTAssertEqual(DiskSpace.shortfall(required: 1_200_000_000, free: 300_000_000),
                       "Not enough disk space — needs about 1.2 GB, 300 MB free.")
    }

    func testDiagnosticsCarryNoSecrets() {
        let log = (1...50).map { "line \($0)" } + [
            "oops Authorization: Bearer ghp_abcdefghijklmnop leaked",
            "pat github_pat_11ABCDEFG0123456789_abcdef here",
        ]
        let report = Diagnostics.build(.init(
            launcherVersion: "1.30.0", os: "Windows 11", arch: "x64", authMode: "Download server",
            relayHost: "jbtheatretools.jamesbreedon.com", devChannel: false, showLock: true, installLocation: "Launcher only",
            apps: [.init(name: "DMX Tools", installed: "v1.1.0", latest: "v1.2.0", status: "Update", held: true)],
            logTail: log, now: date("2026-09-25T12:00:00Z")))
        XCTAssertTrue(report.hasPrefix("JB Theatre Tools diagnostics — 2026-09-25 12:00:00 UTC\nLauncher: v1.30.0\nSystem: Windows 11 (x64)\n"))
        XCTAssertTrue(report.contains("Downloads via: Download server (jbtheatretools.jamesbreedon.com)\n"))
        XCTAssertTrue(report.contains("Show lock: on · Development builds: off\n"))
        XCTAssertTrue(report.contains("  DMX Tools — installed v1.1.0, latest v1.2.0, Update, held\n"))
        XCTAssertTrue(report.contains("Recent log (40 lines):"))
        XCTAssertFalse(report.contains("line 12\n"))
        XCTAssertTrue(report.contains("line 13\n"))
        XCTAssertFalse(report.contains("ghp_abcdefghijklmnop"))
        XCTAssertFalse(report.contains("github_pat_11ABCDEFG"))
        XCTAssertTrue(report.contains("[redacted]"))
        XCTAssertEqual(Diagnostics.redact("Basic c3VpdGU6cGFzcw=="), "Basic [redacted]")
        XCTAssertEqual(Diagnostics.redact("installed dmx v1.2.0"), "installed dmx v1.2.0")
        XCTAssertEqual(Diagnostics.redact("refresh: token rejected (token mode)"), "refresh: token rejected (token mode)")
    }

    func testLauncherWhatsNew() {
        XCTAssertFalse(LauncherWhatsNew.shouldShow(lastSeen: nil, current: "1.30.0"))
        XCTAssertFalse(LauncherWhatsNew.shouldShow(lastSeen: "", current: "1.30.0"))
        XCTAssertFalse(LauncherWhatsNew.shouldShow(lastSeen: "1.30.0", current: "1.30.0"))
        XCTAssertFalse(LauncherWhatsNew.shouldShow(lastSeen: "1.31.0", current: "1.30.0"))
        XCTAssertTrue(LauncherWhatsNew.shouldShow(lastSeen: "1.29.1", current: "1.30.0"))
        XCTAssertTrue(LauncherWhatsNew.shouldShow(lastSeen: "1.30.0-dev.2", current: "1.30.0"))
        // Nothing recorded: an update only when the launcher was already in use (pre-1.30 recorded nothing).
        XCTAssertTrue(LauncherWhatsNew.shouldShow(lastSeen: nil, current: "1.30.0", existingInstall: true))
        XCTAssertTrue(LauncherWhatsNew.shouldShow(lastSeen: "", current: "1.30.0", existingInstall: true))
        XCTAssertFalse(LauncherWhatsNew.shouldShow(lastSeen: "1.30.0", current: "1.30.0", existingInstall: true))
    }
}
