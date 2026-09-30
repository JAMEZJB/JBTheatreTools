import XCTest
@testable import JBTheatreTools

/// Back up all / Restore all: the launcher's own settings file, the bundle, reading the apps' answers, and the two
/// files shared with Stagehand. Everything runs against temporary folders — never the real settings or Keychain.
final class SuiteBackupTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory.appendingPathComponent("jbtt-tests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: dir) }

    private func prefs(_ name: String = "prefs.json") -> FilePrefs { FilePrefs(url: dir.appendingPathComponent(name)) }
    private func secrets(_ name: String = "secrets.json") -> FileLauncherSecrets { FileLauncherSecrets(url: dir.appendingPathComponent(name)) }

    // MARK: launcher settings

    func testLauncherDocumentHasNoPasswordsUnlessAsked() throws {
        let p = prefs()
        p.setPref("dark", "theatre.appearance")
        p.setPref(["psntools"], "theatre.pinnedApps")
        p.setPref("/Users/someone/Apps/JB Theatre Tools.app", "theatre.launcherHomes")   // machine-specific: never included
        let s = secrets()
        try s.write(["downloadPassphrase": "synthetic-phrase"])
        let doc = try LauncherSettings.document(prefs: p, secrets: s, version: "1.32.0", includeSecrets: false, passphrase: nil)
        let data = try SuiteBackup.jsonData(doc)
        let text = String(decoding: data, as: UTF8.self)
        XCTAssertFalse(text.contains("synthetic-phrase"))
        XCTAssertFalse(text.contains("launcherHomes"))
        XCTAssertTrue(doc["secrets"] is NSNull)
        let slots = try XCTUnwrap(doc["secret_slots"] as? [[String: String]])
        XCTAssertEqual(slots.map { $0["label"] }, ["Download passphrase"])
        let app = try XCTUnwrap(doc["app"] as? [String: Any])
        XCTAssertEqual(app["id"] as? String, "jbtheatretools")
        XCTAssertEqual(doc["format"] as? String, "jbtt-settings")
    }

    func testSealedRoundTripOntoAnotherComputer() throws {
        let p = prefs()
        p.setPref("grid", "theatre.viewMode")
        p.setPref(["\u{1}pinned", "Networking"], "theatre.collapsedCategories")
        p.setPref(["pdftools": "full"], "theatre.appVariants")
        p.setPref(true, AppState.devChannelKey)
        let s = secrets()
        try s.write(["downloadPassphrase": "synthetic-phrase", "githubToken": "synthetic-token"])
        let pw = "backup words"
        let doc = try LauncherSettings.document(prefs: p, secrets: s, version: "1.32.0", includeSecrets: true,
                                                passphrase: pw, rounds: 100_000)
        let data = try SuiteBackup.jsonData(doc)
        XCTAssertFalse(String(decoding: data, as: UTF8.self).contains("synthetic-"))
        // The file stores the Pinned section under the plain word, as Windows does.
        let items = try XCTUnwrap(doc["items"] as? [String: Any])
        let settings = try XCTUnwrap((items["settings"] as? [String: Any])?["data"] as? [String: Any])
        XCTAssertEqual(settings["collapsedCategories"] as? [String], ["pinned", "Networking"])

        let p2 = prefs("new.json"), s2 = secrets("new-secrets.json")
        XCTAssertThrowsError(try LauncherSettings.restore(data, prefs: p2, secrets: s2, passphrase: "wrong", restoreSecrets: true)) {
            XCTAssertEqual($0 as? SuiteBackup.Failure, .wrongPassphrase)
        }
        XCTAssertNil(p2.prefValue("theatre.viewMode"), "a wrong passphrase changes nothing")
        XCTAssertThrowsError(try LauncherSettings.verifyPassphrase(data, passphrase: nil, restoreSecrets: true))
        XCTAssertNoThrow(try LauncherSettings.verifyPassphrase(data, passphrase: nil, restoreSecrets: false))

        let out = try LauncherSettings.restore(data, prefs: p2, secrets: s2, passphrase: pw, restoreSecrets: true)
        XCTAssertEqual(p2.prefValue("theatre.viewMode") as? String, "grid")
        XCTAssertEqual(p2.prefValue("theatre.collapsedCategories") as? [String], ["\u{1}pinned", "Networking"])
        XCTAssertEqual(p2.prefValue("theatre.appVariants") as? [String: String], ["pdftools": "full"])
        XCTAssertEqual(p2.prefValue(AppState.devChannelKey) as? Bool, true)
        XCTAssertEqual(s2.read(), ["downloadPassphrase": "synthetic-phrase", "githubToken": "synthetic-token"])
        XCTAssertTrue(out.attention.isEmpty)
    }

    func testPlainPasswordsAndMissingOnes() throws {
        let s = secrets()
        try s.write(["downloadPassphrase": "synthetic-phrase"])
        let plain = try LauncherSettings.document(prefs: prefs(), secrets: s, version: "1", includeSecrets: true, passphrase: nil)
        let sec = try XCTUnwrap(plain["secrets"] as? [String: Any])
        XCTAssertEqual(sec["protected"] as? Bool, false)
        XCTAssertEqual(sec["count"] as? Int, 1)
        // Without the passwords restored, the result says which to enter again (this computer has none).
        let out = try LauncherSettings.restore(try SuiteBackup.jsonData(plain), prefs: prefs("b.json"), secrets: secrets("b-s.json"),
                                               passphrase: nil, restoreSecrets: false)
        XCTAssertEqual(out.attention.first?["label"], "Passwords")
        XCTAssertTrue(out.attention.first?["message"]?.contains("Download passphrase") == true)
    }

    func testRestoreSkipsUnknownValuesAndHoldsNotifications() throws {
        let data = try SuiteBackup.jsonData([
            "format": "jbtt-settings", "schema": 1,
            "app": ["id": "jbtheatretools", "name": "JB Theatre Tools", "version": "9.0.0"],
            "items": ["settings": ["type": "json", "data": ["appearance": "neon", "updateMode": "manual", "notifyUpdates": true]]],
            "secret_slots": [], "secrets": NSNull(),
        ] as [String: Any])
        let p = prefs()
        let out = try LauncherSettings.restore(data, prefs: p, secrets: secrets(), passphrase: nil, restoreSecrets: true)
        XCTAssertNil(p.prefValue("theatre.appearance"))
        XCTAssertEqual(p.prefValue("theatre.updateMode") as? String, "manual")
        XCTAssertNil(p.prefValue(AppState.notifyKey), "macOS permission is asked from the switch, never switched on behind it")
        XCTAssertEqual(out.warnings.count, 1)
        XCTAssertEqual(out.attention.count, 1)
    }

    func testWrongAppAndNewerSchemaAreRefused() throws {
        func doc(_ id: String, _ schema: Int) throws -> Data {
            try SuiteBackup.jsonData(["format": "jbtt-settings", "schema": schema, "app": ["id": id, "name": "PSN Tools"],
                                      "items": [String: Any]()] as [String: Any])
        }
        XCTAssertThrowsError(try LauncherSettings.inspect(try doc("psntools", 1))) {
            guard case .wrongApp = $0 as? SuiteBackup.Failure else { return XCTFail("\($0)") }
        }
        XCTAssertThrowsError(try LauncherSettings.inspect(try doc("jbtheatretools", 2))) {
            guard case .newer = $0 as? SuiteBackup.Failure else { return XCTFail("\($0)") }
        }
        XCTAssertThrowsError(try LauncherSettings.inspect(Data("not json".utf8)))
    }

    // MARK: bundle

    func testManifestRefusesPathsAndDuplicates() throws {
        let data = try SuiteBackup.jsonData([
            "format": "jbtt-suite-backup", "schema": 1, "created": "2026-09-30T21:00:00Z", "launcher_version": "1.32.0",
            "source": ["machine": "Show-PC-2"],
            "apps": [
                ["id": "psntools", "name": "PSN Tools", "version": "v1.7.0", "file": "psntools.jbtt-settings"],
                ["id": "psntools", "name": "Again", "version": "v1", "file": "other.jbtt-settings"],
                ["id": "evil", "name": "Evil", "version": "v1", "file": "../evil.jbtt-settings"],
                ["id": "evil2", "name": "Evil", "version": "v1", "file": "C:\\evil.jbtt-settings"],
                ["id": "evil3", "name": "Evil", "version": "v1", "file": "notes.txt"],
            ],
            "launcher": ["file": "launcher.jbtt-settings"],
        ] as [String: Any])
        let m = try SuiteBundleManifest.parse(data)
        XCTAssertEqual(m.apps.map(\.id), ["psntools"])
        XCTAssertEqual(m.launcherFile, "launcher.jbtt-settings")
        XCTAssertEqual(m.source["machine"], "Show-PC-2")
        XCTAssertThrowsError(try SuiteBundleManifest.parse(try SuiteBackup.jsonData(["format": "jbtt-suite-backup", "schema": 2])))
        XCTAssertThrowsError(try SuiteBundleManifest.parse(Data("{}".utf8)))
    }

    func testBundleWriteAndOpenRoundTrip() throws {
        let staging = dir.appendingPathComponent("staging", isDirectory: true)
        try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: true)
        let manifest = SuiteBundleManifest(created: "2026-09-30T21:00:00Z", source: ["machine": "test"], launcherVersion: "1.32.0",
                                           apps: [SuiteBundleApp(id: "psntools", name: "PSN Tools", version: "v1.7.0",
                                                                 file: "psntools.jbtt-settings", edition: "full")],
                                           launcherFile: nil)
        try SuiteBackup.atomicWrite(try SuiteBackup.jsonData(manifest.json), to: staging.appendingPathComponent("manifest.json"))
        try SuiteBackup.atomicWrite(Data(#"{"format":"jbtt-settings","schema":1,"app":{"id":"psntools"},"items":{},"secrets":{"protected":true}}"#.utf8),
                                    to: staging.appendingPathComponent("psntools.jbtt-settings"))
        let dest = dir.appendingPathComponent(SuiteBackup.bundleFileName())
        try SuiteBundle.write(staging: staging, to: dest)
        let opened = try SuiteBundle.open(dest)
        defer { try? FileManager.default.removeItem(at: opened.folder) }
        XCTAssertEqual(opened.manifest.apps, manifest.apps)
        let info = SuiteBundle.appFileInfo(opened.url("psntools.jbtt-settings"))
        XCTAssertEqual(info.appId, "psntools")
        XCTAssertTrue(info.protected)
        XCTAssertTrue(dest.lastPathComponent.hasPrefix("JB Theatre Tools backup "))
        XCTAssertTrue(dest.lastPathComponent.hasSuffix(".jbtt-backup"))
        XCTAssertThrowsError(try SuiteBundle.open(staging.appendingPathComponent("manifest.json")))
    }

    // MARK: the apps' answers

    private func run(_ code: Int32?, _ result: [String: Any]?, timedOut: Bool = false) -> SettingsCLIRun {
        SettingsCLIRun(exitCode: code, result: result, timedOut: timedOut, launchError: nil)
    }

    func testDescribeAnswers() {
        XCTAssertEqual(SettingsCLIClassify.describe(run(0, ["ok": true, "items": ["Settings", "Presets (3)"], "secrets": ["count": 2]])),
                       .ready(items: ["Settings", "Presets (3)"], secretCount: 2))
        // An app too old for the flags opens its window (killed at the timeout) or exits without answering.
        XCTAssertEqual(SettingsCLIClassify.describe(run(nil, nil, timedOut: true)), .unsupported)
        XCTAssertEqual(SettingsCLIClassify.describe(run(0, nil)), .unsupported)
        XCTAssertEqual(SettingsCLIClassify.describe(run(64, ["ok": false, "code": "usage", "error": "x"])), .unsupported)
        XCTAssertEqual(SettingsCLIClassify.describe(run(1, ["ok": false, "error": "Couldn't read"])), .failed("Couldn't read"))
    }

    func testImportAnswers() {
        XCTAssertEqual(SettingsCLIClassify.importResult(run(0, ["ok": true, "attention": [["label": "NIC", "message": "Check the NIC."]],
                                                                  "warnings": ["w"], "restart_needed": true])),
                       .ok(attention: ["Check the NIC."], warnings: ["w"], restartNeeded: true))
        XCTAssertEqual(SettingsCLIClassify.importResult(run(2, ["ok": false, "code": "wrong_passphrase", "error": "No."])),
                       .passphrase(wrong: true, message: "No."))
        XCTAssertEqual(SettingsCLIClassify.importResult(run(5, ["ok": false, "code": "running", "error": "Quit PSN Tools, then restore its settings."])),
                       .appOpen("Quit PSN Tools, then restore its settings."))
        XCTAssertEqual(SettingsCLIClassify.importResult(run(3, ["ok": false, "code": "wrong_app", "error": "For DMX Tools."])),
                       .failed("For DMX Tools."))
        XCTAssertEqual(SettingsCLIClassify.importResult(run(nil, nil, timedOut: true)), .unsupported)
        XCTAssertEqual(SuiteBackup.line(id: "a", name: "A", .ok(attention: [], warnings: [], restartNeeded: false)).state, .ok)
        XCTAssertEqual(SuiteBackup.line(id: "a", name: "A", .ok(attention: ["x"], warnings: [], restartNeeded: false)).state, .attention)
        XCTAssertEqual(SuiteBackup.line(id: "a", name: "A", .appOpen("x")).state, .retry)
    }

    func testARunThatNeverAnswersIsStopped() throws {
        let script = dir.appendingPathComponent("old-app")
        try "#!/bin/sh\nsleep 30\n".write(to: script, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        let started = Date()
        let r = SettingsCLI.runBlocking(script, ["--settings-describe"], passphrase: nil, timeout: 1)
        XCTAssertTrue(r.timedOut)
        XCTAssertLessThan(Date().timeIntervalSince(started), 10)
        XCTAssertEqual(SettingsCLIClassify.describe(r), .unsupported)
    }

    func testThePassphraseGoesOnStdinNeverTheCommandLine() throws {
        let script = dir.appendingPathComponent("echo-app")
        // Writes its args and its stdin line into the result, like a tiny app would.
        try """
        #!/bin/sh
        read line
        out=""
        while [ $# -gt 0 ]; do if [ "$1" = "--result" ]; then out="$2"; fi; shift; done
        printf '{"ok": true, "stdin": "%s"}' "$line" > "$out"
        """.write(to: script, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        let pw = "synthetic words"
        let r = SettingsCLI.runBlocking(script, ["--settings-export", "x"], passphrase: pw, timeout: 10)
        XCTAssertEqual(r.exitCode, 0)
        XCTAssertEqual(r.result?["stdin"] as? String, "synthetic words")
    }

    // MARK: Stagehand's files

    private func app(_ id: String, since: String?, variants: [AppVariant]? = nil) -> CatalogApp {
        var a = CatalogApp(id: id, name: id.uppercased(), blurb: "", category: nil, whatsNew: nil, whatsNewVersion: nil,
                           owner: "o", repo: "r", assets: [:], variants: variants)
        a.claudeSince = since
        return a
    }

    private func fakeApp(_ name: String) throws -> URL {
        let bundle = dir.appendingPathComponent("\(name).app", isDirectory: true)
        try FileManager.default.createDirectory(at: bundle.appendingPathComponent("Contents/MacOS"), withIntermediateDirectories: true)
        try "#!/bin/sh\n".write(to: bundle.appendingPathComponent("Contents/MacOS/\(name)"), atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: bundle.appendingPathComponent("Contents/MacOS/\(name)").path)
        return bundle
    }

    func testConnectorsListOnlyConnectorBuildsFullFirst() throws {
        let light = try fakeApp("PDF"), full = try fakeApp("PDF Full"), old = try fakeApp("DMX"), sh = try fakeApp("Stagehand")
        let variants = [AppVariant(id: "light", label: "Light", assets: [:]), AppVariant(id: "full", label: "Full", assets: [:])]
        let apps = [app("pdftools", since: "v0.10.0", variants: variants), app("dmxtools", since: "v1.5.0"),
                    app("stagehand", since: "v0.1.0"), app("helocontrol", since: nil)]
        let rec = { (v: String, p: URL) in InstalledRecord(version: v, path: p.path, installedAt: "") }
        let manifest = ["pdftools": rec("v0.10.0", light), "pdftools@full": rec("v0.10.1-dev.2", full),
                        "dmxtools": rec("v1.4.0", old), "stagehand": rec("v0.1.0-dev.1", sh), "helocontrol": rec("v2.0.0", old)]
        let e = Connectors.entries(apps: apps, manifest: manifest)
        XCTAssertEqual(Set(e.keys), ["pdftools"], "DMX is older than its first connector build; Stagehand's dev build predates v0.1.0")
        XCTAssertEqual(e["pdftools"]?["edition"] as? String, "full")
        XCTAssertEqual(e["pdftools"]?["command"] as? String, full.appendingPathComponent("Contents/MacOS/PDF Full").path)
        XCTAssertEqual(e["pdftools"]?["args"] as? [String], ["--mcp"])

        let url = dir.appendingPathComponent("connectors.json")
        Connectors.sync(apps: apps, manifest: manifest, at: url)
        let written = try XCTUnwrap(SuiteBackup.jsonObject(Data(contentsOf: url)))
        XCTAssertEqual(written["schema"] as? Int, 1)
        XCTAssertEqual((written["apps"] as? [String: Any])?.keys.sorted(), ["pdftools"])
    }

    func testSuiteShowLockKeepsTheRestOfClaudeJson() throws {
        let url = dir.appendingPathComponent("claude.json")
        XCTAssertNil(ClaudeSettingsFile.suiteShowLock(at: url))
        ClaudeSettingsFile.setSuiteShowLock(true, at: url)
        XCTAssertEqual(ClaudeSettingsFile.suiteShowLock(at: url), true)
        let perms = try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(perms?.intValue, 0o600)
        // Stagehand's own settings survive the launcher's write.
        try SuiteBackup.jsonData(["schema": 1, "enabled": true, "showLock": true,
                                  "apps": ["dmxtools": ["showLock": true, "perms": ["output": "ask"]]]] as [String: Any])
            .write(to: url)
        ClaudeSettingsFile.setSuiteShowLock(false, at: url)
        let d = try XCTUnwrap(SuiteBackup.jsonObject(Data(contentsOf: url)))
        XCTAssertEqual(d["showLock"] as? Bool, false)
        XCTAssertEqual(d["enabled"] as? Bool, true)
        XCTAssertEqual(((d["apps"] as? [String: Any])?["dmxtools"] as? [String: Any])?["showLock"] as? Bool, true)
        // A damaged file never blocks the switch: it becomes a fresh, disconnected one.
        try Data("{broken".utf8).write(to: url)
        ClaudeSettingsFile.setSuiteShowLock(true, at: url)
        let fresh = try XCTUnwrap(SuiteBackup.jsonObject(Data(contentsOf: url)))
        XCTAssertEqual(fresh["showLock"] as? Bool, true)
        XCTAssertEqual(fresh["enabled"] as? Bool, false)
    }
}

extension SuiteBackupTests {
    /// An app too old for the flags can exit before reading the passphrase line: the launcher must shrug, not crash.
    func testAnAppThatExitsAtOnceDoesNotTakeTheLauncherDown() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("jbtt-pipe-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let script = dir.appendingPathComponent("quick-app")
        try "#!/bin/sh\nexec 0<&-\nexit 0\n".write(to: script, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        for _ in 0..<5 {
            let r = SettingsCLI.runBlocking(script, ["--settings-import", "x"], passphrase: String(repeating: "p", count: 100_000), timeout: 10)
            XCTAssertEqual(SettingsCLIClassify.importResult(r), .unsupported)
        }
    }
}
