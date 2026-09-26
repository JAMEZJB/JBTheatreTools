import SwiftUI
import AppKit

/// A 1px house hairline — the v2 rule is hairline separation, not a shadowed card edge.
struct Hairline: View {
    var body: some View { Rectangle().fill(Color.jbLine).frame(height: 1) }
}

/// `AppState` handed to row/tile views through a plain environment KEY (not `@EnvironmentObject`): reading it
/// this way does NOT subscribe the view to the model's publishes, so a structural publish (order, visibility,
/// header counts) no longer re-runs all 21 row bodies. Rows observe only their own `Row` object.
private struct AppStateKey: EnvironmentKey { static let defaultValue: AppState? = nil }
extension EnvironmentValues {
    var appState: AppState? {
        get { self[AppStateKey.self] }
        set { self[AppStateKey.self] = newValue }
    }
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

/// Drag timing shared by the row and section drops.
enum DragTiming {
    /// The drop-settle glide (floating card → landing slot). `easeOut` over this exact duration finishes with
    /// no tail/overshoot, so the real row/section is revealed the instant the card lands — no perceptible gap.
    static let dropSettle: Double = 0.16
}

/// Drag payload for reordering whole category SECTIONS. Prefixed with a control char so a section-header
/// drag can never be confused with a row/tile drag (which carry a bare app id) sharing the `String` type.
enum CategoryDrag {
    static let prefix = "\u{2}cat\u{2}"
    static func token(_ key: String) -> String { prefix + key }
    static func key(_ token: String) -> String? {
        token.hasPrefix(prefix) ? String(token.dropFirst(prefix.count)) : nil
    }
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
    @State private var downloadingAll = false
    @FocusState private var findFocused: Bool

    var body: some View {
        LoopWatch.mark("ContentView.body")
        return VStack(spacing: 0) {
            header
            Hairline()
            // Banners sit above the find bar, so the bar stays next to the list it filters.
            if state.globalError == nil { banners }
            if state.globalError == nil, state.hasVisibleRows {
                filterBar
                Hairline()
            }
            content
            Hairline()
            credit
        }
        .frame(minWidth: 640, minHeight: 460)
        .background(Color.jbGround)
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
        .sheet(item: $state.activeSheet) { sheet in
            LauncherSheetView(sheet: sheet)
                .environmentObject(state)
                .environment(\.appState, state)
        }
        // The keyboard commands and the menu-bar extra post these; the matching UI state lives here.
        .onReceive(NotificationCenter.default.publisher(for: .jbttRefresh)) { _ in
            // Not under an open sheet: the check may need to show the Keychain explainer sheet.
            guard !refreshing, !sheetOpen, !state.batchRunning, state.hasCredentials else { return }
            Task { await refreshAll() }
        }
        .onReceive(NotificationCenter.default.publisher(for: .jbttFind)) { _ in
            if state.hasVisibleRows { findFocused = true }
        }
        .onReceive(NotificationCenter.default.publisher(for: .jbttSettings)) { _ in
            if !sheetOpen { showSettings = true }
        }
        .onReceive(NotificationCenter.default.publisher(for: .jbttActivity)) { _ in
            if !sheetOpen { state.showActivity() }
        }
        .onReceive(NotificationCenter.default.publisher(for: .jbttUpdateAll)) { _ in
            guard canUpdateAll, !sheetOpen else { return }
            Task { await updateAllAction() }
        }
        .onAppear {
            // After an in-place update: tell the previous launcher this one is up (it then quits) and bin its old bundle.
            SelfUpdate.readAfterUpdate()
            SelfUpdate.started()
            // AppKit gives the first text field (Find apps) the keyboard at launch; the launcher opens with nothing
            // focused, as it did before it had a find field (⌘F focuses it).
            DispatchQueue.main.async {
                for w in NSApp.windows where w.firstResponder is NSText { w.makeFirstResponder(nil) }
            }
        }
        .task {
            state.startScheduler()
            // Dev harness (see main.swift): `JBTT_OPEN_SETTINGS=1` opens Settings at launch so JBTT_SNAPSHOT can capture it.
            if ProcessInfo.processInfo.environment["JBTT_OPEN_SETTINGS"] == "1" { showSettings = true }
            await firstRefresh()
            // Dev harness: `JBTT_AUTO_SELF_UPDATE=1` runs the launcher's in-place update as if Update had been pressed.
            if ProcessInfo.processInfo.environment["JBTT_AUTO_SELF_UPDATE"] == "1" {
                _ = await state.checkLauncherUpdate()
                await state.updateLauncher()
            }
        }
    }

    /// A sheet is already up — a second one can't be presented over it.
    private var sheetOpen: Bool { showSettings || state.showKeychainExplainer || state.activeSheet != nil }

    /// Update All is on offer: something to update, nothing else running, not under show lock.
    private var canUpdateAll: Bool {
        state.hasCredentials && !state.showLock && !state.batchRunning && !refreshing && state.updatesAvailable > 0
    }

    /// " (120 MB)" after a batch label, when the size is known.
    private func sizeSuffix(_ bytes: Int64) -> String { bytes > 0 ? " (\(ByteSize.format(bytes)))" : "" }

    private var header: some View {
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: "theatermasks.fill")
                .font(.system(size: 26))
                .foregroundStyle(.tint)
            // One line each, always: the header's height never changes with the window width (the subtitle drops
            // out when there's no room for it rather than wrapping every control onto two lines).
            VStack(alignment: .leading, spacing: 1) {
                Text("JB Theatre Tools").font(JBFont.title).foregroundStyle(Color.jbText)
                    .lineLimit(1)
                ViewThatFits(in: .horizontal) {
                    Text("Install, update & launch the JB tool suite")
                        .font(JBFont.small).foregroundStyle(Color.jbText2)
                        .lineLimit(1).fixedSize()
                    Color.clear.frame(width: 0, height: 0)
                }
            }
            Spacer()
            if state.hasVisibleRows {
                JBSegmented(segments: AppViewMode.allCases.map {
                                JBSegmented.Segment(id: $0, symbol: $0.symbol, help: $0.label) },
                            selection: $viewMode)
                    .fixedSize()
                    .help("Switch between list and grid view")
            }
            if state.batchRunning {
                // Stop: the download in flight is cancelled and nothing more starts.
                Button {
                    state.stopBatch()
                } label: { Label(state.batchStopRequested ? "Stopping…" : "Stop", systemImage: "stop.circle") }
                .buttonStyle(.jbSecondary)
                .disabled(state.batchStopRequested)
                .help("Stop after the app that's installing now — the download in progress is cancelled")
            } else if state.hasVisibleRows, state.hasCredentials, !state.showLock,
               state.updatesAvailable > 0 || state.hasAnyToDownload(includeFull: false) {
                let updates = state.updatesAvailable
                Menu {
                    if updates > 0 {
                        Button {
                            Task { await updateAllAction() }
                        } label: { Label("Update \(updates) installed app\(updates == 1 ? "" : "s") — incl. Full editions\(sizeSuffix(state.updateAllBytes))", systemImage: "arrow.up.circle") }
                        Divider()
                    }
                    Button {
                        Task { await downloadAllAction(includeFull: false) }
                    } label: { Label("Install every app\(sizeSuffix(state.downloadAllBytes(includeFull: false)))", systemImage: "square.and.arrow.down") }
                    if state.hasFullVariants {
                        Button {
                            Task { await downloadAllAction(includeFull: true) }
                        } label: { Label("Install every app — plus the Full editions\(sizeSuffix(state.downloadAllBytes(includeFull: true)))", systemImage: "square.and.arrow.down.on.square") }
                    }
                } label: {
                    // A plain HStack, not `Label`/`ProgressView`: the borderless popup renders those blank.
                    HStack(spacing: 5) {
                        Image(systemName: updates > 0 ? "arrow.up.circle.fill" : "arrow.down.circle.fill")
                        if downloadingAll { Text("Installing…") }
                        else if updatingAll { Text("Updating…") }
                        else if updates > 0 { Text("Update All (\(updates))") }
                        else { Text("Download All") }
                    }
                }
                .jbMenuPill(.primary)
                .fixedSize()
                .disabled(downloadingAll || updatingAll || refreshing)
                .help("Install or update every app in one go")
            }
            Button {
                Task { await refreshAll() }
            } label: {
                if refreshing { ProgressView().controlSize(.small) }
                else { Label("Refresh", systemImage: "arrow.clockwise") }
            }
            .buttonStyle(.jbSecondary)
            .disabled(refreshing || updatingAll || state.batchRunning || !state.hasCredentials)
            .help("Check every app for updates (⌘R)")
            moreMenu
            Button { showSettings = true } label: {
                Label("Settings", systemImage: "gearshape")
            }
            .buttonStyle(.jbSecondary)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
    }

    /// Show lock, the activity history, setup files and the support report.
    private var moreMenu: some View {
        Menu {
            Button { state.requestShowLock(!state.showLock) } label: {
                Label(state.showLock ? "Turn Off Show Lock" : "Turn On Show Lock",
                      systemImage: state.showLock ? "lock.open" : "lock")
            }
            Divider()
            Button { state.showActivity() } label: { Label("Activity…", systemImage: "clock.arrow.circlepath") }
            Button { state.exportSetup() } label: { Label("Export Setup…", systemImage: "square.and.arrow.up") }
            Button { state.importSetup() } label: { Label("Import Setup…", systemImage: "square.and.arrow.down") }
                .disabled(state.showLock || state.batchRunning)
            Divider()
            Button { state.copyDiagnostics() } label: { Label("Copy Diagnostics", systemImage: "doc.on.clipboard") }
            Button { AppLog.shared.open() } label: { Label("Open Log", systemImage: "doc.text") }
        } label: {
            // An HStack, not `Label`: the borderless popup renders a Label blank. Worded, like every header button
            // (house rule: no icon-only buttons); the lock shows here while on.
            HStack(spacing: 5) {
                Image(systemName: state.showLock ? "lock.fill" : "ellipsis.circle")
                Text("More")
            }
        }
        .jbMenuPill(.secondary)
        .fixedSize()
        .help("Show lock, activity, setup files and diagnostics")
    }

    /// Find & filter: a search field (⌘F, Esc clears), the status filter and the match count.
    private var filterBar: some View {
        HStack(spacing: 10) {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").font(.system(size: 11)).foregroundStyle(Color.jbText3)
                TextField("Find apps", text: $state.searchText)
                    .textFieldStyle(.plain)
                    .font(JBFont.body)
                    .focused($findFocused)
                    .onExitCommand { state.searchText = ""; findFocused = false }
                if !state.searchText.isEmpty {
                    Button { state.searchText = "" } label: {
                        Image(systemName: "xmark.circle.fill").font(.system(size: 11))
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(Color.jbText3)
                    .help("Clear")
                    .accessibilityLabel("Clear search")
                }
            }
            .padding(.horizontal, 8).padding(.vertical, 4)
            .background(RoundedRectangle(cornerRadius: JBRadius.ctl, style: .continuous).fill(Color.jbSunken))
            .overlay(RoundedRectangle(cornerRadius: JBRadius.ctl, style: .continuous)
                .strokeBorder(findFocused ? Color.jbAccent.opacity(0.6) : Color.jbLine))
            .frame(maxWidth: 260)
            JBSegmented(segments: StatusFilter.allCases.map { JBSegmented.Segment(id: $0, label: $0.label) },
                        selection: $state.statusFilter)
                .fixedSize()
            Spacer(minLength: 8)
            Text(state.filterCountText).font(JBFont.small).foregroundStyle(Color.jbText3)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
    }

    @ViewBuilder
    private var banners: some View {
        if let v = state.launcherUpdateAvailable { launcherBanner(v) }
        if let v = state.launcherWhatsNew { whatsNewBanner(v) }
        if state.showLock { lockBanner }
        if !state.hasCredentials { credentialsBanner }
    }

    @ViewBuilder
    private var content: some View {
        if let err = state.globalError {
            banner(err, systemImage: "exclamationmark.triangle.fill", tint: .jbDanger)
            Spacer()
        } else {
            if state.hasCredentials, state.noAppsAccessible {
                banner(authMode == .token
                        ? "This token can’t access any apps. Check the token’s repository access in Settings, or ask whoever set up your access."
                        : "No apps are reachable right now. Check the passphrase in Settings, or ask whoever set up your access.",
                       systemImage: "lock.fill", tint: .jbWarn)
                Spacer()
            } else if !state.hasVisibleRows {
                banner(state.hasHiddenApps
                        ? "Every app is hidden. Show them again from Settings → Hidden apps."
                        : "No apps to show yet. Press Refresh, or check your access in Settings.",
                       systemImage: "eye.slash", tint: .jbText3)
                Spacer()
            } else if state.isFiltering, state.displayGroups.isEmpty {
                noMatches
                Spacer()
            } else if viewMode == .grid {
                gridView
            } else {
                listView
            }
        }
    }

    /// The find bar matched nothing.
    private var noMatches: some View {
        HStack(spacing: 10) {
            Image(systemName: "magnifyingglass").foregroundStyle(Color.jbText3)
            Text(state.searchText.trimmingCharacters(in: .whitespaces).isEmpty
                 ? "No apps match this filter."
                 : "No apps match “\(state.searchText.trimmingCharacters(in: .whitespaces))”.")
                .font(JBFont.body).foregroundStyle(Color.jbText)
            Spacer()
            Button("Clear Filter") { state.clearFilter() }
                .buttonStyle(.jbSecondary)
        }
        .padding(12)
        .bannerTint(.jbText3)
    }

    /// Show lock is on: nothing installs, updates or uninstalls until it's turned off.
    private var lockBanner: some View {
        HStack(spacing: 10) {
            Image(systemName: "lock.fill").foregroundStyle(Color.jbInfo)
            VStack(alignment: .leading, spacing: 1) {
                Text("Show lock is on").font(JBFont.status).foregroundStyle(Color.jbInfo)
                Text("Installs, updates and uninstalls are paused. Launching still works.")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
            }
            Spacer()
            Button("Turn Off") { state.requestShowLock(false) }
                .buttonStyle(.jbSecondary)
        }
        .padding(12)
        .bannerTint(.jbInfo)
    }

    /// Once, after the launcher itself has been updated.
    private func whatsNewBanner(_ version: String) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "sparkles").foregroundStyle(Color.jbAccent)
            Text("JB Theatre Tools was updated to v\(VersionDisplay.norm(version))")
                .font(JBFont.status).foregroundStyle(Color.jbAccent)
            Spacer()
            Button("See What's New") { Task { await state.showLauncherWhatsNew() } }
                .buttonStyle(.jbSecondary)
            Button { state.dismissLauncherWhatsNew() } label: { Image(systemName: "xmark") }
                .buttonStyle(.jbIcon)
                .help("Dismiss")
                .accessibilityLabel("Dismiss")
        }
        .padding(12)
        .bannerTint(.jbAccent)
    }

    /// Detailed list layout — a custom gesture-driven reorderable list (see `ReorderableList`). Uses a
    /// `DragGesture` rather than the drag-and-drop system: the dragged row lifts and tracks the cursor at
    /// the display's frame rate, and other rows slide aside. (The drag-and-drop system delivered hover
    /// events only ~7×/sec and trailed a system drag image, which is what felt laggy.)
    private var listView: some View {
        ReorderableList()
    }

    /// Compact icon-grid layout: an icon + name tile per app, pinned apps first. Tiles drag-reorder on
    /// drop (a lifted card + drop-target highlight); a click launches (when installed) or installs; the
    /// full action set lives in the tile's right-click menu.
    private var gridView: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                ForEach(state.displayGroups) { group in
                    VStack(alignment: .leading, spacing: 10) {
                        CategorySectionHeader(group: group)
                        if !state.isCollapsed(group.key) {
                            LazyVGrid(columns: [GridItem(.adaptive(minimum: 132), spacing: 12)],
                                      alignment: .leading, spacing: 12) {
                                ForEach(group.rows) { row in
                                    AppGridTile(row: row, isPinned: state.isPinned(row.id),
                                                selectedVariantId: state.selectedVariantId(row.app),
                                                isHeld: state.isHeld(row.id), locked: state.showLock,
                                                filtering: state.isFiltering)
                                        .equatable()
                                }
                            }
                        }
                    }
                }
            }
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func launcherBanner(_ version: String) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "arrow.down.circle.fill").foregroundStyle(Color.jbAccent)
            VStack(alignment: .leading, spacing: 1) {
                Text(AppState.versionIsNewer(version, than: state.currentVersion)
                     ? "JB Theatre Tools \(version) is available"
                     : "Back to the release: JB Theatre Tools \(version)")
                    .font(JBFont.status).foregroundStyle(Color.jbAccent)
                Text(state.launcherDownloadMessage ?? "You're running v\(state.currentVersion).")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            Button {
                Task { await state.updateLauncher() }
            } label: {
                if state.launcherDownloading { ProgressView().controlSize(.small) }
                else { Text(state.launcherPendingRestart != nil ? "Restart" : "Update") }
            }
            .disabled(state.launcherDownloading)
            .help("Download, verify and install the new version in place, then restart — open apps keep running")
        }
        .padding(12)
        .bannerTint(.jbAccent)
    }

    private var credentialsBanner: some View {
        HStack(spacing: 10) {
            Image(systemName: "key.fill").foregroundStyle(Color.jbWarn)
            VStack(alignment: .leading, spacing: 1) {
                Text(state.credentialsPrompt).font(JBFont.status).foregroundStyle(Color.jbWarn)
                Text(authMode == .token
                     ? "Settings → paste a fine-grained PAT (Contents: read)."
                     : "Settings → enter the suite passphrase (ask whoever set up your access).")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
            }
            Spacer()
            Button("Open Settings") { showSettings = true }
        }
        .padding(12)
        .bannerTint(.jbWarn)
    }

    /// Rule 28 — the house credit line, carrying the launcher's own version.
    private var credit: some View {
        Text("Created by: James Breedon & Claude Code · v\(state.currentVersion)")
            .font(JBFont.small)
            .foregroundStyle(Color.jbText3)
            .frame(maxWidth: .infinity, alignment: .center)
            .padding(.vertical, 6)
    }

    private func banner(_ text: String, systemImage: String, tint: Color) -> some View {
        HStack(spacing: 10) {
            Image(systemName: systemImage).foregroundStyle(tint)
            Text(text).font(JBFont.body).foregroundStyle(Color.jbText)
            Spacer()
        }
        .padding(12)
        .bannerTint(tint)
    }

    private func firstRefresh() async {
        guard updateMode == .everyLaunch, !refreshing else { return }
        if state.hasCredentials { await refreshAll() }
        await state.checkLauncherUpdate()
        // Dev perf harness only: JBTT_AUTO_DOWNLOAD_ALL=1 runs Download All straight after the first refresh so
        // a profiler (`sample`) can capture a whole run headlessly. Never set on a normal launch.
        if ProcessInfo.processInfo.environment["JBTT_AUTO_DOWNLOAD_ALL"] == "1" { await downloadAllAction(includeFull: false) }
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

    private func downloadAllAction(includeFull: Bool) async {
        downloadingAll = true
        await state.downloadAll(includeFull: includeFull)
        downloadingAll = false
    }
}

/// A small 2×3 dotted drag handle (the "grip"), shown on row hover — grab it to reorder.
struct DragGrip: View {
    var body: some View {
        GripDots().fill(Color.selectorBlue).frame(width: 9, height: 15)
    }
    /// The 2×3 dot grid as ONE shape (one drawing view) rather than six `Circle`s.
    private struct GripDots: Shape {
        func path(in r: CGRect) -> Path {
            var p = Path()
            for row in 0..<3 {
                for col in 0..<2 {
                    p.addEllipse(in: CGRect(x: r.minX + CGFloat(col) * 6, y: r.minY + CGFloat(row) * 6, width: 3, height: 3))
                }
            }
            return p
        }
    }
}

/// The lifted card that follows the cursor during a drag (the `.onDrag` preview) — a compact,
/// shadowed chip with the app's icon and name, shared by the list and grid.
struct DragPreviewCard: View {
    let id: String
    let displayName: String

    var body: some View {
        HStack(spacing: 10) {
            AppIconImage(id: id, displayName: displayName, size: 30)
            Text(displayName).font(JBFont.bodyStrong).foregroundStyle(Color.jbText).lineLimit(1)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 9)
        .background(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).fill(.regularMaterial))
        .overlay(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous)
            .strokeBorder(Color.jbLineStrong))
        // A shadow is allowed here: the card is a FLOATING object (v2 keeps shadows for floating things only).
        .shadow(color: .black.opacity(0.30), radius: 14, y: 7)
        .frame(minWidth: 200, alignment: .leading)
    }
}

/// Holds the live cursor point during a drag. A reference type kept in `@State` (which does NOT subscribe to
/// its `objectWillChange`), so updating `point` 60×/sec re-renders ONLY the floating card that observes it —
/// never the list, rows, or header. That isolation is what keeps the card glued to the pointer.
final class DragCursor: ObservableObject {
    @Published var point: CGPoint = .zero
    /// True only during the brief drop-settle: the card is animating from the release point into the
    /// landing slot. While dragging it's false, so `point` changes stay un-eased (card glued to the cursor).
    @Published var settling = false
    /// Each visible row's global frame. Plain (not published) and written from `onPreferenceChange`, so
    /// updating it — which happens on every scroll frame — never re-renders the list (that was the "slow
    /// scroll" bug). The drag gesture reads it to map the cursor to a target slot.
    var frames: [String: CGRect] = [:]
    /// Each section header's global frame (keyed by group key). Same rationale as `frames`; the live section
    /// drag reads these + the section's row frames to compute each section's midpoint.
    var headerFrames: [String: CGRect] = [:]
}

/// The one group being dragged: its key (Pinned sentinel or a category) and its live-reordered ids.
/// Reordered LOCALLY during a drag — the shared model isn't touched until drop, so nothing outside the list
/// re-renders and the drag never janks.
struct DragGroupOrder { var key: String; var ids: [String] }

/// Reports each visible row's global frame so the drag can map the cursor's Y to a target slot.
struct RowFrameKey: PreferenceKey {
    static var defaultValue: [String: CGRect] = [:]
    static func reduce(value: inout [String: CGRect], nextValue: () -> [String: CGRect]) {
        value.merge(nextValue()) { _, new in new }
    }
}

/// Reports each section header's global frame (keyed by group key), so a live section drag can map the
/// cursor's Y to a target section.
struct HeaderFrameKey: PreferenceKey {
    static var defaultValue: [String: CGRect] = [:]
    static func reduce(value: inout [String: CGRect], nextValue: () -> [String: CGRect]) {
        value.merge(nextValue()) { _, new in new }
    }
}

/// The lifted chip that follows the cursor while dragging a whole category SECTION (shown by `FloatingCard`).
struct CategoryDragChip: View {
    let title: String
    let count: Int

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "line.3.horizontal").font(.system(size: 12, weight: .semibold))
            Text(title.uppercased()).font(JBFont.label).tracking(JBFont.labelTracking)
            Text("\(count)").font(JBFont.label).opacity(0.6)
        }
        .foregroundStyle(Color.selectorBlue)
        .padding(.horizontal, 12).padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).fill(.regularMaterial))
        .overlay(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous)
            .strokeBorder(Color.jbLineStrong))
        // Floating chip → a shadow is in-policy here.
        .shadow(color: .black.opacity(0.28), radius: 12, y: 6)
    }
}

/// One line in the flattened list: a section header (by group key) or an app row (by id). Flattening the
/// whole list into ONE `ForEach` with stable per-item identity is what lets a row *glide* when it changes
/// section (e.g. on Pin) rather than teleporting: the `.row(id)` identity survives the move between sections,
/// so SwiftUI animates the position change instead of removing-and-reinserting it.
enum ListItem: Hashable {
    case header(String)   // group key
    case row(String)      // app id
}

/// The detailed list, modelled on the web prototype (SortableJS): during a drag the rows are reordered in a
/// LOCAL array — the shared model is never touched until drop — so nothing outside this view re-renders and
/// the drag stays smooth; a separate floating card follows the cursor. A plain `VStack` (only ~17 rows, so
/// non-lazy is fine) sidesteps the known LazyVStack-in-ScrollView stutter.
struct ReorderableList: View {
    @EnvironmentObject var state: AppState
    @State private var cursor = DragCursor()
    @State private var draggingId: String?
    @State private var order: DragGroupOrder?
    @State private var draggingCategory: String?
    @State private var catOrder: [String]?

    /// Groups in render order — reordered by the live section-drag order while a section is dragged (Pinned
    /// always stays first), else the model order.
    private func orderedGroups(_ groups: [AppState.DisplayGroup]) -> [AppState.DisplayGroup] {
        guard let co = catOrder else { return groups }
        let byKey = Dictionary(groups.map { ($0.key, $0) }, uniquingKeysWith: { a, _ in a })
        var out: [AppState.DisplayGroup] = []
        if let pinned = groups.first(where: { $0.key == AppState.pinnedGroupKey }) { out.append(pinned) }
        for k in co { if let g = byKey[k] { out.append(g) } }
        for g in groups where g.key != AppState.pinnedGroupKey && !co.contains(g.key) { out.append(g) }
        return out
    }

    /// The flattened render order: each group's header, then (unless collapsed) its rows — in the live row
    /// drag order for the group currently being row-dragged, else model order.
    private func items(_ groups: [AppState.DisplayGroup]) -> [ListItem] {
        var out: [ListItem] = []
        for g in groups {
            out.append(.header(g.key))
            guard !state.isCollapsed(g.key) else { continue }
            let ids = (order?.key == g.key) ? (order?.ids ?? g.rows.map(\.id)) : g.rows.map(\.id)
            out.append(contentsOf: ids.map { ListItem.row($0) })
        }
        return out
    }

    var body: some View {
        let groups = orderedGroups(state.displayGroups)
        let byKey = Dictionary(groups.map { ($0.key, $0) }, uniquingKeysWith: { a, _ in a })
        return ScrollView {
            VStack(alignment: .leading, spacing: 2) {
                ForEach(items(groups), id: \.self) { item in
                    switch item {
                    case .header(let key):
                        if let g = byKey[key] {
                            ListSectionHeader(group: g, cursor: cursor,
                                              draggingCategory: $draggingCategory, catOrder: $catOrder)
                                .background(
                                    GeometryReader { geo in
                                        Color.clear.preference(key: HeaderFrameKey.self,
                                                               value: [key: geo.frame(in: .global)])
                                    }
                                )
                        }
                    case .row(let id):
                        rowView(id).transition(.opacity)
                    }
                }
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 6)
        }
        // The floating card is drawn over the (non-scrolling) viewport; everything is measured in GLOBAL
        // space so the gesture, the frames and the card agree. `origin` converts the global cursor to local.
        .overlay {
            GeometryReader { geo in
                FloatingCard(cursor: cursor, draggingId: draggingId, draggingCategory: draggingCategory,
                             origin: geo.frame(in: .global).origin)
            }
            .allowsHitTesting(false)
        }
        // Store frames on the (non-observed) cursor object — writing them never re-renders the list, so this
        // fires freely on scroll without bogging it, and the frames are always current when a drag starts.
        .onPreferenceChange(RowFrameKey.self) { cursor.frames = $0 }
        .onPreferenceChange(HeaderFrameKey.self) { cursor.headerFrames = $0 }
    }

    @ViewBuilder
    private func rowView(_ id: String) -> some View {
        if let row = state.rows.first(where: { $0.id == id }) {
            AppRowView(row: row, cursor: cursor, draggingId: $draggingId, order: $order,
                       isPinned: state.isPinned(id), selectedVariantId: state.selectedVariantId(row.app),
                       isHeld: state.isHeld(id), locked: state.showLock, filtering: state.isFiltering)
                .equatable()   // body runs only when `row` publishes or these inputs change
                .background(
                    GeometryReader { geo in
                        Color.clear.preference(key: RowFrameKey.self, value: [id: geo.frame(in: .global)])
                    }
                )
        }
    }
}

/// The tap-to-collapse part of a section header: a disclosure chevron + title + app count. Shared by the
/// list and grid headers; clicking it folds/unfolds the section (persisted, per-machine).
struct SectionCollapseLabel: View {
    @EnvironmentObject var state: AppState
    let group: AppState.DisplayGroup
    private var collapsed: Bool { state.isCollapsed(group.key) }

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "chevron.right")
                .font(.system(size: 9, weight: .semibold))
                .rotationEffect(.degrees(collapsed ? 0 : 90))
                .foregroundStyle(Color.selectorBlue)
            Text(group.title.uppercased())
                .font(JBFont.label).tracking(JBFont.labelTracking)
                .foregroundStyle(Color.selectorBlue)
            Text("\(group.rows.count)")
                .font(JBFont.label)
                .foregroundStyle(Color.selectorBlue.opacity(0.7))
                .padding(.horizontal, 5).padding(.vertical, 0.5)
                .background(Capsule().fill(Color.selectorBlue.opacity(0.12)))
        }
        .contentShape(Rectangle())
        .onTapGesture {
            guard !state.isFiltering else { return }   // a filtered list shows every section open
            withAnimation(.spring(response: 0.34, dampingFraction: 0.86)) { state.toggleCollapsed(group.key) }
        }
        .help(state.isFiltering ? "" : (collapsed ? "Show \(group.title)" : "Hide \(group.title)"))
    }
}

/// The always-visible "REORDER ⣿" grip pill (visual only) — the caller attaches the drag (custom gesture in
/// the list, `.draggable` in the grid). Always shown so it's clearly grab-able, and the sole drag source so
/// dragging a section never fights the collapse tap.
struct ReorderPill: View {
    let hovering: Bool
    var body: some View {
        HStack(spacing: 5) {
            Text("REORDER")
                .font(JBFont.label).tracking(JBFont.labelTracking)
                .foregroundStyle(Color.selectorBlue.opacity(hovering ? 0.9 : 0.45))
            DragGrip().frame(width: 14).opacity(hovering ? 0.95 : 0.55)
        }
        .padding(.horizontal, 7).padding(.vertical, 3)
        .background(Capsule().fill(Color.selectorBlue.opacity(hovering ? 0.12 : 0.06)))
        .contentShape(Capsule())
    }
}

/// The GRID's section header — collapse label + a `.draggable` REORDER pill; the whole header is a drop
/// target. (The grid keeps the system drag-and-drop; the LIST uses the custom live drag below.)
struct CategorySectionHeader: View {
    @EnvironmentObject var state: AppState
    let group: AppState.DisplayGroup
    @State private var hovering = false
    @State private var isDropTarget = false
    private var isPinned: Bool { group.key == AppState.pinnedGroupKey }

    var body: some View {
        let bar = HStack(spacing: 6) {
            SectionCollapseLabel(group: group)
            Spacer(minLength: 8)
            if !isPinned && !state.isFiltering {
                ReorderPill(hovering: hovering)
                    .draggable(CategoryDrag.token(group.key)) {
                        CategoryDragChip(title: group.title, count: group.rows.count)
                    }
                    .help("Drag onto another section to move “\(group.title)”")
            }
        }
        .padding(.horizontal, 10).padding(.top, 9).padding(.bottom, 3)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous)
            .fill(isDropTarget ? Color.jbAccent.opacity(0.14) : Color.clear))
        .onHover { hovering = $0 }

        if isPinned {
            bar
        } else {
            bar.dropDestination(for: String.self) { dropped, _ in
                guard !state.isFiltering, let src = dropped.first.flatMap(CategoryDrag.key) else { return false }
                withAnimation(.spring(response: 0.3, dampingFraction: 0.85)) {
                    state.moveCategory(src, onto: group.key)
                }
                return true
            } isTargeted: { isDropTarget = $0 }
        }
    }
}

/// The LIST's section header — same visual, but the REORDER pill carries the SAME custom live-drag as the app
/// rows: a floating chip follows the cursor and the sections reorder live as you pass each one's midpoint
/// (no need to hit the header line), committing on drop with a settle animation.
struct ListSectionHeader: View {
    @EnvironmentObject var state: AppState
    let group: AppState.DisplayGroup
    let cursor: DragCursor
    @Binding var draggingCategory: String?
    @Binding var catOrder: [String]?
    @State private var hovering = false

    private var isPinned: Bool { group.key == AppState.pinnedGroupKey }
    private var isDragging: Bool { draggingCategory == group.key }

    var body: some View {
        HStack(spacing: 6) {
            SectionCollapseLabel(group: group)
            Spacer(minLength: 8)
            if !isPinned && !state.isFiltering {
                ReorderPill(hovering: hovering)
                    .gesture(reorderGesture)
                    .help("Drag to move the “\(group.title)” section")
            }
        }
        .padding(.horizontal, 10).padding(.top, 9).padding(.bottom, 3)
        .frame(maxWidth: .infinity, alignment: .leading)
        .opacity(isDragging ? 0 : 1)
        .overlay { if isDragging { dropSlot } }
        .onHover { hovering = $0 }
    }

    /// Dashed placeholder shown in this header's slot while its section is the one being dragged.
    private var dropSlot: some View {
        RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous)
            .strokeBorder(Color.jbAccent, style: StrokeStyle(lineWidth: 2, dash: [5, 4]))
            .background(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous)
                .fill(Color.jbAccent.opacity(0.08)))
            .padding(.horizontal, 2)
    }

    private var reorderGesture: some Gesture {
        DragGesture(minimumDistance: 3, coordinateSpace: .global)
            .onChanged { value in
                if draggingCategory != group.key {
                    draggingCategory = group.key
                    catOrder = state.categoryOrderKeys
                }
                cursor.point = value.location          // moves ONLY the floating chip (isolated re-render)
                reorderCategories(toY: value.location.y)
            }
            .onEnded { _ in
                guard let order = catOrder else { draggingCategory = nil; return }
                // Settle the chip into the section's header slot, THEN commit + reveal — so a drop anywhere
                // floats home instead of snapping.
                if let slot = cursor.headerFrames[group.key] {
                    cursor.settling = true
                    withAnimation(.easeOut(duration: DragTiming.dropSettle)) {
                        cursor.point = CGPoint(x: slot.minX + 16, y: slot.minY + 14)
                    }
                    DispatchQueue.main.asyncAfter(deadline: .now() + DragTiming.dropSettle) {
                        state.setCategoryOrder(order)
                        catOrder = nil
                        draggingCategory = nil
                        cursor.settling = false
                    }
                } else {
                    state.setCategoryOrder(order)
                    catOrder = nil
                    draggingCategory = nil
                }
            }
    }

    /// Reorders the LOCAL category order so the dragged section sits where the cursor is — comparing the
    /// cursor Y to each section's vertical MIDPOINT (header top → its last row's bottom), so you don't have
    /// to drag exactly to the header line.
    private func reorderCategories(toY y: CGFloat) {
        guard var order = catOrder, let cur = order.firstIndex(of: group.key) else { return }
        func midY(_ key: String) -> CGFloat {
            guard let h = cursor.headerFrames[key] else { return .greatestFiniteMagnitude }
            let bottom = state.rowIds(inSection: key).compactMap { cursor.frames[$0]?.maxY }.max() ?? h.maxY
            return (h.minY + bottom) / 2
        }
        var target = order.count - 1
        for (i, key) in order.enumerated() where y < midY(key) { target = i; break }
        guard target != cur else { return }
        order.move(fromOffsets: IndexSet(integer: cur), toOffset: target > cur ? target + 1 : target)
        withAnimation(.easeOut(duration: 0.14)) { catOrder = order }
    }
}

/// The lifted card that follows the cursor. Observes only `DragCursor`, and its position is never animated,
/// so it stays glued to the pointer. `cursor.point` is global; `origin` is this overlay's global top-left.
struct FloatingCard: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var cursor: DragCursor
    let draggingId: String?
    let draggingCategory: String?
    let origin: CGPoint

    var body: some View {
        ZStack(alignment: .topLeading) {
            if let id = draggingId, let row = state.rows.first(where: { $0.id == id }) {
                DragPreviewCard(id: id, displayName: row.displayName)
                    .offset(x: cursor.point.x - origin.x - 16, y: cursor.point.y - origin.y - 22)
                    // Glue the card to the cursor while dragging (strip any inherited animation); but DON'T
                    // strip it during the drop-settle, so the card springs into its landing slot.
                    .transaction { if !cursor.settling { $0.animation = nil } }
                    .allowsHitTesting(false)
            } else if let key = draggingCategory,
                      let g = state.displayGroups.first(where: { $0.key == key }) {
                CategoryDragChip(title: g.title, count: g.rows.count)
                    .offset(x: cursor.point.x - origin.x - 16, y: cursor.point.y - origin.y - 14)
                    .transaction { if !cursor.settling { $0.animation = nil } }
                    .allowsHitTesting(false)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
}

/// A single catalog row: grip, icon, name/blurb/version, status pill, and action buttons. Grabbing the grip
/// starts the drag: the floating card follows the cursor, this row becomes a dashed placeholder, and the
/// other rows slide aside — driven by reordering a LOCAL array, so there's no shared-state churn and it
/// stays smooth. The order is committed to the model on drop.
struct AppRowView: View, Equatable {
    @Environment(\.appState) private var appState
    private var state: AppState { appState! }
    @ObservedObject var row: AppState.Row
    let cursor: DragCursor
    @Binding var draggingId: String?
    @Binding var order: DragGroupOrder?
    /// Read in the body but owned by AppState — passed as inputs so a pin / variant change re-runs the body
    /// (via `==`) while unrelated publishes don't.
    let isPinned: Bool
    let selectedVariantId: String?
    /// Held at its installed version (no Update offered), show lock on (nothing installs), list filtered (no reorder).
    let isHeld: Bool
    let locked: Bool
    let filtering: Bool

    /// Only these decide whether a PARENT re-render needs this row's body; the row's own `@ObservedObject`
    /// publishes (busy/status/installed…) still re-render it regardless.
    static func == (a: AppRowView, b: AppRowView) -> Bool {
        a.row === b.row && a.cursor === b.cursor && a.isPinned == b.isPinned && a.selectedVariantId == b.selectedVariantId
            && a.isHeld == b.isHeld && a.locked == b.locked && a.filtering == b.filtering
    }
    @State private var hovering = false
    @State private var confirmingUninstall = false
    @State private var menuOpen = false

    private var isDragging: Bool { draggingId == row.id }

    /// Dev A/B switch (`JBTT_NO_RASTER=1`): keeps rows as live views for profiling comparisons.
    private static let rasterDisabled = ProcessInfo.processInfo.environment["JBTT_NO_RASTER"] == "1"

    var body: some View {
        // On macOS, SwiftUI backs every Text, image and filled shape with its own NSView, and AppKit walks
        // and re-lays-out the window's whole view tree on every commit and every scroll step. Twenty-one
        // rows × ~18 views made scrolling, dragging and each install completion stutter. So the row's
        // content (`core`) is rasterised into ONE Metal layer (`drawingGroup`) — and `core` depends only on
        // the row's model state, so it re-renders on an install/status change and never on pointer traffic.
        // Everything the pointer drives — hover wash, grip dots, the live ⋯ menu (an AppKit popup, which
        // cannot live inside a drawing group), hairline, drag placeholder, download bar — is layered OUTSIDE
        // the group, so hovering a row costs a few tiny views and no re-rasterisation.
        Group {
            if Self.rasterDisabled { core } else { core.drawingGroup() }
        }
        .opacity(isDragging ? 0 : 1)
        .background {
            if hovering && !isDragging {
                RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous).fill(Color.jbRowHover)
            }
        }
        .overlay(alignment: .bottom) {
            Color.jbLine
                .frame(height: 1)
                .padding(.leading, 57)
                .padding(.trailing, 10)
                .opacity(hovering || isDragging ? 0 : 1)
        }
        .overlay(alignment: .leading) { gripOverlay }
        // The ⋯ is drawn HERE only, never in the rasterised core (which just reserves its space): the static
        // glyph normally, the live menu while hovered. Drawing both — the core's glyph plus the menu laid over
        // it at a slightly different size — showed a doubled ⋯ on hover and while the menu was open.
        .overlay(alignment: .trailing) {
            Group {
                if (hovering || menuOpen) && !isDragging { liveMenu } else { rowMenuGlyph }
            }
            .padding(.trailing, 10)
        }
        // Moving into the open menu ends the row's hover; without this the live menu was swapped back to the
        // static glyph underneath its own open menu. Latch while THIS row's menu is tracking.
        .onReceive(NotificationCenter.default.publisher(for: NSMenu.didBeginTrackingNotification)) { _ in
            if hovering { menuOpen = true }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSMenu.didEndTrackingNotification)) { _ in
            if menuOpen { menuOpen = false }
        }
        // The download bar is an OVERLAY on the row, not a child of the info column: inserting it into the
        // layout changed the row's height, which re-laid-out every row below it. It ticks ~12×/s, so it
        // must also stay outside the rasterised core.
        .overlay(alignment: .bottomLeading) {
            if row.busy {
                RowProgressBar(id: row.id)
                    .frame(width: 240)
                    .padding(.leading, 86)
                    .padding(.bottom, 3)
            }
        }
        .overlay { if isDragging { dropSlot } }
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .modifier(ErrorHelp(message: row.errorMessage ?? heldHelp))   // tooltips don't reach into the drawing group
        .confirmationDialog("Uninstall \(row.displayName)?",
                            isPresented: $confirmingUninstall, titleVisibility: .visible) {
            Button("Uninstall", role: .destructive) { Task { await state.uninstallAsync(row.id) } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This removes the installed app from your Mac. You can reinstall it anytime.")
        }
    }

    /// Everything the row draws from its MODEL state: grip slot · icon · text column · status pill · actions.
    /// No pointer-dependent state is read here (see `body`), and no AppKit-backed control lives here.
    private var core: some View {
        let _ = LoopWatch.mark("row.core \(row.id)")
        return HStack(alignment: .center, spacing: 11) {
            Color.clear.frame(width: 16)   // the grip's slot; the dots + gesture are layered over it
            AppIconImage(id: row.id, displayName: row.displayName, size: 38)
            infoColumn
            Spacer(minLength: 8)
            statusBadge
            actions
        }
        .padding(.vertical, 9)
        .padding(.horizontal, 10)
        .frame(maxWidth: .infinity)
    }

    /// Attaches the error message as a tooltip only when there is one (an unconditional `.help("")` would
    /// register an empty tooltip on every row).
    private struct ErrorHelp: ViewModifier {
        let message: String?
        func body(content: Content) -> some View {
            if let message { content.help(message) } else { content }
        }
    }

    /// The dashed accent placeholder shown in this row's slot while it's the one being dragged.
    private var dropSlot: some View {
        RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous)
            .strokeBorder(Color.jbAccent, style: StrokeStyle(lineWidth: 2, dash: [5, 4]))
            .background(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous)
                .fill(Color.jbAccent.opacity(0.08)))
            .padding(.horizontal, 4)
            .padding(.vertical, 1)
    }

    /// Grip handle, layered over the row's leading slot: the dots appear on hover, and the (constant) hit
    /// frame carries the reorder `DragGesture`. On macOS a click-drag doesn't scroll (scrolling is
    /// wheel/trackpad), so a plain gesture here doesn't fight the ScrollView.
    private var gripOverlay: some View {
        Color.clear
            .frame(width: 16)
            .frame(maxHeight: .infinity)
            .overlay { if (hovering && !filtering) || isDragging { DragGrip().opacity(0.85) } }
            .contentShape(Rectangle())
            .gesture(reorderGesture)
            .allowsHitTesting(!filtering)   // a filtered list hides rows, so reordering is off until it's cleared
            .padding(.leading, 10)
            .help("Drag to reorder")
    }

    private var reorderGesture: some Gesture {
        DragGesture(minimumDistance: 3, coordinateSpace: .global)
            .onChanged { value in
                if draggingId != row.id {
                    draggingId = row.id
                    let key = state.groupKey(row)   // Pinned, or this row's category — the only group that reorders
                    let ids = state.displayGroups.first { $0.key == key }?.rows.map(\.id) ?? [row.id]
                    order = DragGroupOrder(key: key, ids: ids)
                }
                cursor.point = value.location           // moves ONLY the floating card (isolated re-render)
                reorderLocally(toY: value.location.y)   // reorders the LOCAL array — no shared-state churn
            }
            .onEnded { _ in
                guard let o = order else { draggingId = nil; return }
                // The dragged row (drawn at opacity 0) still occupies its committed slot in the layout, so
                // its reported frame IS the landing slot. Spring the floating card into that slot, THEN
                // commit the order and reveal the row — so a drop from anywhere (even an invalid spot) floats
                // home instead of snapping back.
                if let slot = cursor.frames[row.id] {
                    let key = o.key, ids = o.ids
                    // `easeOut` (no spring tail/overshoot) finishes EXACTLY at `dropSettle`, so we reveal the
                    // row the instant the card arrives — no post-flight "sit". Reveal + commit happen in one
                    // synchronous block → the card vanishes and the row appears in the same frame, seamlessly.
                    cursor.settling = true
                    withAnimation(.easeOut(duration: DragTiming.dropSettle)) {
                        cursor.point = CGPoint(x: slot.minX + 16, y: slot.minY + 22)
                    }
                    DispatchQueue.main.asyncAfter(deadline: .now() + DragTiming.dropSettle) {
                        state.applyGroupOrder(key: key, orderedIds: ids)
                        order = nil
                        draggingId = nil
                        cursor.settling = false
                    }
                } else {
                    state.applyGroupOrder(key: o.key, orderedIds: o.ids)
                    order = nil
                    draggingId = nil
                }
            }
    }

    /// Moves the dragged row within its group's LOCAL order to the slot the cursor is over.
    private func reorderLocally(toY y: CGFloat) {
        guard var o = order else { return }
        var ids = o.ids
        guard let cur = ids.firstIndex(of: row.id) else { return }
        var target = ids.count - 1
        for (i, id) in ids.enumerated() {
            if let f = cursor.frames[id], y < f.midY { target = i; break }
        }
        guard target != cur else { return }
        ids.move(fromOffsets: IndexSet(integer: cur), toOffset: target > cur ? target + 1 : target)
        o.ids = ids
        withAnimation(.easeOut(duration: 0.10)) { order = o }
    }

    private var infoColumn: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 5) {
                Text(row.displayName).font(JBFont.bodyStrong).foregroundStyle(Color.jbText).lineLimit(1)
                if isPinned {
                    Image(systemName: "pin.fill").font(.system(size: 9)).foregroundStyle(Color.selectorBlue)
                }
                if held {
                    Text("HELD").font(JBFont.label).tracking(JBFont.labelTracking)
                        .foregroundStyle(Color.selectorBlue)
                        .padding(.horizontal, 5).padding(.vertical, 0.5)
                        .background(Capsule().fill(Color.selectorBlue.opacity(0.12)))
                }
            }
            Text(row.app.blurb).font(JBFont.small).foregroundStyle(Color.jbText2).lineLimit(1)
            versionLine
            if let error = row.errorMessage {
                // Visible, not a tooltip: failures ("Quit X before updating it…") used to live only in the log.
                Text(error)
                    .font(JBFont.labelRegular)
                    .foregroundStyle(Color.jbDanger)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                whatsNewLine
            }
            variantToggle
        }
    }

    private var versionLine: some View {
        HStack(spacing: 6) {
            Text("Installed: \(installedText)")
            Text("·")
            Text("Latest: \(latestText)")
            if let size = latestSize {
                Text("·")
                Text(size)
            }
            if isDevBuild {
                Text("·")
                Text("dev build").foregroundStyle(Color.jbWarn)
            }
            if row.installed != nil, state.runsTranslated(row) {
                Text("·")
                Text(state.hasSeparateIntelBuild(row) ? "Intel (Rosetta)" : "opens as Intel").foregroundStyle(Color.jbInfo)
            }
        }
        .font(JBFont.labelRegular)
        .foregroundStyle(Color.jbText3)
        .lineLimit(1)   // never wraps: a row's height must not depend on the window width or a download starting
        .truncationMode(.tail)
    }

    /// Held at the installed version (only meaningful once something is installed).
    private var held: Bool { isHeld && row.installed != nil }

    /// The row's tooltip when an update is being held back.
    private var heldHelp: String? {
        guard held, row.status == .updateAvailable, let latest = row.latest, let installed = row.installed else { return nil }
        return "\(VersionDisplay.display(latest)) is available — held at \(VersionDisplay.display(installed))"
    }

    /// "v1.2.0 (3 days ago)" — the latest release and how long it has been out.
    private var latestText: String {
        guard let latest = row.latest else { return "—" }
        guard let published = row.latestRelease?.published else { return latest }
        return "\(latest) (\(RelativeAge.describe(published, now: Date())))"
    }

    /// The selected edition's download size for the latest release.
    private var latestSize: String? {
        guard let id = row.latestAssetId, let asset = row.latestRelease?.assets.first(where: { $0.id == id }),
              asset.size > 0 else { return nil }
        return ByteSize.format(Int64(asset.size))
    }

    /// A development pre-release is installed or on offer (Dev channel).
    private var isDevBuild: Bool {
        (row.installed.map(AppState.isDevTag) ?? false) || (row.latest.map(AppState.isDevTag) ?? false)
    }

    /// The installed version of the SELECTED variant's slot, annotated with that variant's label for
    /// apps that ship variants (each variant is its own install; the toggle picks which one is shown).
    private var installedText: String {
        guard let v = row.installed else { return "—" }
        if row.app.hasVariants, let variant = row.app.variantLabel(selectedVariantId) { return "\(v) (\(variant))" }
        return v
    }

    /// A compact Light/Full toggle, shown inline only for apps that ship variants.
    @ViewBuilder
    private var variantToggle: some View {
        if row.app.hasVariants, let vs = row.app.variants {
            JBSegmented(segments: vs.map { JBSegmented.Segment(id: $0.id, label: $0.label) },
                        selection: Binding(
                            get: { selectedVariantId ?? vs.first?.id ?? "" },
                            set: { state.setVariant(row.id, $0) }),
                        compact: true)
                .fixedSize()
                .disabled(row.busy)
        }
    }

    /// One-line "what's new" for the app's current release, shown only when the catalog carries it.
    /// Labelled "New in vX.Y.Z:" when a version is present, else "What's new:".
    @ViewBuilder
    private var whatsNewLine: some View {
        if let note = row.whatsNew, !note.isEmpty {
            (
                Text(whatsNewLabel).fontWeight(.semibold)
                + Text(" ") + Text(note)
            )
            .font(JBFont.labelRegular)
            .foregroundStyle(Color.selectorBlue)
            .lineLimit(2)
            .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var whatsNewLabel: String {
        if let v = row.whatsNewVersion, !v.isEmpty { return "New in \(v):" }
        return "What's new:"
    }

    @ViewBuilder
    private var statusBadge: some View {
        switch row.status {
        case .checking:
            badge("Checking…", color: .jbText3)   // the header's Refresh spinner is the one live indicator
        case .upToDate:
            badge("Up to date", color: .jbOk)
        case .updateAvailable:
            if held { badge("Held", color: .selectorBlue) } else { badge("Update", color: .jbAccent) }
        case .notInstalled:
            badge("Not installed", color: .jbText2)
        case .installed:
            badge("Installed", color: .jbText2)
        case .noRelease:
            badge("No release", color: .jbText2)
        case .missingAsset:
            badge("No macOS build", color: .jbWarn)
        case .error(let msg):
            badge("Error", color: .jbDanger).help(msg)
        case .noAccess:
            // Row is filtered out of the list; nothing to show.
            EmptyView()
        case .unknown:
            EmptyView()
        }
    }

    private func badge(_ text: String, color: Color) -> some View {
        Text(text)
            .font(JBFont.label)
            .padding(.horizontal, 9).padding(.vertical, 3)
            .background(color.opacity(0.15))
            .foregroundStyle(color)
            .clipShape(Capsule())
    }

    @ViewBuilder
    private var actions: some View {
        HStack(spacing: 8) {
            // A download in flight can be cancelled; the row then goes back to how it was. Cancel takes the place of
            // Install / Update / Retry, so the row keeps its width (a width change re-wraps the text and re-lays-out
            // every row below it).
            if row.busy && row.cancellable {
                Button("Cancel") { state.cancelDownload(row.id) }
                    .buttonStyle(.jbSecondary)
                    .help("Stop this download")
                    .accessibilityLabel("Cancel downloading \(row.displayName)")
            } else if !locked {
                // Install / Update / Retry — depends on the checked status. Show lock offers none of them, and a
                // held app offers no Update / Retry (that would move it off its version).
                switch row.status {
                case .notInstalled:
                    installButton(title: "Install")
                case .updateAvailable where !held:
                    installButton(title: "Update")
                case .error where !held:
                    installButton(title: row.installed == nil ? "Install" : "Retry")
                default:
                    EmptyView()
                }
            }
            // Dev channel switched off but a dev build is still here: one click back to the release (a downgrade
            // — the version picker in ⋯ does the same, this just makes it obvious).
            if !locked, let tag = state.backToReleaseTag(row) {
                Button("Back to release") { Task { await state.install(row.id, tag: tag) } }
                    .buttonStyle(.jbSecondary)
                    .disabled(row.busy)
                    .help("Install the release \(tag) in place of the development build")
            }
            // Launch — available whenever something is installed, even before a refresh has run.
            if row.installed != nil { launchButton }
            rowMenu   // every visible row has the ⋯ menu (reordering is always available)
        }
        .controlSize(.small)
    }

    /// The ⋯ slot in the rasterised core: the glyph's size, drawn invisibly (see the overlay in `body`).
    private var rowMenu: some View {
        rowMenuGlyph.hidden()
    }

    /// The static ⋯ (every row that isn't hovered); `liveMenu` replaces it on hover.
    private var rowMenuGlyph: some View {
        Image(systemName: "ellipsis.circle")
            .font(.system(size: 11, weight: .medium))
            .foregroundStyle(Color.selectorBlue)
            .padding(2)
            .opacity(row.busy ? 0.45 : 1)
    }

    /// Overflow menu: pin/hide/reorder, pick a variant, install a specific (older) version, or uninstall.
    /// An AppKit popup underneath, so it exists only for the hovered row (one in the list, not 21) and is
    /// built fresh on hover — its labels (Pin/Unpin, Move Up/Down…) are always current when it opens.
    private var liveMenu: some View {
        Menu {
            AppMenuButtons(row: row, requestUninstall: { confirmingUninstall = true })
        } label: {
            Image(systemName: "ellipsis.circle").font(.system(size: 11, weight: .medium))   // = rowMenuGlyph
        }
        .jbMenuPill(.icon)   // house rule 21: selectors/menus are slate-blue, not the purple accent
        .fixedSize()
        .disabled(row.busy)
        .help("Variant, reorder, other versions & uninstall")
    }

    private func installButton(title: String) -> some View {
        Button(title) {
            Task { await state.install(row.id) }
        }
        .buttonStyle(.jbPrimary)
        .disabled(row.busy || row.latestAssetId == nil)
    }

    private var launchButton: some View {
        Button("Launch") { state.launch(row.id) }
            .buttonStyle(.jbSecondary)
            .disabled(row.installing)   // a download alone doesn't stop the app being opened
    }
}

/// A row's download progress bar. Observes ONLY `ProgressHub`, so a progress tick re-renders this bar and nothing
/// else (the rows array — and with it every row + the header — is untouched by progress).
struct RowProgressBar: View {
    @EnvironmentObject var hub: ProgressHub
    let id: String
    var body: some View {
        // Drawn in SwiftUI rather than `ProgressView(value:)` (an AppKit NSProgressIndicator): there is no
        // hosted control to lay out or measure, and the fill scales from the leading edge so the bar takes
        // whatever width its container gives it — no GeometryReader.
        let fraction = min(max(hub.progress[id] ?? 0, 0), 1)
        Capsule().fill(Color.jbLine)
            .overlay(alignment: .leading) {
                Rectangle().fill(Color.jbAccent).scaleEffect(x: fraction, y: 1, anchor: .leading)
            }
            .clipShape(Capsule())
            .frame(height: 4)
    }
}

/// The app's icon at a given size: the installed app's REAL icon once installed; else the per-app icon
/// bundled in the launcher (Resources/<id>.png); else a tinted monogram tile. Shared by list & grid.
struct AppIconImage: View {
    let id: String
    let displayName: String
    var size: CGFloat = 40

    /// `NSWorkspace.icon(forFile:)` is a comparatively heavy call; cache the result per path so a redraw
    /// (e.g. during a download) doesn't re-resolve it every time (audit F12). A path's icon is stable for
    /// the session; a reinstall reuses the same path, which is fine.
    private static let iconCache = NSCache<NSString, NSImage>()
    private static func installedIcon(_ path: String) -> NSImage {
        if let hit = iconCache.object(forKey: path as NSString) { return hit }
        LoopWatch.mark("icon miss \(path)")
        let img = thumbnail(NSWorkspace.shared.icon(forFile: path))
        iconCache.setObject(img, forKey: path as NSString)
        return img
    }

    /// Icons are cached as a fixed 128 px bitmap (enough for the 52 pt grid tile @2x). Caching the ORIGINAL
    /// (a 512 px PNG, or an NSWorkspace icon with reps up to 1024 px) meant every redraw of every row
    /// resampled a large image down to 38–52 pt with high interpolation. Rendered via a bitmap context so it's
    /// safe to run off the main thread (prewarm).
    private static func thumbnail(_ img: NSImage, px: Int = 128) -> NSImage {
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
              let ctx = NSGraphicsContext(bitmapImageRep: rep) else { return img }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = ctx
        ctx.imageInterpolation = .high
        img.draw(in: NSRect(x: 0, y: 0, width: px, height: px), from: .zero, operation: .copy, fraction: 1)
        NSGraphicsContext.restoreGraphicsState()
        let out = NSImage(size: NSSize(width: px, height: px))
        out.addRepresentation(rep)
        return out
    }

    /// After an install/update: drop any stale entry for that bundle path and resolve the fresh icon here
    /// (called from the detached install task), so the first post-install render finds it cached instead of
    /// running NSWorkspace.icon(forFile:) + the thumbnail on the main thread at the row flip.
    static func refreshInstalledIcon(_ path: String) {
        iconCache.removeObject(forKey: path as NSString)
        _ = installedIcon(path)
    }

    /// Fills both caches off the main thread at boot (called from `AppState.init` via a detached task), so the
    /// first frame never blocks on icon resolution.
    static func prewarm(paths: [String], ids: [String]) {
        for p in paths { _ = installedIcon(p) }
        for id in ids { _ = bundledIcon(id) }
    }

    var body: some View {
        // The launcher's own tile first (the House Style glyph-only set, identical across mac / Windows /
        // Android), then the installed app's real icon for anything without a tile, then a monogram. Rows
        // used to prefer the installed icon — a mixed list while apps adopt the new icons at their own pace.
        if let bundled = Self.bundledIcon(id) {
            Image(nsImage: bundled)
                .resizable().interpolation(.high)
                .frame(width: size, height: size)
        } else if let path = InstallManager.shared.installedPath(id)?.path {
            Image(nsImage: Self.installedIcon(path))
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

    /// Cache decoded bundled icons too (audit F12 cached only the installed-app icon) — otherwise a
    /// not-installed row re-reads and re-decodes its PNG from the app bundle on every redraw, which during
    /// a drag-reorder (all rows redraw per frame) stutters the main thread.
    private static let bundledCache = NSCache<NSString, NSImage>()
    static func bundledIcon(_ id: String) -> NSImage? {
        if let hit = bundledCache.object(forKey: id as NSString) { return hit }
        guard let url = Bundle.main.url(forResource: id, withExtension: "png"),
              let raw = NSImage(contentsOf: url) else { return nil }
        let img = thumbnail(raw)
        bundledCache.setObject(img, forKey: id as NSString)
        return img
    }
}

/// The per-row action menu, shared by the list row's ⋯ button and the grid tile's right-click menu:
/// pin/hide/reorder, pick a variant (Light/Full), install a specific version, Dock/alias, uninstall.
struct AppMenuButtons: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var row: AppState.Row
    /// Called when the user picks Uninstall — the host view shows its own confirmation dialog.
    var requestUninstall: () -> Void

    // Split into sections: as one Group the body outgrew Swift's type-checker ("unable to type-check this
    // expression in reasonable time").
    var body: some View {
        Group {
            arrangeSection
            infoSection
            variantSection
            archSection
            versionsSection
            installedSection
        }
    }

    @ViewBuilder private var arrangeSection: some View {
        Button(state.isPinned(row.id) ? "Unpin from Top" : "Pin to Top") {
            withAnimation(.spring(response: 0.42, dampingFraction: 0.82)) { state.togglePin(row.id) }
        }
        Button("Move Up") {
            withAnimation(.spring(response: 0.3, dampingFraction: 0.85)) { state.moveRow(row.id, up: true) }
        }
        .disabled(!state.canMove(row.id, up: true))
        Button("Move Down") {
            withAnimation(.spring(response: 0.3, dampingFraction: 0.85)) { state.moveRow(row.id, up: false) }
        }
        .disabled(!state.canMove(row.id, up: false))
        Button("Hide from List") {
            withAnimation(.easeInOut(duration: 0.22)) { state.setHidden(row.id, true) }
        }
    }

    @ViewBuilder private var infoSection: some View {
        Divider()
        Button("Details…") { state.showDetails(row.id) }
        if !row.releases.isEmpty {
            Button("Release Notes…") { state.showReleaseNotes(row.id) }
        }
        if row.installed != nil {
            // Held: Update All and automatic updates leave the app at its version.
            Button(state.isHeld(row.id) ? "Release Hold" : "Hold at This Version") { state.toggleHold(row.id) }
        }
    }

    @ViewBuilder private var variantSection: some View {
        if row.app.hasVariants, let vs = row.app.variants {
            Divider()
            Picker("Variant", selection: Binding(
                get: { state.selectedVariantId(row.app) ?? vs.first?.id ?? "" },
                set: { state.setVariant(row.id, $0) }
            )) {
                ForEach(vs) { Text($0.label).tag($0.id) }
            }
        }
    }

    /// Apple silicon Macs: run this edition as Intel through Rosetta (a universal app is opened as Intel; an edition
    /// with its own Intel build is reinstalled — asked first). Only offered when the edition can run both ways.
    @ViewBuilder private var archSection: some View {
        if state.canChooseIntel(row) {
            Divider()
            Toggle(state.hasSeparateIntelBuild(row) ? "Use the Intel Build (Rosetta)" : "Open as Intel (Rosetta)", isOn: Binding(
                get: { state.prefersIntel(row) },
                set: { on in Task { await state.setRunsAsIntel(row.id, on) } }
            ))
            .disabled(state.showLock || row.busy)
        }
    }

    @ViewBuilder private var versionsSection: some View {
        let offered = state.offeredReleases(row)
        if !offered.isEmpty {
            Divider()
            Section("Install version") {
                ForEach(offered) { rel in
                    Button { Task { await state.install(row.id, tag: rel.tagName, lenient: true) } }
                        label: { Text(versionLabel(rel)) }
                        .disabled(state.showLock || row.busy)
                }
            }
        }
        if let tag = state.rollbackTag(row) {
            Button("Roll Back to \(VersionDisplay.display(tag))…") { state.requestRollBack(row.id, to: tag) }
                .disabled(state.showLock || row.busy)
        }
    }

    @ViewBuilder private var installedSection: some View {
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
                .disabled(state.showLock || row.busy)
        }
    }

    private func versionLabel(_ rel: ReleaseInfo) -> String {
        var s = rel.tagName
        if rel.prerelease { s += " (pre-release)" }
        if rel.tagName == row.installed { s += "  ✓ installed" }
        return s
    }
}

/// One tile in the grid view: a large icon + name and a compact status line, on a raised card. A click
/// launches the app (if installed) or installs it; the full action set lives in the right-click menu.
/// Dragging a tile lifts a card and reorders it within its pin group on drop (the drop target highlights).
struct AppGridTile: View, Equatable {
    @Environment(\.appState) private var appState
    private var state: AppState { appState! }
    @ObservedObject var row: AppState.Row
    let isPinned: Bool
    let selectedVariantId: String?
    let isHeld: Bool
    let locked: Bool
    let filtering: Bool

    static func == (a: AppGridTile, b: AppGridTile) -> Bool {
        a.row === b.row && a.isPinned == b.isPinned && a.selectedVariantId == b.selectedVariantId
            && a.isHeld == b.isHeld && a.locked == b.locked && a.filtering == b.filtering
    }

    private var held: Bool { isHeld && row.installed != nil }
    @State private var hovering = false
    @State private var isDropTarget = false
    @State private var confirmingUninstall = false

    var body: some View {
        VStack(spacing: 0) {
            AppIconImage(id: row.id, displayName: row.displayName, size: 52)
            Text(row.displayName)
                .font(JBFont.smallStrong)
                .foregroundStyle(Color.jbText)
                .multilineTextAlignment(.center)
                .lineLimit(2)
                .frame(height: 28)
                .padding(.top, 5)
            statusCaption
                .padding(.top, 2)
                .padding(.bottom, 4)
            // Light/Full toggle, as on the list row — the only other way to switch in grid
            // view was a Picker buried in the right-click menu. Every tile reserves the slot so the grid's
            // rows stay one height.
            if row.app.hasVariants, let vs = row.app.variants {
                JBSegmented(segments: vs.map { JBSegmented.Segment(id: $0.id, label: $0.label) },
                            selection: Binding(
                                get: { selectedVariantId ?? vs.first?.id ?? "" },
                                set: { state.setVariant(row.id, $0) }),
                            compact: true)
                    .fixedSize()
                    .disabled(row.busy)
            } else {
                Color.clear.frame(height: 20)
            }
        }
        .frame(maxWidth: .infinity)
        .frame(height: 128)   // the original 140-pt tile (128 + 2 × 6 padding): the toggle fits inside it
        .padding(6)
        // v2: tiles are flat panels separated by a hairline — no card shadow (shadows are for floating
        // things only). Hover firms the hairline rather than lifting the tile.
        .background(
            RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous)
                .fill(hovering ? Color.jbRaised : Color.jbSurface)
        )
        .overlay(
            RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous)
                .strokeBorder(hovering ? Color.jbLineStrong : Color.jbLine)
        )
        .overlay(alignment: .topTrailing) {
            if isPinned {
                Image(systemName: "pin.fill").font(.system(size: 9))
                    .foregroundStyle(Color.selectorBlue).padding(8)
            }
        }
        .overlay(alignment: .bottom) {
            if row.busy {
                RowProgressBar(id: row.id)
                    .padding(.horizontal, 12).padding(.bottom, 8)
            }
        }
        .overlay(alignment: .topLeading) {
            // A download in flight can be cancelled from the tile too.
            if row.busy && row.cancellable {
                Button { state.cancelDownload(row.id) } label: {
                    Image(systemName: "xmark.circle.fill").font(.system(size: 13))
                }
                .buttonStyle(.jbIcon)
                .padding(5)
                .help("Stop this download")
                .accessibilityLabel("Cancel downloading \(row.displayName)")
            }
        }
        .overlay {
            if isDropTarget {
                RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous)
                    .strokeBorder(Color.jbAccent, style: StrokeStyle(lineWidth: 2, dash: [5, 4]))
                    .background(RoundedRectangle(cornerRadius: JBRadius.panel, style: .continuous)
                        .fill(Color.jbAccent.opacity(0.08)))
            }
        }
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .onTapGesture { primaryAction() }
        .draggable(row.id) {
            DragPreviewCard(id: row.id, displayName: row.displayName)
        }
        .dropDestination(for: String.self) { items, _ in
            // Only a tile drag (a bare app id) reorders here — ignore a section-header drag (category token), and
            // any drop while the list is filtered (hidden tiles would make the order ambiguous).
            guard !filtering, let dragged = items.first, CategoryDrag.key(dragged) == nil else { return false }
            var moved = false
            withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                moved = state.moveRow(dragged, onto: row.id)
            }
            // Return whether it moved: a drop onto another section is a no-op, so `false` floats the tile home.
            return moved
        } isTargeted: { isDropTarget = $0 }
        .contextMenu { AppMenuButtons(row: row, requestUninstall: { confirmingUninstall = true }) }
        .confirmationDialog("Uninstall \(row.displayName)?",
                            isPresented: $confirmingUninstall, titleVisibility: .visible) {
            Button("Uninstall", role: .destructive) { Task { await state.uninstallAsync(row.id) } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This removes the installed app from your Mac. You can reinstall it anytime.")
        }
        .help(tooltip)
    }

    /// Click behaviour: launch when installed, otherwise install (when the status allows it and show lock is off).
    private func primaryAction() {
        if row.installed != nil { state.launch(row.id); return }   // launch() waits only for an install in progress
        if row.busy { return }
        guard !locked else { return }
        switch row.status {
        case .notInstalled, .updateAvailable, .error: Task { await state.install(row.id) }
        default: break
        }
    }

    @ViewBuilder
    private var statusCaption: some View {
        Text(captionText)
            .font(JBFont.label)
            .foregroundStyle(captionColor)
            .lineLimit(1)
    }

    /// The status only — which edition this is shows in the tile's own Light/Full toggle.
    private var captionText: String {
        let base: String
        switch row.status {
        case .updateAvailable: base = held ? "Held" : "Update"
        case .upToDate:        base = row.installed ?? "Installed"
        case .installed:       base = row.installed ?? "Installed"
        case .notInstalled:    base = "Install"
        case .noRelease:       base = "No release"
        case .missingAsset:    base = "No macOS build"
        case .checking:        base = "Checking…"
        case .error:           base = "Error"
        default:               base = row.installed ?? " "
        }
        // A development build installed or on offer (Dev channel) is marked, as in the list.
        if (row.installed.map(AppState.isDevTag) ?? false) || (row.latest.map(AppState.isDevTag) ?? false) { return base + " · dev" }
        return base
    }

    private var captionColor: Color {
        switch row.status {
        case .updateAvailable:      return held ? .selectorBlue : .jbAccent
        case .upToDate:             return .jbOk
        case .missingAsset:         return .jbWarn
        case .error:                return .jbDanger
        default:                    return .jbText2
        }
    }

    private var tooltip: String {
        var t = row.displayName
        if let v = row.installed {
            t += " — installed \(v)"
            if row.app.hasVariants, let vl = row.app.variantLabel(selectedVariantId) { t += " (\(vl))" }
            if held, row.status == .updateAvailable, let latest = row.latest {
                t += " — held (\(VersionDisplay.display(latest)) is available)"
            }
        } else if row.status == .notInstalled, !locked {
            t += " — click to install"
        }
        return t
    }
}
