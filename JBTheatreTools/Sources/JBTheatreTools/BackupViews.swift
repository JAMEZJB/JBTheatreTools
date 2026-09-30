import SwiftUI
import AppKit
import UniformTypeIdentifiers

// Settings → "Back Up All Apps…" / "Restore All Apps…" (see SettingsBackup.swift for the work itself).

extension SuiteBackup {
    static var bundleType: UTType { UTType(filenameExtension: bundleExt) ?? .data }

    /// ~/Documents/JB Theatre Tools — where a backup is offered first (house rule: user output goes to Documents).
    static var documentsFolder: URL {
        let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        return docs.appendingPathComponent("JB Theatre Tools", isDirectory: true)
    }
}

// MARK: - Back up all apps

@MainActor
final class BackupAllModel: ObservableObject {
    struct Row: Identifiable {
        let target: BackupTarget
        var probe: AppBackupProbe?
        var slot: BackupSlot?
        var chosen = false
        var id: String { target.id }
    }
    enum Phase: Equatable {
        case choosing
        case running(String)
        case done(lines: [SuiteBackupLine], file: URL?)
    }

    @Published var rows: [Row] = []
    @Published var includeLauncher = true
    @Published var includeSecrets = false
    @Published var passphrase = ""
    @Published var confirm = ""
    @Published var phase: Phase = .choosing
    @Published var error: String?
    let launcherSecrets: Int

    init(targets: [BackupTarget]) {
        rows = targets.map { Row(target: $0) }
        launcherSecrets = KeychainLauncherSecrets().present().count
    }

    var checking: Bool { rows.contains { $0.probe == nil } }

    /// Asks every installed app what it would save — three at a time, so a Back up doesn't start every app at once.
    func probeAll() async {
        let targets = rows.map(\.target)
        await withTaskGroup(of: (String, BackupSlot?, AppBackupProbe).self) { group in
            var next = 0
            func add() {
                guard next < targets.count else { return }
                let t = targets[next]
                next += 1
                group.addTask { let r = await SuiteBackup.probe(t); return (t.id, r.slot, r.probe) }
            }
            for _ in 0..<3 { add() }
            for await (id, slot, probe) in group {
                record(id, slot, probe)
                add()
            }
        }
    }

    private func record(_ id: String, _ slot: BackupSlot?, _ probe: AppBackupProbe) {
        guard let i = rows.firstIndex(where: { $0.id == id }) else { return }
        rows[i].probe = probe
        rows[i].slot = slot
        if case .ready = probe { rows[i].chosen = true }
    }

    var secretCount: Int {
        rows.reduce(includeLauncher ? launcherSecrets : 0) { n, r in
            guard r.chosen, case .ready(_, let c) = r.probe else { return n }
            return n + c
        }
    }

    var passphraseProblem: String? {
        guard includeSecrets, !passphrase.isEmpty || !confirm.isEmpty else { return nil }
        if passphrase.contains(where: \.isNewline) { return "A passphrase can't contain line breaks." }
        return passphrase == confirm ? nil : "The two passphrases don't match."
    }

    var canBackUp: Bool {
        !checking && (includeLauncher || rows.contains { $0.chosen }) && passphraseProblem == nil
    }

    func backUp(version: String) async {
        let panel = NSSavePanel()
        panel.title = "Back Up All Apps"
        panel.nameFieldStringValue = SuiteBackup.bundleFileName()
        panel.allowedContentTypes = [SuiteBackup.bundleType]
        panel.canCreateDirectories = true
        try? FileManager.default.createDirectory(at: SuiteBackup.documentsFolder, withIntermediateDirectories: true)
        panel.directoryURL = SuiteBackup.documentsFolder
        guard panel.runModal() == .OK, let dest = panel.url else { return }
        let chosen = rows.compactMap { r -> (target: BackupTarget, slot: BackupSlot)? in
            guard r.chosen, let slot = r.slot else { return nil }
            return (r.target, slot)
        }
        phase = .running("Starting\u{2026}")
        error = nil
        let pass = includeSecrets && !passphrase.isEmpty ? passphrase : nil
        do {
            let lines = try await SuiteBackup.backUpAll(
                chosen, includeLauncher: includeLauncher, prefs: UserDefaults.standard, secrets: KeychainLauncherSecrets(),
                launcherVersion: version, includeSecrets: includeSecrets, passphrase: pass, dest: dest,
                progress: { msg in Task { @MainActor in self.phase = .running(msg) } })
            passphrase = ""
            confirm = ""
            AppLog.shared.log("backed up \(chosen.count) app(s)\(includeLauncher ? " + the launcher" : "") to \(dest.lastPathComponent)")
            phase = .done(lines: lines, file: dest)
        } catch {
            AppLog.shared.log("back up all FAILED: \(error.localizedDescription)")
            self.error = error.localizedDescription
            phase = .choosing
        }
    }
}

struct BackupAllView: View {
    @EnvironmentObject var state: AppState
    @StateObject var model: BackupAllModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Back Up All Apps").font(JBFont.title).foregroundStyle(Color.jbText)
            switch model.phase {
            case .choosing: choosing
            case .running(let msg): running(msg)
            case .done(let lines, let file): BackupResultList(lines: lines, intro: file.map { "Saved \($0.lastPathComponent)." })
                HStack {
                    if let file {
                        Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([file]) }
                            .buttonStyle(.jbSecondary)
                    }
                    Spacer()
                    Button("Done") { dismiss() }.buttonStyle(.jbPrimary).keyboardShortcut(.defaultAction)
                }
            }
        }
        .padding(22)
        .frame(width: 560, height: 520, alignment: .topLeading)
        .background(Color.jbGround)
        .tint(.jbAccent)
        .task { await model.probeAll() }
    }

    @ViewBuilder private var choosing: some View {
        Text("Saves each app's settings, and this launcher's, into one file you can restore on this Mac or a new show computer. Nothing is changed.")
            .font(JBFont.small).foregroundStyle(Color.jbText2).fixedSize(horizontal: false, vertical: true)
        ScrollView {
            VStack(alignment: .leading, spacing: 8) {
                Toggle(isOn: $model.includeLauncher) {
                    rowLabel("JB Theatre Tools", "This launcher's settings: list order, pins, editions, updates, appearance")
                }
                ForEach($model.rows) { $row in
                    Toggle(isOn: $row.chosen) { rowLabel(row.target.name, detail(row)) }
                        .disabled(!isReady(row))
                }
                if model.rows.isEmpty {
                    Text("No apps are installed, so only the launcher's settings can be backed up.")
                        .font(JBFont.small).foregroundStyle(Color.jbText2)
                }
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).fill(Color.jbSurface))
        .overlay(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).strokeBorder(Color.jbLine))
        if model.secretCount > 0 {
            Toggle("Include saved passwords (\(model.secretCount))", isOn: $model.includeSecrets)
                .font(JBFont.body)
            if model.includeSecrets {
                HStack {
                    SecureField("Passphrase (optional)", text: $model.passphrase).textFieldStyle(.roundedBorder)
                    SecureField("Confirm passphrase", text: $model.confirm).textFieldStyle(.roundedBorder)
                }
                Text(model.passphrase.isEmpty
                     ? "Leave blank to save the passwords unprotected \u{2014} anyone with the file can read them."
                     : "You'll need this passphrase to restore the passwords. It isn't saved anywhere.")
                    .font(JBFont.small).foregroundStyle(model.passphrase.isEmpty ? Color.jbWarn : Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
                if let p = model.passphraseProblem {
                    Text(p).font(JBFont.small).foregroundStyle(Color.jbDanger)
                }
            }
        }
        if let e = model.error {
            Text(e).font(JBFont.small).foregroundStyle(Color.jbDanger).fixedSize(horizontal: false, vertical: true)
        }
        Spacer(minLength: 0)
        HStack {
            if model.checking {
                Text("Checking which apps can back up\u{2026}").font(JBFont.small).foregroundStyle(Color.jbText2)
            }
            Spacer()
            // House rule: the action is filled, the safe choice has Return.
            Button("Cancel") { dismiss() }.buttonStyle(.jbSecondary).keyboardShortcut(.defaultAction)
            Button("Back Up\u{2026}") { Task { await model.backUp(version: state.currentVersion) } }
                .buttonStyle(.jbPrimary)
                .disabled(!model.canBackUp)
        }
        .onExitCommand { dismiss() }
    }

    private func running(_ msg: String) -> some View {
        VStack(spacing: 12) {
            Spacer()
            ProgressView().controlSize(.small)
            Text(msg).font(JBFont.body).foregroundStyle(Color.jbText2)
            Spacer()
        }
        .frame(maxWidth: .infinity)
    }

    private func isReady(_ row: BackupAllModel.Row) -> Bool {
        if case .ready = row.probe { return true }
        return false
    }

    private func detail(_ row: BackupAllModel.Row) -> String {
        switch row.probe {
        case nil: return "Checking\u{2026}"
        case .ready(let items, let n)?:
            let what = items.isEmpty ? "Nothing saved yet" : items.joined(separator: ", ")
            return n > 0 ? "\(what) \u{00B7} \(n) saved password\(n == 1 ? "" : "s")" : what
        case .unsupported?: return SettingsCLIClassify.unsupportedMessage
        case .failed(let m)?: return m
        }
    }

    private func rowLabel(_ title: String, _ detail: String) -> some View {
        VStack(alignment: .leading, spacing: 1) {
            Text(title).font(JBFont.bodyMedium).foregroundStyle(Color.jbText)
            Text(detail).font(JBFont.small).foregroundStyle(Color.jbText2).fixedSize(horizontal: false, vertical: true)
        }
    }
}

/// The combined result: one line per app, with what to check.
struct BackupResultList: View {
    let lines: [SuiteBackupLine]
    let intro: String?

    var body: some View {
        if let intro { Text(intro).font(JBFont.body).foregroundStyle(Color.jbText) }
        ScrollView {
            VStack(alignment: .leading, spacing: 10) {
                ForEach(lines) { line in
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Image(systemName: symbol(line.state)).foregroundStyle(color(line.state)).frame(width: 16)
                        VStack(alignment: .leading, spacing: 2) {
                            Text("\(line.name) \u{2014} \(title(line.state))").font(JBFont.bodyMedium).foregroundStyle(Color.jbText)
                            ForEach(Array(line.detail.enumerated()), id: \.offset) { _, d in
                                Text(d).font(JBFont.small).foregroundStyle(Color.jbText2)
                                    .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                            }
                        }
                    }
                }
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).fill(Color.jbSurface))
        .overlay(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).strokeBorder(Color.jbLine))
    }

    private func title(_ s: SuiteBackupLine.State) -> String {
        switch s {
        case .ok: return "done"
        case .attention: return "done, check these in its Settings"
        case .unsupported: return "not yet"
        case .failed: return "failed"
        case .skipped: return "skipped"
        case .retry: return "not done yet"
        }
    }
    private func symbol(_ s: SuiteBackupLine.State) -> String {
        switch s {
        case .ok: return "checkmark.circle.fill"
        case .attention: return "exclamationmark.circle.fill"
        case .unsupported, .skipped: return "minus.circle"
        case .failed: return "xmark.octagon.fill"
        case .retry: return "arrow.clockwise.circle"
        }
    }
    private func color(_ s: SuiteBackupLine.State) -> Color {
        switch s {
        case .ok: return .jbOk
        case .attention, .retry: return .jbWarn
        case .unsupported, .skipped: return .jbText3
        case .failed: return .jbDanger
        }
    }
}

// MARK: - Restore all apps

@MainActor
final class RestoreAllModel: ObservableObject {
    struct Row: Identifiable {
        let app: SuiteBundleApp
        let file: URL
        let info: SuiteBundle.AppFileInfo
        let catalogApp: CatalogApp?
        var target: BackupTarget?
        var include = true
        var installFirst = true
        var open = false
        var id: String { app.id }
    }
    enum Phase: Equatable {
        case choosing
        case running(String)
        case done
    }

    let opened: SuiteBundle.Opened
    let launcherInfo: LauncherSettings.Info?
    let launcherData: Data?
    @Published var rows: [Row]
    @Published var includeLauncher: Bool
    @Published var restoreSecrets = true
    @Published var passphrase = ""
    @Published var passError: String?
    @Published var phase: Phase = .choosing
    @Published var lines: [SuiteBackupLine] = []
    /// Apps (and the launcher) whose restore has finished one way or another — Try Again re-runs only the rest.
    private var finished = Set<String>()

    init(opened: SuiteBundle.Opened, apps: [CatalogApp], targets: [BackupTarget]) {
        self.opened = opened
        rows = opened.manifest.apps.map { a in
            let f = opened.url(a.file)
            return Row(app: a, file: f, info: SuiteBundle.appFileInfo(f),
                       catalogApp: apps.first { $0.id == a.id }, target: targets.first { $0.id == a.id })
        }
        if let lf = opened.manifest.launcherFile, let data = try? Data(contentsOf: opened.url(lf)),
           let info = try? LauncherSettings.inspect(data).info {
            launcherData = data
            launcherInfo = info
        } else {
            launcherData = nil
            launcherInfo = nil
        }
        includeLauncher = launcherInfo != nil
        refreshOpen()
    }

    deinit { try? FileManager.default.removeItem(at: opened.folder) }

    var source: String {
        let m = opened.manifest
        var parts = [m.source["machine"] ?? "another computer"]
        if let os = m.source["os"] { parts.append(os) }
        if let d = ISO8601DateFormatter().date(from: m.created) {
            parts.append(d.formatted(date: .abbreviated, time: .shortened))
        }
        return parts.joined(separator: " \u{00B7} ")
    }

    var hasSecrets: Bool {
        rows.contains { $0.include && $0.info.hasSecrets } || (includeLauncher && launcherInfo?.hasSecrets == true)
    }
    var needsPassphrase: Bool {
        restoreSecrets && (rows.contains { $0.include && $0.info.protected } || (includeLauncher && launcherInfo?.protected == true))
    }
    var canRestore: Bool {
        (includeLauncher || rows.contains { $0.include }) && (!needsPassphrase || !passphrase.isEmpty)
    }

    /// Marks apps that are open right now (a restore can't change an open app's settings).
    func refreshOpen() {
        for i in rows.indices {
            rows[i].open = rows[i].target?.slots.contains { InstallManager.shared.runningInstance($0.installKey) != nil } ?? false
        }
    }

    private func setLine(_ line: SuiteBackupLine) {
        if let i = lines.firstIndex(where: { $0.id == line.id }) { lines[i] = line } else { lines.append(line) }
        if line.state != .retry { finished.insert(line.id) }
    }

    var hasRetry: Bool { lines.contains { $0.state == .retry } }

    func run(state: AppState) async {
        passError = nil
        let pass = passphrase.isEmpty ? nil : passphrase
        // A wrong passphrase stops before anything changes (checked on the launcher's own sealed passwords).
        if includeLauncher, let data = launcherData, !finished.contains(SuiteBackup.launcherId) {
            do { try LauncherSettings.verifyPassphrase(data, passphrase: pass, restoreSecrets: restoreSecrets) }
            catch { passError = error.localizedDescription; return }
        }
        phase = .running("Starting\u{2026}")
        refreshOpen()
        for idx in rows.indices where rows[idx].include && !finished.contains(rows[idx].id) {
            let row = rows[idx]
            let name = row.catalogApp?.name ?? row.app.name
            guard row.info.readable, row.info.appId == row.app.id else {
                setLine(SuiteBackupLine(id: row.id, name: name, state: .failed, detail: ["This part of the backup can't be read."]))
                continue
            }
            guard let app = row.catalogApp else {
                setLine(SuiteBackupLine(id: row.id, name: name, state: .skipped, detail: ["This app isn't in this launcher's list."]))
                continue
            }
            var target = row.target
            if target == nil {
                guard row.installFirst else {
                    setLine(SuiteBackupLine(id: row.id, name: name, state: .skipped, detail: ["Not installed here, so it was skipped."]))
                    continue
                }
                phase = .running("Installing \(name)\u{2026}")
                let edition = row.app.edition.flatMap { e in app.variants?.contains { $0.id == e } == true ? e : nil }
                await state.install(app.id, variantOverride: edition)
                target = SuiteBackup.targets(apps: [app], installedVersion: { InstallManager.shared.installedVersion($0) },
                                             installedPath: { InstallManager.shared.installedPath($0) }).first
                rows[idx].target = target
                guard target != nil else {
                    setLine(SuiteBackupLine(id: row.id, name: name, state: .failed,
                                            detail: ["It couldn't be installed, so its settings weren't restored. Install it from the list, then restore again."]))
                    continue
                }
            }
            guard let target else { continue }
            if target.slots.contains(where: { InstallManager.shared.runningInstance($0.installKey) != nil }) {
                rows[idx].open = true
                setLine(SuiteBackupLine(id: row.id, name: name, state: .retry,
                                        detail: ["\(name) is open. Quit it, then press Try Again."]))
                continue
            }
            phase = .running("Restoring \(name)\u{2026}")
            let outcome = await SuiteBackup.restoreApp(file: row.file, slots: target.slots, passphrase: pass,
                                                       restoreSecrets: restoreSecrets)
            if case .passphrase(_, let message) = outcome {
                // Nothing changed for this app; ask again and carry on from here.
                passError = message
                phase = lines.isEmpty ? .choosing : .done
                return
            }
            AppLog.shared.log("restore \(row.id): \(outcome)")
            setLine(SuiteBackup.line(id: row.id, name: name, outcome))
        }
        if includeLauncher, let data = launcherData, !finished.contains(SuiteBackup.launcherId) {
            phase = .running("Restoring JB Theatre Tools\u{2026}")
            do {
                let o = try LauncherSettings.restore(data, prefs: UserDefaults.standard, secrets: KeychainLauncherSecrets(),
                                                     passphrase: pass, restoreSecrets: restoreSecrets)
                setLine(SuiteBackupLine(id: SuiteBackup.launcherId, name: SuiteBackup.launcherName,
                                        state: o.attention.isEmpty ? .ok : .attention,
                                        detail: SettingsCLIClassify.lines(o.attention) + o.warnings))
                state.reloadPreferences()
            } catch {
                setLine(SuiteBackupLine(id: SuiteBackup.launcherId, name: SuiteBackup.launcherName, state: .failed,
                                        detail: [error.localizedDescription]))
            }
        }
        passphrase = ""
        phase = .done
    }
}

struct RestoreAllView: View {
    @EnvironmentObject var state: AppState
    @StateObject var model: RestoreAllModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Restore All Apps").font(JBFont.title).foregroundStyle(Color.jbText)
            Text("From \(model.source)").font(JBFont.small).foregroundStyle(Color.jbText2)
            switch model.phase {
            case .choosing: choosing
            case .running(let msg):
                VStack(spacing: 12) {
                    Spacer()
                    ProgressView().controlSize(.small)
                    Text(msg).font(JBFont.body).foregroundStyle(Color.jbText2)
                    Spacer()
                }
                .frame(maxWidth: .infinity)
            case .done: done
            }
        }
        .padding(22)
        .frame(width: 560, height: 540, alignment: .topLeading)
        .background(Color.jbGround)
        .tint(.jbAccent)
    }

    @ViewBuilder private var choosing: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 10) {
                ForEach($model.rows) { $row in
                    VStack(alignment: .leading, spacing: 4) {
                        Toggle(isOn: $row.include) {
                            VStack(alignment: .leading, spacing: 1) {
                                Text(row.catalogApp?.name ?? row.app.name).font(JBFont.bodyMedium).foregroundStyle(Color.jbText)
                                Text(status(row)).font(JBFont.small)
                                    .foregroundStyle(row.open ? Color.jbWarn : Color.jbText2)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                        }
                        if row.include, row.target == nil, row.catalogApp != nil {
                            Toggle("Install it first", isOn: $row.installFirst)
                                .font(JBFont.small).padding(.leading, 20)
                        }
                    }
                }
                if model.launcherInfo != nil {
                    Toggle(isOn: $model.includeLauncher) {
                        VStack(alignment: .leading, spacing: 1) {
                            Text("JB Theatre Tools").font(JBFont.bodyMedium).foregroundStyle(Color.jbText)
                            Text("This launcher's settings \u{2014} restored last").font(JBFont.small).foregroundStyle(Color.jbText2)
                        }
                    }
                }
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).fill(Color.jbSurface))
        .overlay(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).strokeBorder(Color.jbLine))
        if model.hasSecrets {
            Toggle("Restore saved passwords", isOn: $model.restoreSecrets).font(JBFont.body)
        }
        if model.needsPassphrase {
            SecureField("Backup passphrase", text: $model.passphrase).textFieldStyle(.roundedBorder)
        }
        if let e = model.passError {
            Text(e).font(JBFont.small).foregroundStyle(Color.jbDanger).fixedSize(horizontal: false, vertical: true)
        }
        Text("Each app keeps a copy of its current settings first, so its own Settings can undo the restore.")
            .font(JBFont.small).foregroundStyle(Color.jbText2).fixedSize(horizontal: false, vertical: true)
        Spacer(minLength: 0)
        HStack {
            Button("Check Again") { model.refreshOpen() }.buttonStyle(.jbSecondary)
                .opacity(model.rows.contains { $0.open } ? 1 : 0)
            Spacer()
            Button("Cancel") { dismiss() }.buttonStyle(.jbSecondary).keyboardShortcut(.defaultAction)
            Button("Restore") { Task { await model.run(state: state) } }
                .buttonStyle(.jbPrimary)
                .disabled(!model.canRestore || state.showLock)
        }
        .onExitCommand { dismiss() }
    }

    @ViewBuilder private var done: some View {
        BackupResultList(lines: model.lines, intro: nil)
        if let e = model.passError {
            SecureField("Backup passphrase", text: $model.passphrase).textFieldStyle(.roundedBorder)
            Text(e).font(JBFont.small).foregroundStyle(Color.jbDanger)
        }
        HStack {
            Spacer()
            if model.hasRetry || model.passError != nil {
                Button("Try Again") { Task { await model.run(state: state) } }
                    .buttonStyle(.jbSecondary)
                    .disabled(model.passError != nil && model.passphrase.isEmpty)
            }
            Button("Done") { dismiss() }.buttonStyle(.jbPrimary).keyboardShortcut(.defaultAction)
        }
    }

    private func status(_ row: RestoreAllModel.Row) -> String {
        let from = row.app.version.isEmpty ? "" : "Backed up from \(row.app.version). "
        if !row.info.readable { return "This part of the backup can't be read." }
        if row.catalogApp == nil { return from + "Not in this launcher's list \u{2014} it will be skipped." }
        if row.target == nil {
            return from + (row.installFirst ? "Not installed \u{2014} it will be installed first." : "Not installed \u{2014} it will be skipped.")
        }
        if row.open { return "Open \u{2014} quit it before restoring." }
        return from + "Installed."
    }
}
