import SwiftUI
import AppKit

extension Color {
    /// JB Theatre Tools suite accent (signed-off palette): purple #AF52DE.
    /// Applied via `.tint(...)` (this SwiftPM app has no asset catalog for an AccentColor asset).
    static let jbAccent = Color(red: 175 / 255, green: 82 / 255, blue: 222 / 255)
    /// Shared house "selector" colour (slate-blue #6E8299) for pop-up dropdowns & overflow menus, so the
    /// purple accent stays reserved for primary actions / header / icon (house-style rule 21).
    static let selectorBlue = Color(red: 110 / 255, green: 130 / 255, blue: 153 / 255)
}

/// User-selectable window appearance. `.system` follows macOS.
enum AppAppearance: String, CaseIterable, Identifiable {
    case system, light, dark
    var id: String { rawValue }

    var label: String {
        switch self {
        case .system: return "System"
        case .light: return "Light"
        case .dark: return "Dark"
        }
    }

    var colorScheme: ColorScheme? {
        switch self {
        case .system: return nil
        case .light: return .light
        case .dark: return .dark
        }
    }
}

/// When the launcher automatically checks for updates.
enum UpdateCheckMode: String, CaseIterable, Identifiable {
    case everyLaunch, manual, never
    var id: String { rawValue }

    var label: String {
        switch self {
        case .everyLaunch: return "Every launch"
        case .manual: return "Manual only"
        case .never: return "Never"
        }
    }
}

/// What the window's close (X) button does. Default = quit, on both platforms (house convention).
/// `keepRunning` leaves the app alive — in the Dock on macOS, in the system tray on Windows.
enum CloseBehavior: String, CaseIterable, Identifiable {
    case quit, keepRunning
    var id: String { rawValue }

    var label: String {
        switch self {
        case .quit: return "Quit"
        case .keepRunning: return "Keep running"
        }
    }
}

/// How the app catalog is laid out: a detailed list, or a compact icon grid.
enum AppViewMode: String, CaseIterable, Identifiable {
    case list, grid
    var id: String { rawValue }
    var label: String { self == .list ? "List" : "Grid" }
    var symbol: String { self == .list ? "list.bullet" : "square.grid.2x2" }
}

struct ContentView: View {
    @EnvironmentObject var state: AppState
    @AppStorage("theatre.appearance") private var appearance: AppAppearance = .system
    @AppStorage("theatre.viewMode") private var viewMode: AppViewMode = .list
    @AppStorage("theatre.updateMode") private var updateMode: UpdateCheckMode = .everyLaunch
    @AppStorage("theatre.closeBehavior") private var closeBehavior: CloseBehavior = .quit
    @AppStorage("theatre.installToApplications") private var installToApplications = false
    @AppStorage("theatre.authMode") private var authMode: AuthMode = .server
    @State private var showSettings = false
    @State private var refreshing = false
    @State private var updatingAll = false

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            content
            Divider()
            credit
        }
        .frame(minWidth: 600, minHeight: 440)
        .tint(.jbAccent)
        // Intercept the window's close button so "keep running" can hide instead of quit.
        // The closure reads the live setting from UserDefaults at close time.
        .background(WindowCloseConfigurator(shouldKeepRunning: {
            UserDefaults.standard.string(forKey: "theatre.closeBehavior") == CloseBehavior.keepRunning.rawValue
        }))
        .preferredColorScheme(appearance.colorScheme)
        .sheet(isPresented: $showSettings) {
            SettingsView(appearance: $appearance, updateMode: $updateMode, closeBehavior: $closeBehavior,
                         installToApplications: $installToApplications, authMode: $authMode)
                .environmentObject(state)
        }
        .sheet(isPresented: $state.showKeychainExplainer, onDismiss: { state.acknowledgeKeychainExplainer() }) {
            KeychainExplainerView { state.acknowledgeKeychainExplainer() }
        }
        .task { await firstRefresh() }
    }

    private var header: some View {
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: "theatermasks.fill")
                .font(.system(size: 26))
                .foregroundStyle(.tint)
            VStack(alignment: .leading, spacing: 1) {
                Text("JB Theatre Tools").font(.headline)
                Text("Install, update & launch the JB tool suite")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if state.hasVisibleRows {
                Picker("View", selection: $viewMode) {
                    ForEach(AppViewMode.allCases) { mode in
                        Image(systemName: mode.symbol).help(mode.label).tag(mode)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                .help("Switch between list and grid view")
            }
            if state.updatesAvailable > 0 {
                Button {
                    Task { await updateAllAction() }
                } label: {
                    if updatingAll { ProgressView().controlSize(.small) }
                    else { Label("Update All (\(state.updatesAvailable))", systemImage: "arrow.down.circle.fill") }
                }
                .buttonStyle(.borderedProminent)
                .disabled(updatingAll || refreshing)
            }
            Button {
                Task { await refreshAll() }
            } label: {
                if refreshing { ProgressView().controlSize(.small) }
                else { Label("Refresh", systemImage: "arrow.clockwise") }
            }
            .disabled(refreshing || updatingAll || !state.hasCredentials)
            Button { showSettings = true } label: {
                Label("Settings", systemImage: "gearshape")
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
    }

    @ViewBuilder
    private var content: some View {
        if let err = state.globalError {
            banner(err, systemImage: "exclamationmark.triangle.fill", tint: .red)
            Spacer()
        } else {
            if let v = state.launcherUpdateAvailable { launcherBanner(v) }
            if !state.hasCredentials { credentialsBanner }
            if state.hasCredentials, state.noAppsAccessible {
                banner(authMode == .token
                        ? "This token can’t access any apps. Check the token’s repository access in Settings, or ask James."
                        : "No apps are reachable right now. Check the passphrase in Settings, or ask James.",
                       systemImage: "lock.fill", tint: .orange)
                Spacer()
            } else if !state.hasVisibleRows {
                banner(state.hasHiddenApps
                        ? "Every app is hidden. Show them again from Settings → Hidden apps."
                        : "No apps to show yet. Press Refresh, or check your access in Settings.",
                       systemImage: "eye.slash", tint: .gray)
                Spacer()
            } else if viewMode == .grid {
                gridView
            } else {
                listView
            }
        }
    }

    /// Detailed list layout (two groups: pinned floats to the top; each group drag-reorders on its own —
    /// drag a row within its group; move a row between groups with Pin / Unpin).
    private var listView: some View {
        List {
            if !state.pinnedDisplayRows.isEmpty {
                Section("Pinned") {
                    ForEach(state.pinnedDisplayRows) { row in
                        AppRowView(row: row)
                    }
                    .onMove { state.moveInList(pinned: true, from: $0, to: $1) }
                }
            }
            Section {
                ForEach(state.mainDisplayRows) { row in
                    AppRowView(row: row)
                }
                .onMove { state.moveInList(pinned: false, from: $0, to: $1) }
            }
        }
        .listStyle(.plain)
    }

    /// Compact icon-grid layout: an icon + name tile per app, pinned apps first. Per-app actions live in
    /// the tile's right-click menu; a click launches (installed) or installs. Drag-reorder is list-only.
    private var gridView: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                if !state.pinnedDisplayRows.isEmpty {
                    gridSection("Pinned", rows: state.pinnedDisplayRows)
                    gridSection("All apps", rows: state.mainDisplayRows)
                } else {
                    gridSection(nil, rows: state.mainDisplayRows)
                }
            }
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    @ViewBuilder
    private func gridSection(_ title: String?, rows: [AppState.Row]) -> some View {
        if !rows.isEmpty {
            VStack(alignment: .leading, spacing: 10) {
                if let title {
                    Text(title).font(.caption).bold().foregroundStyle(.secondary)
                }
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 128), spacing: 14)],
                          alignment: .leading, spacing: 14) {
                    ForEach(rows) { row in AppGridTile(row: row) }
                }
            }
        }
    }

    private func launcherBanner(_ version: String) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "arrow.down.circle.fill").foregroundStyle(Color.jbAccent)
            VStack(alignment: .leading, spacing: 1) {
                Text("JB Theatre Tools \(version) is available").font(.callout).bold()
                Text(state.launcherDownloadMessage ?? "You're running v\(state.currentVersion).")
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            Button {
                Task { await state.downloadLauncherUpdate() }
            } label: {
                if state.launcherDownloading { ProgressView().controlSize(.small) }
                else { Text("Download Update") }
            }
            .disabled(state.launcherDownloading)
        }
        .padding(12)
        .background(Color.jbAccent.opacity(0.12))
    }

    private var credentialsBanner: some View {
        HStack(spacing: 10) {
            Image(systemName: "key.fill").foregroundStyle(.orange)
            VStack(alignment: .leading, spacing: 1) {
                Text(state.credentialsPrompt).font(.callout).bold()
                Text(authMode == .token
                     ? "Settings → paste a fine-grained PAT (Contents: read)."
                     : "Settings → enter the suite passphrase (ask James).")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button("Open Settings") { showSettings = true }
        }
        .padding(12)
        .background(Color.orange.opacity(0.12))
    }

    private var credit: some View {
        Text("Created by: James Breedon & Claude Code")
            .font(.caption2)
            .foregroundColor(.secondary)
            .frame(maxWidth: .infinity, alignment: .center)
            .padding(.vertical, 6)
    }

    private func banner(_ text: String, systemImage: String, tint: Color) -> some View {
        HStack(spacing: 10) {
            Image(systemName: systemImage).foregroundStyle(tint)
            Text(text).font(.callout)
            Spacer()
        }
        .padding(12)
        .background(tint.opacity(0.12))
    }

    private func firstRefresh() async {
        guard updateMode == .everyLaunch, !refreshing else { return }
        if state.hasCredentials { await refreshAll() }
        await state.checkLauncherUpdate()
    }

    private func refreshAll() async {
        refreshing = true
        await state.refreshAll()
        refreshing = false
    }

    private func updateAllAction() async {
        updatingAll = true
        await state.updateAll()
        updatingAll = false
    }
}

/// A single catalog row: name, blurb, version line, status badge, and action buttons.
struct AppRowView: View {
    @EnvironmentObject var state: AppState
    let row: AppState.Row
    @State private var confirmingUninstall = false

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            iconView
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 5) {
                    Text(row.displayName).font(.body).bold()
                    if state.isPinned(row.id) {
                        Image(systemName: "pin.fill").font(.caption2).foregroundStyle(Color.selectorBlue)
                    }
                }
                Text(row.app.blurb).font(.caption).foregroundStyle(.secondary)
                versionLine
                whatsNewLine
                variantToggle
                if row.busy {
                    ProgressView(value: row.progress)
                        .frame(maxWidth: 240)
                        .controlSize(.small)
                }
            }
            Spacer()
            statusBadge
            actions
        }
        .padding(.vertical, 10)
        .padding(.horizontal, 4)
        .confirmationDialog("Uninstall \(row.displayName)?",
                            isPresented: $confirmingUninstall, titleVisibility: .visible) {
            Button("Uninstall", role: .destructive) { state.uninstall(row.id) }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This removes the installed app from your Mac. You can reinstall it anytime.")
        }
    }

    /// Leading icon: the installed app's REAL icon once installed; else the bundled per-app icon; else
    /// a tinted monogram tile. (Shared with the grid tile via `AppIconImage`.)
    private var iconView: some View {
        AppIconImage(id: row.id, displayName: row.displayName, size: 40)
    }

    private var versionLine: some View {
        HStack(spacing: 6) {
            Text("Installed: \(installedText)")
            Text("·").foregroundStyle(.secondary)
            Text("Latest: \(row.latest ?? "—")")
        }
        .font(.caption2)
        .foregroundStyle(.secondary)
    }

    /// The installed version of the SELECTED variant's slot, annotated with that variant's label for
    /// apps that ship variants (each variant is its own install; the toggle picks which one is shown).
    private var installedText: String {
        guard let v = row.installed else { return "—" }
        if let variant = state.selectedVariantLabel(row.app) { return "\(v) (\(variant))" }
        return v
    }

    /// A compact Standard/Full toggle, shown inline only for apps that ship variants.
    @ViewBuilder
    private var variantToggle: some View {
        if row.app.hasVariants, let vs = row.app.variants {
            Picker("", selection: Binding(
                get: { state.selectedVariantId(row.app) ?? vs.first?.id ?? "" },
                set: { state.setVariant(row.id, $0) }
            )) {
                ForEach(vs) { Text($0.label).tag($0.id) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .controlSize(.mini)
            .fixedSize()
            .tint(.selectorBlue)
            .disabled(row.busy)
        }
    }

    /// One-line "what's new" for the app's current release, shown only when the catalog carries it.
    /// Labelled "New in vX.Y.Z:" when a version is present, else "What's new:".
    @ViewBuilder
    private var whatsNewLine: some View {
        if let note = row.app.whatsNew, !note.isEmpty {
            (
                Text(whatsNewLabel).fontWeight(.semibold)
                + Text(" ") + Text(note)
            )
            .font(.caption2)
            .foregroundStyle(Color.selectorBlue)
            .lineLimit(2)
            .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var whatsNewLabel: String {
        if let v = row.app.whatsNewVersion, !v.isEmpty { return "New in \(v):" }
        return "What's new:"
    }

    @ViewBuilder
    private var statusBadge: some View {
        switch row.status {
        case .checking:
            ProgressView().controlSize(.small)
        case .upToDate:
            badge("Up to date", color: .green)
        case .updateAvailable:
            badge("Update", color: .jbAccent)
        case .notInstalled:
            badge("Not installed", color: .secondary)
        case .installed:
            badge("Installed", color: .secondary)
        case .noRelease:
            badge("No release", color: .secondary)
        case .missingAsset:
            badge("No macOS build", color: .orange)
        case .error(let msg):
            badge("Error", color: .red).help(msg)
        case .noAccess:
            // Row is filtered out of the list; nothing to show.
            EmptyView()
        case .unknown:
            EmptyView()
        }
    }

    private func badge(_ text: String, color: Color) -> some View {
        Text(text)
            .font(.caption2).bold()
            .padding(.horizontal, 8).padding(.vertical, 3)
            .background(color.opacity(0.15))
            .foregroundStyle(color)
            .clipShape(Capsule())
    }

    @ViewBuilder
    private var actions: some View {
        HStack(spacing: 8) {
            // Install / Update / Retry — depends on the checked status.
            switch row.status {
            case .notInstalled:
                installButton(title: "Install")
            case .updateAvailable:
                installButton(title: "Update")
            case .error:
                installButton(title: row.installed == nil ? "Install" : "Retry")
            default:
                EmptyView()
            }
            // Launch — available whenever something is installed, even before a refresh has run.
            if row.installed != nil { launchButton }
            rowMenu   // every visible row has the ⋯ menu (reordering is always available)
        }
    }

    /// Overflow menu: pin/hide/reorder, pick a variant, install a specific (older) version, or uninstall.
    private var rowMenu: some View {
        Menu {
            AppMenuButtons(row: row, requestUninstall: { confirmingUninstall = true })
        } label: {
            Image(systemName: "ellipsis.circle")
        }
        .menuStyle(.borderlessButton)
        .tint(.selectorBlue)   // house rule 21: selectors/menus are slate-blue, not the purple accent
        .fixedSize()
        .disabled(row.busy)
        .help("Variant, reorder, other versions & uninstall")
    }

    private func installButton(title: String) -> some View {
        Button(title) {
            Task { await state.install(row.id) }
        }
        .buttonStyle(.borderedProminent)
        .disabled(row.busy || row.latestAssetId == nil)
    }

    private var launchButton: some View {
        Button("Launch") { state.launch(row.id) }
            .disabled(row.busy)
    }
}

/// The app's icon at a given size: the installed app's REAL icon once installed; else the per-app icon
/// bundled in the launcher (Resources/<id>.png); else a tinted monogram tile. Shared by list & grid.
struct AppIconImage: View {
    let id: String
    let displayName: String
    var size: CGFloat = 40

    var body: some View {
        if let path = InstallManager.shared.installedPath(id)?.path {
            Image(nsImage: NSWorkspace.shared.icon(forFile: path))
                .resizable().interpolation(.high)
                .frame(width: size, height: size)
        } else if let bundled = Self.bundledIcon(id) {
            Image(nsImage: bundled)
                .resizable().interpolation(.high)
                .frame(width: size, height: size)
        } else {
            RoundedRectangle(cornerRadius: size * 0.22, style: .continuous)
                .fill(Color.jbAccent.opacity(0.15))
                .frame(width: size, height: size)
                .overlay(
                    Text(displayName.first.map { String($0).uppercased() } ?? "•")
                        .font(.system(size: size * 0.45, weight: .semibold))
                        .foregroundStyle(Color.jbAccent)
                )
        }
    }

    static func bundledIcon(_ id: String) -> NSImage? {
        guard let url = Bundle.main.url(forResource: id, withExtension: "png") else { return nil }
        return NSImage(contentsOf: url)
    }
}

/// The per-row action menu, shared by the list row's ⋯ button and the grid tile's right-click menu:
/// pin/hide/reorder, pick a variant (Standard/Full), install a specific version, Dock/alias, uninstall.
struct AppMenuButtons: View {
    @EnvironmentObject var state: AppState
    let row: AppState.Row
    /// Called when the user picks Uninstall — the host view shows its own confirmation dialog.
    var requestUninstall: () -> Void

    var body: some View {
        Group {
            Button(state.isPinned(row.id) ? "Unpin from Top" : "Pin to Top") { state.togglePin(row.id) }
            Button("Move Up") { state.moveRow(row.id, up: true) }
                .disabled(!state.canMove(row.id, up: true))
            Button("Move Down") { state.moveRow(row.id, up: false) }
                .disabled(!state.canMove(row.id, up: false))
            Button("Hide from List") { state.setHidden(row.id, true) }
            if row.app.hasVariants, let vs = row.app.variants {
                Divider()
                Picker("Variant", selection: Binding(
                    get: { state.selectedVariantId(row.app) ?? vs.first?.id ?? "" },
                    set: { state.setVariant(row.id, $0) }
                )) {
                    ForEach(vs) { Text($0.label).tag($0.id) }
                }
            }
            if !row.releases.isEmpty {
                Divider()
                Section("Install version") {
                    ForEach(row.releases) { rel in
                        Button { Task { await state.install(row.id, tag: rel.tagName) } }
                            label: { Text(versionLabel(rel)) }
                    }
                }
            }
            if row.installed != nil {
                Divider()
                Button(state.isDockPinned(row.id) ? "Remove from Dock" : "Add to Dock") {
                    state.toggleDockPin(row.id)
                }
                Button(state.hasDesktopAlias(row.id) ? "Remove Desktop Alias" : "Add Desktop Alias") {
                    state.toggleDesktopAlias(row.id)
                }
                Divider()
                Button("Uninstall \(row.displayName)", role: .destructive) { requestUninstall() }
            }
        }
    }

    private func versionLabel(_ rel: ReleaseInfo) -> String {
        var s = rel.tagName
        if rel.prerelease { s += " (pre-release)" }
        if rel.tagName == row.installed { s += "  ✓ installed" }
        return s
    }
}

/// One tile in the grid view: a large icon + name, with a compact status line. A click launches the
/// app (if installed) or installs it; the full action set lives in the right-click menu (shared with
/// the list row). Drag-reorder stays a list-view feature; reordering here is via the menu's Move Up/Down.
struct AppGridTile: View {
    @EnvironmentObject var state: AppState
    let row: AppState.Row
    @State private var confirmingUninstall = false

    var body: some View {
        VStack(spacing: 8) {
            AppIconImage(id: row.id, displayName: row.displayName, size: 54)
            Text(row.displayName)
                .font(.caption).bold()
                .multilineTextAlignment(.center)
                .lineLimit(2)
                .frame(height: 30)
            statusCaption
        }
        .frame(maxWidth: .infinity)
        .frame(height: 140)
        .padding(8)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.secondary.opacity(0.06)))
        .overlay(alignment: .topTrailing) {
            if state.isPinned(row.id) {
                Image(systemName: "pin.fill").font(.caption2)
                    .foregroundStyle(Color.selectorBlue).padding(6)
            }
        }
        .overlay(alignment: .bottom) {
            if row.busy {
                ProgressView(value: row.progress).controlSize(.small)
                    .padding(.horizontal, 12).padding(.bottom, 8)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { primaryAction() }
        .contextMenu { AppMenuButtons(row: row, requestUninstall: { confirmingUninstall = true }) }
        .confirmationDialog("Uninstall \(row.displayName)?",
                            isPresented: $confirmingUninstall, titleVisibility: .visible) {
            Button("Uninstall", role: .destructive) { state.uninstall(row.id) }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This removes the installed app from your Mac. You can reinstall it anytime.")
        }
        .help(tooltip)
    }

    /// Click behaviour: launch when installed, otherwise install (when the status allows it).
    private func primaryAction() {
        if row.busy { return }
        if row.installed != nil { state.launch(row.id); return }
        switch row.status {
        case .notInstalled, .updateAvailable, .error: Task { await state.install(row.id) }
        default: break
        }
    }

    @ViewBuilder
    private var statusCaption: some View {
        let variant = state.selectedVariantLabel(row.app)
        Text(captionText(variant: variant))
            .font(.caption2)
            .foregroundStyle(captionColor)
            .lineLimit(1)
    }

    private func captionText(variant: String?) -> String {
        let base: String
        switch row.status {
        case .updateAvailable: base = "Update"
        case .upToDate:        base = row.installed ?? "Installed"
        case .installed:       base = row.installed ?? "Installed"
        case .notInstalled:    base = "Install"
        case .noRelease:       base = "No release"
        case .missingAsset:    base = "No macOS build"
        case .checking:        base = "Checking…"
        case .error:           base = "Error"
        default:               base = row.installed ?? " "
        }
        if let v = variant, row.app.hasVariants { return "\(base) · \(v)" }
        return base
    }

    private var captionColor: Color {
        switch row.status {
        case .updateAvailable: return .jbAccent
        case .upToDate:        return .green
        case .missingAsset, .error: return .orange
        default:               return .secondary
        }
    }

    private var tooltip: String {
        var t = row.displayName
        if let v = row.installed {
            t += " — installed \(v)"
            if let vl = state.selectedVariantLabel(row.app) { t += " (\(vl))" }
        } else if row.status == .notInstalled {
            t += " — click to install"
        }
        return t
    }
}
