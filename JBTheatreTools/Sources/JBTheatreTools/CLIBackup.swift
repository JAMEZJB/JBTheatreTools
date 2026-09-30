import Foundation

/// `--backup-all` / `--restore-all`: the same work as Settings → "Back up all apps…" / "Restore all apps…", headless.
/// The command line never installs anything: an app in the backup that isn't installed here is listed and skipped.
extension CLI {
    static func backupCommand(_ cmd: String, catalog: Catalog, args: [String], file: String?) -> Int32 {
        var path: String?
        var only: Set<String>?
        var includeSecrets = false, noSecrets = false, noLauncher = false, stdinPass = false
        var i = 0
        while i < args.count {
            let a = args[i]
            switch a {
            case cmd: break
            case "--include-passwords": includeSecrets = true
            case "--no-passwords": noSecrets = true
            case "--no-launcher": noLauncher = true
            case "--passphrase-stdin": stdinPass = true
            case "--apps":
                i += 1
                if i < args.count { only = Set(args[i].split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }) }
            case "--catalog", "--token", "--server", "--server-pass", "--tag": i += 1
            default: if !a.hasPrefix("--"), path == nil { path = a }
            }
            i += 1
        }
        guard let path = path ?? file else {
            fputs("error: give the backup file: \(cmd) <file>\n", stderr)
            return 64
        }
        // The backup's passphrase: one stdin line, else the environment — never the command line.
        let entered = stdinPass ? readLine(strippingNewline: true) : ProcessInfo.processInfo.environment["JBTT_SETTINGS_PASSPHRASE"]
        let pass = entered.flatMap { $0.isEmpty ? nil : $0 }
        // Test home: the launcher's own settings + passwords come from files in it, never the real ones.
        let testHome = ProcessInfo.processInfo.environment["JBTT_BACKUP_TEST_HOME"].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0) }
        let prefs: LauncherPrefs = testHome.map { FilePrefs(url: $0.appendingPathComponent("prefs.json")) } ?? UserDefaults.standard
        let secrets: LauncherSecrets = testHome.map { FileLauncherSecrets(url: $0.appendingPathComponent("secrets.json")) }
            ?? KeychainLauncherSecrets()
        let version = (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "0.0.0"
        let im = InstallManager.shared
        let targets = SuiteBackup.targets(apps: catalog.apps, installedVersion: { im.installedVersion($0) },
                                          installedPath: { im.installedPath($0) })
            .filter { only?.contains($0.id) ?? true }
        var code: Int32 = 0
        let url = URL(fileURLWithPath: (path as NSString).expandingTildeInPath)
        func show(_ lines: [SuiteBackupLine]) {
            for l in lines {
                let tag: String
                switch l.state {
                case .ok: tag = "OK"
                case .attention: tag = "CHECK"
                case .unsupported: tag = "NOT YET"
                case .failed: tag = "FAILED"
                case .skipped: tag = "SKIPPED"
                case .retry: tag = "AGAIN"
                case .empty: tag = "EMPTY"
                }
                print("  \(tag.padding(toLength: 8, withPad: " ", startingAt: 0)) \(l.name)")
                for d in l.detail { print("           \(d)") }
                if l.state == .failed { code = 1 }
            }
        }
        if cmd == "--backup-all" {
            runBlockingBackup {
                var chosen: [(target: BackupTarget, slot: BackupSlot)] = []
                var lines: [SuiteBackupLine] = []
                for t in targets {
                    let (slot, probe) = await SuiteBackup.probe(t)
                    if let slot { chosen.append((t, slot)); continue }
                    switch probe {
                    case .failed(let m): lines.append(SuiteBackupLine(id: t.id, name: t.name, state: .failed, detail: [m]))
                    default: lines.append(SuiteBackupLine(id: t.id, name: t.name, state: .unsupported,
                                                          detail: [SettingsCLIClassify.unsupportedMessage]))
                    }
                }
                do {
                    lines += try await SuiteBackup.backUpAll(chosen, includeLauncher: !noLauncher, prefs: prefs, secrets: secrets,
                                                             launcherVersion: version, includeSecrets: includeSecrets,
                                                             passphrase: pass, dest: url)
                    print("Saved \(url.path)")
                } catch {
                    fputs("error: \(error.localizedDescription)\n", stderr)
                    code = 1
                }
                show(lines)
            }
            return code
        }
        // --restore-all
        let opened: SuiteBundle.Opened
        do { opened = try SuiteBundle.open(url) } catch {
            fputs("error: \(error.localizedDescription)\n", stderr)
            return 4
        }
        defer { try? FileManager.default.removeItem(at: opened.folder) }
        let m = opened.manifest
        print("Backup from \(m.source["machine"] ?? "another computer") (\(m.source["os"] ?? "?")), \(m.created)")
        // A wrong passphrase stops here, before any app's settings change.
        if !noLauncher, let lf = m.launcherFile, let data = try? Data(contentsOf: opened.url(lf)) {
            do { try LauncherSettings.verifyPassphrase(data, passphrase: pass, restoreSecrets: !noSecrets) }
            catch let e as SuiteBackup.Failure where e == .wrongPassphrase || e == .passphraseNeeded {
                fputs("error: \(e.localizedDescription) Nothing was changed.\n", stderr)
                return 2
            } catch {}
        }
        runBlockingBackup {
            var lines: [SuiteBackupLine] = []
            for app in m.apps where only?.contains(app.id) ?? true {
                guard let t = targets.first(where: { $0.id == app.id }) else {
                    lines.append(SuiteBackupLine(id: app.id, name: app.name, state: .skipped,
                                                 detail: ["Not installed on this computer \u{2014} install it, then restore again."]))
                    continue
                }
                let outcome = await SuiteBackup.restoreApp(file: opened.url(app.file), slots: t.slots,
                                                            passphrase: pass, restoreSecrets: !noSecrets)
                if case .passphrase(_, _) = outcome { code = 2 }
                if case .appOpen = outcome, code == 0 { code = 5 }
                lines.append(SuiteBackup.line(id: app.id, name: t.name, outcome))
            }
            if !noLauncher, let lf = m.launcherFile, let data = try? Data(contentsOf: opened.url(lf)) {
                do {
                    let o = try LauncherSettings.restore(data, prefs: prefs, secrets: secrets, passphrase: pass,
                                                         restoreSecrets: !noSecrets)
                    let detail = SettingsCLIClassify.lines(o.attention) + o.warnings
                    lines.append(SuiteBackupLine(id: SuiteBackup.launcherId, name: SuiteBackup.launcherName,
                                                 state: o.attention.isEmpty ? .ok : .attention, detail: detail))
                } catch SuiteBackup.Failure.wrongPassphrase {
                    code = 2
                    lines.append(SuiteBackupLine(id: SuiteBackup.launcherId, name: SuiteBackup.launcherName, state: .retry,
                                                 detail: [SuiteBackup.Failure.wrongPassphrase.localizedDescription]))
                } catch SuiteBackup.Failure.passphraseNeeded {
                    code = 2
                    lines.append(SuiteBackupLine(id: SuiteBackup.launcherId, name: SuiteBackup.launcherName, state: .retry,
                                                 detail: [SuiteBackup.Failure.passphraseNeeded.localizedDescription]))
                } catch {
                    lines.append(SuiteBackupLine(id: SuiteBackup.launcherId, name: SuiteBackup.launcherName, state: .failed,
                                                 detail: [error.localizedDescription]))
                }
            }
            show(lines)
        }
        return code
    }

    private static func runBlockingBackup(_ body: @escaping () async -> Void) {
        let sem = DispatchSemaphore(value: 0)
        Task { await body(); sem.signal() }
        sem.wait()
    }
}
