import SwiftUI
import AppKit

// The v1.30 secondary views: release notes, an app's details, the activity history, the setup-import preview
// (all presented through ONE `.sheet(item:)` — see `LauncherSheetView`), the menu-bar quick-launch menu and the
// keyboard commands. House Style v2 throughout: kit tokens, hairlines, no card shadows.

extension Notification.Name {
    /// Posted by the keyboard commands / menu-bar extra; ContentView owns the matching UI state.
    static let jbttRefresh = Notification.Name("jbtt.refresh")
    static let jbttFind = Notification.Name("jbtt.find")
    static let jbttSettings = Notification.Name("jbtt.settings")
    static let jbttUpdateAll = Notification.Name("jbtt.updateAll")
    static let jbttActivity = Notification.Name("jbtt.activity")
}

/// Routes `AppState.activeSheet` to its view.
struct LauncherSheetView: View {
    @EnvironmentObject var state: AppState
    let sheet: LauncherSheet

    var body: some View {
        switch sheet {
        case .notes(let model):
            ReleaseNotesView(model: model)
        case .details(let id):
            if let row = state.rows.first(where: { $0.id == id }) {
                AppDetailsView(row: row)
            } else {
                SheetFrame(title: "App") { Text("This app is no longer in the list.").font(JBFont.body) }
            }
        case .activity(let events):
            ActivityView(events: events)
        case .importPreview(let model):
            ImportPreviewView(model: model)
        }
    }
}

/// The shared chrome for these sheets: a title, the content, a Done button.
struct SheetFrame<Content: View>: View {
    @EnvironmentObject var state: AppState
    let title: String
    var width: CGFloat = 560
    var height: CGFloat? = nil
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(title).font(JBFont.title).foregroundStyle(Color.jbText)
            content
            HStack {
                Spacer()
                Button("Done") { state.activeSheet = nil }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(22)
        .frame(width: width, height: height, alignment: .topLeading)
        .background(Color.jbGround)
        .tint(.jbAccent)
    }
}

/// In-app release notes: every release, newest first, with its date and notes (Markdown made readable by
/// `ReleaseNotesText`); releases newer than the installed version are marked "New since your version".
struct ReleaseNotesView: View {
    let model: NotesModel

    var body: some View {
        SheetFrame(title: model.title, height: 560) {
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    if model.loading {
                        HStack(spacing: 8) {
                            ProgressView().controlSize(.small)
                            Text("Loading the release notes…").font(JBFont.body).foregroundStyle(Color.jbText2)
                        }
                    }
                    if let message = model.message {
                        Text(message).font(JBFont.body).foregroundStyle(Color.jbText)
                            .fixedSize(horizontal: false, vertical: true)
                            .textSelection(.enabled)
                    }
                    ForEach(model.releases) { rel in
                        releaseBlock(rel)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(14)
            }
            .background(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).fill(Color.jbSurface))
            .overlay(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).strokeBorder(Color.jbLine))
        }
    }

    private func releaseBlock(_ rel: ReleaseInfo) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 8) {
                Text(VersionDisplay.display(rel.tagName)).font(JBFont.bodyStrong).foregroundStyle(Color.jbText)
                if let flag = flag(rel) {
                    Text(flag.text).font(JBFont.label).foregroundStyle(flag.color)
                        .padding(.horizontal, 7).padding(.vertical, 2)
                        .background(Capsule().fill(flag.color.opacity(0.14)))
                }
            }
            if let meta = meta(rel) {
                Text(meta).font(JBFont.small).foregroundStyle(Color.jbText2)
            }
            Text(ReleaseNotesText.plain(rel.body))
                .font(JBFont.body).foregroundStyle(Color.jbText)
                .fixedSize(horizontal: false, vertical: true)
                .textSelection(.enabled)
        }
    }

    private func meta(_ rel: ReleaseInfo) -> String? {
        var bits: [String] = []
        if let p = rel.published { bits.append("\(RelativeAge.shortDate(p)) (\(RelativeAge.describe(p, now: Date())))") }
        if rel.prerelease { bits.append(AppState.isDevTag(rel.tagName) ? "development build" : "pre-release") }
        return bits.isEmpty ? nil : bits.joined(separator: " · ")
    }

    private func flag(_ rel: ReleaseInfo) -> (text: String, color: Color)? {
        guard let installed = model.installed else { return nil }
        if VersionDisplay.equal(rel.tagName, installed) { return ("Installed", .jbOk) }
        if AppState.versionIsNewer(rel.tagName, than: installed) { return ("New since your version", .jbAccent) }
        return nil
    }
}

/// Everything about one app: what's installed where, when and how big; the latest release; hold and roll back
/// state — with Show in Finder and Release Notes.
struct AppDetailsView: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var row: AppState.Row
    @State private var sizeOnDisk: String?

    private var key: String { state.installKey(for: row.app) }
    private var record: InstalledRecord? { row.installed == nil ? nil : InstallManager.shared.record(key) }
    private var location: URL? { row.installed == nil ? nil : InstallManager.shared.installedPath(key) }
    private var latestRelease: ReleaseInfo? { row.latest.flatMap { tag in row.releases.first { $0.tagName == tag } } }

    var body: some View {
        SheetFrame(title: row.displayName, width: 520) {
            HStack(alignment: .top, spacing: 14) {
                AppIconImage(id: row.id, displayName: row.displayName, size: 52)
                VStack(alignment: .leading, spacing: 8) {
                    if !row.app.blurb.isEmpty {
                        Text(row.app.blurb).font(JBFont.body).foregroundStyle(Color.jbText2)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    detailRows
                }
            }
            HStack(spacing: 8) {
                if let location {
                    Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([location]) }
                        .buttonStyle(.jbSecondary)
                }
                if !row.releases.isEmpty {
                    Button("Release Notes…") { state.showReleaseNotes(row.id) }
                        .buttonStyle(.jbSecondary)
                }
            }
        }
        .task(id: row.installed) {
            guard row.installed != nil else { sizeOnDisk = nil; return }
            let k = key
            let bytes = await Task.detached(priority: .utility) { InstallManager.shared.sizeOnDisk(k) }.value
            sizeOnDisk = ByteSize.format(bytes)
        }
    }

    private var detailRows: some View {
        VStack(alignment: .leading, spacing: 5) {
            item("Section", row.app.category)
            item("Installed", installedText)
            item("Installed on", record.flatMap { RelativeAge.parseISO($0.installedAt) }.map(longDate))
            item("Location", location?.path)
            if row.installed != nil { item("Size on disk", sizeOnDisk ?? "Calculating…") }
            item("Latest", row.latest.map(VersionDisplay.display))
            item("Released", latestRelease?.published.map { "\(RelativeAge.shortDate($0)) (\(RelativeAge.describe($0, now: Date())))" })
            item("Download size", downloadSize)
            if row.installed != nil, state.isHeld(row.id) { item("Updates", "Held at this version — Update All leaves it alone") }
            item("Previous version", record?.previousVersion.map(VersionDisplay.display))
        }
    }

    private var installedText: String {
        guard let v = row.installed else { return "Not installed" }
        let edition = row.app.hasVariants ? row.app.variantLabel(state.selectedVariantId(row.app)) : nil
        return VersionDisplay.display(v) + (edition.map { " (\($0))" } ?? "")
    }

    private var downloadSize: String? {
        guard let id = row.latestAssetId, let asset = latestRelease?.assets.first(where: { $0.id == id }), asset.size > 0 else { return nil }
        return ByteSize.format(Int64(asset.size))
    }

    private func longDate(_ d: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "d MMM yyyy HH:mm"
        return f.string(from: d)
    }

    @ViewBuilder
    private func item(_ label: String, _ value: String?) -> some View {
        if let value {
            HStack(alignment: .firstTextBaseline, spacing: 10) {
                Text(label.uppercased()).font(JBFont.label).tracking(JBFont.labelTracking)
                    .foregroundStyle(Color.jbText3)
                    .frame(width: 118, alignment: .leading)
                Text(value).font(JBFont.body).foregroundStyle(Color.jbText)
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)
            }
        }
    }
}

/// The activity history, newest first: "Today 14:02   Updated DMX Tools v1.1.0 → v1.2.0".
struct ActivityView: View {
    let events: [ActivityEvent]

    var body: some View {
        SheetFrame(title: "Activity", width: 600, height: 480) {
            if events.isEmpty {
                Text("Nothing yet — installs, updates and removals will be listed here.")
                    .font(JBFont.body).foregroundStyle(Color.jbText2)
                Spacer()
            } else {
                ScrollView {
                    let now = Date()
                    VStack(alignment: .leading, spacing: 6) {
                        ForEach(Array(events.reversed().enumerated()), id: \.offset) { _, e in
                            HStack(alignment: .firstTextBaseline, spacing: 12) {
                                Text(ActivityHistory.when(e.at, now: now))
                                    .font(JBFont.small).foregroundStyle(Color.jbText3)
                                    .frame(width: 136, alignment: .leading)
                                Text(ActivityHistory.describe(e))
                                    .font(JBFont.body)
                                    .foregroundStyle(e.action == "failed" ? Color.jbDanger : Color.jbText)
                                    .fixedSize(horizontal: false, vertical: true)
                                    .textSelection(.enabled)
                            }
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(14)
                }
                .background(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).fill(Color.jbSurface))
                .overlay(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).strokeBorder(Color.jbLine))
            }
        }
    }
}

/// The preview before a setup file is applied: what installs, what's skipped, and whether to take the file's layout.
struct ImportPreviewView: View {
    @EnvironmentObject var state: AppState
    let model: ImportPreviewModel
    @State private var applyLayout = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Import Setup").font(JBFont.title).foregroundStyle(Color.jbText)
            Text("From \(model.source)").font(JBFont.small).foregroundStyle(Color.jbText2)
            ScrollView {
                Text(model.summary)
                    .font(JBFont.body).foregroundStyle(Color.jbText)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(14)
            }
            .background(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).fill(Color.jbSurface))
            .overlay(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).strokeBorder(Color.jbLine))
            if model.layout != nil {
                Toggle("Also use this file's list layout (pinned, hidden and order of apps and sections)", isOn: $applyLayout)
                    .font(JBFont.body)
            }
            HStack {
                Spacer()
                Button("Cancel") { state.activeSheet = nil }
                    .keyboardShortcut(.cancelAction)
                Button(model.plan.toInstall.isEmpty ? "Apply" : "Install") {
                    let apply = applyLayout
                    Task { await state.runImport(model, applyLayout: apply) }
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(22)
        .frame(width: 540, height: 460, alignment: .topLeading)
        .background(Color.jbGround)
        .tint(.jbAccent)
    }
}

/// The menu-bar extra (Settings → "Show in the menu bar"): launch any installed app directly, check for updates,
/// open the window, quit.
struct QuickLaunchMenu: View {
    @ObservedObject var state: AppState

    var body: some View {
        let slots = state.installedSlotsForLaunch
        if slots.isEmpty {
            Text("No apps installed")
        } else {
            ForEach(slots, id: \.key) { slot in
                Button(slot.name) { state.launchSlot(slot.key) }
            }
        }
        Divider()
        Button("Check for Updates") { NotificationCenter.default.post(name: .jbttRefresh, object: nil) }
        Button("Open JB Theatre Tools") {
            NSApp.unhide(nil)
            NSApp.activate(ignoringOtherApps: true)
            NSApp.windows.first(where: { $0.canBecomeMain })?.makeKeyAndOrderFront(nil)
        }
        Divider()
        Button("Quit JB Theatre Tools") { NSApp.terminate(nil) }
    }
}

/// Keyboard shortcuts: ⌘R check, ⌘F find, ⌘, settings, ⌘U update all, ⌘L show lock, ⌘Y activity, ⌘1 / ⌘2 view.
struct LauncherCommands: Commands {
    @ObservedObject var state: AppState

    private func post(_ name: Notification.Name) { NotificationCenter.default.post(name: name, object: nil) }

    var body: some Commands {
        CommandGroup(replacing: .appSettings) {
            Button("Settings…") { post(.jbttSettings) }
                .keyboardShortcut(",", modifiers: .command)
        }
        // Edit → Find: replaces the text system's Find submenu, whose own ⌘F would otherwise claim the shortcut first.
        CommandGroup(replacing: .textEditing) {
            Button("Find Apps") { post(.jbttFind) }
                .keyboardShortcut("f", modifiers: .command)
        }
        CommandMenu("Apps") {
            Button("Check for Updates") { post(.jbttRefresh) }
                .keyboardShortcut("r", modifiers: .command)
            Button("Update All") { post(.jbttUpdateAll) }
                .keyboardShortcut("u", modifiers: .command)
                .disabled(state.showLock || state.batchRunning || state.updatesAvailable == 0)
            Divider()
            Button(state.showLock ? "Turn Off Show Lock" : "Turn On Show Lock") { state.setShowLock(!state.showLock) }
                .keyboardShortcut("l", modifiers: .command)
            Button("Activity") { post(.jbttActivity) }
                .keyboardShortcut("y", modifiers: .command)
            Divider()
            Button("View as List") { UserDefaults.standard.set(AppViewMode.list.rawValue, forKey: "theatre.viewMode") }
                .keyboardShortcut("1", modifiers: .command)
            Button("View as Grid") { UserDefaults.standard.set(AppViewMode.grid.rawValue, forKey: "theatre.viewMode") }
                .keyboardShortcut("2", modifiers: .command)
        }
    }
}
