import SwiftUI
import AppKit
import UniformTypeIdentifiers

// Settings → Settings backup: "Back up all apps…" / "Restore all apps…" (the work itself is SettingsBackup.swift).
// House rules: the action is the filled button; Cancel is the safe choice and Esc; Return presses nothing in a
// passphrase field (no default button).

extension SuiteBackup {
    static var bundleType: UTType { UTType(filenameExtension: bundleExt) ?? .data }

    /// ~/Documents/JB Theatre Tools — where a backup is offered first (house rule: user output goes to Documents).
    static var documentsFolder: URL {
        let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        return docs.appendingPathComponent("JB Theatre Tools", isDirectory: true)
    }
}

/// The one-line words for a result, backup or restore.
enum BackupWords {
    static func backup(_ l: SuiteBackupLine) -> String {
        switch l.state {
        case .ok, .attention: return "Saved"
        case .empty: return "Nothing to back up"
        case .unsupported: return "Not backed up \u{2014} this app's version can't back up its settings yet; update it"
        case .failed: return "Failed \u{2014} " + (l.detail.first ?? "it stopped with an error")
        case .skipped, .retry: return "Not backed up"
        }
    }

    static func restore(_ l: SuiteBackupLine) -> String {
        switch l.state {
        case .ok, .empty: return "Restored"
        case .attention: return "Restored \u{2014} check these in Settings:"
        case .unsupported: return "Not restored \u{2014} this app's version can't restore settings yet; update it, then try again"
        case .failed: return "Failed \u{2014} " + (l.detail.first ?? "it stopped with an error")
        case .skipped: return "Skipped" + (l.detail.first.map { " \u{2014} " + $0 } ?? "")
        case .retry: return "Not restored yet \u{2014} " + (l.detail.first ?? "try again")
        }
    }

    /// The lines under the title (the ones already in the title aren't repeated).
    static func detail(_ l: SuiteBackupLine) -> [String] {
        switch l.state {
        case .failed, .skipped, .retry: return Array(l.detail.dropFirst())
        case .unsupported: return []
        default: return l.detail
        }
    }

    static func color(_ s: SuiteBackupLine.State) -> Color {
        switch s {
        case .ok, .empty: return .jbOk
        case .attention, .retry: return .jbWarn
        case .unsupported, .skipped: return .jbText3
        case .failed: return .jbDanger
        }
    }

    static func symbol(_ s: SuiteBackupLine.State) -> String {
        switch s {
        case .ok, .empty: return "checkmark.circle.fill"
        case .attention: return "exclamationmark.circle.fill"
        case .unsupported, .skipped: return "minus.circle"
        case .failed: return "xmark.octagon.fill"
        case .retry: return "arrow.clockwise.circle"
        }
    }
}

/// A rounded panel around a scrolling list, like the Settings panels.
private struct ListPanel<Content: View>: View {
    @ViewBuilder var content: Content
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 10) { content }
                .padding(12)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).fill(Color.jbSurface))
        .overlay(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).strokeBorder(Color.jbLine))
    }
}

private func titledRow(_ title: String, _ detail: String, detailColor: Color = .jbText2) -> some View {
    VStack(alignment: .leading, spacing: 1) {
        Text(title).font(JBFont.bodyMedium).foregroundStyle(Color.jbText)
        Text(detail).font(JBFont.small).foregroundStyle(detailColor).fixedSize(horizontal: false, vertical: true)
    }
}

/// One result line: a mark, "<App> — <words>", and anything to check under it.
struct BackupResultRow: View {
    let line: SuiteBackupLine
    let words: String
    let detail: [String]

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Image(systemName: BackupWords.symbol(line.state)).foregroundStyle(BackupWords.color(line.state)).frame(width: 16)
            VStack(alignment: .leading, spacing: 2) {
                Text("\(line.name) \u{2014} \(words)").font(JBFont.bodyMedium).foregroundStyle(Color.jbText)
                    .fixedSize(horizontal: false, vertical: true)
                ForEach(Array(detail.enumerated()), id: \.offset) { _, d in
                    Text(d).font(JBFont.small).foregroundStyle(Color.jbText2)
                        .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                }
            }
        }
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
        case running
        case done(lines: [SuiteBackupLine], file: URL)
    }

    @Published var rows: [Row] = []
    @Published var includeLauncher = true
    @Published var includeSecrets = false
    @Published var passphrase = ""
    @Published var confirm = ""
    @Published var phase: Phase = .choosing
    /// While it runs: what each app is doing ("Backing up…"), by id ("" = the file itself).
    @Published var progress: [String: String] = [:]
    @Published var error: String?
    let launcherSecrets: Int

    init(targets: [BackupTarget]) {
        rows = targets.map { Row(target: $0) }
        launcherSecrets = KeychainLauncherSecrets().present().count
    }

    var checking: Bool { rows.contains { $0.probe == nil } }

    /// Asks every installed app what it would save — three at a time, so a backup doesn't start every app at once.
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

    var mismatch: Bool { includeSecrets && passphrase != confirm && !(passphrase.isEmpty && confirm.isEmpty) }

    var canBackUp: Bool { !checking && (includeLauncher || rows.contains { $0.chosen }) && !mismatch }

    func backUp(version: String) async {
        guard canBackUp else { return }
        let panel = NSSavePanel()
        panel.title = "Back up all apps"
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
        progress = Dictionary(uniqueKeysWithValues: chosen.map { ($0.target.id, "Waiting") })
        if includeLauncher { progress[SuiteBackup.launcherId] = "Waiting" }
        phase = .running
        error = nil
        let pass = includeSecrets && !passphrase.isEmpty ? passphrase : nil
        do {
            let lines = try await SuiteBackup.backUpAll(
                chosen, includeLauncher: includeLauncher, prefs: UserDefaults.standard, secrets: KeychainLauncherSecrets(),
                launcherVersion: version, includeSecrets: includeSecrets, passphrase: pass, dest: dest,
                progress: { id, msg in Task { @MainActor in self.progress[id] = msg } })
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
            Text("Back up all apps").font(JBFont.title).foregroundStyle(Color.jbText)
            switch model.phase {
            case .choosing: choosing
            case .running: running
            case .done(let lines, let file): done(lines, file)
            }
        }
        .padding(22)
        .frame(width: 560, height: 540, alignment: .topLeading)
        .background(Color.jbGround)
        .tint(.jbAccent)
        .task { await model.probeAll() }
    }

    @ViewBuilder private var choosing: some View {
        Text("Each app saves its own settings into one backup you can restore here or on a new show computer. Nothing is changed.")
            .font(JBFont.small).foregroundStyle(Color.jbText2).fixedSize(horizontal: false, vertical: true)
        ListPanel {
            ForEach($model.rows) { $row in
                Toggle(isOn: $row.chosen) { titledRow(row.target.name, detail(row)) }
                    .disabled(!isReady(row))
            }
            Toggle(isOn: $model.includeLauncher) {
                titledRow("JB Theatre Tools (this launcher)", "List order, pins, editions, update and appearance settings")
            }
        }
        if model.secretCount > 0 {
            Toggle("Include saved passwords (\(model.secretCount))", isOn: $model.includeSecrets)
                .font(JBFont.body)
            if model.includeSecrets {
                HStack(alignment: .top) {
                    SecureField("Passphrase (optional)", text: $model.passphrase).textFieldStyle(.roundedBorder)
                    VStack(alignment: .leading, spacing: 3) {
                        SecureField("Confirm passphrase", text: $model.confirm).textFieldStyle(.roundedBorder)
                        if model.mismatch {
                            Text("The passphrases don't match.").font(JBFont.small).foregroundStyle(Color.jbDanger)
                        }
                    }
                }
                Text(model.passphrase.isEmpty
                     ? "Leave blank to save the passwords unprotected \u{2014} anyone with the file can read them."
                     : "The passwords are locked with this passphrase. Without it they can't be restored; the rest of the settings still can.")
                    .font(JBFont.small).foregroundStyle(model.passphrase.isEmpty ? Color.jbWarn : Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
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
            Button("Cancel") { dismiss() }.buttonStyle(.jbSecondary).keyboardShortcut(.cancelAction)
            Button("Back up\u{2026}") { Task { await model.backUp(version: state.currentVersion) } }
                .buttonStyle(.jbPrimary)
                .disabled(!model.canBackUp)
        }
    }

    @ViewBuilder private var running: some View {
        ListPanel {
            ForEach(model.rows.filter { model.progress[$0.id] != nil }) { row in
                titledRow(row.target.name, model.progress[row.id] ?? "")
            }
            if let p = model.progress[SuiteBackup.launcherId] { titledRow("JB Theatre Tools (this launcher)", p) }
        }
        HStack(spacing: 8) {
            ProgressView().controlSize(.small)
            Text(model.progress[""] ?? "Backing up\u{2026}").font(JBFont.small).foregroundStyle(Color.jbText2)
        }
    }

    @ViewBuilder private func done(_ lines: [SuiteBackupLine], _ file: URL) -> some View {
        ListPanel {
            ForEach(lines) { l in BackupResultRow(line: l, words: BackupWords.backup(l), detail: BackupWords.detail(l)) }
        }
        Text("Saved \(file.lastPathComponent)").font(JBFont.body).foregroundStyle(Color.jbText)
            .fixedSize(horizontal: false, vertical: true)
        HStack {
            Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([file]) }
                .buttonStyle(.jbSecondary)
            Spacer()
            Button("Done") { dismiss() }.buttonStyle(.jbPrimary).keyboardShortcut(.defaultAction)
        }
    }

    private func isReady(_ row: BackupAllModel.Row) -> Bool {
        if case .ready = row.probe { return true }
        return false
    }

    private func detail(_ row: BackupAllModel.Row) -> String {
        switch row.probe {
        case nil: return "Checking\u{2026}"
        case .ready(let items, let n)?:
            let what = items.isEmpty ? "Nothing to back up yet" : items.joined(separator: ", ")
            return n > 0 ? "\(what) \u{00B7} \(n) saved password\(n == 1 ? "" : "s")" : what
        case .unsupported?: return SettingsCLIClassify.unsupportedMessage
        case .failed(let m)?: return m
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

    var from: String {
        let m = opened.manifest
        return [m.source["machine"] ?? "another computer", m.source["os"]].compactMap { $0 }.joined(separator: " \u{00B7} ")
    }

    var made: String? {
        ISO8601DateFormatter().date(from: opened.manifest.created).map { $0.formatted(date: .abbreviated, time: .shortened) }
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
                setLine(SuiteBackupLine(id: row.id, name: name, state: .failed, detail: ["this part of the backup can't be read"]))
                continue
            }
            guard let app = row.catalogApp else {
                setLine(SuiteBackupLine(id: row.id, name: name, state: .skipped, detail: ["it isn't in this launcher's list"]))
                continue
            }
            var target = row.target
            if target == nil {
                guard row.installFirst else {
                    setLine(SuiteBackupLine(id: row.id, name: name, state: .skipped, detail: ["not installed here"]))
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
                                            detail: ["it couldn't be installed. Install it from the list, then restore again."]))
                    continue
                }
            }
            guard let target else { continue }
            if target.slots.contains(where: { InstallManager.shared.runningInstance($0.installKey) != nil }) {
                rows[idx].open = true
                setLine(SuiteBackupLine(id: row.id, name: name, state: .retry, detail: ["it's open. Quit it, then press Try Again."]))
                continue
            }
            phase = .running("Restoring \(name)\u{2026}")
            let outcome = await SuiteBackup.restoreApp(file: row.file, slots: target.slots, passphrase: pass,
                                                       restoreSecrets: restoreSecrets)
            if case .passphrase(_, let message) = outcome {
                // Nothing changed for this app: stay on the sheet with the error under the field.
                passError = message
                phase = lines.isEmpty ? .choosing : .done
                return
            }
            AppLog.shared.log("restore \(row.id): \(outcome)")
            if case .appOpen = outcome {
                setLine(SuiteBackupLine(id: row.id, name: name, state: .retry, detail: ["it's open. Quit it, then press Try Again."]))
                continue
            }
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
            Text("Restore all apps?").font(JBFont.title).foregroundStyle(Color.jbText)
            VStack(alignment: .leading, spacing: 2) {
                Text("From \(model.from)").font(JBFont.small).foregroundStyle(Color.jbText2)
                if let made = model.made { Text("Made \(made)").font(JBFont.small).foregroundStyle(Color.jbText2) }
            }
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
        .frame(width: 560, height: 560, alignment: .topLeading)
        .background(Color.jbGround)
        .tint(.jbAccent)
    }

    @ViewBuilder private var choosing: some View {
        ListPanel {
            ForEach($model.rows) { $row in
                VStack(alignment: .leading, spacing: 4) {
                    Toggle(isOn: $row.include) {
                        titledRow(row.catalogApp?.name ?? row.app.name, status(row), detailColor: row.open ? .jbWarn : .jbText2)
                    }
                    if row.include, row.target == nil, row.catalogApp != nil {
                        Picker("", selection: $row.installFirst) {
                            Text("Install first").tag(true)
                            Text("Skip").tag(false)
                        }
                        .pickerStyle(.segmented)   // a two-way switch, like Light / Dark
                        .labelsHidden()
                        .fixedSize()
                        .padding(.leading, 20)
                    }
                }
            }
            if model.launcherInfo != nil {
                Toggle(isOn: $model.includeLauncher) {
                    titledRow("JB Theatre Tools (this launcher)", "Its own settings \u{2014} restored last")
                }
            }
        }
        if model.hasSecrets {
            Toggle("Restore saved passwords", isOn: $model.restoreSecrets).font(JBFont.body)
        }
        if model.needsPassphrase { passphraseField }
        Text("Each app's current settings are copied first; Undo is in that app's Settings \u{2192} Settings backup.")
            .font(JBFont.small).foregroundStyle(Color.jbText2).fixedSize(horizontal: false, vertical: true)
        if state.showLock {
            Text("Restoring waits until show lock is off.").font(JBFont.small).foregroundStyle(Color.jbInfo)
        }
        Spacer(minLength: 0)
        HStack {
            if model.rows.contains(where: { $0.open }) {
                Button("Check Again") { model.refreshOpen() }.buttonStyle(.jbSecondary)
            }
            Spacer()
            Button("Cancel") { dismiss() }.buttonStyle(.jbSecondary).keyboardShortcut(.cancelAction)
            Button("Restore") { Task { await model.run(state: state) } }
                .buttonStyle(.jbPrimary)
                .disabled(!model.canRestore || state.showLock)
        }
    }

    @ViewBuilder private var passphraseField: some View {
        VStack(alignment: .leading, spacing: 3) {
            SecureField("Backup passphrase", text: $model.passphrase).textFieldStyle(.roundedBorder)
            if let e = model.passError {
                Text(e).font(JBFont.small).foregroundStyle(Color.jbDanger).fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    @ViewBuilder private var done: some View {
        ListPanel {
            ForEach(model.lines) { l in BackupResultRow(line: l, words: BackupWords.restore(l), detail: BackupWords.detail(l)) }
        }
        if model.passError != nil { passphraseField }
        HStack {
            Spacer()
            if model.hasRetry || model.passError != nil {
                Button("Try Again") { Task { await model.run(state: state) } }
                    .buttonStyle(.jbSecondary)
                    .disabled((model.needsPassphrase && model.passphrase.isEmpty) || state.showLock)
            }
            Button("Done") { dismiss() }.buttonStyle(.jbPrimary).keyboardShortcut(.defaultAction)
        }
    }

    private func status(_ row: RestoreAllModel.Row) -> String {
        if !row.info.readable { return "This part of the backup can't be read" }
        if row.catalogApp == nil { return "Not in this launcher's list \u{2014} it will be skipped" }
        if row.target == nil { return "Not installed" }
        if row.open { return "Open \u{2014} quit it first" }
        return "Will restore"
    }
}
